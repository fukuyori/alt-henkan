namespace AltHenkan;

internal static class KeyHoldPolicy
{
    // Windows SDK Ime.h: VK_DBE_SBCSCHAR / VK_DBE_DBCSCHAR.
    // WinUser.h also assigns these values to VK_OEM_AUTO / VK_OEM_ENLW.
    // Limit the IME interpretation to Japanese layouts; do not discard OEM keys globally.
    public const uint ImeSingleByte = 0xF3;
    public const uint ImeDoubleByte = 0xF4;

    public static bool IsWidthKeyCode(uint key) => key is ImeSingleByte or ImeDoubleByte;

    public static bool IsImeWidthCommand(uint key, nuint keyboardLayout) =>
        (keyboardLayout & 0xFFFF) == 0x0411 && IsWidthKeyCode(key);

    public static void PruneNonAltKeys(HashSet<uint> trackedKeys, nuint keyboardLayout,
        Func<uint, bool> isKeyDown)
    {
        trackedKeys.RemoveWhere(key => IsImeWidthCommand(key, keyboardLayout) || !isKeyDown(key));
    }

    public static bool AnyKeyOrMouseButtonDown(nuint keyboardLayout, Func<uint, bool> isKeyDown)
    {
        for (uint key = 1; key < 255; key++)
        {
            if (!IsImeWidthCommand(key, keyboardLayout) && isKeyDown(key)) return true;
        }
        return false;
    }
}
