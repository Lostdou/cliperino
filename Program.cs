using System.Runtime.InteropServices;
using Cliperino.Capture;
using Cliperino.UI;
using Cliperino.Update;

namespace Cliperino;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(-1);
            return PipelineBuilder.RunDiagnosticsAsync(Console.Out).GetAwaiter().GetResult();
        }

        Updater.FinishPendingUpdate(args);
        using var mutex = new Mutex(true, @"Local\Cliperino.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Cliperino ya está abierto. Buscá el ícono en la bandeja del sistema.", "Cliperino",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
