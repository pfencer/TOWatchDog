using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TOWatchDog.Configuration;

namespace TOWatchDog.Monitoring;

/// <summary>
/// Sorveglia un singolo applicativo: lo avvia, ne attende la terminazione e,
/// in base alla politica configurata, lo riavvia applicando ritardo e limiti.
/// </summary>
public sealed class ApplicationSupervisor
{
    // HttpClient condiviso tra tutti i supervisori per le sonde di health-check HTTP.
    private static readonly HttpClient HttpProbeClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan // il timeout effettivo è gestito per-richiesta.
    };

    private readonly MonitoredApplication _app;
    private readonly ILogger _logger;

    // Timestamp dei riavvii recenti, usati per applicare il limite su finestra scorrevole.
    private readonly Queue<DateTimeOffset> _restartTimestamps = new();

    public ApplicationSupervisor(MonitoredApplication app, ILogger logger)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => _app.Name;

    /// <summary>
    /// Esegue il ciclo di sorveglianza fino a quando non viene richiesto l'arresto
    /// tramite <paramref name="stoppingToken"/> o non viene superato il limite di riavvii.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_app.StartupDelaySeconds > 0)
        {
            _logger.LogInformation(
                "[{App}] Attesa iniziale di {Delay}s prima dell'avvio.",
                _app.Name, _app.StartupDelaySeconds);
            await DelaySafeAsync(TimeSpan.FromSeconds(_app.StartupDelaySeconds), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                process = StartOrAttach();
                if (process is null)
                {
                    // Impossibile avviare: attende il ritardo di riavvio e riprova (se consentito).
                    if (!TryRegisterRestartAndCheckLimit())
                    {
                        break;
                    }

                    await DelaySafeAsync(TimeSpan.FromSeconds(_app.RestartDelaySeconds), stoppingToken);
                    continue;
                }

                _logger.LogInformation(
                    "[{App}] In esecuzione (PID {Pid}).", _app.Name, process.Id);

                // Affianca all'attesa della terminazione un monitor di integrità (risorse
                // e stato "non risponde"): riparte chi dei due si conclude per primo.
                using var healthCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var exitTask = process.WaitForExitAsync(stoppingToken);
                var healthTask = MonitorHealthAsync(process, healthCts.Token);

                var finished = await Task.WhenAny(exitTask, healthTask);

                bool forcedByHealth = false;
                if (finished == healthTask)
                {
                    // Il monitor si è concluso per primo: true = stato non integro rilevato.
                    forcedByHealth = await healthTask;
                }

                // Interrompe il monitor (nel caso sia il processo a essere uscito per primo)
                // e ne osserva l'eventuale eccezione di annullamento.
                healthCts.Cancel();
                await ObserveHealthTaskAsync(healthTask);

                if (forcedByHealth && !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "[{App}] Stato non integro: forzatura della terminazione per il riavvio.",
                        _app.Name);
                    ForceTerminate(process);
                }

                // Assicura che l'attesa di uscita sia completata (il processo esce dopo il kill).
                await exitTask;

                // Uscita del processo (naturale, o forzata dal monitor di integrità).
                int exitCode = SafeGetExitCode(process);
                _logger.LogWarning(
                    "[{App}] Terminato con codice di uscita {ExitCode}.",
                    _app.Name, exitCode);

                // Uno stato non integro è considerato un guasto: il riavvio è previsto per le
                // politiche Always e OnFailure, mentre Never resta comunque rispettata.
                bool restart = forcedByHealth
                    ? _app.RestartPolicy != RestartPolicy.Never
                    : ShouldRestart(exitCode);

                if (!restart)
                {
                    _logger.LogInformation(
                        "[{App}] La politica '{Policy}' non prevede il riavvio per questa terminazione. Sorveglianza conclusa.",
                        _app.Name, _app.RestartPolicy);
                    break;
                }

                if (!TryRegisterRestartAndCheckLimit())
                {
                    break;
                }

                _logger.LogInformation(
                    "[{App}] Riavvio tra {Delay}s.", _app.Name, _app.RestartDelaySeconds);
                await DelaySafeAsync(TimeSpan.FromSeconds(_app.RestartDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Arresto richiesto: chiude il processo in modo controllato ed esce dal ciclo.
                await ShutdownProcessAsync(process);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "[{App}] Errore imprevisto durante la sorveglianza.", _app.Name);

                if (!TryRegisterRestartAndCheckLimit())
                {
                    break;
                }

                await DelaySafeAsync(TimeSpan.FromSeconds(_app.RestartDelaySeconds), stoppingToken);
            }
            finally
            {
                process?.Dispose();
            }
        }

        _logger.LogInformation("[{App}] Sorveglianza terminata.", _app.Name);
    }

    /// <summary>
    /// Avvia un nuovo processo oppure, se configurato, si aggancia ad uno già esistente.
    /// Restituisce null se l'avvio non è andato a buon fine.
    /// </summary>
    private Process? StartOrAttach()
    {
        if (_app.AttachToExisting)
        {
            var existing = FindExistingProcess();
            if (existing is not null)
            {
                _logger.LogInformation(
                    "[{App}] Agganciato al processo già in esecuzione (PID {Pid}).",
                    _app.Name, existing.Id);
                return existing;
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _app.ExecutablePath,
            Arguments = _app.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = ResolveWorkingDirectory(),
        };

        foreach (var kvp in _app.Environment)
        {
            startInfo.Environment[kvp.Key] = kvp.Value;
        }

        _logger.LogInformation(
            "[{App}] Avvio di '{Path} {Args}'.",
            _app.Name, _app.ExecutablePath, _app.Arguments);

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogError(
                    "[{App}] Process.Start ha restituito null: avvio non riuscito.", _app.Name);
            }

            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{App}] Impossibile avviare l'eseguibile.", _app.Name);
            return null;
        }
    }

    private string ResolveWorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_app.WorkingDirectory))
        {
            return _app.WorkingDirectory!;
        }

        return Path.GetDirectoryName(Path.GetFullPath(_app.ExecutablePath)) ?? string.Empty;
    }

    private Process? FindExistingProcess()
    {
        var targetName = Path.GetFileNameWithoutExtension(_app.ExecutablePath);
        if (string.IsNullOrEmpty(targetName))
        {
            return null;
        }

        try
        {
            var matches = Process.GetProcessesByName(targetName);
            if (matches.Length == 0)
            {
                return null;
            }

            // Mantiene il primo processo trovato e libera gli altri handle.
            var chosen = matches[0];
            for (int i = 1; i < matches.Length; i++)
            {
                matches[i].Dispose();
            }

            return chosen;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "[{App}] Ricerca del processo esistente non riuscita.", _app.Name);
            return null;
        }
    }

    private bool ShouldRestart(int exitCode) => _app.RestartPolicy switch
    {
        RestartPolicy.Always => true,
        RestartPolicy.OnFailure => exitCode != 0,
        RestartPolicy.Never => false,
        _ => false
    };

    /// <summary>
    /// Registra un tentativo di riavvio e verifica che non sia stato superato il limite
    /// consentito nella finestra temporale configurata.
    /// </summary>
    /// <returns>true se il riavvio è consentito, false se il limite è stato superato.</returns>
    private bool TryRegisterRestartAndCheckLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromSeconds(_app.RestartWindowSeconds);

        // Rimuove i riavvii usciti dalla finestra scorrevole.
        while (_restartTimestamps.Count > 0 && now - _restartTimestamps.Peek() > window)
        {
            _restartTimestamps.Dequeue();
        }

        _restartTimestamps.Enqueue(now);

        if (_app.MaxRestarts > 0 && _restartTimestamps.Count > _app.MaxRestarts)
        {
            _logger.LogError(
                "[{App}] Superato il limite di {Max} riavvii in {Window}s. Sorveglianza interrotta (circuit breaker).",
                _app.Name, _app.MaxRestarts, _app.RestartWindowSeconds);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Arresta in modo controllato il processo sorvegliato durante lo shutdown del watchdog.
    /// </summary>
    private async Task ShutdownProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            _logger.LogInformation(
                "[{App}] Richiesta di arresto controllato del processo (PID {Pid}).",
                _app.Name, process.Id);

            // Tenta prima una chiusura pulita.
            try
            {
                process.CloseMainWindow();
            }
            catch
            {
                // Nessuna finestra principale (processo console/servizio): si procede oltre.
            }

            var timeout = TimeSpan.FromSeconds(_app.GracefulShutdownTimeoutSeconds);
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                _logger.LogInformation(
                    "[{App}] Processo terminato in modo controllato.", _app.Name);
                return;
            }
            catch (OperationCanceledException)
            {
                // Timeout scaduto: si forza la terminazione.
            }

            if (!process.HasExited)
            {
                _logger.LogWarning(
                    "[{App}] Terminazione controllata non riuscita entro {Timeout}s: chiusura forzata.",
                    _app.Name, _app.GracefulShutdownTimeoutSeconds);
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "[{App}] Errore durante l'arresto controllato del processo.", _app.Name);
        }
    }

    /// <summary>
    /// Controlla periodicamente lo stato di integrità del processo: risorse (reattività,
    /// memoria, CPU) e sonde di liveness attive (file heartbeat, endpoint HTTP).
    /// Restituisce true se, per il numero di controlli consecutivi configurato, il processo
    /// risulta non integro e va quindi forzato il riavvio. Termina restituendo false quando
    /// il monitor viene annullato o il processo esce autonomamente.
    /// </summary>
    private async Task<bool> MonitorHealthAsync(Process process, CancellationToken token)
    {
        bool resourceChecks = _app.EnableNotRespondingCheck || _app.MaxMemoryMB > 0 || _app.MaxCpuPercent > 0;
        bool heartbeatCheck = !string.IsNullOrWhiteSpace(_app.HeartbeatFilePath) && _app.HeartbeatTimeoutSeconds > 0;
        bool httpCheck = !string.IsNullOrWhiteSpace(_app.HealthCheckUrl);

        // Nessun controllo configurato: resta in attesa passiva fino all'annullamento.
        if (_app.HealthCheckIntervalSeconds <= 0 || (!resourceChecks && !heartbeatCheck && !httpCheck))
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // Annullamento atteso.
            }

            return false;
        }

        var interval = TimeSpan.FromSeconds(_app.HealthCheckIntervalSeconds);
        int consecutiveUnhealthy = 0;

        // Istante di avvio del monitor: base per il primo aggiornamento del file heartbeat.
        DateTimeOffset monitorStart = DateTimeOffset.UtcNow;

        // Riferimenti per il calcolo incrementale dell'utilizzo CPU.
        DateTimeOffset lastSample = DateTimeOffset.UtcNow;
        TimeSpan lastCpu = TimeSpan.Zero;
        bool cpuInitialized = false;
        try
        {
            lastCpu = process.TotalProcessorTime;
            cpuInitialized = true;
        }
        catch
        {
            // Metrica CPU non disponibile per questo processo.
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (SafeHasExited(process))
            {
                return false;
            }

            var reasons = new List<string>();

            // --- Sonda: file heartbeat (indipendente dalle metriche di processo) ---
            if (heartbeatCheck)
            {
                CheckHeartbeatFile(monitorStart, reasons);
            }

            // --- Sonda: endpoint HTTP (indipendente dalle metriche di processo) ---
            if (httpCheck)
            {
                await CheckHttpEndpointAsync(reasons, token);
            }

            // --- Metriche di processo (richiedono Refresh) ---
            if (resourceChecks && TryRefresh(process))
            {
                // Stato "non risponde" (solo Windows, app con finestra).
                if (_app.EnableNotRespondingCheck && OperatingSystem.IsWindows())
                {
                    try
                    {
                        if (!process.Responding)
                        {
                            reasons.Add("non risponde (NotResponding)");
                        }
                    }
                    catch
                    {
                        // Proprietà non applicabile a questo processo: ignorata.
                    }
                }

                // Memoria (working set).
                if (_app.MaxMemoryMB > 0)
                {
                    try
                    {
                        long memMB = process.WorkingSet64 / (1024 * 1024);
                        if (memMB > _app.MaxMemoryMB)
                        {
                            reasons.Add($"memoria {memMB} MB > {_app.MaxMemoryMB} MB");
                        }
                    }
                    catch
                    {
                        // Metrica non disponibile: ignorata.
                    }
                }

                // CPU (percentuale normalizzata sul totale dei core).
                if (_app.MaxCpuPercent > 0 && cpuInitialized)
                {
                    try
                    {
                        var now = DateTimeOffset.UtcNow;
                        var currentCpu = process.TotalProcessorTime;
                        double cpuDeltaMs = (currentCpu - lastCpu).TotalMilliseconds;
                        double wallDeltaMs = (now - lastSample).TotalMilliseconds;
                        lastCpu = currentCpu;
                        lastSample = now;

                        if (wallDeltaMs > 0)
                        {
                            int cores = Math.Max(1, Environment.ProcessorCount);
                            double cpuPercent = cpuDeltaMs / (wallDeltaMs * cores) * 100.0;
                            if (cpuPercent > _app.MaxCpuPercent)
                            {
                                reasons.Add($"CPU {cpuPercent:F0}% > {_app.MaxCpuPercent}%");
                            }
                        }
                    }
                    catch
                    {
                        // Metrica non disponibile: ignorata.
                    }
                }
            }

            if (reasons.Count > 0)
            {
                consecutiveUnhealthy++;
                _logger.LogWarning(
                    "[{App}] Controllo di integrità fallito ({Count}/{Threshold}): {Reasons}.",
                    _app.Name, consecutiveUnhealthy, _app.UnhealthyChecksBeforeRestart,
                    string.Join("; ", reasons));

                if (consecutiveUnhealthy >= _app.UnhealthyChecksBeforeRestart)
                {
                    return true;
                }
            }
            else
            {
                consecutiveUnhealthy = 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifica l'età dell'ultima modifica del file heartbeat. Se il file non esiste ancora,
    /// l'età è calcolata dall'avvio del monitor, così l'applicativo dispone del timeout per
    /// crearlo la prima volta. Aggiunge un motivo di fallimento se la soglia è superata.
    /// </summary>
    private void CheckHeartbeatFile(DateTimeOffset monitorStart, List<string> reasons)
    {
        try
        {
            var path = _app.HeartbeatFilePath!;
            var timeout = TimeSpan.FromSeconds(_app.HeartbeatTimeoutSeconds);
            DateTimeOffset lastActivity = File.Exists(path)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                : monitorStart;

            var age = DateTimeOffset.UtcNow - lastActivity;
            if (age > timeout)
            {
                reasons.Add(
                    $"heartbeat non aggiornato da {age.TotalSeconds:F0}s (> {_app.HeartbeatTimeoutSeconds}s)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "[{App}] Impossibile leggere il file heartbeat '{Path}'.",
                _app.Name, _app.HeartbeatFilePath);
            reasons.Add("file heartbeat non leggibile");
        }
    }

    /// <summary>
    /// Interroga in GET l'endpoint di health-check HTTP. Un esito diverso da 2xx (o un errore
    /// di rete / timeout) aggiunge un motivo di fallimento.
    /// </summary>
    private async Task CheckHttpEndpointAsync(List<string> reasons, CancellationToken token)
    {
        var url = _app.HealthCheckUrl!;
        var httpTimeout = TimeSpan.FromSeconds(_app.HealthCheckHttpTimeoutSeconds);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(httpTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await HttpProbeClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                reasons.Add($"health-check HTTP: codice {(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Arresto del watchdog in corso: non è un fallimento dell'applicativo.
        }
        catch (OperationCanceledException)
        {
            reasons.Add($"health-check HTTP: timeout dopo {_app.HealthCheckHttpTimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            reasons.Add($"health-check HTTP non raggiungibile ({ex.GetType().Name})");
        }
    }

    private static bool TryRefresh(Process process)
    {
        try
        {
            process.Refresh();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Osserva il task del monitor dopo l'annullamento, assorbendone l'eventuale eccezione.
    /// </summary>
    private static async Task ObserveHealthTaskAsync(Task healthTask)
    {
        try
        {
            await healthTask;
        }
        catch (OperationCanceledException)
        {
            // Annullamento atteso.
        }
        catch
        {
            // Qualsiasi altra eccezione del monitor è già stata gestita internamente.
        }
    }

    /// <summary>
    /// Forza la terminazione del processo (e del suo albero) ignorando eventuali errori.
    /// </summary>
    private void ForceTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "[{App}] Impossibile forzare la terminazione del processo.", _app.Name);
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static int SafeGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Attesa che assorbe l'eccezione di annullamento, per non propagarla come errore.
    /// </summary>
    private static async Task DelaySafeAsync(TimeSpan delay, CancellationToken token)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
            // Arresto richiesto durante l'attesa: uscita silenziosa.
        }
    }
}
