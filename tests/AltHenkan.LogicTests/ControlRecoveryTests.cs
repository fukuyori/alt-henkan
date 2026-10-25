using AltHenkan;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class ControlRecoveryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly uint[] Controls = [NativeMethods.VkLControl, NativeMethods.VkRControl];

    public static void Run(Action<string, bool> check)
    {
        ReplayIncident(check);
        InjectedControlRightAltSequence(check);
        foreach (var key in Controls)
        foreach (var passed in new[] { false, true })
        {
            ExcludeStaleSide(key, passed, check);
            Repress(key, passed, check);
            Recover(key, passed, check);
            PreserveHeld(key, passed, check);
            ReentrantConsume(key, passed, check);
        }
        foreach (var key in Controls) PreserveHidden(key, check);
        SamplingAndCost(check);
        RecoveryLog(check);
    }

    private static void ReplayIncident(Action<string, bool> check)
    {
        using var f = new Fixture();
        var key = NativeMethods.VkLControl;
        f.Key(key, false);
        f.Key(NativeMethods.VkLMenu, false);
        // Fault injection models missing hook ups and a released OS state;
        // it does not establish why Windows failed to deliver the real ups.
        f.Platform.SetDown(key, false);
        f.Platform.SetDown(NativeMethods.VkLMenu, false);
        var alt = typeof(AltInputService).GetField("_leftAlt", Private)!.GetValue(f.Service)!;
        var recoverAlt = typeof(AltInputService).GetMethod("ReconcileReleasedAlt", Private)!;
        recoverAlt.Invoke(f.Service, [alt, true, (nint)1, false, 1000L]);
        recoverAlt.Invoke(f.Service, [alt, true, (nint)1, false, 2000L]);
        f.Platform.Sent.Clear();
        check("Incident: ordinary F passes before Ctrl timer recovery", f.Key((uint)Keys.F, false) == 0);
        f.Key((uint)Keys.F, true);
        f.Tap((uint)Keys.X);
        f.Mouse();
        check("Incident: keyboard and mouse do not reinject stale Ctrl", f.Platform.Sent.Count == 0 && f.Platform.AllUp);
        check("Incident: callback exclusion does not prematurely reset Ctrl", f.Flag(key, "IsDown") && f.Flag(key, "NativeDownSent"));
        f.Tick();
        f.Tick(1000);
        check("Incident: guarded timer clears Ctrl without synthesizing input", !f.Flag(key, "IsDown") && f.Platform.Sent.Count == 0);
        check("Incident: late Ctrl up passes without a tap", f.Key(key, true) == 0 && f.Platform.Sent.Count == 0);
    }

    private static void InjectedControlRightAltSequence(Action<string, bool> check)
    {
        // Non-own injected Ctrl is managed by this service, despite LLKHF_INJECTED.
        // This exercises that path with right Alt, not real AltGr layout semantics.
        using var f = new Fixture();
        for (var i = 0; i < 3; i++)
        {
            f.Platform.Deliver(NativeMethods.VkLControl, false, flags: 0x10);
            f.Key(NativeMethods.VkRMenu, false);
            check("Managed injected Ctrl plus right Alt has no Ctrl+Alt+F binding", f.Key((uint)Keys.F, false) == 0);
            f.Key((uint)Keys.F, true);
            f.Key(NativeMethods.VkRMenu, true);
            f.Platform.Deliver(NativeMethods.VkLControl, true, flags: 0x10);
        }
        check("Repeated managed injected Ctrl/right Alt sequences leave no Ctrl/Alt state",
            !f.Flag(Controls[0], "IsDown") && !f.AltDown(false) && f.Platform.AllUp);
        f.Platform.Sent.Clear();
        check("Plain F after managed injected Ctrl/Alt sequence passes", f.Key((uint)Keys.F, false) == 0);
        f.Key((uint)Keys.F, true);
        check("Plain input has no modifier reinjection", f.Platform.Sent.Count == 0);
    }

    private static void ExcludeStaleSide(uint key, bool passed, Action<string, bool> check)
    {
        foreach (var external in new[] { false, true })
        {
            using var f = new Fixture();
            f.Visible(key, passed, released: true);
            var other = key == Controls[0] ? Controls[1] : Controls[0];
            if (external) f.Platform.SetDown(other, true); // Native Ctrl not owned by this service.
            else f.Key(other, false);
            f.Platform.Sent.Clear();
            check($"{key:X}/{passed}/{external}: live opposite Ctrl still invokes Emacs", f.Key((uint)Keys.F, false) == 1);
            f.Key((uint)Keys.F, true);
            check("Excluded side never becomes hidden or consumed", !f.Flag(key, "HiddenFromForeground") && !f.Flag(key, "ConsumedByEmacs"));
            check("Opposite Ctrl emits the expected action", f.Platform.Sent.Contains(((uint)Keys.Right, false)));
            f.Tap((uint)Keys.X);
            f.Mouse();
            check("Neither Consume nor Promote sends the excluded Ctrl", f.Platform.Sent.All(e => e.Key != key));
        }
        using var generic = new Fixture();
        generic.Visible(key, passed, released: true);
        generic.Platform.SetDown(NativeMethods.VkControl, true);
        check("Generic Ctrl does not revive a stale side", generic.Key((uint)Keys.F, false) == 0);
        generic.Key((uint)Keys.F, true);
        generic.Tap((uint)Keys.X);
        check("Generic Ctrl cannot hide or reinject stale Ctrl", !generic.Flag(key, "HiddenFromForeground") && generic.Platform.Sent.Count == 0);
        generic.Platform.SetDown(NativeMethods.VkControl, false);
        generic.Tap(NativeMethods.VkRMenu);
        check("Stale Ctrl does not prevent a normal Alt IME tap",
            generic.Platform.Sent.Contains((JapaneseImeKey.ImeOnVirtualKey, false)) &&
            generic.Platform.Sent.All(e => e.Key != key));
    }

    private static void Repress(uint key, bool passed, Action<string, bool> check)
    {
        using var f = new Fixture();
        f.Visible(key, passed, released: true);
        f.Tick(); // A pending release confirmation must be invalidated by down.
        check($"{key:X}/{passed}: pressing Ctrl again passes to Windows", f.Key(key, false) == 0);
        check("Repress transfers ownership to original input", f.Flag(key, "OriginalDownPassed") && !f.Flag(key, "NativeDownSent"));
        f.Tap((uint)Keys.C);
        check("Ctrl+C passes without synthesis", f.Platform.Down(key) && f.Platform.Sent.Count == 0);
        for (var i = 0; i < 3; i++) check("Native Ctrl repeats pass", f.Key(key, false) == 0);
        f.Platform.SetDown(key, false); // Another missing release on the new gesture.
        f.Tick(1000);
        check("Repress starts a fresh confirmation interval", f.Flag(key, "IsDown"));
        f.Tick(1000);
        check("New gesture can recover after its own confirmation", !f.Flag(key, "IsDown"));
        f.Key(key, true);
        check("Recovery followed by delayed up never creates Ctrl tap", f.Platform.Sent.Count == 0);
        f.Key(key, false);
        check("Fresh press after recovery is deferred normally", f.Flag(key, "HiddenFromForeground"));
        f.Key(key, true);
        check("Fresh Ctrl tap after recovery remains paired", f.Platform.Sent.SequenceEqual(new[] { (key, false), (key, true) }) && f.Platform.AllUp);
    }

    private static void Recover(uint key, bool passed, Action<string, bool> check)
    {
        using var f = new Fixture();
        f.Visible(key, passed, released: true);
        f.Platform.Reliable = false;
        f.Tick(); f.Tick(1000);
        check("Unavailable state never clears Ctrl", f.Flag(key, "IsDown"));
        f.Platform.Reliable = true;
        f.Tick(1000); f.Tick(999);
        check("Subsecond confirmation keeps Ctrl bookkeeping", f.Flag(key, "IsDown"));
        f.Platform.ForegroundWindow = 2;
        f.Tick(1);
        check("Foreground change restarts confirmation", f.Flag(key, "IsDown"));
        f.Tick(3000);
        check("Long observation gap restarts confirmation", f.Flag(key, "IsDown"));
        f.Platform.ForegroundWindow = 0;
        f.Tick(1000);
        f.Platform.ForegroundWindow = 2;
        f.Tick(1000);
        check("Missing foreground discards prior confirmation", f.Flag(key, "IsDown"));
        // Use the real SendInput wrapper, with a fake empty send, to verify that
        // reentrant maintenance discards confirmation while input is in flight.
        f.Platform.OnSend = () => f.Tick(1000);
        typeof(AltInputService).GetMethod("SendKeyboardInputs", Private)!.Invoke(f.Service, [Array.Empty<NativeMethods.Input>()]);
        f.Tick(1000);
        check("In-flight maintenance requires fresh Ctrl confirmation", f.Flag(key, "IsDown"));
        f.Tick(1000);
        check($"{key:X}/{passed}: guarded release clears bookkeeping only", !f.Flag(key, "IsDown") && f.Platform.Sent.Count == 0);
        check("Recovery requests an idle hook renewal", (bool)typeof(AltInputService).GetField("_refreshRequested", Private)!.GetValue(f.Service)!);
    }

    private static void PreserveHeld(uint key, bool passed, Action<string, bool> check)
    {
        using var f = new Fixture();
        f.Visible(key, passed, released: false);
        for (var i = 0; i < 120; i++) f.Tick(1000);
        check("120 second visible Ctrl hold is preserved", f.Flag(key, "IsDown") && f.Platform.Down(key));
        check("Visible hold needs no guarded desktop/token queries", f.Platform.GuardedReads == 0);
        f.Key((uint)Keys.F, false); f.Key((uint)Keys.F, true);
        check("Held visible Ctrl still invokes Emacs", f.Platform.Sent.Contains(((uint)Keys.Right, false)));
        f.Tap((uint)Keys.S);
        check("Held Ctrl is promoted back for native Ctrl+S", f.Platform.Down(key));
        f.Key(key, true);
        check("Held Ctrl releases normally after mixed input", !f.Flag(key, "IsDown") && f.Platform.AllUp);
    }

    private static void ReentrantConsume(uint key, bool passed, Action<string, bool> check)
    {
        using var f = new Fixture();
        f.Visible(key, passed, released: false);
        // Fault injection: a new non-own down arrives after the synthetic up
        // has updated Windows state, but before Send returns to Consume.
        f.Platform.AfterSend = () => f.Key(key, false);
        f.Key((uint)Keys.F, false);
        f.Key((uint)Keys.F, true);
        check($"{key:X}/{passed}: Consume preserves a reentrant native Ctrl press",
            f.Flag(key, "OriginalDownPassed") && !f.Flag(key, "HiddenFromForeground"));
        check("Reentrant Ctrl is released and restored around navigation",
            f.Platform.Sent.SequenceEqual(new[] { (key, true), (key, true),
                ((uint)Keys.Right, false), ((uint)Keys.Right, true), (key, false) }));
        check("Reentrant Ctrl up reaches Windows and leaves no stuck Ctrl",
            f.Key(key, true) == 0 && f.Platform.AllUp && !f.Flag(key, "IsDown"));

        using var released = new Fixture();
        released.Visible(key, passed, released: false);
        released.Platform.AfterSend = () => released.Key(key, true);
        released.Tap((uint)Keys.F);
        check("Consume does not overwrite a gesture ended by a reentrant up",
            !released.Flag(key, "IsDown") && !released.Flag(key, "ConsumedByEmacs") &&
            !released.Flag(key, "OriginalDownPassed") && !released.Flag(key, "NativeDownSent") &&
            released.Platform.AllUp);
    }

    private static void PreserveHidden(uint key, Action<string, bool> check)
    {
        using var f = new Fixture();
        f.Key(key, false);
        for (var i = 0; i < 120; i++) f.Tick(1000);
        check("120 second hidden Ctrl hold remains active", f.Flag(key, "HiddenFromForeground"));
        check("Hidden hold adds no guarded sampling", f.Platform.GuardedReads == 0);
        f.Key((uint)Keys.F, false); f.Key((uint)Keys.F, true);
        check("Hidden Ctrl still invokes Emacs after long hold", f.Platform.Sent.Contains(((uint)Keys.Right, false)));
        for (var i = 0; i < 120; i++) f.Tick(1000);
        check("Consumed hidden Ctrl is not recovered", f.Flag(key, "IsDown") && f.Flag(key, "ConsumedByEmacs"));
        f.Tap((uint)Keys.S);
        check("Legitimate consumed Ctrl can still be promoted", f.Platform.Down(key));
        f.Key(key, true);
        check("Hidden Ctrl gesture eventually releases", f.Platform.AllUp);
    }

    private static void SamplingAndCost(Action<string, bool> check)
    {
        using var f = new Fixture();
        var tick = typeof(AltInputService).GetMethod("ReconcileReleasedModifiers", Private)!.CreateDelegate<Action>(f.Service);
        for (var i = 0; i < 10000; i++) tick();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 100000; i++) tick();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check("Idle modifier maintenance adds no allocation or state queries",
            bytes == 0 && f.Platform.StateReads == 0 && f.Platform.GuardedReads == 0);
        Console.WriteLine($"Idle modifier maintenance: 100000 calls, {ms:F2} ms, {bytes} bytes, no OS state queries (local probe).");
        f.Key(Controls[0], false); f.Key(NativeMethods.VkLMenu, false);
        f.Platform.SetDown(Controls[0], false); f.Platform.SetDown(NativeMethods.VkLMenu, false);
        // Recovery is independent of the later idle/all-keys-up gate used for
        // hook renewal (a held IME virtual key can keep that gate closed).
        f.Platform.SetDown(0x19, true);
        f.Tick();
        check("Alt and Ctrl share one guarded read per timer tick", f.Platform.GuardedReads == 1);
        f.Tick(1000);
        check("Shared guarded sample can recover both Ctrl and Alt", !f.Flag(Controls[0], "IsDown") &&
            !f.AltDown(true));
        check("Ctrl recovery does not release unrelated held input", f.Platform.Down(0x19));
        f.Platform.SetDown(0x19, false);
    }

    private static void RecoveryLog(Action<string, bool> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AltHenkan-CtrlRecovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "diagnostics.log");
        try
        {
            DiagnosticLog.Initialize(path: path);
            using (var f = new Fixture())
            {
                f.Visible(Controls[0], false, released: true);
                f.Tick(); f.Tick(1000);
            }
            DiagnosticLog.Shutdown();
            check("Ctrl recovery is recorded in the normal diagnostic log", File.ReadAllText(path).Contains("Recovered released Left Ctrl:"));
            var entries = File.ReadAllLines(path + ".input").Where(l => l.Contains("reason=RecoveredControl")).ToArray();
            check("Ctrl recovery trace identifies side and before/after state", entries.Length == 2 &&
                entries.All(l => l.Contains("key=LeftControl")) && entries[0].Contains("state=0x340005") &&
                entries[1].Contains("index=1") && entries[1].Contains("state=0x340000"));
        }
        finally { DiagnosticLog.Shutdown(); Directory.Delete(directory, true); }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ModifierReleaseTests.Platform Platform = new() { Now = Environment.TickCount64 };
        public readonly AltInputService Service;
        private readonly Action _tick;
        public Fixture()
        {
            Service = ModifierReleaseTests.Create(Platform, true);
            _tick = typeof(AltInputService).GetMethod("ReconcileReleasedModifiers", Private)!.CreateDelegate<Action>(Service);
        }
        public nint Key(uint key, bool up) => Platform.Deliver(key, up);
        public void Tap(uint key) { Key(key, false); Key(key, true); }
        public void Tick(long elapsed = 0) { Platform.Now += elapsed; _tick(); }
        public bool Flag(uint key, string name)
            => StateFlag(key == Controls[0] ? "_leftControl" : "_rightControl", name);
        public bool AltDown(bool left) => StateFlag(left ? "_leftAlt" : "_rightAlt", "IsDown");
        private bool StateFlag(string field, string name)
        {
            var state = typeof(AltInputService).GetField(field, Private)!.GetValue(Service)!;
            return (bool)state.GetType().GetProperty(name)!.GetValue(state)!;
        }
        public void Visible(uint key, bool passed, bool released)
        {
            if (passed) Key((uint)Keys.X, false);
            Key(key, false);
            if (!passed) Key((uint)Keys.X, false);
            Key((uint)Keys.X, true);
            if (released) Platform.SetDown(key, false);
            Platform.Sent.Clear();
        }
        public void Mouse()
        {
            var callback = typeof(AltInputService).GetMethod("MouseHookCallback", Private)!.CreateDelegate<NativeMethods.HookProc>(Service);
            var data = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MouseLlHookStruct>());
            try
            {
                Marshal.StructureToPtr(new NativeMethods.MouseLlHookStruct(), data, false);
                callback(0, NativeMethods.WmLButtonDown, data);
                callback(0, NativeMethods.WmLButtonUp, data);
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        public void Dispose() => Service.Dispose();
    }
}
