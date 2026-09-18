namespace AltHenkan;

// The owning thread alone creates, uses and destroys the dispatcher and hooks.
internal sealed class DedicatedMessageLoop : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _initialize;
    private readonly Action _cleanup;
    private readonly object _dispatchGate = new();
    private readonly HashSet<TaskCompletionSource> _pending = [];
    private Control? _dispatcher;
    private int _stopping;

    public DedicatedMessageLoop(Action initialize, Action cleanup)
    {
        _initialize = initialize;
        _cleanup = cleanup;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Alt Henkan keyboard hooks"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        try
        {
            _ready.Task.GetAwaiter().GetResult();
        }
        catch
        {
            _thread.Join();
            throw;
        }
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    public bool IsAlive => _thread.IsAlive;

    public Task InvokeAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_dispatchGate)
        {
            if (_stopping != 0)
            {
                completion.SetException(new ObjectDisposedException(nameof(DedicatedMessageLoop)));
                return completion.Task;
            }

            _pending.Add(completion);
            try
            {
                _dispatcher!.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        action();
                        completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                    finally
                    {
                        lock (_dispatchGate)
                        {
                            _pending.Remove(completion);
                        }
                    }
                }));
            }
            catch (InvalidOperationException exception)
            {
                _pending.Remove(completion);
                completion.TrySetException(exception);
            }
        }

        return completion.Task;
    }

    private void Run()
    {
        try
        {
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            _dispatcher = dispatcher;
            _initialize();
            _ready.SetResult();
            Application.Run();
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            DiagnosticLog.Write($"Hook message loop failed: {exception}");
        }
        finally
        {
            lock (_dispatchGate)
            {
                _stopping = 1;
                foreach (var completion in _pending)
                {
                    completion.TrySetException(new ObjectDisposedException(nameof(DedicatedMessageLoop)));
                }
                _pending.Clear();
            }
            try
            {
                _cleanup();
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write($"Hook cleanup failed: {exception}");
            }
        }
    }

    public void Dispose()
    {
        lock (_dispatchGate)
        {
            var needsExit = _stopping == 0;
            _stopping = 1;

            if (needsExit && _thread.IsAlive)
            {
                try
                {
                    _dispatcher!.BeginInvoke(new Action(Application.ExitThread));
                }
                catch (InvalidOperationException)
                {
                    // The message loop has already exited.
                }
            }
        }

        if (_thread.IsAlive && Environment.CurrentManagedThreadId != ManagedThreadId)
        {
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                DiagnosticLog.Write("Hook message loop did not stop within 5 seconds.");
            }
        }
    }
}
