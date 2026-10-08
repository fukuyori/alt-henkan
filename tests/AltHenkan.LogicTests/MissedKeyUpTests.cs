using AltHenkan;

// Characterization of the current failure paths, NOT proof of the reported Windows-wide incident.
// No Windows hooks or synthetic input: omit a release in the real state machine's event sequence.
internal static class MissedKeyUpTests
{
    public static void Run(Action<string, bool> check)
    {
        var settings = new EmacsShortcutSettings();
        foreach (var shortcut in EmacsBindings.All)
        {
            var state = new EmacsKeyboardState();
            state.SetActive(true);
            var captured = state.HandleKey(shortcut.SourceKey, true, true, shortcut.Modifiers,
                settings, out var binding);
            check($"Fault setup captures {shortcut.Shortcut}", captured && binding == shortcut);

            // The source-key release is lost. All modifiers have since been released.
            var swallowed = state.HandleKey(shortcut.SourceKey, true, true, NavigationModifiers.None,
                settings, out binding);
            check($"Lost key-up swallows a plain press after {shortcut.Shortcut}",
                swallowed && binding is null && state.CapturedKeyCount == 1);
            foreach (var elapsed in new[] { 30_000, 300_000, 3_600_000 })
                check($"Retained {shortcut.Shortcut} capture prevents renewal at {elapsed} ms",
                    !HookMaintenancePolicy.ShouldRefresh(elapsed, (uint)elapsed, false,
                        state.GestureActive, refreshRequested: true));

            // Control: a later delivered release repairs this particular source-key capture.
            state.HandleKey(shortcut.SourceKey, false, true, NavigationModifiers.None, settings, out _);
            check($"Delivered key-up restores plain input after {shortcut.Shortcut}",
                !state.HandleKey(shortcut.SourceKey, true, true, NavigationModifiers.None, settings, out _) &&
                !state.GestureActive);
        }

        foreach (var left in new[] { true, false })
        {
            // Inputs correspond to a retained tracked Ctrl down, despite all OS samples being up.
            var ctrl = new ControlModifierSnapshot(left, !left, false, false, false);
            var state = new EmacsKeyboardState();
            state.SetActive(true);
            var binding = EmacsBindings.All.First(b => b.Shortcut == EmacsShortcut.ControlF);
            check($"Retained {(left ? "left" : "right")} Ctrl translates a plain F press",
                state.HandleKey(binding.SourceKey, true, true,
                    ctrl.AnyDown ? NavigationModifiers.Control : NavigationModifiers.None, settings, out var actual) &&
                actual == binding);
            check("Retained Ctrl postpones even an explicitly requested renewal",
                !HookMaintenancePolicy.ShouldRefresh(3_600_000, 3_600_000, false,
                    ctrl.LeftTrackedDown || ctrl.RightTrackedDown, true));
        }

        var caps = new EmacsKeyboardState();
        caps.HandleCapsLock(true, true, out _);
        check("Lost CapsLock up leaves a gesture active", caps.CapsLockCaptured && caps.GestureActive);
        check("Lost CapsLock up postpones renewal",
            !HookMaintenancePolicy.ShouldRefresh(60_000, 60_000, false, caps.GestureActive, true));
        caps.ResetTransient();
        check("Explicit state reset clears capture and preserves selected mode",
            !caps.GestureActive && caps.Active &&
            HookMaintenancePolicy.ShouldRefresh(60_000, 60_000, false, caps.GestureActive, true));
        check("Control case: an actually held key still prevents unsafe renewal",
            !HookMaintenancePolicy.ShouldRefresh(60_000, 60_000, true, false, true));
    }
}
