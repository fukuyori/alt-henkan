namespace AltHenkan;

internal enum EmacsInitialMode { Normal, Emacs }

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

    public EmacsInitialMode EmacsInitialMode { get; init; } = EmacsInitialMode.Normal;

    public bool ShowModeChangeOverlay { get; init; } = true;

    public EmacsShortcutSettings EmacsShortcuts { get; init; } = new();

    public ModifierSideSelection EmacsControlSide { get; init; } = ModifierSideSelection.Both;

    public ModifierSideSelection EmacsAltSide { get; init; } = ModifierSideSelection.Both;

    internal bool InitialEmacsActive => Enabled && EmacsEnabled && EmacsInitialMode == EmacsInitialMode.Emacs;

    public AppSettings Normalize()
    {
        return this with
        {
            EmacsShortcuts = EmacsShortcuts ?? new EmacsShortcutSettings(),
            EmacsInitialMode = Enum.IsDefined(EmacsInitialMode) ? EmacsInitialMode : EmacsInitialMode.Normal,
            EmacsControlSide = Enum.IsDefined(EmacsControlSide) ? EmacsControlSide : ModifierSideSelection.Both,
            EmacsAltSide = Enum.IsDefined(EmacsAltSide) ? EmacsAltSide : ModifierSideSelection.Both,
            LongPressMilliseconds = Math.Clamp(
                LongPressMilliseconds,
                MinimumLongPressMilliseconds,
                MaximumLongPressMilliseconds)
        };
    }
}
