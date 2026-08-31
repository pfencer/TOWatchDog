using System.ComponentModel.DataAnnotations;

namespace TOWatchDog.Configuration;

/// <summary>
/// Descrive un singolo applicativo da monitorare e le relative regole di riavvio.
/// </summary>
public sealed class MonitoredApplication
{
    /// <summary>
    /// Nome logico dell'applicativo, usato nei log per identificarlo.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Percorso completo dell'eseguibile da avviare e sorvegliare.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Argomenti da passare all'eseguibile all'avvio.
    /// </summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// Directory di lavoro del processo. Se vuota viene usata quella dell'eseguibile.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Indica se questo applicativo è abilitato al monitoraggio.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Politica di riavvio applicata alla terminazione del processo.
    /// </summary>
    public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.Always;

    /// <summary>
    /// Ritardo (in secondi) atteso prima di riavviare l'applicativo dopo una terminazione.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int RestartDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Numero massimo di riavvii consentiti all'interno della finestra
    /// <see cref="RestartWindowSeconds"/>. Il valore 0 indica riavvii illimitati.
    /// Superata la soglia, il supervisore interrompe i tentativi (circuit breaker).
    /// </summary>
    [Range(0, int.MaxValue)]
    public int MaxRestarts { get; set; } = 5;

    /// <summary>
    /// Ampiezza (in secondi) della finestra scorrevole usata per contare i riavvii
    /// ai fini del limite <see cref="MaxRestarts"/>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RestartWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Ritardo iniziale (in secondi) prima del primo avvio dell'applicativo.
    /// Utile per scaglionare gli avvii o attendere dipendenze esterne.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int StartupDelaySeconds { get; set; }

    /// <summary>
    /// Se true, all'avvio il supervisore cerca un processo già in esecuzione con lo
    /// stesso nome eseguibile e vi si "aggancia" invece di avviarne uno nuovo.
    /// </summary>
    public bool AttachToExisting { get; set; }

    /// <summary>
    /// Tempo massimo (in secondi) concesso al processo per terminare in modo pulito
    /// quando il watchdog viene arrestato, prima di forzarne la chiusura.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int GracefulShutdownTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Variabili d'ambiente aggiuntive da impostare per il processo avviato.
    /// </summary>
    public IDictionary<string, string?> Environment { get; set; }
        = new Dictionary<string, string?>();

    // ----- Monitoraggio risorse e stato "non risponde" -----

    /// <summary>
    /// Intervallo (in secondi) tra un controllo di integrità e il successivo.
    /// Il valore 0 disabilita completamente il monitoraggio di risorse e reattività.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int HealthCheckIntervalSeconds { get; set; }

    /// <summary>
    /// Se true, verifica che l'applicativo non sia in stato "non risponde".
    /// Significativo solo su Windows per applicazioni con finestra (message pump):
    /// su altre piattaforme o per processi console il controllo viene ignorato.
    /// </summary>
    public bool EnableNotRespondingCheck { get; set; }

    /// <summary>
    /// Soglia massima di memoria di lavoro (working set, in MB) tollerata.
    /// Il valore 0 disabilita il controllo sulla memoria.
    /// </summary>
    [Range(0, long.MaxValue)]
    public long MaxMemoryMB { get; set; }

    /// <summary>
    /// Soglia massima di utilizzo CPU (in percentuale, normalizzata sul totale dei core:
    /// 0-100) tollerata. Il valore 0 disabilita il controllo sulla CPU.
    /// </summary>
    [Range(0, 100)]
    public int MaxCpuPercent { get; set; }

    /// <summary>
    /// Numero di controlli di integrità falliti consecutivi tollerati prima di forzare
    /// il riavvio dell'applicativo. Evita riavvii dovuti a picchi momentanei.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int UnhealthyChecksBeforeRestart { get; set; } = 3;

    // ----- Sonde di liveness attive -----

    /// <summary>
    /// Percorso di un file "heartbeat" che l'applicativo deve aggiornare periodicamente
    /// (riscrivendone il contenuto o aggiornandone il timestamp) finché è vivo e reattivo.
    /// Se l'ultima modifica del file diventa più vecchia di <see cref="HeartbeatTimeoutSeconds"/>,
    /// il processo è considerato bloccato. Lasciare vuoto per disabilitare il controllo.
    /// </summary>
    public string? HeartbeatFilePath { get; set; }

    /// <summary>
    /// Età massima (in secondi) consentita per l'ultima modifica del file heartbeat.
    /// Il valore 0 disabilita il controllo. Dopo l'avvio, l'applicativo dispone di questo
    /// stesso intervallo per creare per la prima volta il file.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int HeartbeatTimeoutSeconds { get; set; }

    /// <summary>
    /// URL di un endpoint di health-check HTTP interrogato in GET: una risposta con codice
    /// 2xx è considerata sana, qualsiasi altro esito (errore, timeout, codice non 2xx) è
    /// considerato un fallimento. Lasciare vuoto per disabilitare il controllo.
    /// </summary>
    public string? HealthCheckUrl { get; set; }

    /// <summary>
    /// Timeout (in secondi) della richiesta HTTP di health-check.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int HealthCheckHttpTimeoutSeconds { get; set; } = 5;
}
