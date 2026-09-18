using AltHenkan;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Diagnostics;

var failures = new List<string>();
var testsRun = 0;
EmacsTests.Run((name, passed) => Check(name, true, passed));

Check(
    "Short press sends conversion",
    AltPressAction.Conversion,
    AltPressDecision.Decide(599, longPressEnabled: true, longPressMilliseconds: 600));
Check(
    "Threshold duration sends native Alt",
    AltPressAction.NativeAlt,
    AltPressDecision.Decide(600, longPressEnabled: true, longPressMilliseconds: 600));
Check(
    "Long press sends native Alt",
    AltPressAction.NativeAlt,
    AltPressDecision.Decide(1200, longPressEnabled: true, longPressMilliseconds: 600));
Check(
    "Disabled long press still sends conversion",
    AltPressAction.Conversion,
    AltPressDecision.Decide(1200, longPressEnabled: false, longPressMilliseconds: 600));

var normalizedLow = new AppSettings { LongPressMilliseconds = 1 }.Normalize();
Check(
    "Threshold is clamped to minimum",
    AppSettings.MinimumLongPressMilliseconds,
    normalizedLow.LongPressMilliseconds);

var normalizedHigh = new AppSettings { LongPressMilliseconds = 9999 }.Normalize();
Check(
    "Threshold is clamped to maximum",
    AppSettings.MaximumLongPressMilliseconds,
    normalizedHigh.LongPressMilliseconds);

Check(
    "Right Alt sends VK_IME_ON",
    (ushort)0x16,
    JapaneseImeKey.ImeOnVirtualKey);
Check(
    "Left Alt sends VK_IME_OFF",
    (ushort)0x1A,
    JapaneseImeKey.ImeOffVirtualKey);
Check(
    "Native INPUT has the Windows ABI size",
    IntPtr.Size == 8 ? 40 : 28,
    Marshal.SizeOf<NativeMethods.Input>());

Check("Idle refresh becomes due", true,
    HookMaintenancePolicy.ShouldRefresh(30_000, 1000, false, false, false));
Check("Refresh does not run during input", false,
    HookMaintenancePolicy.ShouldRefresh(30_000, 999, false, false, false));
Check("Refresh does not run with a held key or mouse button", false,
    HookMaintenancePolicy.ShouldRefresh(30_000, 5000, true, false, false));
Check("Refresh does not interrupt an Alt gesture", false,
    HookMaintenancePolicy.ShouldRefresh(30_000, 5000, false, true, true));
Check("Explicit recovery runs before the periodic interval", true,
    HookMaintenancePolicy.ShouldRefresh(10, 1000, false, false, true));
Check("Explicit recovery still waits for idle", false,
    HookMaintenancePolicy.ShouldRefresh(10, 0, false, false, true));
Check("Healthy hooks are not refreshed too early", false,
    HookMaintenancePolicy.ShouldRefresh(29_999, 1000, false, false, false));
Check("Idle calculation handles tick-count wrap", (uint)1000,
    HookMaintenancePolicy.CalculateIdleMilliseconds(500, uint.MaxValue - 499));
Check("Future last-input time is treated conservatively", (uint)0,
    HookMaintenancePolicy.CalculateIdleMilliseconds(500, 600));
Check("LASTINPUTINFO has the Windows ABI size", 8,
    Marshal.SizeOf<NativeMethods.LastInputInfo>());

var callerThread = Environment.CurrentManagedThreadId;
var initializedOnThread = 0;
var cleanedUpOnThread = 0;
var messageLoop = new DedicatedMessageLoop(
    () => initializedOnThread = Environment.CurrentManagedThreadId,
    () => cleanedUpOnThread = Environment.CurrentManagedThreadId);
