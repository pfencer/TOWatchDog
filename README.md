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
- **Health-check periodico** con riavvio forzato al superamento delle soglie:
  - stato **NotResponding** (app appesa/bloccata) — solo Windows, app con finestra;
  - **memoria** (working set) oltre soglia;
  - **CPU** oltre soglia (percentuale normalizzata sul totale dei core);
  - tolleranza configurabile di N controlli falliti consecutivi prima del riavvio.
- **Sonde di liveness attive** per rilevare app "vive ma bloccate" su qualsiasi piattaforma:
  - **file heartbeat**: l'app aggiorna periodicamente un file; se smette, scatta il riavvio;
  - **health-check HTTP**: GET su un endpoint, esito non 2xx/timeout considerato guasto.
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

samples/DemoApp/                      # App WinForms di test (solo Windows)
└── ...                               # Provoca crash, hang, consumo risorse a comando
```

## Applicazione di test

In `samples/DemoApp` è presente un'applicazione WinForms (solo Windows) per verificare dal vivo
tutti i meccanismi di monitoraggio (crash, uscita con codice errore, hang/NotResponding, stop del
file heartbeat, consumo di memoria e CPU). Vedi [samples/DemoApp/README.md](samples/DemoApp/README.md).

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
| `HealthCheckIntervalSeconds` | int | `0` | Intervallo dei controlli di integrità (`0` = disabilitati). |
| `EnableNotRespondingCheck` | bool | `false` | Rileva lo stato NotResponding (solo Windows, app con finestra). |
| `MaxMemoryMB` | long | `0` | Soglia memoria working set in MB (`0` = disabilitato). |
| `MaxCpuPercent` | int | `0` | Soglia CPU % sul totale dei core, 0-100 (`0` = disabilitato). |
| `UnhealthyChecksBeforeRestart` | int | `3` | Controlli falliti consecutivi tollerati prima del riavvio forzato. |
| `HeartbeatFilePath` | string | `null` | File heartbeat aggiornato dall'app (vuoto = disabilitato). |
| `HeartbeatTimeoutSeconds` | int | `0` | Età massima dell'ultima modifica del file heartbeat (`0` = disabilitato). |
| `HealthCheckUrl` | string | `null` | Endpoint HTTP interrogato in GET (vuoto = disabilitato). |
| `HealthCheckHttpTimeoutSeconds` | int | `5` | Timeout della richiesta HTTP di health-check. |

### Monitoraggio risorse e stato "non risponde"

Se `HealthCheckIntervalSeconds > 0`, un monitor parallelo campiona il processo a quell'intervallo
e ne verifica reattività, memoria e CPU. Quando una o più metriche restano oltre soglia per
`UnhealthyChecksBeforeRestart` controlli **consecutivi**, il watchdog **forza la terminazione**
del processo (`Kill` dell'intero albero) e ne innesca il riavvio secondo le normali regole.

> **Nota su NotResponding:** `Process.Responding` è significativo solo su **Windows** per
> applicazioni dotate di finestra e message pump (GUI). Per processi console o su Linux/macOS il
> controllo non è applicabile e viene ignorato; memoria e CPU restano invece disponibili su tutte
> le piattaforme.
>
> La percentuale CPU è **normalizzata sul totale dei core** (0-100): 100% significa saturazione di
> tutti i core della macchina.

### Sonde di liveness attive (file heartbeat / HTTP)

Le sonde di liveness rilevano l'app **bloccata ma ancora in esecuzione** (deadlock, loop infinito,
event loop fermo) su **qualsiasi piattaforma**, colmando i limiti di `NotResponding`. Anch'esse
usano `HealthCheckIntervalSeconds` e `UnhealthyChecksBeforeRestart`.

**File heartbeat** — richiede la cooperazione dell'applicativo, che deve **aggiornare
periodicamente** il file indicato (riscriverlo o toccarne il timestamp) da un punto del codice che
si ferma quando l'app si blocca (tipicamente un timer o il ciclo di lavoro principale). Il watchdog
verifica solo l'età dell'ultima modifica: se supera `HeartbeatTimeoutSeconds`, forza il riavvio.
Dopo l'avvio, l'app ha a disposizione lo stesso timeout per creare il file la prima volta.

Esempio minimale lato applicativo (C#):

```csharp
// Da eseguire in un timer/loop finché l'app è viva e reattiva
File.WriteAllText("/var/run/mio-servizio/heartbeat", DateTime.UtcNow.ToString("o"));
```

> Regola pratica: imposta `HeartbeatTimeoutSeconds` a un valore ampiamente superiore alla
> frequenza con cui l'app aggiorna il file (es. l'app scrive ogni 10s → timeout 60s), per non
> generare falsi positivi.

**Health-check HTTP** — non richiede file: il watchdog interroga in GET l'`HealthCheckUrl` (es. un
endpoint `/health` esposto dall'app). Una risposta **2xx** è considerata sana; un codice diverso,
un errore di rete o un timeout (`HealthCheckHttpTimeoutSeconds`) contano come fallimento.

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
