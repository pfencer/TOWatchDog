using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TOWatchDog.Configuration;
using TOWatchDog.Monitoring;

namespace TOWatchDog;

/// <summary>
/// Servizio in background che avvia un <see cref="ApplicationSupervisor"/> per ciascun
/// applicativo abilitato e ne coordina l'esecuzione fino all'arresto del watchdog.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly WatchdogOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IOptions<WatchdogOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<Worker> logger)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabledApps = _options.Applications
            .Where(a => a.Enabled)
            .ToList();

        if (enabledApps.Count == 0)
        {
            _logger.LogWarning(
                "Nessun applicativo abilitato in configurazione: il watchdog resterà in attesa senza attività.");
            return;
        }

        _logger.LogInformation(
            "TOWatchDog avviato. Applicativi sorvegliati: {Count}.", enabledApps.Count);

        var supervisorLogger = _loggerFactory.CreateLogger<ApplicationSupervisor>();

        var tasks = enabledApps
            .Select(app => new ApplicationSupervisor(app, supervisorLogger).RunAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(tasks);

        _logger.LogInformation("TOWatchDog: tutti i supervisori sono terminati.");
    }
}
