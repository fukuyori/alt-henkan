using AltHenkan;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

internal static class EmacsTests
{
    public static void Run(Action<string, bool> check)
    {
        var defaults = new AppSettings();
        check("Emacs is opt-in, starts normal and mode overlay defaults on", !defaults.EmacsEnabled &&
            defaults.EmacsInitialMode == EmacsInitialMode.Normal && defaults.ShowModeChangeOverlay);
        var initialState = new EmacsKeyboardState();
        check("Selected Emacs initial mode activates state", initialState.SetActive((defaults with
        {
            Enabled = true, EmacsEnabled = true, EmacsInitialMode = EmacsInitialMode.Emacs
        }).InitialEmacsActive) && initialState.Active);
        check("Feature off overrides selected Emacs initial mode", initialState.SetActive((defaults with
        {
            Enabled = true, EmacsEnabled = false, EmacsInitialMode = EmacsInitialMode.Emacs
        }).InitialEmacsActive) && !initialState.Active);
        var expected = new (EmacsShortcut Shortcut, Keys Source, NavigationModifiers Modifiers, Keys Target, bool Control)[]
        {
            (EmacsShortcut.ControlB, Keys.B, NavigationModifiers.Control, Keys.Left, false),
            (EmacsShortcut.ControlF, Keys.F, NavigationModifiers.Control, Keys.Right, false),
            (EmacsShortcut.ControlP, Keys.P, NavigationModifiers.Control, Keys.Up, false),
            (EmacsShortcut.ControlN, Keys.N, NavigationModifiers.Control, Keys.Down, false),
            (EmacsShortcut.ControlA, Keys.A, NavigationModifiers.Control, Keys.Home, false),
            (EmacsShortcut.ControlE, Keys.E, NavigationModifiers.Control, Keys.End, false),
            (EmacsShortcut.AltB, Keys.B, NavigationModifiers.Alt, Keys.Left, true),
            (EmacsShortcut.AltF, Keys.F, NavigationModifiers.Alt, Keys.Right, true),
            (EmacsShortcut.AltLess, Keys.Oemcomma, NavigationModifiers.Alt | NavigationModifiers.Shift, Keys.Home, true),
            (EmacsShortcut.AltGreater, Keys.OemPeriod, NavigationModifiers.Alt | NavigationModifiers.Shift, Keys.End, true),
            (EmacsShortcut.ControlD, Keys.D, NavigationModifiers.Control, Keys.Delete, false),
            (EmacsShortcut.AltD, Keys.D, NavigationModifiers.Alt, Keys.Delete, true),
            (EmacsShortcut.ControlK, Keys.K, NavigationModifiers.Control, Keys.Delete, false)
        };
        check("Thirteen Emacs bindings exist", EmacsBindings.All.Count == expected.Length);
        foreach (var item in expected)
        {
            var binding = EmacsBindings.Resolve((uint)item.Source, item.Modifiers, defaults.EmacsShortcuts);
            check($"{item.Shortcut} mapping", binding is not null && binding.Shortcut == item.Shortcut &&
                binding.TargetKey == (ushort)item.Target && binding.TargetControl == item.Control);
            var disabled = defaults.EmacsShortcuts.WithEnabled(item.Shortcut, false);
            check($"{item.Shortcut} can be disabled independently", !disabled.IsEnabled(item.Shortcut) &&
                expected.Where(other => other.Shortcut != item.Shortcut).All(other => disabled.IsEnabled(other.Shortcut)) &&
                EmacsBindings.Resolve((uint)item.Source, item.Modifiers, disabled) is null);
            var wrongShift = item.Modifiers ^ NavigationModifiers.Shift;
            check($"{item.Shortcut} requires the exact Shift state", EmacsBindings.Resolve((uint)item.Source,
                wrongShift, defaults.EmacsShortcuts) is null);
            check($"{item.Shortcut} excludes Windows modifier", EmacsBindings.Resolve((uint)item.Source,
                item.Modifiers | NavigationModifiers.Windows, defaults.EmacsShortcuts) is null);
        }
        check("Ctrl+Alt is not remapped", EmacsBindings.Resolve((uint)Keys.B,
            NavigationModifiers.Control | NavigationModifiers.Alt, defaults.EmacsShortcuts) is null);
        check("Ctrl+V remains native", EmacsBindings.Resolve((uint)Keys.V, NavigationModifiers.Control, defaults.EmacsShortcuts) is null);

        var state = new EmacsKeyboardState();
        check("Startup mode is normal", !state.Active);
        check("Disabled feature passes native CapsLock", !state.HandleCapsLock(true, false, out var changed) && !changed);
        check("Native CapsLock held while enabling does not become mode toggle", !state.HandleCapsLock(true, true, out changed) && !changed && !state.Active);
        check("Disabled feature passes CapsLock up", !state.HandleCapsLock(false, false, out _));
        check("CapsLock selects Emacs", state.HandleCapsLock(true, true, out changed) && changed && state.Active);
        check("CapsLock repeat does not toggle", state.HandleCapsLock(true, true, out changed) && !changed && state.Active);
        check("CapsLock up is paired", state.HandleCapsLock(false, true, out changed) && !changed && !state.GestureActive);
        foreach (var key in new[] { Keys.B, Keys.D, Keys.K, Keys.D1, Keys.Oemcomma, Keys.OemPeriod })
        {
            check($"Normal input {key} passes through", !state.HandleKey((uint)key, true, true,
                NavigationModifiers.None, defaults.EmacsShortcuts, out var binding) && binding is null);
            check($"Normal input {key} up passes through", !state.HandleKey((uint)key, false, true,
                NavigationModifiers.None, defaults.EmacsShortcuts, out _));
        }
        check("Ctrl+B consumes source and emits navigation", state.HandleKey((uint)Keys.B, true, true,
            NavigationModifiers.Control, defaults.EmacsShortcuts, out var output) && output?.Shortcut == EmacsShortcut.ControlB);
        check("Held Ctrl+B repeats navigation", state.HandleKey((uint)Keys.B, true, true,
            NavigationModifiers.Control, defaults.EmacsShortcuts, out output) && output is not null);
        check("Captured repeat stops when modifiers change", state.HandleKey((uint)Keys.B, true, true,
            NavigationModifiers.Alt, defaults.EmacsShortcuts, out output) && output is null);
        check("Captured repeat does not leak after feature off", state.HandleKey((uint)Keys.B, true, false,
            NavigationModifiers.None, defaults.EmacsShortcuts, out output) && output is null);
        check("Captured key up remains paired after modifier released", state.HandleKey((uint)Keys.B, false, false,
            NavigationModifiers.None, defaults.EmacsShortcuts, out output) && output is null && !state.GestureActive);
        state.HandleKey((uint)Keys.B, true, true, NavigationModifiers.None, defaults.EmacsShortcuts, out _);
        check("A native key press does not become remapped midway", !state.HandleKey((uint)Keys.B, true, true,
            NavigationModifiers.Control, defaults.EmacsShortcuts, out output) && output is null);
        state.HandleKey((uint)Keys.B, false, true, NavigationModifiers.Control, defaults.EmacsShortcuts, out _);
        check("An individually disabled shortcut passes through", !state.HandleKey((uint)Keys.B, true, true,
            NavigationModifiers.Control, defaults.EmacsShortcuts.WithEnabled(EmacsShortcut.ControlB, false), out output) && output is null);
        state.HandleKey((uint)Keys.B, false, true, NavigationModifiers.None, defaults.EmacsShortcuts, out _);
        state.HandleKey((uint)Keys.B, true, true, NavigationModifiers.Control, defaults.EmacsShortcuts, out _);
        check("Hook renewal preserves mode and resets transient state", ResetPreservesMode(state));
        check("CapsLock selects normal again", state.HandleCapsLock(true, true, out changed) && changed && !state.Active);
        state.HandleCapsLock(false, true, out _);
        check("Normal mode leaves Ctrl+B native", !state.HandleKey((uint)Keys.B, true, true,
            NavigationModifiers.Control, defaults.EmacsShortcuts, out output) && output is null);
        state.HandleKey((uint)Keys.B, false, true, NavigationModifiers.Control, defaults.EmacsShortcuts, out _);
        state.HandleCapsLock(true, true, out _);
        check("Disabling exits Emacs mode", state.Disable() && !state.Active);
        check("Disabling while CapsLock held keeps release paired", state.HandleCapsLock(false, false, out _));

        var old = JsonSerializer.Deserialize<AppSettings>("{\"Enabled\":true,\"LeftAltLongPressEnabled\":false,\"LongPressMilliseconds\":850}")!.Normalize();
        check("Old settings retain Alt options and default Emacs off", !old.EmacsEnabled && !old.LeftAltLongPressEnabled &&
            old.LongPressMilliseconds == 850 && old.EmacsShortcuts.ControlB);
        check("Null shortcut settings are normalized", JsonSerializer.Deserialize<AppSettings>("{\"EmacsShortcuts\":null}")!.Normalize().EmacsShortcuts.ControlB);
        var custom = old with { EmacsEnabled = true, EmacsInitialMode = EmacsInitialMode.Emacs, ShowModeChangeOverlay = false,
            EmacsControlSide = ModifierSideSelection.Left, EmacsAltSide = ModifierSideSelection.Right,
            EmacsShortcuts = defaults.EmacsShortcuts.WithEnabled(EmacsShortcut.ControlA, false).WithEnabled(EmacsShortcut.AltF, false) };
        check("Settings JSON round trips all options", JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(custom)) == custom);