try
{
    Check("Hook message loop is not on the caller/UI thread", true,
        messageLoop.ManagedThreadId != callerThread);
    Check("Initialization happens on the message-loop thread", messageLoop.ManagedThreadId,
        initializedOnThread);
    var commandThread = 0;
    messageLoop.InvokeAsync(() => commandThread = Environment.CurrentManagedThreadId)
        .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    Check("Commands run without pumping caller/UI messages", messageLoop.ManagedThreadId,
        commandThread);

    var failureObserved = false;
    try
    {
        messageLoop.InvokeAsync(() => throw new IOException("Expected test failure"))
            .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
    catch (IOException)
    {
        failureObserved = true;
    }
    Check("Command errors are observable", true, failureObserved);
    messageLoop.InvokeAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    Check("Message loop survives a command error", true, messageLoop.IsAlive);
}
finally
{
    messageLoop.Dispose();
}
Check("Message-loop shutdown joins its thread", false, messageLoop.IsAlive);
Check("Cleanup occurs on the owning thread", initializedOnThread, cleanedUpOnThread);
var stoppedCommandRejected = false;
try
{
    messageLoop.InvokeAsync(() => { }).GetAwaiter().GetResult();
}
catch (ObjectDisposedException)
{
    stoppedCommandRejected = true;
}
Check("Stopped message loops reject further commands", true, stoppedCommandRejected);

var startupFailureObserved = false;
var failedStartupCleanedUp = false;
try
{
    using var failedLoop = new DedicatedMessageLoop(
        () => throw new IOException("Expected startup failure"),
        () => failedStartupCleanedUp = true);
}
catch (IOException)
{
    startupFailureObserved = true;
}
Check("Message-loop startup failure is propagated", true, startupFailureObserved);
Check("Failed message-loop startup cleans up", true, failedStartupCleanedUp);

using (var writerStarted = new ManualResetEventSlim())
using (var releaseWriter = new ManualResetEventSlim())
{
    var output = new StringWriter();
    using var queue = new QueuedDiagnosticWriter(() =>
    {
        writerStarted.Set();
        if (!releaseWriter.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Test writer was not released.");
        }
        return output;
    }, capacity: 2);
    Check("Diagnostic worker starts", true, writerStarted.Wait(TimeSpan.FromSeconds(5)));
    try
    {
        var started = Stopwatch.GetTimestamp();
        Check("First diagnostic entry is queued", true, queue.TryWrite("first"));
        Check("Second diagnostic entry is queued", true, queue.TryWrite("second"));
        Check("Full diagnostic queue drops rather than blocks", false, queue.TryWrite("third"));
        Check("Blocked disk does not stall logging producers", true,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds < 500);
        Check("Dropped diagnostic entries are counted", 1, queue.DroppedEntries);
    }
    finally
    {
        releaseWriter.Set();
    }
    queue.Complete();
    queue.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    Check("Diagnostic worker drains entries in order", true,
        output.ToString().IndexOf("first", StringComparison.Ordinal) <
        output.ToString().IndexOf("second", StringComparison.Ordinal));
    Check("Diagnostic worker reports omitted entries", true,
        output.ToString().Contains("dropped 1 entries", StringComparison.Ordinal));
}

using (var failedWriter = new QueuedDiagnosticWriter(
    () => throw new IOException("Expected disk failure")))
{
    failedWriter.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    Check("Diagnostic disk errors are isolated", true, failedWriter.LastError is IOException);
    Check("Failed diagnostic sink stops accepting entries", false, failedWriter.TryWrite("ignored"));
}

// Opt-in native smoke test. Hooks remain disabled (pass-through) and no input is injected.
if (args.Contains("--native-hooks", StringComparer.Ordinal))
{
    using var service = new AltInputService(new AppSettings { Enabled = false });
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var serviceType = typeof(AltInputService);
    var hookLoop = (DedicatedMessageLoop)serviceType.GetField("_hookThread", flags)!.GetValue(service)!;
    Check("Native hooks are installed away from the caller/UI thread", true,
        hookLoop.ManagedThreadId != callerThread);
    var renewed = false;
    hookLoop.InvokeAsync(() =>
    {
        var generation = (int)serviceType.GetField("_hookGeneration", flags)!.GetValue(service)!;
        serviceType.GetMethod("UnregisterHooks", flags)!.Invoke(service, null);
        serviceType.GetMethod("RegisterHooks", flags)!.Invoke(service, null);
        renewed = (int)serviceType.GetField("_hookGeneration", flags)!.GetValue(service)! == generation + 1 &&
                  (nint)serviceType.GetField("_keyboardHook", flags)!.GetValue(service)! != 0 &&
                  (nint)serviceType.GetField("_mouseHook", flags)!.GetValue(service)! != 0;
    }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    Check("Native keyboard and mouse hooks can recover after removal", true, renewed);

    var generationBeforeMaintenance = 0;
    hookLoop.InvokeAsync(() =>
    {
        generationBeforeMaintenance = (int)serviceType.GetField("_hookGeneration", flags)!.GetValue(service)!;
        serviceType.GetMethod("UnregisterHooks", flags)!.Invoke(service, null);
        serviceType.GetField("_lastRefreshAtMilliseconds", flags)!.SetValue(service,
            Environment.TickCount64 - HookMaintenancePolicy.RefreshIntervalMilliseconds);
    }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    var restoredByTimer = false;
    uint maximumObservedIdle = 0;
    uint lastObservedIdle = 0;
    var heldKeysAtIdle = new HashSet<int>();
    var maintenanceDeadline = Environment.TickCount64 + 15_000;
    while (!restoredByTimer && Environment.TickCount64 < maintenanceDeadline)
    {
        Thread.Sleep(100);
        hookLoop.InvokeAsync(() =>
        {
            var info = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
            if (NativeMethods.GetLastInputInfo(ref info))
            {
                lastObservedIdle = HookMaintenancePolicy.CalculateIdleMilliseconds(unchecked((uint)Environment.TickCount), info.Time);
                maximumObservedIdle = Math.Max(maximumObservedIdle, lastObservedIdle);
                if (lastObservedIdle >= HookMaintenancePolicy.MinimumIdleMilliseconds)
                {
                    for (var key = 1; key < 255; key++)
                    {
                        if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0) heldKeysAtIdle.Add(key);
                    }
                }
            }
            restoredByTimer = (int)serviceType.GetField("_hookGeneration", flags)!.GetValue(service)! >
                generationBeforeMaintenance;
        }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
    if (!restoredByTimer)
    {
        Console.Error.WriteLine($"Native maintenance timeout: maximum OS idle={maximumObservedIdle} ms, last OS idle={lastObservedIdle} ms, held virtual keys observed at idle=[{string.Join(", ", heldKeysAtIdle.Order().Select(key => $"0x{key:X2}"))}].");
    }
    Check("Maintenance timer automatically reinstalls removed hooks at idle", true, restoredByTimer);
    service.Dispose();
    Check("Native service shutdown joins the hook thread", false, hookLoop.IsAlive);
    Check("Native shutdown clears keyboard hook", (nint)0,
        (nint)serviceType.GetField("_keyboardHook", flags)!.GetValue(service)!);
    Check("Native shutdown clears mouse hook", (nint)0,
        (nint)serviceType.GetField("_mouseHook", flags)!.GetValue(service)!);
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine($"All {testsRun} tests passed.");
return 0;

void Check<T>(string name, T expected, T actual)
    where T : notnull
{
    testsRun++;
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        failures.Add($"{name}: expected {expected}, actual {actual}");
    }
}
