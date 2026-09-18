namespace AltHenkan;

internal readonly record struct NavigationKeyStroke(ushort VirtualKey, bool KeyUp);

internal static class NavigationInputPlan
{
    public static IReadOnlyList<NavigationKeyStroke> Create(
        EmacsBinding binding, IReadOnlyList<ushort> nativeModifiersDown)
    {
        var result = new List<NavigationKeyStroke>();
        // Release native Ctrl/Alt/Shift so they do not modify the translated operation.
        foreach (var modifier in nativeModifiersDown)
        {
            result.Add(new(modifier, true));
        }
        if (binding.TargetControl)
        {
            result.Add(new((ushort)Keys.LControlKey, false));
        }
        if (binding.Action == EmacsInputAction.DeleteToLineEnd)
        {
            // At line end Shift+End selects nothing, so Delete joins the next line.
            result.Add(new((ushort)Keys.LShiftKey, false));
            result.Add(new((ushort)Keys.End, false));
            result.Add(new((ushort)Keys.End, true));
            result.Add(new((ushort)Keys.LShiftKey, true));
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