        var ctrlB = EmacsBindings.All.Single(item => item.Shortcut == EmacsShortcut.ControlB);
        var altB = EmacsBindings.All.Single(item => item.Shortcut == EmacsShortcut.AltB);
        check("Ctrl+B temporarily releases and restores left Ctrl", NavigationInputPlan.Create(ctrlB, [(ushort)Keys.LControlKey]).SequenceEqual(new[]
        {
            new NavigationKeyStroke((ushort)Keys.LControlKey, true), new NavigationKeyStroke((ushort)Keys.Left, false),
            new NavigationKeyStroke((ushort)Keys.Left, true), new NavigationKeyStroke((ushort)Keys.LControlKey, false)
        }));
        check("Pending Alt+B emits only Ctrl+Left without native Alt", NavigationInputPlan.Create(altB, []).SequenceEqual(new[]
        {
            new NavigationKeyStroke((ushort)Keys.LControlKey, false), new NavigationKeyStroke((ushort)Keys.Left, false),
            new NavigationKeyStroke((ushort)Keys.Left, true), new NavigationKeyStroke((ushort)Keys.LControlKey, true)
        }));
        check("Native right Alt is released and restored around word movement", NavigationInputPlan.Create(altB, [(ushort)Keys.RMenu]).SequenceEqual(new[]
        {
            new NavigationKeyStroke((ushort)Keys.RMenu, true), new NavigationKeyStroke((ushort)Keys.LControlKey, false),
            new NavigationKeyStroke((ushort)Keys.Left, false), new NavigationKeyStroke((ushort)Keys.Left, true),
            new NavigationKeyStroke((ushort)Keys.LControlKey, true), new NavigationKeyStroke((ushort)Keys.RMenu, false)
        }));
        check("Both held Ctrl keys are restored", NavigationInputPlan.Create(ctrlB,
            [(ushort)Keys.LControlKey, (ushort)Keys.RControlKey]).Select(item => (item.VirtualKey, item.KeyUp)).SequenceEqual(new[]
        {
            ((ushort)Keys.LControlKey, true), ((ushort)Keys.RControlKey, true), ((ushort)Keys.Left, false),
            ((ushort)Keys.Left, true), ((ushort)Keys.LControlKey, false), ((ushort)Keys.RControlKey, false)
        }));

