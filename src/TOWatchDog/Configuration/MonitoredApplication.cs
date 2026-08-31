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
}
