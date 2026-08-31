using System;
using System.Windows.Forms;

namespace TOWatchDog.DemoApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Inizializzazione standard delle applicazioni WinForms (.NET 8).
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
