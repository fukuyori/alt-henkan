namespace AltHenkan;

internal readonly record struct InputSendResult(uint Sent, int Error);
internal readonly record struct InputForeground(nint Window, uint Process, uint Thread);
internal readonly record struct InputModifierState(bool Reliable, nint Foreground,
    bool LeftAltDown, bool RightAltDown, bool LeftControlDown, bool RightControlDown);

// Native boundary also lets tests exercise real handlers without installing hooks or injecting input.
internal class InputPlatform
{
    public virtual long MonotonicMilliseconds => Environment.TickCount64;
    public virtual InputModifierState ReadModifierState()
    {
        var reliable = AsyncKeyStateAccess.TryReadModifiers(out var foreground, out var leftAlt, out var rightAlt,
            out var leftControl, out var rightControl);
        return new(reliable, foreground, leftAlt, rightAlt, leftControl, rightControl);
    }
    public virtual InputSendResult Send(NativeMethods.Input[] inputs) => NativeMethods.SendKeyboardInputsDetailed(inputs);
    public virtual bool IsDown(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
    public virtual nint NextKeyboard(nint hook, int code, nint wParam, nint lParam) =>
        NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    public virtual nint NextMouse(nint hook, int code, nint wParam, nint lParam) =>
        NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    public virtual InputForeground Foreground()
    {
        var window = NativeMethods.GetForegroundWindow();
        var thread = NativeMethods.GetWindowThreadProcessId(window, out var process);
        return new(window, process, thread);
    }
}
