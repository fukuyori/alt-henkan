using AltHenkan;
using System.Text;

internal static class DiagnosticTests
{
    public static void Run(Action<string, bool> check)
    {
        // A test-owned directory only; never touch the user's actual diagnostics.
        var directory = Path.Combine(Path.GetTempPath(), "AltHenkan-DiagnosticTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "sessions.log");
            for (var session = 0; session < 3; session++)
            {
                DiagnosticLog.Initialize(verbose: session == 1, path: path);
                DiagnosticLog.Write($"session marker {session}");
                DiagnosticLog.WriteVerbose($"detail marker {session}");
                DiagnosticLog.Shutdown();
            }
            var log = File.ReadAllText(path);
            check("Normal starts retain all prior sessions", Enumerable.Range(0, 3)
                .All(i => log.Contains($"session marker {i}", StringComparison.Ordinal)));
            check("Normal starts include session metadata and shutdown boundaries",
                log.Split("Session started:").Length == 4 && log.Split("Session ended.").Length == 4 &&
                log.Contains("version=", StringComparison.Ordinal));
            check("Detailed per-operation logging is opt-in",
                log.Contains("detail marker 1", StringComparison.Ordinal) &&
                !log.Contains("detail marker 0", StringComparison.Ordinal) &&
                !log.Contains("detail marker 2", StringComparison.Ordinal));

            var rotatedPath = Path.Combine(directory, "rotate.log");
            var records = Enumerable.Range(0, 10).Select(i => $"record-{i}:" + new string('あ', 40)).ToArray();
            // Each record fits alone, but two exceed 256 bytes. Reopen for every write as a restart control.
            foreach (var record in records)
            {
                using var writer = new RotatingDiagnosticWriter(rotatedPath, 256, 2);
                writer.WriteLineAsync(record).GetAwaiter().GetResult();
            }
            check("Rolling logs retain current plus configured archives",
                Directory.GetFiles(directory, "rotate.log*").Length == 3);
            check("Oldest archives expire while newest records remain ordered",
                File.ReadAllText(rotatedPath).TrimEnd() == records[9] &&
                File.ReadAllText(rotatedPath + ".1").TrimEnd() == records[8] &&
                File.ReadAllText(rotatedPath + ".2").TrimEnd() == records[7]);
            check("UTF-8 byte limits hold across restarts and rotations",
                Directory.GetFiles(directory, "rotate.log*").All(p => new FileInfo(p).Length <= 256));

            using (var writer = new RotatingDiagnosticWriter(rotatedPath, 256, 2))
                writer.WriteLineAsync(string.Concat(Enumerable.Repeat("あ😀", 500))).GetAwaiter().GetResult();
            check("Oversized Unicode records are truncated within the file budget",
                new FileInfo(rotatedPath).Length <= 256 && File.ReadAllText(rotatedPath).Contains("[truncated]"));
            var strictUtf8 = new UTF8Encoding(false, true);
            check("Truncated Unicode output is valid UTF-8", strictUtf8.GetString(File.ReadAllBytes(rotatedPath)).Length > 0);

            var fullPath = Path.Combine(directory, "full.log");
            File.WriteAllText(fullPath, new string('x', 256), new UTF8Encoding(false));
            using (var writer = new RotatingDiagnosticWriter(fullPath, 256, 2))
                writer.WriteLineAsync("new session").GetAwaiter().GetResult();
            check("A full log is archived intact on startup",
                File.ReadAllText(fullPath + ".1") == new string('x', 256) &&
                File.ReadAllText(fullPath).Contains("new session"));

            var failPath = Path.Combine(directory, "failure.log");
            File.WriteAllText(failPath, new string('x', 200));
            Directory.CreateDirectory(failPath + ".1"); // Prevent archive creation during rotation.
            using var queue = new QueuedDiagnosticWriter(() => new RotatingDiagnosticWriter(failPath, 256, 2));
            queue.TryWrite(new string('y', 100));
            queue.Complete();
            queue.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            check("Rotation failures are isolated from the input producer",
                queue.LastError is IOException or UnauthorizedAccessException && !queue.TryWrite("after failure"));
            check("Failed rotation preserves the previous log", File.ReadAllText(failPath) == new string('x', 200));
        }
        finally
        {
            DiagnosticLog.Shutdown();
            Directory.Delete(directory, recursive: true);
        }
    }
}
