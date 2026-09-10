using System.Runtime.InteropServices;

namespace AltHenkan;

internal sealed class AltInputService : IDisposable
{
    private const nuint OwnInputMarker = 0x414C5448;

    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly HashSet<uint> _nonAltKeysDown = [];
    private readonly AltState _leftAlt = new(AltSide.Left);
    private readonly AltState _rightAlt = new(AltSide.Right);
    private nint _keyboardHook;
    private nint _mouseHook;
    private AppSettings _settings;
    private bool _disposed;

    public event Action<int>? InputInjectionFailed;

    public AltInputService(AppSettings settings)
    {
        _settings = settings.Normalize();
        _keyboardCallback = KeyboardHookCallback;
        _mouseCallback = MouseHookCallback;

        var module = NativeMethods.GetModuleHandle(null);
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _keyboardCallback,
            module,
            0);
        if (_keyboardHook == 0)
        {
            throw new InvalidOperationException("キーボードフックを登録できませんでした。");
        }

        DiagnosticLog.Write($"Keyboard hook registered: 0x{_keyboardHook:X}.");

        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _mouseCallback,
            module,
            0);
        if (_mouseHook == 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
            throw new InvalidOperationException("マウスフックを登録できませんでした。");
        }

        DiagnosticLog.Write($"Mouse hook registered: 0x{_mouseHook:X}.");
    }

    public void UpdateSettings(AppSettings settings)
    {
        var wasEnabled = _settings.Enabled;
        _settings = settings.Normalize();

        if (wasEnabled && !_settings.Enabled)
        {
            ResetActiveStates();
        }
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0 || _disposed)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        if (data.ExtraInfo == OwnInputMarker)
        {
            DiagnosticLog.Write(
                $"Injected keyboard event observed: message=0x{unchecked((int)wParam):X4}, vk=0x{data.VirtualKeyCode:X2}, scan=0x{data.ScanCode:X2}, flags=0x{data.Flags:X2}.");
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        if (!_settings.Enabled)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var message = unchecked((int)wParam);
        var isKeyDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
        var isKeyUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
        if (!isKeyDown && !isKeyUp)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var isExtended = (data.Flags & NativeMethods.LlkhfExtended) != 0;
        if (data.VirtualKeyCode == NativeMethods.VkLMenu ||
            data.VirtualKeyCode == NativeMethods.VkMenu && !isExtended)
        {
            DiagnosticLog.Write(
                $"Left Alt {(isKeyDown ? "down" : "up")}: vk=0x{data.VirtualKeyCode:X2}, scan=0x{data.ScanCode:X2}, flags=0x{data.Flags:X2}.");
            return HandleAltEvent(_leftAlt, code, wParam, lParam, data, isKeyDown);
        }

        if (data.VirtualKeyCode == NativeMethods.VkRMenu ||
            data.VirtualKeyCode == NativeMethods.VkMenu && isExtended)
        {
            DiagnosticLog.Write(
                $"Right Alt {(isKeyDown ? "down" : "up")}: vk=0x{data.VirtualKeyCode:X2}, scan=0x{data.ScanCode:X2}, flags=0x{data.Flags:X2}.");
            return HandleAltEvent(_rightAlt, code, wParam, lParam, data, isKeyDown);
        }

        if (_leftAlt.IsDown || _rightAlt.IsDown)
        {
            PromotePendingAltsToNative();
        }

        if (isKeyDown)
        {
            _nonAltKeysDown.Add(data.VirtualKeyCode);
        }
        else
        {
            _nonAltKeysDown.Remove(data.VirtualKeyCode);
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private nint HandleAltEvent(
        AltState state,
        int code,
        nint wParam,
        nint lParam,
        NativeMethods.KbdLlHookStruct data,
        bool isKeyDown)
    {
        if (isKeyDown)
        {
            if (state.IsDown)
            {
                return state.OriginalDownPassed
                    ? NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam)
                    : 1;
            }

            state.Start(data.ScanCode, (data.Flags & NativeMethods.LlkhfExtended) != 0);

            var otherAlt = state.Side == AltSide.Left ? _rightAlt : _leftAlt;
            if (otherAlt.IsDown)
            {
                PromotePendingAltToNative(otherAlt);
                PromotePendingAltToNative(state);
                return 1;
            }

            if (IsAnyNonAltKeyDown())
            {
                state.OriginalDownPassed = true;
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            return 1;
        }

        if (!state.IsDown)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var heldMilliseconds = Environment.TickCount64 - state.DownAtMilliseconds;
        state.IsDown = false;

        if (state.OriginalDownPassed)
        {
            state.Reset();
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        if (state.NativeDownSent)
        {
            DiagnosticLog.Write($"{state.Side} Alt used with another input; sending native Alt up.");
            SendAlt(state, keyUp: true);
            state.Reset();
            return 1;
        }

        var longPressEnabled = state.Side == AltSide.Left
            ? _settings.LeftAltLongPressEnabled
            : _settings.RightAltLongPressEnabled;
        var action = AltPressDecision.Decide(
            heldMilliseconds,
            longPressEnabled,
            _settings.LongPressMilliseconds);

        if (action == AltPressAction.NativeAlt)
        {
            DiagnosticLog.Write(
                $"{state.Side} Alt held for {heldMilliseconds} ms; sending native Alt tap.");
            SendAltTap(state);
        }
        else
        {
            DiagnosticLog.Write(
                $"{state.Side} Alt held for {heldMilliseconds} ms; sending {(state.Side == AltSide.Left ? "IME off" : "IME on")} tap.");
            SendImeStateTap(state.Side);
        }

        state.Reset();
        return 1;
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && !_disposed && _settings.Enabled && IsMouseInteraction(unchecked((int)wParam)))
        {
            PromotePendingAltsToNative();
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void PromotePendingAltsToNative()
    {
        PromotePendingAltToNative(_leftAlt);
        PromotePendingAltToNative(_rightAlt);
    }

    private static bool IsMouseInteraction(int message)
    {
        return message is
            NativeMethods.WmLButtonDown or NativeMethods.WmLButtonUp or
            NativeMethods.WmRButtonDown or NativeMethods.WmRButtonUp or
            NativeMethods.WmMButtonDown or NativeMethods.WmMButtonUp or
            NativeMethods.WmXButtonDown or NativeMethods.WmXButtonUp or
            NativeMethods.WmMouseWheel or NativeMethods.WmMouseHWheel;
    }

    private void PromotePendingAltToNative(AltState state)
    {
        if (!state.IsDown || state.OriginalDownPassed || state.NativeDownSent)
        {
            return;
        }

        SendAlt(state, keyUp: false);
        state.NativeDownSent = true;
        DiagnosticLog.Write($"{state.Side} Alt used with another input; sent native Alt down.");
    }

    private bool IsAnyNonAltKeyDown()
    {
        return _nonAltKeysDown.Count > 0 ||
               IsVirtualKeyDown(NativeMethods.VkShift) ||
               IsVirtualKeyDown(NativeMethods.VkControl) ||
               IsVirtualKeyDown(NativeMethods.VkLWin) ||
               IsVirtualKeyDown(NativeMethods.VkRWin);
    }

    private static bool IsVirtualKeyDown(uint virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;
    }

    private void SendImeStateTap(AltSide side)
    {
        var virtualKey = side == AltSide.Left
            ? JapaneseImeKey.ImeOffVirtualKey
            : JapaneseImeKey.ImeOnVirtualKey;

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        var keyboardLayout = unchecked((nuint)NativeMethods.GetKeyboardLayout(foregroundThread));
        DiagnosticLog.Write(
            $"Foreground keyboard layout before injection: HKL=0x{keyboardLayout:X}, LANGID=0x{keyboardLayout & 0xFFFF:X4}.");

        SendKeyboardInputs(
            CreateVirtualKeyInput(virtualKey, keyUp: false),
            CreateVirtualKeyInput(virtualKey, keyUp: true));
    }

    private void SendAltTap(AltState state)
    {
        SendKeyboardInputs(
            CreateScanCodeInput(state.ScanCode, state.IsExtended, keyUp: false),
            CreateScanCodeInput(state.ScanCode, state.IsExtended, keyUp: true));
    }

    private void SendAlt(AltState state, bool keyUp)
    {
        SendKeyboardInputs(
            CreateScanCodeInput(state.ScanCode, state.IsExtended, keyUp));
    }

    private void SendKeyboardInputs(params NativeMethods.Input[] inputs)
    {
        if (NativeMethods.SendKeyboardInputs(inputs, out var errorCode))
        {
            DiagnosticLog.Write($"SendInput succeeded for {inputs.Length} keyboard event(s).");
        }
        else
        {
            DiagnosticLog.Write(
                $"SendInput failed for {inputs.Length} keyboard event(s); Windows error={errorCode}.");
            InputInjectionFailed?.Invoke(errorCode);
        }
    }

    // Sends by virtual key (KEYEVENTF_SCANCODE deliberately omitted) so the IME
    // receives VK_IME_ON / VK_IME_OFF regardless of the hardware layout.
    private static NativeMethods.Input CreateVirtualKeyInput(ushort virtualKey, bool keyUp)
    {
        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? NativeMethods.KeyeventfKeyUp : 0,
                    ExtraInfo = OwnInputMarker
                }
            }
        };
    }

    private static NativeMethods.Input CreateScanCodeInput(ushort scanCode, bool isExtended, bool keyUp)
    {
        var flags = NativeMethods.KeyeventfScanCode;
        if (isExtended)
        {
            flags |= NativeMethods.KeyeventfExtendedKey;
        }

        if (keyUp)
        {
            flags |= NativeMethods.KeyeventfKeyUp;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    ScanCode = scanCode,
                    Flags = flags,
                    ExtraInfo = OwnInputMarker
                }
            }
        };
    }

    private void ResetActiveStates()
    {
        foreach (var state in new[] { _leftAlt, _rightAlt })
        {
            if (state.IsDown && state.NativeDownSent)
            {
                SendAlt(state, keyUp: true);
            }

            state.Reset();
        }

        _nonAltKeysDown.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ResetActiveStates();
        _disposed = true;

        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }

        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }

        DiagnosticLog.Write("Hooks unregistered.");
    }

    private enum AltSide
    {
        Left,
        Right
    }

    private sealed class AltState(AltSide side)
    {
        public AltSide Side { get; } = side;

        public bool IsDown { get; set; }

        public bool OriginalDownPassed { get; set; }

        public bool NativeDownSent { get; set; }

        public long DownAtMilliseconds { get; set; }

        public ushort ScanCode { get; set; }

        public bool IsExtended { get; set; }

        public void Start(uint scanCode, bool isExtended)
        {
            IsDown = true;
            OriginalDownPassed = false;
            NativeDownSent = false;
            DownAtMilliseconds = Environment.TickCount64;
            ScanCode = checked((ushort)scanCode);
            IsExtended = isExtended;
        }

        public void Reset()
        {
            IsDown = false;
            OriginalDownPassed = false;
            NativeDownSent = false;
            DownAtMilliseconds = 0;
            ScanCode = 0;
            IsExtended = false;
        }
    }
}
