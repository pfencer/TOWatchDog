namespace TOWatchDog.Configuration;

/// <summary>
/// Opzioni di configurazione del watchdog, popolate dalla sezione "Watchdog"
/// del file appsettings.json.
/// </summary>
public sealed class WatchdogOptions
{
    /// <summary>
    /// Nome della sezione di configurazione associata a queste opzioni.
    /// </summary>
    public const string SectionName = "Watchdog";

    /// <summary>
    /// Elenco degli applicativi da monitorare.
    /// </summary>
    public IList<MonitoredApplication> Applications { get; set; }
        = new List<MonitoredApplication>();
}
