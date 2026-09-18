namespace AltHenkan;

internal enum ModifierSideSelection { Both, Left, Right }

[Flags]
internal enum ModifierSides { None = 0, Left = 1, Right = 2, Both = Left | Right }

internal sealed record EmacsSideFilter(
    ModifierSides ControlDown, ModifierSides AltDown,
    ModifierSideSelection ControlSelection, ModifierSideSelection AltSelection)
{
    public bool Matches(NavigationModifiers modifiers) =>
        (!modifiers.HasFlag(NavigationModifiers.Control) || Allows(ControlDown, ControlSelection)) &&
        (!modifiers.HasFlag(NavigationModifiers.Alt) || Allows(AltDown, AltSelection));

    public static bool Allows(ModifierSides down, ModifierSideSelection selection) => selection switch
    {
        ModifierSideSelection.Left => (down & ModifierSides.Left) != 0,
        ModifierSideSelection.Right => (down & ModifierSides.Right) != 0,
        _ => down != ModifierSides.None
    };
}
