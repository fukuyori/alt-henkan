namespace AltHenkan;

internal readonly record struct ControlModifierSnapshot(
    bool LeftTrackedDown,
    bool RightTrackedDown,
    bool AsyncAnyDown,
    bool AsyncLeftDown,
    bool AsyncRightDown,
    bool LeftNativeVisible = false,
    bool RightNativeVisible = false)
{
    // Hidden Ctrl is intentionally absent from Windows state. A visible Ctrl,
    // however, must not stay active solely because a key-up was missed.
    public bool AnyDown => Sides != ModifierSides.None || AsyncAnyDown;

    public ModifierSides Sides =>
        (AsyncLeftDown || LeftTrackedDown && !LeftNativeVisible ? ModifierSides.Left : ModifierSides.None) |
        (AsyncRightDown || RightTrackedDown && !RightNativeVisible ? ModifierSides.Right : ModifierSides.None);
}
