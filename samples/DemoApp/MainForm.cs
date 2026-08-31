using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TOWatchDog.DemoApp;

/// <summary>
/// Applicazione di esempio per verificare i meccanismi di monitoraggio di TOWatchDog.
/// Consente di provocare a comando: crash, uscita con codice errore, blocco della UI
/// (NotResponding), interruzione dell'aggiornamento del file heartbeat, consumo di
/// memoria e consumo di CPU.
/// </summary>
public sealed class MainForm : Form
{
    // Heartbeat
    private readonly TextBox _heartbeatPath;
    private readonly NumericUpDown _heartbeatInterval;
    private readonly CheckBox _heartbeatEnabled;
    private readonly Label _heartbeatStatus;
    private readonly System.Windows.Forms.Timer _heartbeatTimer;

    // Risorse
    private readonly List<byte[]> _memoryBlocks = new();
    private readonly Label _memoryStatus;
    private CancellationTokenSource? _cpuCts;
    private readonly CheckBox _cpuLoad;

    // Log
    private readonly TextBox _log;

    public MainForm()
    {
        Text = "TOWatchDog - Applicazione di test";
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(580, 700);
        StartPosition = FormStartPosition.CenterScreen;
        FormClosing += OnFormClosing;

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10)
        };
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "Avvia questa applicazione tramite TOWatchDog, poi usa i pulsanti "
                 + "per simulare i diversi guasti e osservare il riavvio.",
            AutoSize = false,
            Size = new Size(540, 40)
        });

        // ---------------- Heartbeat ----------------
        var gbHeartbeat = new GroupBox { Text = "File heartbeat", Size = new Size(540, 150) };

        gbHeartbeat.Controls.Add(new Label { Text = "Percorso file:", Location = new Point(12, 28), AutoSize = true });
        _heartbeatPath = new TextBox
        {
            Location = new Point(110, 25),
            Width = 410,
            Text = Path.Combine(AppContext.BaseDirectory, "demo-heartbeat.txt")
        };
        gbHeartbeat.Controls.Add(_heartbeatPath);

        gbHeartbeat.Controls.Add(new Label { Text = "Intervallo (s):", Location = new Point(12, 62), AutoSize = true });
        _heartbeatInterval = new NumericUpDown
        {
            Location = new Point(110, 60),
            Width = 60,
            Minimum = 1,
            Maximum = 3600,
            Value = 5
        };
        _heartbeatInterval.ValueChanged += (_, _) =>
            _heartbeatTimer!.Interval = (int)_heartbeatInterval.Value * 1000;
        gbHeartbeat.Controls.Add(_heartbeatInterval);

        _heartbeatEnabled = new CheckBox
        {
            Text = "Aggiorna heartbeat",
            Location = new Point(200, 61),
            AutoSize = true,
            Checked = true
        };
        _heartbeatEnabled.CheckedChanged += (_, _) =>
            Log(_heartbeatEnabled.Checked
                ? "Aggiornamento heartbeat ATTIVATO."
                : "Aggiornamento heartbeat INTERROTTO (simula hang rilevabile da file).");
        gbHeartbeat.Controls.Add(_heartbeatEnabled);

        _heartbeatStatus = new Label { Location = new Point(12, 95), AutoSize = true, Text = "Heartbeat non ancora scritto." };
        gbHeartbeat.Controls.Add(_heartbeatStatus);

        gbHeartbeat.Controls.Add(new Label
        {
            Text = "Suggerimento: deseleziona la casella per fermare l'aggiornamento senza bloccare la UI.",
            Location = new Point(12, 120),
            AutoSize = true,
            ForeColor = Color.DimGray
        });

        root.Controls.Add(gbHeartbeat);

        // ---------------- Terminazione ----------------
        var gbExit = new GroupBox { Text = "Terminazione", Size = new Size(540, 70) };
        gbExit.Controls.Add(MakeButton("Crash (eccezione)", 12, 25, 165, CrashUnhandled));
        gbExit.Controls.Add(MakeButton("Esci con codice 1", 187, 25, 165, () => ExitWithCode(1)));
        gbExit.Controls.Add(MakeButton("Esci normalmente (0)", 362, 25, 165, () => ExitWithCode(0)));
        root.Controls.Add(gbExit);

        // ---------------- Blocco UI / Hang ----------------
        var gbHang = new GroupBox { Text = "Blocco UI (NotResponding)", Size = new Size(540, 80) };
        gbHang.Controls.Add(new Label { Text = "Durata (s):", Location = new Point(12, 30), AutoSize = true });
        var hangSeconds = new NumericUpDown
        {
            Location = new Point(85, 27),
            Width = 60,
            Minimum = 1,
            Maximum = 3600,
            Value = 30
        };
        gbHang.Controls.Add(hangSeconds);
        gbHang.Controls.Add(MakeButton("Blocca UI temporaneamente", 160, 25, 190, () => HangFor((int)hangSeconds.Value)));
        gbHang.Controls.Add(MakeButton("Blocco permanente (deadlock)", 360, 25, 165, HangForever));
        root.Controls.Add(gbHang);

        // ---------------- Risorse ----------------
        var gbRes = new GroupBox { Text = "Risorse (memoria e CPU)", Size = new Size(540, 110) };
        gbRes.Controls.Add(MakeButton("Alloca +100 MB", 12, 25, 165, AllocateMemory));
        gbRes.Controls.Add(MakeButton("Rilascia memoria", 187, 25, 165, FreeMemory));
        _memoryStatus = new Label { Location = new Point(362, 30), AutoSize = true, Text = "Allocati: 0 MB" };
        gbRes.Controls.Add(_memoryStatus);

        _cpuLoad = new CheckBox { Text = "Carico CPU al massimo (tutti i core)", Location = new Point(12, 70), AutoSize = true };
        _cpuLoad.CheckedChanged += (_, _) => ToggleCpuLoad(_cpuLoad.Checked);
        gbRes.Controls.Add(_cpuLoad);
        root.Controls.Add(gbRes);

        // ---------------- Log ----------------
        var gbLog = new GroupBox { Text = "Log", Size = new Size(540, 180) };
        _log = new TextBox
        {
            Location = new Point(12, 22),
            Size = new Size(515, 145),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical
        };
        gbLog.Controls.Add(_log);
        root.Controls.Add(gbLog);

        // Timer heartbeat
        _heartbeatTimer = new System.Windows.Forms.Timer { Interval = (int)_heartbeatInterval.Value * 1000 };
        _heartbeatTimer.Tick += (_, _) => WriteHeartbeat();
        _heartbeatTimer.Start();

        Log($"Applicazione avviata (PID {Environment.ProcessId}).");
        WriteHeartbeat();
    }

    private Button MakeButton(string text, int x, int y, int width, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 30)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------------- Heartbeat ----------------

    private void WriteHeartbeat()
    {
        if (!_heartbeatEnabled.Checked)
        {
            return;
        }

        try
        {
            File.WriteAllText(_heartbeatPath.Text, DateTime.Now.ToString("o"));
            _heartbeatStatus.Text = $"Ultimo aggiornamento: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _heartbeatStatus.Text = "Errore scrittura heartbeat: " + ex.Message;
        }
    }

    // ---------------- Terminazione ----------------

    private void CrashUnhandled()
    {
        Log("Provoco un crash con eccezione non gestita su thread in background...");
        // Un'eccezione non gestita su un thread non-UI termina immediatamente il processo
        // con codice diverso da zero: simula un crash reale.
        var thread = new Thread(() => throw new InvalidOperationException("Crash simulato dalla demo"))
        {
            IsBackground = false
        };
        thread.Start();
    }

    private void ExitWithCode(int code)
    {
        Log($"Uscita del processo con codice {code}.");
        Environment.Exit(code);
    }

    // ---------------- Hang ----------------

    private void HangFor(int seconds)
    {
        Log($"Blocco il thread della UI per {seconds}s (l'app risulterà 'Non risponde').");
        // Blocca il message pump: la UI diventa NotResponding e, poiché il timer heartbeat
        // gira sullo stesso thread, anche l'aggiornamento del file si ferma.
        Thread.Sleep(seconds * 1000);
        Log("UI sbloccata.");
        WriteHeartbeat();
    }

    private void HangForever()
    {
        Log("Blocco permanente del thread della UI (deadlock simulato).");
        while (true)
        {
            Thread.Sleep(1000);
        }
    }

    // ---------------- Risorse ----------------

    private void AllocateMemory()
    {
        try
        {
            const int blockSize = 100 * 1024 * 1024; // 100 MB
            var block = new byte[blockSize];
            // Tocca le pagine per forzarne l'effettiva allocazione (commit).
            for (int i = 0; i < block.Length; i += 4096)
            {
                block[i] = 1;
            }

            _memoryBlocks.Add(block);
            _memoryStatus.Text = $"Allocati: {_memoryBlocks.Count * 100} MB";
            Log($"Allocati altri 100 MB (totale ~{_memoryBlocks.Count * 100} MB).");
        }
        catch (OutOfMemoryException)
        {
            Log("Memoria esaurita: impossibile allocare altro.");
        }
    }

    private void FreeMemory()
    {
        _memoryBlocks.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _memoryStatus.Text = "Allocati: 0 MB";
        Log("Memoria rilasciata.");
    }

    private void ToggleCpuLoad(bool enabled)
    {
        if (enabled)
        {
            _cpuCts = new CancellationTokenSource();
            var token = _cpuCts.Token;
            int threads = Environment.ProcessorCount;
            for (int i = 0; i < threads; i++)
            {
                var worker = new Thread(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        // Ciclo di calcolo per saturare il core.
                    }
                })
                {
                    IsBackground = true
                };
                worker.Start();
            }

            Log($"Carico CPU avviato su {threads} thread.");
        }
        else
        {
            _cpuCts?.Cancel();
            _cpuCts = null;
            Log("Carico CPU fermato.");
        }
    }

    // ---------------- Utility ----------------

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (_log.InvokeRequired)
        {
            _log.BeginInvoke(() => _log.AppendText(line));
        }
        else
        {
            _log.AppendText(line);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _cpuCts?.Cancel();
        _heartbeatTimer.Stop();
    }
}
