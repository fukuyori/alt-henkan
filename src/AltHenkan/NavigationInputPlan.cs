namespace AltHenkan;

internal readonly record struct NavigationKeyStroke(ushort VirtualKey, bool KeyUp);

internal static class NavigationInputPlan
{
    public static IReadOnlyList<NavigationKeyStroke> Create(
        EmacsBinding binding, IReadOnlyList<ushort> nativeModifiersDown)
    {
        var result = new List<NavigationKeyStroke>();
        // Ctrl/Alt must not modify the translated arrow (Ctrl+B is Left, not Ctrl+Left).
        foreach (var modifier in nativeModifiersDown)
        {
            result.Add(new(modifier, true));
        }
        if (binding.TargetControl)
        {
            result.Add(new((ushort)Keys.LControlKey, false));
        }
        result.Add(new(binding.TargetKey, false));
        result.Add(new(binding.TargetKey, true));
        if (binding.TargetControl)
        {
            result.Add(new((ushort)Keys.LControlKey, true));
        }
        foreach (var modifier in nativeModifiersDown)
        {
            result.Add(new(modifier, false));
        }
        return result;
    }
}
