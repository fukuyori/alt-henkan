namespace AltHenkan;

internal readonly record struct ControlModifierSnapshot(
    bool LeftTrackedDown,
    bool RightTrackedDown,
    bool AsyncAnyDown,
    bool AsyncLeftDown,
    bool AsyncRightDown)
{
    public bool AnyDown => LeftTrackedDown || RightTrackedDown || AsyncAnyDown;

    public ModifierSides Sides =>
        (LeftTrackedDown || AsyncLeftDown ? ModifierSides.Left : ModifierSides.None) |
        (RightTrackedDown || AsyncRightDown ? ModifierSides.Right : ModifierSides.None);
}
