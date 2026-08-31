# DemoApp — applicazione di test per TOWatchDog

Applicazione **WinForms (.NET 8, solo Windows)** che permette di provocare a comando i guasti
gestiti da TOWatchDog, per verificarne dal vivo i meccanismi di rilevamento e riavvio.

> Richiede Windows: WinForms non è disponibile su Linux/macOS. Il watchdog invece è multipiattaforma.

## Comandi disponibili

| Sezione | Comando | Cosa verifica nel watchdog |
|---------|---------|----------------------------|
| **File heartbeat** | Casella *Aggiorna heartbeat* (deselezionare) | Rilevamento hang via file (`HeartbeatFilePath` + `HeartbeatTimeoutSeconds`): il file smette di aggiornarsi ma il processo resta vivo. |
| **Terminazione** | *Crash (eccezione)* | Riavvio dopo crash (eccezione non gestita, codice ≠ 0). |
| **Terminazione** | *Esci con codice 1* | Politica `OnFailure` / `Always`. |
| **Terminazione** | *Esci normalmente (0)* | Differenza tra `Always` (riavvia) e `OnFailure` (non riavvia). |
| **Blocco UI** | *Blocca UI temporaneamente* | Stato `NotResponding` (`EnableNotRespondingCheck`) e, di riflesso, stallo del heartbeat. |
| **Blocco UI** | *Blocco permanente (deadlock)* | Hang definitivo: NotResponding + heartbeat fermo. |
| **Risorse** | *Alloca +100 MB* | Soglia memoria (`MaxMemoryMB`). |
| **Risorse** | *Carico CPU al massimo* | Soglia CPU (`MaxCpuPercent`). |

Il riquadro **Log** riporta le azioni eseguite e il PID del processo (utile per seguire i riavvii).

## Come provare

1. Compila entrambi i progetti: `dotnet build` dalla radice della solution.
2. Configura il watchdog perché avvii la DemoApp (esempio sotto), impostando lo **stesso**
   percorso del file heartbeat mostrato nella maschera.
3. Avvia il watchdog: `dotnet run --project src/TOWatchDog`.
4. Premi i pulsanti della DemoApp e osserva i log del watchdog che rileva il guasto e riavvia.

### Esempio di configurazione del watchdog (`appsettings.json`)

```jsonc
{
  "Watchdog": {
    "Applications": [
      {
        "Name": "DemoApp",
        "ExecutablePath": "C:\\percorso\\samples\\DemoApp\\bin\\Debug\\net8.0-windows\\TOWatchDog.DemoApp.exe",
        "Enabled": true,
        "RestartPolicy": "OnFailure",
        "RestartDelaySeconds": 3,
        "MaxRestarts": 10,
        "RestartWindowSeconds": 120,

        "HealthCheckIntervalSeconds": 5,
        "UnhealthyChecksBeforeRestart": 3,

        "EnableNotRespondingCheck": true,
        "MaxMemoryMB": 500,
        "MaxCpuPercent": 85,

        "HeartbeatFilePath": "C:\\percorso\\samples\\DemoApp\\bin\\Debug\\net8.0-windows\\demo-heartbeat.txt",
        "HeartbeatTimeoutSeconds": 20
      }
    ]
  }
}
```

> Nota: usa `RestartPolicy: "Always"` se vuoi che anche l'uscita normale (codice 0) venga riavviata.
> Con `OnFailure`, il pulsante *Esci normalmente (0)* termina la sorveglianza senza riavvio.
