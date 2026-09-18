namespace AltHenkan;

internal sealed record AppSettings
{
    internal const int DefaultLongPressMilliseconds = 600;
    internal const int MinimumLongPressMilliseconds = 200;
    internal const int MaximumLongPressMilliseconds = 3000;

    public bool Enabled { get; init; } = true;

    public bool LeftAltLongPressEnabled { get; init; } = true;

    public bool RightAltLongPressEnabled { get; init; } = true;

    public int LongPressMilliseconds { get; init; } = DefaultLongPressMilliseconds;

    public bool EmacsEnabled { get; init; }

    public bool ShowModeChangeOverlay { get; init; } = true;

    public EmacsShortcutSettings EmacsShortcuts { get; init; } = new();

    public ModifierSideSelection EmacsControlSide { get; init; } = ModifierSideSelection.Both;

    public ModifierSideSelection EmacsAltSide { get; init; } = ModifierSideSelection.Both;

    public AppSettings Normalize()
    {
        return this with
        {
            EmacsShortcuts = EmacsShortcuts ?? new EmacsShortcutSettings(),
            EmacsControlSide = Enum.IsDefined(EmacsControlSide) ? EmacsControlSide : ModifierSideSelection.Both,
            EmacsAltSide = Enum.IsDefined(EmacsAltSide) ? EmacsAltSide : ModifierSideSelection.Both,
            LongPressMilliseconds = Math.Clamp(
                LongPressMilliseconds,
                MinimumLongPressMilliseconds,
                MaximumLongPressMilliseconds)
        };
    }
}
