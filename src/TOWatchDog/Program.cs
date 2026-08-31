using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TOWatchDog;
using TOWatchDog.Configuration;

var builder = Host.CreateApplicationBuilder(args);

// Consente l'esecuzione come servizio Windows o daemon systemd (Linux),
// oltre che come normale applicazione console.
builder.Services.AddWindowsService(options => options.ServiceName = "TOWatchDog");
builder.Services.AddSystemd();

// Vincola la sezione "Watchdog" del file di configurazione alle opzioni fortemente tipizzate,
// con validazione degli attributi (DataAnnotations) all'avvio.
builder.Services
    .AddOptions<WatchdogOptions>()
    .Bind(builder.Configuration.GetSection(WatchdogOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => options.Applications.All(a => !string.IsNullOrWhiteSpace(a.ExecutablePath)),
        "Ogni applicativo monitorato deve specificare un ExecutablePath valido.")
    .ValidateOnStart();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
