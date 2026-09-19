using AltHenkan;
using System.Text.Json;
using System.Windows.Forms;

internal static class EmacsExpansionTests
{
    public static void Run(Action<string, bool> check)
    {
        var defaults = new AppSettings();
        check("Both sides default to either key for existing settings", defaults.EmacsControlSide == ModifierSideSelection.Both && defaults.EmacsAltSide == ModifierSideSelection.Both);
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"EmacsEnabled\":true,\"EmacsShortcuts\":{\"ControlA\":false}}")!.Normalize();
        check("Legacy shortcut flags are retained and new operations default on", !legacy.EmacsShortcuts.ControlA && legacy.EmacsShortcuts.ControlD &&
            legacy.EmacsShortcuts.ControlK && legacy.EmacsShortcuts.AltLess && legacy.EmacsShortcuts.AltGreater && legacy.EmacsShortcuts.AltD);
        check("Legacy side selections default to both", legacy.EmacsControlSide == ModifierSideSelection.Both && legacy.EmacsAltSide == ModifierSideSelection.Both);
        check("Legacy settings default to normal initial mode", legacy.EmacsInitialMode == EmacsInitialMode.Normal && !legacy.InitialEmacsActive);
        var invalid = (defaults with { EmacsControlSide = (ModifierSideSelection)99, EmacsAltSide = (ModifierSideSelection)(-1) }).Normalize();
        check("Invalid side settings normalize to both", invalid.EmacsControlSide == ModifierSideSelection.Both && invalid.EmacsAltSide == ModifierSideSelection.Both);
        check("Invalid initial mode normalizes to normal", (defaults with { EmacsInitialMode = (EmacsInitialMode)99 }).Normalize().EmacsInitialMode == EmacsInitialMode.Normal);
        check("App-wide disabled setting overrides Emacs initial mode", !(defaults with
        {
            Enabled = false, EmacsEnabled = true, EmacsInitialMode = EmacsInitialMode.Emacs
        }).InitialEmacsActive);

        foreach (var binding in EmacsBindings.All)
        {
            var usesControl = (binding.Modifiers & NavigationModifiers.Control) != 0;
            foreach (var selection in Enum.GetValues<ModifierSideSelection>())
            foreach (var down in Enum.GetValues<ModifierSides>())
            {
                var filter = new EmacsSideFilter(usesControl ? down : ModifierSides.None, usesControl ? ModifierSides.None : down,
                    usesControl ? selection : ModifierSideSelection.Both, usesControl ? ModifierSideSelection.Both : selection);
                var expected = selection switch
                {
                    ModifierSideSelection.Left => down is ModifierSides.Left or ModifierSides.Both,
                    ModifierSideSelection.Right => down is ModifierSides.Right or ModifierSides.Both,
                    _ => down != ModifierSides.None
                };
                check($"{binding.Shortcut}: {selection} accepts {down} correctly", (EmacsBindings.Resolve(binding.SourceKey, binding.Modifiers,
                    defaults.EmacsShortcuts, filter) is not null) == expected);
            }
            var unrelatedSide = new EmacsSideFilter(usesControl ? ModifierSides.Left : ModifierSides.None, usesControl ? ModifierSides.None : ModifierSides.Right,
                ModifierSideSelection.Left, ModifierSideSelection.Right);
            check($"{binding.Shortcut}: other modifier side setting is independent", EmacsBindings.Resolve(binding.SourceKey, binding.Modifiers,
                defaults.EmacsShortcuts, unrelatedSide) == binding);

            var state = ActiveState();
            check($"{binding.Shortcut}: captures down", state.HandleKey(binding.SourceKey, true, true, binding.Modifiers,
                defaults.EmacsShortcuts, out var output) && output == binding);
            check($"{binding.Shortcut}: does not leak a held key when side setting changes", state.HandleKey(binding.SourceKey, true, true, binding.Modifiers,
                defaults.EmacsShortcuts, out output, new(ModifierSides.Left, ModifierSides.Left, ModifierSideSelection.Right, ModifierSideSelection.Right)) && output is null);
            check($"{binding.Shortcut}: release remains paired after side change", state.HandleKey(binding.SourceKey, false, true, NavigationModifiers.None,
                defaults.EmacsShortcuts, out output) && output is null);
        }

