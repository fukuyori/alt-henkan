using AltHenkan;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static class AltRecoveryTests
{
    public static void RunNative(AltInputService service, DedicatedMessageLoop loop, Action<string, bool> check)
    {
        loop.InvokeAsync(() =>
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(AltInputService);
            var reconcile = type.GetMethod("ReconcileReleasedAlt", flags)!;
            long Counter(string name) => (long)type.GetField(name, flags)!.GetValue(service)!;
            var checkAll = type.GetMethod("ReconcileReleasedModifiers", flags)!;
            foreach (var side in new[] { "_leftAlt", "_rightAlt" })
            {
                var state = type.GetField(side, flags)!.GetValue(service)!;
                var stateType = state.GetType();
                void Set(string property, object value) => stateType.GetProperty(property)!.SetValue(state, value);
                bool Get(string property) => (bool)stateType.GetProperty(property)!.GetValue(state)!;
                void Reset() => stateType.GetMethod("Reset")!.Invoke(state, null);
                void Observe(bool reliable, bool down, long now) =>
                    reconcile.Invoke(service, [state, reliable, (nint)1, down, now]);
                try
                {
                    stateType.GetMethod("Start")!.Invoke(state, [(uint)0x38, side == "_rightAlt"]);
                    Set("NativeDownSent", true);
                    Observe(true, false, 1000);
                    check($"Native {side} state survives first release sample", Get("IsDown"));
                    Observe(true, false, 2000);
                    check($"Native {side} stale state is reset on the actual service", !Get("IsDown") && !Get("NativeDownSent"));
                    check("Actual service requests idle hook renewal after Alt reconciliation",
                        (bool)type.GetField("_refreshRequested", flags)!.GetValue(service)!);

                    stateType.GetMethod("Start")!.Invoke(state, [(uint)0x38, side == "_rightAlt"]);
                    Observe(true, false, 1000);
                    Observe(true, false, 2000);
                    check($"Native {side} hidden long-press state is preserved", Get("IsDown"));
                    Set("OriginalDownPassed", true);
                    Observe(true, true, 3000);
                    Observe(true, true, 4000);
                    check($"Native {side} held forwarded Alt is preserved", Get("IsDown"));
                    Observe(false, false, 5000);
                    Observe(false, false, 6000);
                    check($"Native {side} does not recover from inaccessible samples", Get("IsDown"));
                    Observe(true, false, 7000);
                    var checksBefore = Counter("_altReconcileChecks");
                    var skipsBefore = Counter("_altReconcileInjectionSkips");
                    var altSkipsBefore = Counter("_altReconcileInjectionSkipsWithAlt");
                    type.GetField("_inputInjectionDepth", flags)!.SetValue(service, 1);
                    checkAll.Invoke(service, null);
                    type.GetField("_inputInjectionDepth", flags)!.SetValue(service, 0);
                    check("Artificial in-flight sample counts one check and one Alt-held exclusion",
                        Counter("_altReconcileChecks") == checksBefore + 1 &&
                        Counter("_altReconcileInjectionSkips") == skipsBefore + 1 &&
                        Counter("_altReconcileInjectionSkipsWithAlt") == altSkipsBefore + 1);
                    Observe(true, false, 8000);
                    check("In-flight SendInput invalidates release confirmation", Get("IsDown"));
                    Observe(true, false, 9000);
                    check("Forwarded Alt recovers after fresh reliable observations", !Get("IsDown"));
                }
                finally
                {
                    // Never let a synthetic test state reach Dispose's real key-up sender.
                    Reset();
                    type.GetField("_inputInjectionDepth", flags)!.SetValue(service, 0);
                }
            }
            var checks = Counter("_altReconcileChecks");
            var skips = Counter("_altReconcileInjectionSkips");
            var altSkips = Counter("_altReconcileInjectionSkipsWithAlt");
            checkAll.Invoke(service, null);
            check("Idle check counts a call without a false injection exclusion",
                Counter("_altReconcileChecks") == checks + 1 &&
                Counter("_altReconcileInjectionSkips") == skips);
            try
            {
                // Artificial depth verifies instrumentation, not real Windows reentrancy.
                type.GetField("_inputInjectionDepth", flags)!.SetValue(service, 2);
                checkAll.Invoke(service, null);
                check("In-flight check without Alt is distinguished from Alt-held exclusion",
                    Counter("_altReconcileInjectionSkips") == skips + 1 &&
                    Counter("_altReconcileInjectionSkipsWithAlt") == altSkips &&
                    (int)type.GetField("_maxAltReconcileInjectionDepth", flags)!.GetValue(service)! == 2);
            }
            finally { type.GetField("_inputInjectionDepth", flags)!.SetValue(service, 0); }
            var calls = Counter("_inputInjectionCalls");
            // An empty batch exercises the real wrapper without sending a keyboard event.
            type.GetMethod("SendKeyboardInputs", flags)!.Invoke(service, [Array.Empty<NativeMethods.Input>()]);
            check("Input wrapper counts calls, records depth, and restores depth on return",
                Counter("_inputInjectionCalls") == calls + 1 &&
                (int)type.GetField("_maxInputInjectionDepth", flags)!.GetValue(service)! >= 1 &&
                (int)type.GetField("_inputInjectionDepth", flags)!.GetValue(service)! == 0);
            checkAll.Invoke(service, null);
            check("Completed input wrapper does not produce another exclusion",
                Counter("_altReconcileInjectionSkips") == skips + 1);
            var reliable = AsyncKeyStateAccess.TryReadAlt(out _, out _, out _);
            Console.WriteLine($"Native Alt state access guard: reliable={reliable}.");
        }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    public static void Run(Action<string, bool> check)
    {
        RunAltRepress(check);
        // Match 2026-10-11: tracked left Alt and NativeDownSent, but async Alt is up.
        var stale = new AltModifierSnapshot(true, false, true, false);
        check("Released native Alt is not used as an Emacs modifier", !stale.Active);
        check("Released native Alt is not restored after Emacs input", !stale.ShouldRestore);
        var binding = EmacsBindings.All.First(b => b.Shortcut == EmacsShortcut.ControlF);
        var plan = NavigationInputPlan.Create(binding,
            stale.ShouldRestore ? [(ushort)NativeMethods.VkLMenu] : []);
        check("Navigation with stale native Alt contains no Alt reinjection",
            plan.All(stroke => stroke.VirtualKey != NativeMethods.VkLMenu));
        var letter = new EmacsKeyboardState();
        letter.SetActive(true);
        check("Plain F passes after native Alt release instead of becoming Alt+F",
            !letter.HandleKey((uint)Keys.F, true, true, stale.Active ? NavigationModifiers.Alt : NavigationModifiers.None,
                new EmacsShortcutSettings(), out _));

        foreach (var original in new[] { false, true })
        {
            var held = new AltModifierSnapshot(true, original, !original, true);
            var released = held with { AsyncDown = false };
            check("Held forwarded/injected Alt still participates in shortcuts", held.Active && held.ShouldRestore);
            var heldPlan = NavigationInputPlan.Create(binding, held.ShouldRestore ? [(ushort)NativeMethods.VkLMenu] : []);
            check("Held native Alt is released for navigation and restored afterwards",
                heldPlan[0] == new NavigationKeyStroke((ushort)NativeMethods.VkLMenu, true) &&
                heldPlan[^1] == new NavigationKeyStroke((ushort)NativeMethods.VkLMenu, false));
            var monitor = new ModifierReleaseMonitor();
            check("First disagreement does not reset a native Alt gesture", !monitor.Observe(released, true, 1, 0));
            check("Subsecond disagreement is not sufficient", !monitor.Observe(released, true, 1, 999));
            check("One second of confirmed disagreement allows recovery", monitor.Observe(released, true, 1, 1000));
            monitor.Reset();
            for (var second = 0; second <= 120; second++)
                check("Long native Alt hold is never released by elapsed time", !monitor.Observe(held, true, 1, second * 1000));
        }

        var deferred = new AltModifierSnapshot(true, false, false, false);
        check("Deferred physical Alt remains an active modifier even though Windows reports up",
            deferred.Active && !deferred.ShouldRestore);
        var hiddenMonitor = new ModifierReleaseMonitor();
        for (var second = 0; second <= 120; second++)
            check("Long deferred / Emacs-consumed Alt gesture is preserved",
                !hiddenMonitor.Observe(deferred, true, 1, second * 1000));
        check("Untracked external native Alt can still select a shortcut",
            new AltModifierSnapshot(false, false, false, true).Active);

        var gate = new ModifierReleaseMonitor();
        gate.Observe(stale, true, 1, 0);
        check("Unavailable key state cancels confirmation", !gate.Observe(stale, false, 1, 1000));
        check("Access restoration requires a new first sample", !gate.Observe(stale, true, 1, 2000));
        check("Confirmed accessible state recovers", gate.Observe(stale, true, 1, 3000));
        gate.Reset();
        gate.Observe(stale, true, 1, 0);
        check("Foreground switch restarts confirmation", !gate.Observe(stale, true, 2, 1000));
        check("Missing foreground cancels confirmation", !gate.Observe(stale, true, 0, 2000));
        gate.Observe(stale, true, 1, 3000);
        check("Timer gap / suspend does not count as continuous observation", !gate.Observe(stale, true, 1, 10000));
        check("New down observation cancels confirmation", !gate.Observe(stale with { AsyncDown = true }, true, 1, 11000));
        check("Following release requires a fresh interval", !gate.Observe(stale, true, 1, 12000));
        gate.Reset();
        check("A new physical gesture cannot inherit old confirmation", !gate.Observe(stale, true, 1, 13000));
    }

    private static void RunAltRepress(Action<string, bool> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(AltInputService);
        var stateType = type.GetNestedType("AltState", BindingFlags.NonPublic)!;
        var sideType = type.GetNestedType("AltSide", BindingFlags.NonPublic)!;
        var handle = type.GetMethod("HandleAltEvent", flags)!;
        var reconcile = type.GetMethod("ReconcileReleasedAlt", flags)!;
        foreach (var side in new[] { "Left", "Right" })
        foreach (var origin in new[] { "injected", "passed", "hidden" })
        foreach (var scenario in new[] { "stale before sample", "stale after sample", "held repeat" })
        {
            // Exercise the actual event handler, replacing only hook forwarding.
            // Skip the constructor so no global hooks/timers are installed, and never Dispose
            // this synthetic service (which would otherwise send native key releases).
            var service = (AltInputService)RuntimeHelpers.GetUninitializedObject(type);
            var state = Activator.CreateInstance(stateType, [Enum.Parse(sideType, side)])!;
            var forwarded = new List<nint>();
            var windowsDown = origin != "hidden" && scenario == "held repeat";
            NativeMethods.HookProc next = (code, message, dataPointer) =>
            {
                forwarded.Add(message);
                windowsDown = message == NativeMethods.WmSysKeyDown;
                return 73;
            };
            type.GetField("_nextKeyboardHook", flags)!.SetValue(service, next);
            void Set(string property, object value) => stateType.GetProperty(property)!.SetValue(state, value);
            bool Get(string property) => (bool)stateType.GetProperty(property)!.GetValue(state)!;
            void Observe(long now) => reconcile.Invoke(service, [state, true, (nint)1, windowsDown, now]);
            var data = new NativeMethods.KbdLlHookStruct
            {
                VirtualKeyCode = side == "Left" ? NativeMethods.VkLMenu : NativeMethods.VkRMenu,
                ScanCode = 0x38,
                Flags = side == "Right" ? NativeMethods.LlkhfExtended : 0
            };
            nint Down() => (nint)handle.Invoke(service,
                [state, 0, (nint)NativeMethods.WmSysKeyDown, (nint)0, data, true])!;
            stateType.GetMethod("Start")!.Invoke(state, [data.ScanCode, side == "Right"]);
            Set("NativeDownSent", origin == "injected");
            Set("OriginalDownPassed", origin == "passed");
            var downAt = stateType.GetProperty("DownAtMilliseconds")!.GetValue(state);
            if (scenario == "stale after sample") Observe(1000);
            var label = $"{side} {origin} Alt, {scenario}";
            var native = origin != "hidden";
            var routedCorrectly = Down() == (native ? 73 : 1);
            check($"{label}: additional down is forwarded only for native Alt", routedCorrectly);
            // A regression must fail without entering a native SendInput path later in this test.
            if (!routedCorrectly) continue;
            var monitor = stateType.GetProperty("ReleaseMonitor")!.GetValue(state)!;
            check($"{label}: down invalidates previous release confirmation",
                monitor.GetType().GetField("_releasedSince", flags)!.GetValue(monitor) is null);
            check($"{label}: repeat keeps the original hold start time",
                Equals(downAt, stateType.GetProperty("DownAtMilliseconds")!.GetValue(state)));
            Observe(2000);
            Observe(3000);
            Observe(120000);
            check($"{label}: holding the new press does not clear internal state", Get("IsDown"));
            if (!Get("IsDown")) continue;
            check($"{label}: a further repeat keeps the same routing", Down() == (native ? 73 : 1));
            check($"{label}: no false recovery requests hook renewal",
                !(bool)type.GetField("_refreshRequested", flags)!.GetValue(service)!);
            if (native)
            {
                var physicalRelease = Get("IsDown") && Get("OriginalDownPassed") && !Get("NativeDownSent");
                check($"{label}: release is routed to the physical pass-through branch", physicalRelease);
                if (!physicalRelease) continue;
                var up = (nint)handle.Invoke(service,
                    [state, 0, (nint)NativeMethods.WmSysKeyUp, (nint)0, data, false])!;
                check($"{label}: physical up follows down without synthesized input",
                    up == 73 && forwarded.SequenceEqual(new nint[]
                    { NativeMethods.WmSysKeyDown, NativeMethods.WmSysKeyDown, NativeMethods.WmSysKeyUp }));
                check($"{label}: release clears internal and simulated Windows state",
                    !Get("IsDown") && !Get("OriginalDownPassed") && !Get("NativeDownSent") && !windowsDown);
            }
            else
            {
                check($"{label}: deferred repeats never reach Windows", forwarded.Count == 0);
                stateType.GetMethod("Reset")!.Invoke(state, null);
            }
        }
    }
}
