namespace AltHenkan;

internal enum AltPressAction
{
    Conversion,
    NativeAlt
}

internal static class AltPressDecision
{
    public static AltPressAction Decide(
        long heldMilliseconds,
        bool longPressEnabled,
        int longPressMilliseconds)
    {
        return longPressEnabled && heldMilliseconds >= longPressMilliseconds
            ? AltPressAction.NativeAlt
            : AltPressAction.Conversion;
    }
}

