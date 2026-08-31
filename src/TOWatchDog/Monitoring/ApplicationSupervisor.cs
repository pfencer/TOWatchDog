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

                await process.WaitForExitAsync(stoppingToken);

                // Uscita naturale del processo (non causata dall'arresto del watchdog).
                int exitCode = SafeGetExitCode(process);
                _logger.LogWarning(
                    "[{App}] Terminato con codice di uscita {ExitCode}.",
                    _app.Name, exitCode);

                if (!ShouldRestart(exitCode))
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
