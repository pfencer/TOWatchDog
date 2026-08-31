# TOWatchDog

Watchdog scritto in **C# / .NET 8** per il monitoraggio dello stato di esecuzione di uno o più
applicativi. In caso di **crash** o **interruzione** dell'esecuzione, il watchdog provvede al
**riavvio automatico** dell'applicativo secondo politiche configurabili.

Può girare come normale applicazione console, come **servizio Windows** o come **daemon systemd** su Linux.

## Caratteristiche

- Monitoraggio di più applicativi in parallelo, ciascuno gestito da un supervisore dedicato.
- Rilevamento della terminazione del processo (crash o uscita) e riavvio automatico.
- Politiche di riavvio configurabili:
  - `Always` — riavvia sempre;
  - `OnFailure` — riavvia solo con codice di uscita diverso da zero;
  - `Never` — non riavviare.
- **Backoff** configurabile tra un riavvio e il successivo (`RestartDelaySeconds`).
- **Circuit breaker**: limite massimo di riavvii (`MaxRestarts`) entro una finestra temporale
  scorrevole (`RestartWindowSeconds`), per evitare cicli di riavvio senza fine.
- Ritardo di avvio iniziale (`StartupDelaySeconds`) per scaglionare gli avvii o attendere dipendenze.
- Possibilità di **agganciarsi** a un processo già in esecuzione (`AttachToExisting`).
- Arresto controllato (graceful shutdown) dei processi sorvegliati allo stop del watchdog.
- Variabili d'ambiente aggiuntive per processo.
- Logging strutturato tramite `Microsoft.Extensions.Logging`.

## Struttura del progetto

```
TOWatchDog.sln
src/TOWatchDog/
├── Program.cs                         # Bootstrap dell'host generico
├── Worker.cs                          # BackgroundService che orchestra i supervisori
├── appsettings.json                  # Configurazione (elenco applicativi + logging)
├── Configuration/
│   ├── WatchdogOptions.cs            # Opzioni radice (sezione "Watchdog")
│   ├── MonitoredApplication.cs       # Descrizione di un applicativo monitorato
│   └── RestartPolicy.cs             # Enum politica di riavvio
└── Monitoring/
    └── ApplicationSupervisor.cs      # Ciclo avvio → attesa → riavvio per un applicativo
```

## Prerequisiti

- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

## Compilazione ed esecuzione

```bash
# Ripristino dipendenze e build
dotnet build

# Esecuzione come applicazione console
dotnet run --project src/TOWatchDog
```

## Configurazione

Gli applicativi da sorvegliare si definiscono nella sezione `Watchdog:Applications` di
`appsettings.json`. Ogni voce accetta i seguenti campi:

| Campo | Tipo | Default | Descrizione |
|-------|------|---------|-------------|
| `Name` | string | — | Nome logico usato nei log (obbligatorio). |
| `ExecutablePath` | string | — | Percorso dell'eseguibile (obbligatorio). |
| `Arguments` | string | `""` | Argomenti passati all'avvio. |
| `WorkingDirectory` | string | cartella dell'eseguibile | Directory di lavoro. |
| `Enabled` | bool | `true` | Abilita/disabilita il monitoraggio della voce. |
| `RestartPolicy` | string | `Always` | `Always`, `OnFailure` o `Never`. |
| `RestartDelaySeconds` | int | `5` | Ritardo prima del riavvio. |
| `MaxRestarts` | int | `5` | Riavvii massimi nella finestra (`0` = illimitati). |
| `RestartWindowSeconds` | int | `60` | Ampiezza finestra scorrevole per il conteggio. |
| `StartupDelaySeconds` | int | `0` | Ritardo prima del primo avvio. |
| `AttachToExisting` | bool | `false` | Aggancia un processo omonimo già in esecuzione. |
| `GracefulShutdownTimeoutSeconds` | int | `10` | Attesa per la chiusura pulita allo stop. |
| `Environment` | object | `{}` | Variabili d'ambiente aggiuntive. |

### Esempio

```json
{
  "Watchdog": {
    "Applications": [
      {
        "Name": "MioServizio",
        "ExecutablePath": "/usr/local/bin/mio-servizio",
        "Arguments": "--config /etc/mio-servizio/config.yaml",
        "Enabled": true,
        "RestartPolicy": "OnFailure",
        "RestartDelaySeconds": 10,
        "MaxRestarts": 0,
        "RestartWindowSeconds": 300
      }
    ]
  }
}
```

> Nota: nel file `appsettings.json` incluso i due esempi hanno `Enabled: false`.
> Impostare `Enabled: true` e valorizzare `ExecutablePath` con l'eseguibile reale.

## Pubblicazione

```bash
# Windows (self-contained, singolo file)
dotnet publish src/TOWatchDog -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Linux
dotnet publish src/TOWatchDog -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

## Installazione come servizio Windows

```powershell
# Da PowerShell con privilegi di amministratore, dopo la publish
New-Service -Name "TOWatchDog" -BinaryPathName "C:\percorso\TOWatchDog.exe" -DisplayName "TOWatchDog" -StartupType Automatic
Start-Service -Name "TOWatchDog"
```

## Installazione come daemon systemd (Linux)

Creare `/etc/systemd/system/towatchdog.service`:

```ini
[Unit]
Description=TOWatchDog
After=network.target

[Service]
Type=notify
ExecStart=/opt/towatchdog/TOWatchDog
WorkingDirectory=/opt/towatchdog
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Poi:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now towatchdog
```
