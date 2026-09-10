namespace AltHenkan;

internal static class JapaneseImeKey
{
    // Virtual-key codes that switch the IME state directly. Supported by the
    // Microsoft IME on Windows 10 version 1903 and later. They are sent by
    // virtual key only: with KEYEVENTF_SCANCODE alone, Windows derives the
    // virtual key from the active hardware layout, and on a US 101/102 layout
    // no scan code maps to these keys.
    public const ushort ImeOnVirtualKey = 0x16;

    public const ushort ImeOffVirtualKey = 0x1A;
}
