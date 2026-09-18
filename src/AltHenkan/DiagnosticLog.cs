using System.Text;

namespace AltHenkan;

internal static class DiagnosticLog
{
    private static QueuedDiagnosticWriter? _writer;

    // %LOCALAPPDATA%\AltHenkan: the installed executable lives under Program Files,
    // which is not writable by a normal user.
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AltHenkan",
        "AltHenkan-diagnostics.log");

    public static void Initialize(bool enabled)
    {
        Shutdown();
        if (!enabled)
        {
            return;
        }

        _writer = new QueuedDiagnosticWriter(() =>
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            return new StreamWriter(Path, append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        });
        Write("Diagnostic logging started.");
    }

    public static void Write(string message)
    {
        Volatile.Read(ref _writer)?.TryWrite(message);
    }

    public static void Shutdown() => Interlocked.Exchange(ref _writer, null)?.Dispose();
}
