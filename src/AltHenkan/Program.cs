using System.Threading;

namespace AltHenkan;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\AltHenkan.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Alt Henkan は既に起動しています。",
                "Alt Henkan",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        DiagnosticLog.Initialize(args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase));
        ApplicationConfiguration.Initialize();
        DiagnosticLog.Write("Windows Forms initialized.");

        try
        {
            using var context = new TrayApplicationContext();
            DiagnosticLog.Write("Tray application context initialized.");
            Application.Run(context);
            DiagnosticLog.Write("Application message loop ended.");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write($"Fatal error: {exception}");
            MessageBox.Show(
                $"Alt Henkan を起動できませんでした。\n\n{exception.Message}",
                "Alt Henkan",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            DiagnosticLog.Shutdown();
        }
    }
}
