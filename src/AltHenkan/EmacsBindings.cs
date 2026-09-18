namespace AltHenkan;

internal enum EmacsShortcut { ControlB, ControlF, ControlP, ControlN, ControlA, ControlE, AltB, AltF }

[Flags]
internal enum NavigationModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }

internal sealed record EmacsShortcutSettings
{
    public bool ControlB { get; init; } = true;
    public bool ControlF { get; init; } = true;
    public bool ControlP { get; init; } = true;
    public bool ControlN { get; init; } = true;
    public bool ControlA { get; init; } = true;
    public bool ControlE { get; init; } = true;
    public bool AltB { get; init; } = true;
    public bool AltF { get; init; } = true;

    public bool IsEnabled(EmacsShortcut shortcut) => shortcut switch
    {
        EmacsShortcut.ControlB => ControlB,
        EmacsShortcut.ControlF => ControlF,
        EmacsShortcut.ControlP => ControlP,
        EmacsShortcut.ControlN => ControlN,
        EmacsShortcut.ControlA => ControlA,
        EmacsShortcut.ControlE => ControlE,
        EmacsShortcut.AltB => AltB,
        EmacsShortcut.AltF => AltF,
        _ => false
    };

    public EmacsShortcutSettings WithEnabled(EmacsShortcut shortcut, bool enabled) => shortcut switch
    {
        EmacsShortcut.ControlB => this with { ControlB = enabled },
        EmacsShortcut.ControlF => this with { ControlF = enabled },
        EmacsShortcut.ControlP => this with { ControlP = enabled },
        EmacsShortcut.ControlN => this with { ControlN = enabled },
        EmacsShortcut.ControlA => this with { ControlA = enabled },
        EmacsShortcut.ControlE => this with { ControlE = enabled },
        EmacsShortcut.AltB => this with { AltB = enabled },
        EmacsShortcut.AltF => this with { AltF = enabled },
        _ => this
    };
}

internal sealed record EmacsBinding(
    EmacsShortcut Shortcut, string Label, uint SourceKey, NavigationModifiers Modifiers,
    ushort TargetKey, bool TargetControl = false);

internal static class EmacsBindings
{
    public static bool IsSourceKey(uint key) => key is (uint)Keys.B or (uint)Keys.F or
        (uint)Keys.P or (uint)Keys.N or (uint)Keys.A or (uint)Keys.E;

    public static IReadOnlyList<EmacsBinding> All { get; } = Array.AsReadOnly<EmacsBinding>([
        new(EmacsShortcut.ControlB, "Ctrl+B：左へ移動", (uint)Keys.B, NavigationModifiers.Control, (ushort)Keys.Left),
        new(EmacsShortcut.ControlF, "Ctrl+F：右へ移動", (uint)Keys.F, NavigationModifiers.Control, (ushort)Keys.Right),
        new(EmacsShortcut.ControlP, "Ctrl+P：上へ移動", (uint)Keys.P, NavigationModifiers.Control, (ushort)Keys.Up),
        new(EmacsShortcut.ControlN, "Ctrl+N：下へ移動", (uint)Keys.N, NavigationModifiers.Control, (ushort)Keys.Down),
        new(EmacsShortcut.ControlA, "Ctrl+A：行頭へ移動", (uint)Keys.A, NavigationModifiers.Control, (ushort)Keys.Home),
        new(EmacsShortcut.ControlE, "Ctrl+E：行末へ移動", (uint)Keys.E, NavigationModifiers.Control, (ushort)Keys.End),
        new(EmacsShortcut.AltB, "Alt+B：前の単語へ移動", (uint)Keys.B, NavigationModifiers.Alt, (ushort)Keys.Left, true),
        new(EmacsShortcut.AltF, "Alt+F：次の単語へ移動", (uint)Keys.F, NavigationModifiers.Alt, (ushort)Keys.Right, true)
    ]);

    public static EmacsBinding? Resolve(uint key, NavigationModifiers modifiers, EmacsShortcutSettings settings)
        => All.FirstOrDefault(binding => binding.SourceKey == key && binding.Modifiers == modifiers &&
            settings.IsEnabled(binding.Shortcut));
}