        var inputFactory = typeof(AltInputService).GetMethod("CreateVirtualKeyInput", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var key in new[] { Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home, Keys.End, Keys.Delete, Keys.RControlKey, Keys.RMenu })
        {
            var input = (NativeMethods.Input)inputFactory.Invoke(null, [(ushort)key, false])!;
            check($"{key} injection uses extended virtual key ABI", input.Type == NativeMethods.InputKeyboard &&
                input.Data.Keyboard.VirtualKey == (ushort)key && input.Data.Keyboard.Flags == NativeMethods.KeyeventfExtendedKey &&
                input.Data.Keyboard.ExtraInfo != 0);
        }
        var imeInput = (NativeMethods.Input)inputFactory.Invoke(null, [JapaneseImeKey.ImeOnVirtualKey, true])!;
        check("Existing IME virtual key encoding remains unchanged", imeInput.Data.Keyboard.VirtualKey == JapaneseImeKey.ImeOnVirtualKey &&
            imeInput.Data.Keyboard.Flags == NativeMethods.KeyeventfKeyUp);

        using var uiThread = new DedicatedMessageLoop(() => { }, () => { });
        uiThread.InvokeAsync(() =>
        {
            using var form = new SettingsForm(custom);
            check("Unified settings form preserves all existing and new options", form.CreateSettings(custom) == custom);
            var controls = Descendants(form).ToArray();
            var tabs = controls.OfType<TabControl>().Single();
            check("Alt, Emacs and display settings have separate tabs", tabs.TabPages.Cast<TabPage>()
                .Select(page => page.Text).SequenceEqual(new[] { "Alt設定", "Emacs設定", "表示設定" }));
            var feature = controls.OfType<CheckBox>().Single(box => box.Text.StartsWith("CapsLock"));
            var shortcuts = controls.OfType<CheckBox>().Where(box => EmacsBindings.All.Any(binding => binding.Label == box.Text)).ToArray();
            check("Settings has thirteen individual shortcut controls", shortcuts.Length == 13 && shortcuts.All(box => box.Enabled));
            var controlSide = controls.OfType<ComboBox>().Single(box => box.Name == "EmacsControlSide");
            var altSide = controls.OfType<ComboBox>().Single(box => box.Name == "EmacsAltSide");
            var initialMode = controls.OfType<ComboBox>().Single(box => box.Name == "EmacsInitialMode");
            check("Initial mode selector displays normal and Emacs", initialMode.Items.Cast<string>().SequenceEqual(new[] { "通常", "Emacs" }) &&
                initialMode.SelectedIndex == 1);
            check("Side selectors display left, right and both", controlSide.Items.Cast<string>().SequenceEqual(new[] { "左", "右", "両方" }) &&
                altSide.Items.Cast<string>().SequenceEqual(new[] { "左", "右", "両方" }));
            check("Side selectors load independent saved values", controlSide.SelectedIndex == 0 && altSide.SelectedIndex == 1);
            feature.Checked = false;
            check("Feature off disables controls but retains their selections", shortcuts.All(box => !box.Enabled) &&
                form.CreateSettings(custom).EmacsShortcuts == custom.EmacsShortcuts && !form.CreateSettings(custom).EmacsEnabled);
            check("Feature off retains initial mode and side values while disabling inputs", !initialMode.Enabled && !controlSide.Enabled && !altSide.Enabled &&
                form.CreateSettings(custom).EmacsInitialMode == EmacsInitialMode.Emacs &&
                form.CreateSettings(custom).EmacsControlSide == custom.EmacsControlSide && form.CreateSettings(custom).EmacsAltSide == custom.EmacsAltSide);
            feature.Checked = true;
            var ctrlA = shortcuts.Single(box => box.Text.StartsWith("Ctrl+A"));
            ctrlA.Checked = true;
            check("Individual UI selection is saved", form.CreateSettings(custom).EmacsShortcuts.ControlA);
            controlSide.SelectedIndex = 1;
            altSide.SelectedIndex = 2;
            initialMode.SelectedIndex = 0;
            check("Initial mode selector change is saved", form.CreateSettings(custom).EmacsInitialMode == EmacsInitialMode.Normal);
            check("Side selector changes are saved independently", form.CreateSettings(custom).EmacsControlSide == ModifierSideSelection.Right &&
                form.CreateSettings(custom).EmacsAltSide == ModifierSideSelection.Both);
            var ctrlD = shortcuts.Single(box => box.Text.StartsWith("Ctrl+D"));
            ctrlD.Checked = false;
            check("New delete shortcut can be disabled individually in UI", !form.CreateSettings(custom).EmacsShortcuts.ControlD &&
                form.CreateSettings(custom).EmacsShortcuts.ControlK && form.CreateSettings(custom).EmacsShortcuts.AltD);
            using var overlay = new ModeOverlayForm();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var parameters = (CreateParams)typeof(ModeOverlayForm).GetProperty("CreateParams", flags)!.GetValue(overlay)!;
            check("Mode overlay does not activate or appear on taskbar", !overlay.ShowInTaskbar &&
                (bool)typeof(ModeOverlayForm).GetProperty("ShowWithoutActivation", flags)!.GetValue(overlay)! &&
                (parameters.ExStyle & 0x08000000) != 0);
        }).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    private static bool ResetPreservesMode(EmacsKeyboardState state)
    {
        state.ResetTransient();
        return state.Active && !state.GestureActive;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
