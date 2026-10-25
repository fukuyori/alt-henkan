using AltHenkan;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class ModifierReleaseTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly uint[] Modifiers =
        [NativeMethods.VkLControl, NativeMethods.VkRControl, NativeMethods.VkLMenu, NativeMethods.VkRMenu];
    private static readonly string[] StateFields = ["_leftControl", "_rightControl", "_leftAlt", "_rightAlt"];

    public static void Run(Action<string, bool> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AltHenkan-Release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "diagnostics.log");
        try
        {
            DiagnosticLog.Initialize(path: path);
            RunSequences(check);
            RunReentry(check);
            DiagnosticLog.Shutdown();
            check("Normal modifier sequences and reentry produce no residual diagnostics",
                !File.ReadAllText(path).Contains("Modifier release residual:") &&
                !File.ReadAllText(path + ".input").Contains("reason=ModifierReleaseResidual"));

            // Both trace-enabled and trace-disabled paths must detect an actual residual.
            foreach (var traceEnabled in new[] { true, false })
            {
                var faultPath = Path.Combine(directory, $"fault-{traceEnabled}.log");
                DiagnosticLog.Initialize(path: faultPath);
                if (!traceEnabled) InputTrace.Shutdown();
                foreach (var key in Modifiers) RunFailedRelease(key, check);
                DiagnosticLog.Shutdown();
                var faults = File.ReadAllLines(faultPath).Where(l => l.Contains("Modifier release residual:")).ToArray();
                check($"Trace={traceEnabled}: repeated ups are deduplicated and a recovered gesture rearms reporting", faults.Length == 8);
                check($"Trace={traceEnabled}: fault reports identify the remaining sent-down state",
                    faults.All(l => l.Contains("residual=0x4")) &&
                    new[] { "LeftControl", "RightControl", "LeftAlt", "RightAlt" }.All(k => faults.Count(l => l.Contains($"key={k},")) == 2));
                if (traceEnabled)
                {
                    var trace = File.ReadAllLines(faultPath + ".input");
                    var residuals = trace.Where(l => l.Contains("reason=ModifierReleaseResidual")).ToArray();
                    check("Residual trace contains modifier identity, remaining flags and release metadata",
                        residuals.Length == 8 && residuals.All(l => l.Contains("residual=0x4 up=True") && l.Contains("eventTime=123")));
                    check("Each residual can be joined to its received key-up and exception fallback",
                        residuals.All(l =>
                        {
                            var obs = l.Split(' ').Single(p => p.StartsWith("obs="));
                            return trace.Any(e => e.Contains("kind=KeyBegin") && e.Contains("up=True") && e.Split(' ').Contains(obs)) &&
                                trace.Any(e => e.Contains("kind=Fallback") && e.Split(' ').Contains(obs));
                        }));
                }
            }
            RunHealthyAuditCost(check);
        }
        finally
        {
            DiagnosticLog.Shutdown();
            Directory.Delete(directory, true);
        }
    }

    private static void RunSequences(Action<string, bool> check)
    {
        // PowerToys #46693/#46672 inspired sequences, independently implemented
        // for this app's Ctrl+F binding. Fake input is not a Windows/AltGr repro.
        foreach (var ctrl in Modifiers.Take(2))
        foreach (var alt in Modifiers.Skip(2))
        foreach (var emacs in new[] { true, false })
        foreach (var ctrlFirst in new[] { true, false })
        foreach (var cycles in new[] { 1, 3 })
        foreach (var extra in new[] { true, false })
        {
            var platform = new Platform();
            using var service = Create(platform, emacs);
            var label = $"Ctrl={ctrl:X} Alt={alt:X} Emacs={emacs} CtrlUpFirst={ctrlFirst} cycles={cycles} extra={extra}";
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                platform.Deliver(ctrl, false);
                platform.Deliver(alt, false);
                platform.Deliver(ctrlFirst ? ctrl : alt, true);
                platform.Deliver(ctrlFirst ? alt : ctrl, true);
            }
            check($"{label}: modifier prelude leaves no internal or simulated OS state", Clear(service) && platform.AllUp);
            platform.Sent.Clear();
            platform.Deliver(ctrl, false);
            platform.Deliver((uint)Keys.F, false);
            if (extra) platform.Deliver((uint)Keys.X, false);
            platform.Deliver(ctrlFirst ? ctrl : (uint)Keys.F, true);
            platform.Deliver(ctrlFirst ? (uint)Keys.F : ctrl, true);
            if (extra) platform.Deliver((uint)Keys.X, true);
            check($"{label}: subsequent shortcut emits the configured action only when enabled",
                platform.Sent.Contains(((uint)Keys.Right, false)) == emacs &&
                platform.Sent.Contains(((uint)Keys.Right, true)) == emacs);
            check($"{label}: shortcut and all modifiers finish released", Clear(service) && platform.AllUp);
            var sentBefore = platform.Sent.Count;
            check($"{label}: following ordinary input passes without a stale modifier",
                platform.Deliver((uint)Keys.A, false) == 0 && platform.Deliver((uint)Keys.A, true) == 0 &&
                platform.Sent.Count == sentBefore && platform.AllUp);
        }
    }

    private static void RunReentry(Action<string, bool> check)
    {
        foreach (var key in Modifiers)
        {
            var platform = new Platform();
            using var service = Create(platform, false);
            // With Emacs disabled Ctrl passes directly; an ordinary held key
            // similarly makes Alt pass. Reenter forwarding of the release with
            // a new down: this is fault injection, not an observed OS ordering.
            platform.Deliver((uint)Keys.X, false);
            platform.Deliver(key, false);
            platform.OnForward = () => platform.Deliver(key, false);
            platform.Deliver(key, true);
            check($"{key:X}: new down during release remains tracked", StateFlag(service, key, "IsDown"));
            platform.Deliver(key, true);
            platform.Deliver((uint)Keys.X, true);
            check($"{key:X}: reentrant new gesture can subsequently release", Clear(service) && platform.AllUp);
        }
    }

    private static void RunFailedRelease(uint key, Action<string, bool> check)
    {
        var platform = new Platform();
        using var service = Create(platform, true);
        for (var episode = 0; episode < 2; episode++)
        {
            platform.Deliver(key, false);
            platform.Deliver((uint)Keys.X, false); // Promote the deferred modifier.
            platform.Deliver((uint)Keys.X, true);
            check($"{key:X}: fault setup has a sent modifier", StateFlag(service, key, "NativeDownSent"));
            platform.FailSend = true;
            platform.Deliver(key, true); // Throws before the handler can reset its flags.
            check($"{key:X}: diagnostic observes but does not repair the injected residual",
                !StateFlag(service, key, "IsDown") && StateFlag(service, key, "NativeDownSent"));
            for (var i = 0; i < 3; i++) platform.Deliver(key, true);
            // A new complete gesture clears the residual and rearms the diagnostic.
            platform.Deliver(key, false);
            platform.Deliver(key, true);
            check($"{key:X}: normal next gesture clears the fault state", Clear(service) && platform.AllUp);
        }
    }

    private static void RunHealthyAuditCost(Action<string, bool> check)
    {
        // Compare actual handlers directly, excluding the existing hook struct
        // marshalling allocation from the cost of this added diagnostic.
        var platform = new Platform();
        using var service = Create(platform, false);
        foreach (var field in StateFields)
        {
            var state = typeof(AltInputService).GetField(field, Private)!.GetValue(service)!;
            typeof(ModifierReleaseTests).GetMethod(nameof(MeasureHealthyAudit), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(state.GetType()).Invoke(null, [service, state, platform, check, field]);
        }
    }

    private delegate nint ModifierHandler<T>(T state, int code, nint message, nint pointer,
        NativeMethods.KbdLlHookStruct data, bool down);

    private static void MeasureHealthyAudit<T>(AltInputService service, T state, Platform platform,
        Action<string, bool> check, string label)
    {
        var name = label.Contains("Alt") ? "HandleAltEvent" : "HandleControlEvent";
        var core = typeof(AltInputService).GetMethod(name + "Core", Private)!.CreateDelegate<ModifierHandler<T>>(service);
        var audited = typeof(AltInputService).GetMethod(name, Private)!.CreateDelegate<ModifierHandler<T>>(service);
        (long Bytes, double Milliseconds) Measure(ModifierHandler<T> handler)
        {
            for (var i = 0; i < 10000; i++) handler(state, 0, NativeMethods.WmKeyUp, 0, default, false);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var i = 0; i < 100000; i++) handler(state, 0, NativeMethods.WmKeyUp, 0, default, false);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            return (GC.GetAllocatedBytesForCurrentThread() - allocated, elapsed.TotalMilliseconds);
        }
        var calls = platform.StateReads;
        var baseline = Measure(core);
        var result = Measure(audited);
        check($"{label}: 100000 healthy checks add no managed allocations", result.Bytes == 0 && baseline.Bytes == 0);
        check($"{label}: healthy checks add no key-state queries", platform.StateReads == calls);
        Console.WriteLine($"Release audit probe {label}: 100000 events, core={baseline.Milliseconds:F2} ms, " +
            $"audited={result.Milliseconds:F2} ms, allocations={result.Bytes} bytes (local microbenchmark, not whole-app overhead).");
    }

    internal static AltInputService Create(Platform platform, bool emacs)
    {
        var service = new AltInputService(new AppSettings
        { Enabled = true, EmacsEnabled = emacs, EmacsInitialMode = EmacsInitialMode.Emacs }, platform);
        platform.Keyboard = typeof(AltInputService).GetMethod("KeyboardHookCallback", Private)!
            .CreateDelegate<NativeMethods.HookProc>(service);
        return service;
    }

    private static bool StateFlag(AltInputService service, uint key, string property)
    {
        var state = typeof(AltInputService).GetField(StateFields[Array.IndexOf(Modifiers, key)], Private)!.GetValue(service)!;
        return (bool)state.GetType().GetProperty(property)!.GetValue(state)!;
    }

    private static bool Clear(AltInputService service) => Modifiers.All(key =>
        new[] { "IsDown", "OriginalDownPassed", "NativeDownSent", "ConsumedByEmacs" }.All(p => !StateFlag(service, key, p))) &&
        Modifiers.Skip(2).All(key => !StateFlag(service, key, "DeferredShiftChord")) &&
        ((EmacsKeyboardState)typeof(AltInputService).GetField("_emacsState", Private)!.GetValue(service)!).CapturedKeyCount == 0;

    internal sealed class Platform : InputPlatform
    {
        public NativeMethods.HookProc Keyboard = null!;
        public Action? OnForward;
        public Action? OnSend;
        public Action? AfterSend;
        public bool FailSend;
        public int StateReads;
        public int GuardedReads;
        public long? Now;
        public override long MonotonicMilliseconds => Now ?? base.MonotonicMilliseconds;
        public bool Reliable = true;
        public nint ForegroundWindow = 1;
        public readonly List<(uint Key, bool Up)> Sent = [];
        private readonly ushort _rightScan = (ushort)(NativeMethods.MapVirtualKey((uint)Keys.Right, NativeMethods.MapvkVkToVscEx) & 0xFF);
        private readonly HashSet<uint> _down = [];
        public bool AllUp => _down.Count == 0;
        public void SetDown(uint key, bool down)
        {
            if (down) _down.Add(key); else _down.Remove(key);
        }
        public bool Down(uint key) => _down.Contains(key);
        public override InputModifierState ReadModifierState()
        {
            GuardedReads++;
            return new(Reliable, ForegroundWindow, Down(NativeMethods.VkLMenu), Down(NativeMethods.VkRMenu),
                Down(NativeMethods.VkLControl), Down(NativeMethods.VkRControl));
        }
        public override bool IsDown(int key)
        {
            StateReads++;
            return _down.Contains((uint)key) || key == NativeMethods.VkControl &&
                (_down.Contains(NativeMethods.VkLControl) || _down.Contains(NativeMethods.VkRControl));
        }
        public override InputForeground Foreground() => new(ForegroundWindow, 2, 3);
        public override nint NextMouse(nint hook, int code, nint wParam, nint lParam) => 0;
        public override nint NextKeyboard(nint hook, int code, nint wParam, nint lParam)
        {
            var callback = OnForward;
            OnForward = null;
            callback?.Invoke();
            return 0;
        }
        public nint Deliver(uint key, bool up, nuint marker = 0, uint flags = 0)
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.KbdLlHookStruct>());
            try
            {
                Marshal.StructureToPtr(new NativeMethods.KbdLlHookStruct
                {
                    VirtualKeyCode = key, ScanCode = key is NativeMethods.VkLControl or NativeMethods.VkRControl ? 0x1Du : 0x38u,
                    ExtraInfo = marker, Time = 123,
                    Flags = flags | (key is NativeMethods.VkRMenu or NativeMethods.VkRControl ? 1u : 0) | (up ? 0x80u : 0)
                }, pointer, false);
                var message = key is NativeMethods.VkLMenu or NativeMethods.VkRMenu
                    ? (up ? NativeMethods.WmSysKeyUp : NativeMethods.WmSysKeyDown)
                    : (up ? NativeMethods.WmKeyUp : NativeMethods.WmKeyDown);
                var result = Keyboard(0, message, pointer);
                if (result == 0) { if (up) _down.Remove(key); else _down.Add(key); }
                return result;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        public override InputSendResult Send(NativeMethods.Input[] inputs)
        {
            if (FailSend) { FailSend = false; throw new IOException("Release test send fault"); }
            var callback = OnSend;
            OnSend = null;
            var after = AfterSend;
            AfterSend = null;
            callback?.Invoke();
            foreach (var input in inputs)
            {
                var key = input.Data.Keyboard;
                var extended = (key.Flags & NativeMethods.KeyeventfExtendedKey) != 0;
                var vk = key.VirtualKey != 0 ? key.VirtualKey : key.ScanCode == 0x1D
                    ? (extended ? NativeMethods.VkRControl : NativeMethods.VkLControl)
                    : key.ScanCode == 0x38 ? (extended ? NativeMethods.VkRMenu : NativeMethods.VkLMenu)
                    : key.ScanCode == _rightScan && extended ? (uint)Keys.Right
                    : throw new InvalidOperationException($"Unexpected test scan code: {key.ScanCode:X}");
                var up = (key.Flags & NativeMethods.KeyeventfKeyUp) != 0;
                Sent.Add((vk, up));
                Deliver(vk, up, key.ExtraInfo, 0x10);
            }
            after?.Invoke();
            return new((uint)inputs.Length, 0);
        }
    }
}