        var rejectedState = ActiveState();
        var leftOnly = new EmacsSideFilter(ModifierSides.Right, ModifierSides.None, ModifierSideSelection.Left, ModifierSideSelection.Both);
        check("Rejected Ctrl side passes native source down", !rejectedState.HandleKey((uint)Keys.D, true, true, NavigationModifiers.Control,
            defaults.EmacsShortcuts, out var rejectedOutput, leftOnly) && rejectedOutput is null);
        check("Rejected Ctrl side passes native source release", !rejectedState.HandleKey((uint)Keys.D, false, true, NavigationModifiers.None,
            defaults.EmacsShortcuts, out _));
        check("Alt comma without Shift is not an angle shortcut", EmacsBindings.Resolve((uint)Keys.Oemcomma, NavigationModifiers.Alt, defaults.EmacsShortcuts) is null);
        check("Alt period without Shift is not an angle shortcut", EmacsBindings.Resolve((uint)Keys.OemPeriod, NavigationModifiers.Alt, defaults.EmacsShortcuts) is null);
        check("Extra Ctrl blocks an angle shortcut", EmacsBindings.Resolve((uint)Keys.Oemcomma,
            NavigationModifiers.Alt | NavigationModifiers.Shift | NavigationModifiers.Control, defaults.EmacsShortcuts) is null);

        var less = Binding(EmacsShortcut.AltLess);
        var greater = Binding(EmacsShortcut.AltGreater);
        check("Alt+< releases Shift and sends Ctrl+Home, not a selection", NavigationInputPlan.Create(less, [(ushort)Keys.LShiftKey]).SequenceEqual(new[]
        {
            Stroke(Keys.LShiftKey, true), Stroke(Keys.LControlKey, false), Stroke(Keys.Home, false), Stroke(Keys.Home, true),
            Stroke(Keys.LControlKey, true), Stroke(Keys.LShiftKey, false)
        }));
        check("Alt+> restores right Alt and right Shift", NavigationInputPlan.Create(greater, [(ushort)Keys.RShiftKey, (ushort)Keys.RMenu]).SequenceEqual(new[]
        {
            Stroke(Keys.RShiftKey, true), Stroke(Keys.RMenu, true), Stroke(Keys.LControlKey, false), Stroke(Keys.End, false), Stroke(Keys.End, true),
            Stroke(Keys.LControlKey, true), Stroke(Keys.RShiftKey, false), Stroke(Keys.RMenu, false)
        }));
        check("Ctrl+D sends plain Delete after releasing native Ctrl", NavigationInputPlan.Create(Binding(EmacsShortcut.ControlD), [(ushort)Keys.RControlKey]).SequenceEqual(new[]
        {
            Stroke(Keys.RControlKey, true), Stroke(Keys.Delete, false), Stroke(Keys.Delete, true), Stroke(Keys.RControlKey, false)
        }));
        check("Alt+D sends Ctrl+Delete without copy or cut", NavigationInputPlan.Create(Binding(EmacsShortcut.AltD), []).SequenceEqual(new[]
        {
            Stroke(Keys.LControlKey, false), Stroke(Keys.Delete, false), Stroke(Keys.Delete, true), Stroke(Keys.LControlKey, true)
        }));
        var ctrlK = Binding(EmacsShortcut.ControlK);
        check("Ctrl+K has the delete-to-line-end action", ctrlK.Action == EmacsInputAction.DeleteToLineEnd);
        check("Ctrl+K selects to End then deletes and restores native Ctrl", NavigationInputPlan.Create(ctrlK, [(ushort)Keys.LControlKey]).SequenceEqual(new[]
        {
            Stroke(Keys.LControlKey, true), Stroke(Keys.LShiftKey, false), Stroke(Keys.End, false), Stroke(Keys.End, true),
            Stroke(Keys.LShiftKey, true), Stroke(Keys.Delete, false), Stroke(Keys.Delete, true), Stroke(Keys.LControlKey, false)
        }));
        foreach (var shortcut in new[] { EmacsShortcut.ControlD, EmacsShortcut.AltD, EmacsShortcut.ControlK })
        {
            check($"{shortcut}: deletion does not send copy, cut or paste", NavigationInputPlan.Create(Binding(shortcut), []).All(stroke =>
                stroke.VirtualKey != (ushort)Keys.C && stroke.VirtualKey != (ushort)Keys.X && stroke.VirtualKey != (ushort)Keys.V));
        }
        check("Enabled angles defer pending Alt+Shift on selected side", EmacsBindings.ShouldDeferAltShift(defaults, ModifierSides.Left));
        check("Disabled angles preserve native Alt+Shift handling", !EmacsBindings.ShouldDeferAltShift(defaults with
        {
            EmacsShortcuts = defaults.EmacsShortcuts with { AltLess = false, AltGreater = false }
        }, ModifierSides.Left));
        check("Rejected Alt side does not defer native Alt+Shift", !EmacsBindings.ShouldDeferAltShift(defaults with { EmacsAltSide = ModifierSideSelection.Right }, ModifierSides.Left));
    }

    private static EmacsKeyboardState ActiveState()
    {
        var state = new EmacsKeyboardState();
        state.HandleCapsLock(true, true, out _);
        state.HandleCapsLock(false, true, out _);
        return state;
    }

    private static EmacsBinding Binding(EmacsShortcut shortcut) => EmacsBindings.All.Single(binding => binding.Shortcut == shortcut);
    private static NavigationKeyStroke Stroke(Keys key, bool up) => new((ushort)key, up);
}
