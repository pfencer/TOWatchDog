namespace TOWatchDog.Configuration;

/// <summary>
/// Politica di riavvio applicata quando un applicativo monitorato termina.
/// </summary>
public enum RestartPolicy
{
    /// <summary>
    /// Riavvia sempre l'applicativo, indipendentemente dal codice di uscita.
    /// </summary>
    Always,

    /// <summary>
    /// Riavvia solo se l'applicativo termina con un codice di uscita diverso da zero
    /// (ovvero in caso di crash o errore).
    /// </summary>
    OnFailure,

    /// <summary>
    /// Non riavviare mai automaticamente l'applicativo.
    /// </summary>
    Never
}
