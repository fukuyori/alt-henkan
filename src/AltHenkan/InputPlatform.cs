namespace AltHenkan;

internal readonly record struct InputSendResult(uint Sent, int Error);
internal readonly record struct InputForeground(nint Window, uint Process, uint Thread);

// Native boundary also lets tests exercise real handlers without installing hooks or injecting input.
internal class InputPlatform
{
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
