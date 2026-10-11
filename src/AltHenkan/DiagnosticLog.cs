namespace AltHenkan;

internal static class DiagnosticLog
{
    private static QueuedDiagnosticWriter? _writer;
    public static bool VerboseEnabled { get; private set; }
    public static int DroppedEntries => Volatile.Read(ref _writer)?.DroppedEntries ?? 0;

    // %LOCALAPPDATA%\AltHenkan: the installed executable lives under Program Files,
    // which is not writable by a normal user.
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AltHenkan",
        "AltHenkan-diagnostics.log");

    public static void Initialize(bool verbose = false, string? path = null)
    {
        Shutdown();
        VerboseEnabled = verbose;
        var session = Guid.NewGuid();
        _writer = new QueuedDiagnosticWriter(() => new RotatingDiagnosticWriter(path ?? Path));
        Write($"Session started: id={session:N}, version={typeof(DiagnosticLog).Assembly.GetName().Version}, " +
            $"pid={Environment.ProcessId}, verbose={verbose}, OS={Environment.OSVersion.Version}, " +
            $"64bit={Environment.Is64BitProcess}.");
        InputTrace.Initialize((path ?? Path) + ".input", session);
    }

    public static void Write(string message)
    {
        Volatile.Read(ref _writer)?.TryWrite(message);
    }

    public static void WriteVerbose(string message)
    {
        if (VerboseEnabled) Write(message);
    }

    public static void Shutdown()
    {
        InputTrace.Shutdown();
        var writer = Interlocked.Exchange(ref _writer, null);
        if (writer is null) return;
        writer.TryWrite("Session ended.");
        writer.Dispose();
    }
}
