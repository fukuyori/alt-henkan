using System.Threading.Channels;

namespace AltHenkan;

internal sealed class QueuedDiagnosticWriter : IDisposable
{
    private readonly Channel<(DateTimeOffset Time, string Message)> _entries;
    private readonly Task _worker;
    private int _droppedEntries;

    public QueuedDiagnosticWriter(Func<TextWriter> createWriter, int capacity = 2048)
    {
        _entries = Channel.CreateBounded<(DateTimeOffset, string)>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // TryWrite returns false when full; producers never wait for disk I/O.
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(async () =>
        {
            try
            {
                using var writer = createWriter();
                await foreach (var entry in _entries.Reader.ReadAllAsync())
                {
                    await writer.WriteLineAsync($"{entry.Time:O} {entry.Message}");
                }

                if (DroppedEntries > 0)
                {
                    await writer.WriteLineAsync($"Diagnostic queue dropped {DroppedEntries} entries.");
                }
                await writer.FlushAsync();
            }
            catch (Exception exception)
            {
                LastError = exception;
                _entries.Writer.TryComplete();
                // Logging must never terminate the application or a hook callback.
            }
        });
    }

    public Task Completion => _worker;

    public Exception? LastError { get; private set; }

    public int DroppedEntries => Volatile.Read(ref _droppedEntries);

    public bool TryWrite(string message)
    {
        if (_entries.Writer.TryWrite((DateTimeOffset.Now, message)))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedEntries);
        return false;
    }

    public void Complete() => _entries.Writer.TryComplete();

    public void Dispose()
    {
        Complete();
        // Only application shutdown waits for flushing, never a hook callback.
        _worker.Wait(TimeSpan.FromSeconds(2));
    }
}
