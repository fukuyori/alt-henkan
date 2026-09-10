using System.Text;

namespace AltHenkan;

internal static class DiagnosticLog
{
    private static readonly object SyncRoot = new();
    private static bool _enabled;

    // %LOCALAPPDATA%\AltHenkan: the installed executable lives under Program Files,
    // which is not writable by a normal user.
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AltHenkan",
        "AltHenkan-diagnostics.log");

    public static void Initialize(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            return;
        }

        lock (SyncRoot)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(
                Path,
                $"{DateTimeOffset.Now:O} Diagnostic logging started.{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public static void Write(string message)
    {
        if (!_enabled)
        {
            return;
        }

        lock (SyncRoot)
        {
            File.AppendAllText(
                Path,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}

