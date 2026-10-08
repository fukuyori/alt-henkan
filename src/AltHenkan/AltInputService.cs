using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace AltHenkan;

internal sealed class AltInputService : IDisposable
{
    private const nuint OwnInputMarker = 0x414C5448;

    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly HashSet<uint> _nonAltKeysDown = [];
    private readonly AltState _leftAlt = new(AltSide.Left);
    private readonly AltState _rightAlt = new(AltSide.Right);
    private readonly DeferredControlState _leftControl = new(ModifierSides.Left);
    private readonly DeferredControlState _rightControl = new(ModifierSides.Right);
    private readonly EmacsKeyboardState _emacsState = new();
    private readonly bool _initialNativeCapsLockOn;
    private readonly DedicatedMessageLoop _hookThread;
    private System.Windows.Forms.Timer? _maintenanceTimer;
    private nint _keyboardHook;
    private nint _mouseHook;
    private AppSettings _settings;
    private volatile bool _disposed;
    private bool _refreshRequested;
    private long _lastRefreshAtMilliseconds;
    private int _hookGeneration;
    private long _keyboardCallbackCount;
    private long _mouseCallbackCount;
    private long _suppressedKeyboardEventCount;
    private long _inputInjectionFailures;
    private long _lastStatusAtMilliseconds;
    private long _imeWidthCommandEvents;

    public event Action<int>? InputInjectionFailed;
    public event Action<bool>? EmacsModeChanged;

    public AltInputService(AppSettings settings)
    {
        _settings = settings.Normalize();
        _emacsState.SetActive(_settings.InitialEmacsActive);
        _initialNativeCapsLockOn = (NativeMethods.GetKeyState((int)NativeMethods.VkCapsLock) & 1) != 0;
        _keyboardCallback = KeyboardHookCallback;
        _mouseCallback = MouseHookCallback;

        _hookThread = new DedicatedMessageLoop(InitializeHookThread, CleanupHookThread);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void InitializeHookThread()
    {
        DiagnosticLog.Write($"Dedicated hook thread started: managed thread {Environment.CurrentManagedThreadId}.");
        DiagnosticLog.Write($"Initial keyboard mode: {(_emacsState.Active ? "Emacs" : "Normal")}.");
        RegisterHooks();
        _lastStatusAtMilliseconds = Environment.TickCount64;
        WriteHookStatus("startup");
        EnsureNativeCapsLockOff(_initialNativeCapsLockOn);
        _maintenanceTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _maintenanceTimer.Tick += (_, _) => MaintainHooks();
        _maintenanceTimer.Start();
    }

    private void RegisterHooks()
    {
        var module = NativeMethods.GetModuleHandle(null);
        var keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _keyboardCallback,
            module,
            0);
        if (keyboardHook == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "キーボードフックを登録できませんでした。");
        }

        var mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl,
            _mouseCallback,
            module,
            0);
        if (mouseHook == 0)
        {
            var errorCode = Marshal.GetLastWin32Error();
            NativeMethods.UnhookWindowsHookEx(keyboardHook);
            throw new Win32Exception(errorCode, "マウスフックを登録できませんでした。");
        }

        // Install replacements first: a failed renewal must not discard a working pair.
        UnregisterHooks();
        _keyboardHook = keyboardHook;
        _mouseHook = mouseHook;
        _lastRefreshAtMilliseconds = Environment.TickCount64;
        _refreshRequested = false;
        _hookGeneration++;
        DiagnosticLog.Write(
            $"Hooks registered: generation={_hookGeneration}, keyboard=0x{_keyboardHook:X}, mouse=0x{_mouseHook:X}, keyboard callbacks={_keyboardCallbackCount}, mouse callbacks={_mouseCallbackCount}.");
    }

    public void UpdateSettings(AppSettings settings)
    {
        var normalized = settings.Normalize();
        // GetKeyState is queue-relative: read on the UI caller, not the hook-only thread.
        var nativeCapsLockOn = (NativeMethods.GetKeyState((int)NativeMethods.VkCapsLock) & 1) != 0;
        PostToHookThread(() =>
        {
            var wasEnabled = _settings.Enabled;
            var wasEmacsEnabled = _settings.Enabled && _settings.EmacsEnabled;
            _settings = normalized;
            WriteHookStatus("settings changed");

            if (!_settings.Enabled || !_settings.EmacsEnabled)
            {
                if (_emacsState.Disable())
                {
                    NotifyEmacsModeChanged();
                }
            }
            else if (!wasEmacsEnabled)
            {
                _emacsState.SetActive(_settings.EmacsInitialMode == EmacsInitialMode.Emacs);
                EnsureNativeCapsLockOff(nativeCapsLockOn);
                NotifyEmacsModeChanged();
            }

            if (wasEnabled && !_settings.Enabled)
            {
                ResetActiveStates(resetEmacsTransient: false);
            }
            else if (!wasEnabled && _settings.Enabled)
            {
                RequestRefresh("enabled");
            }
        });
    }

    private async void PostToHookThread(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _hookThread.InvokeAsync(action).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write($"Hook-thread command failed: {exception}");
        }
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        var startedAt = Stopwatch.GetTimestamp();
        _keyboardCallbackCount++;
        try
        {
            return KeyboardHookCore(code, wParam, lParam);
        }
        catch (Exception exception)
        {
            _refreshRequested = true;
            DiagnosticLog.Write($"Keyboard callback failed; renewal requested: {exception}");
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed.TotalMilliseconds >= 100)
            {
                DiagnosticLog.Write($"Slow keyboard callback: {elapsed.TotalMilliseconds:F1} ms.");
            }
        }
    }

    private nint KeyboardHookCore(int code, nint wParam, nint lParam)
    {
        if (code < 0 || _disposed)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        if (data.ExtraInfo == OwnInputMarker)
        {
            if (DiagnosticLog.VerboseEnabled) DiagnosticLog.WriteVerbose(
                $"Injected keyboard event observed: message=0x{unchecked((int)wParam):X4}, vk=0x{data.VirtualKeyCode:X2}, scan=0x{data.ScanCode:X2}, flags=0x{data.Flags:X2}.");
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var message = unchecked((int)wParam);
        var isKeyDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
        var isKeyUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
        if (!isKeyDown && !isKeyUp)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        // These IME commands can remain set in GetAsyncKeyState. Pass them through,
        // but do not retain them as held keys or promote pending Alt/Ctrl gestures.
        if (KeyHoldPolicy.IsWidthKeyCode(data.VirtualKeyCode) &&
            KeyHoldPolicy.IsImeWidthCommand(data.VirtualKeyCode, GetForegroundKeyboardLayout()))
        {
            _nonAltKeysDown.Remove(data.VirtualKeyCode);
            _imeWidthCommandEvents++;
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var isExtended = (data.Flags & NativeMethods.LlkhfExtended) != 0;

        var emacsEnabled = _settings.Enabled && _settings.EmacsEnabled;
        if (data.VirtualKeyCode == NativeMethods.VkCapsLock &&
            _emacsState.HandleCapsLock(isKeyDown, emacsEnabled, out var modeChanged))
        {
            ConsumePendingAltGestures();
            if (modeChanged)
            {
                NotifyEmacsModeChanged();
            }
            return SuppressKeyboardEvent();
        }

        if (data.VirtualKeyCode == NativeMethods.VkLControl ||
            data.VirtualKeyCode == NativeMethods.VkControl && !isExtended)
        {
            return HandleControlEvent(_leftControl, code, wParam, lParam, data, isKeyDown);
        }

        if (data.VirtualKeyCode == NativeMethods.VkRControl ||
            data.VirtualKeyCode == NativeMethods.VkControl && isExtended)
        {
            return HandleControlEvent(_rightControl, code, wParam, lParam, data, isKeyDown);
        }

        var modifiers = isKeyDown && emacsEnabled && _emacsState.Active && EmacsBindings.IsSourceKey(data.VirtualKeyCode)
            ? GetNavigationModifiers() : NavigationModifiers.None;
        if (_emacsState.HandleKey(data.VirtualKeyCode, isKeyDown, emacsEnabled,
            modifiers, _settings.EmacsShortcuts, out var binding,
            modifiers == NavigationModifiers.None ? null : GetEmacsSideFilter()))
        {
            if (binding is not null)
            {
                if ((binding.Modifiers & NavigationModifiers.Alt) != 0)
                {
                    ConsumePendingAltGestures();
                }
                if ((binding.Modifiers & NavigationModifiers.Control) != 0)
                {
                    ConsumePendingControlGestures();
                }
                SendNavigation(binding);
            }
            return SuppressKeyboardEvent();
        }

        if (!_settings.Enabled)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        PromotePendingControlsToNative();

        if (data.VirtualKeyCode == NativeMethods.VkLMenu ||
            data.VirtualKeyCode == NativeMethods.VkMenu && !isExtended)
        {
            DiagnosticLog.WriteVerbose(
                $"Left Alt {(isKeyDown ? "down" : "up")}: vk=0x{data.VirtualKeyCode:X2}, scan=0x{data.ScanCode:X2}, flags=0x{data.Flags:X2}.");
            return HandleAltEvent(_leftAlt, code, wParam, lParam, data, isKeyDown);
        }

        if (data.VirtualKeyCode == NativeMethods.VkRMenu ||
            data.VirtualKeyCode == NativeMethods.VkMenu && isExtended)
        {
            DiagnosticLog.WriteVerbose(
                $"Right Alt {(isKeyDown ? "down" : "up")}: vk=0x{data.VirtualKeyCode:X2}, scan=0x{data.ScanCode:X2}, flags=0x{data.Flags:X2}.");
            return HandleAltEvent(_rightAlt, code, wParam, lParam, data, isKeyDown);
        }

        if (_leftAlt.IsDown || _rightAlt.IsDown)
        {
            if (IsShiftKey(data.VirtualKeyCode) && isKeyDown && CanDeferAltShift())
            {
                // Alt+< / Alt+> need Shift. Keep pending Alt hidden until the next key.
                MarkDeferredShiftChord();
            }
            else if (!(IsShiftKey(data.VirtualKeyCode) && isKeyUp &&
                (_leftAlt.ConsumedByEmacs || _rightAlt.ConsumedByEmacs)))
            {
                PromotePendingAltsToNative();
            }
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

    private nint HandleControlEvent(
        DeferredControlState state,
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
                    : SuppressKeyboardEvent();
            }

            state.Start(data.ScanCode, (data.Flags & NativeMethods.LlkhfExtended) != 0);
            if (!CanDeferControl(state) || IsAnyOtherInputDownForControl())
            {
                state.OriginalDownPassed = true;
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            var otherControl = state.Side == ModifierSides.Left ? _rightControl : _leftControl;
            if (otherControl.IsDown)
            {
                PromotePendingControlToNative(otherControl);
                PromotePendingControlToNative(state);
            }
            return SuppressKeyboardEvent();
        }

        if (!state.IsDown)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        state.IsDown = false;
        if (state.OriginalDownPassed)
        {
            state.Reset();
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        if (state.NativeDownSent)
        {
            SendControl(state, keyUp: true);
            state.Reset();
            return SuppressKeyboardEvent();
        }

        if (!state.ConsumedByEmacs)
        {
            SendControl(state, keyUp: false);
            SendControl(state, keyUp: true);
        }
        state.Reset();
        return SuppressKeyboardEvent();
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
                    : SuppressKeyboardEvent();
            }

            state.Start(data.ScanCode, (data.Flags & NativeMethods.LlkhfExtended) != 0);

            var otherAlt = state.Side == AltSide.Left ? _rightAlt : _leftAlt;
            if (otherAlt.IsDown)
            {
                PromotePendingAltToNative(otherAlt);
                PromotePendingAltToNative(state);
                return SuppressKeyboardEvent();
            }

            if (IsAnyNonAltKeyDown())
            {
                if (IsVirtualKeyDown(NativeMethods.VkShift) && CanDeferAltShift() &&
                    !_emacsState.GestureActive && _nonAltKeysDown.All(IsShiftKey))
                {
                    state.DeferredShiftChord = true;
                }
                else
                {
                    state.OriginalDownPassed = true;
                    return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }
            }

            return SuppressKeyboardEvent();
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
            DiagnosticLog.WriteVerbose($"{state.Side} Alt used with another input; sending native Alt up.");
            SendAlt(state, keyUp: true);
            state.Reset();
            return SuppressKeyboardEvent();
        }

        if (state.ConsumedByEmacs)
        {
            state.Reset();
            return SuppressKeyboardEvent();
        }

        if (state.DeferredShiftChord)
        {
            // No angle shortcut followed: preserve the native Alt+Shift chord.
            SendAltTap(state);
            state.Reset();
            return SuppressKeyboardEvent();
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
            DiagnosticLog.WriteVerbose(
                $"{state.Side} Alt held for {heldMilliseconds} ms; sending native Alt tap.");
            SendAltTap(state);
        }
        else
        {
            DiagnosticLog.WriteVerbose(
                $"{state.Side} Alt held for {heldMilliseconds} ms; sending {(state.Side == AltSide.Left ? "IME off" : "IME on")} tap.");
            SendImeStateTap(state.Side);
        }

        state.Reset();
        return SuppressKeyboardEvent();
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        var startedAt = Stopwatch.GetTimestamp();
        _mouseCallbackCount++;
        try
        {
            return MouseHookCore(code, wParam, lParam);
        }
        catch (Exception exception)
        {
            _refreshRequested = true;
            DiagnosticLog.Write($"Mouse callback failed; renewal requested: {exception}");
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed.TotalMilliseconds >= 100)
            {
                DiagnosticLog.Write($"Slow mouse callback: {elapsed.TotalMilliseconds:F1} ms.");
            }
        }
    }

    private nint MouseHookCore(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && !_disposed && _settings.Enabled && IsMouseInteraction(unchecked((int)wParam)))
        {
            PromotePendingControlsToNative();
            PromotePendingAltsToNative();
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void PromotePendingAltsToNative()
    {
        PromotePendingAltToNative(_leftAlt);
        PromotePendingAltToNative(_rightAlt);
    }

    private void PromotePendingControlsToNative()
    {
        PromotePendingControlToNative(_leftControl);
        PromotePendingControlToNative(_rightControl);
    }

    private void PromotePendingControlToNative(DeferredControlState state)
    {
        if (!state.IsDown || state.OriginalDownPassed || state.NativeDownSent)
        {
            return;
        }

        SendControl(state, keyUp: false);
        state.NativeDownSent = true;
        DiagnosticLog.WriteVerbose($"{state.Side} Ctrl used with native input; sent Ctrl down.");
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
        DiagnosticLog.WriteVerbose($"{state.Side} Alt used with another input; sent native Alt down.");
    }

    private bool IsAnyNonAltKeyDown()
    {
        // A missed key-up must not classify every later Alt tap as a chord.
        KeyHoldPolicy.PruneNonAltKeys(_nonAltKeysDown, GetForegroundKeyboardLayout(), IsVirtualKeyDown);
        return _nonAltKeysDown.Count > 0 || _emacsState.GestureActive ||
               IsVirtualKeyDown(NativeMethods.VkShift) ||
               IsVirtualKeyDown(NativeMethods.VkControl) ||
               IsVirtualKeyDown(NativeMethods.VkLWin) ||
               IsVirtualKeyDown(NativeMethods.VkRWin);
    }

    private bool IsAnyOtherInputDownForControl()
    {
        KeyHoldPolicy.PruneNonAltKeys(_nonAltKeysDown, GetForegroundKeyboardLayout(), IsVirtualKeyDown);
        return _nonAltKeysDown.Count > 0 || _leftAlt.IsDown || _rightAlt.IsDown ||
               IsVirtualKeyDown(NativeMethods.VkShift) ||
               IsVirtualKeyDown(NativeMethods.VkLWin) || IsVirtualKeyDown(NativeMethods.VkRWin);
    }

    private static bool IsVirtualKeyDown(uint virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;
    }

    private static nuint GetForegroundKeyboardLayout()
    {
        var window = NativeMethods.GetForegroundWindow();
        // An unknown foreground must not silently select the hook thread's layout.
        if (window == 0) return 0;
        var thread = NativeMethods.GetWindowThreadProcessId(window, out _);
        return thread == 0 ? 0 : unchecked((nuint)NativeMethods.GetKeyboardLayout(thread));
    }

    private NavigationModifiers GetNavigationModifiers()
    {
        var result = NavigationModifiers.None;
        if (GetControlModifierSnapshot().AnyDown) result |= NavigationModifiers.Control;
        if (_leftAlt.IsDown || _rightAlt.IsDown || IsVirtualKeyDown(NativeMethods.VkMenu)) result |= NavigationModifiers.Alt;
        if (IsVirtualKeyDown(NativeMethods.VkShift)) result |= NavigationModifiers.Shift;
        if (IsVirtualKeyDown(NativeMethods.VkLWin) || IsVirtualKeyDown(NativeMethods.VkRWin)) result |= NavigationModifiers.Windows;
        return result;
    }

    private void ConsumePendingAltGestures()
    {
        if (_leftAlt.IsDown) _leftAlt.ConsumedByEmacs = true;
        if (_rightAlt.IsDown) _rightAlt.ConsumedByEmacs = true;
    }

    private void ConsumePendingControlGestures()
    {
        ConsumePendingControlGesture(_leftControl);
        ConsumePendingControlGesture(_rightControl);
    }

    private void ConsumePendingControlGesture(DeferredControlState state)
    {
        if (!state.IsDown)
        {
            return;
        }

        if (state.OriginalDownPassed || state.NativeDownSent)
        {
            SendControl(state, keyUp: true);
            DiagnosticLog.WriteVerbose($"{state.Side} Ctrl returned to deferred state for Emacs input.");
        }

        state.OriginalDownPassed = false;
        state.NativeDownSent = false;
        state.ConsumedByEmacs = true;
    }

    private bool CanDeferControl(DeferredControlState state) =>
        _settings.Enabled && _settings.EmacsEnabled && _emacsState.Active &&
        EmacsSideFilter.Allows(state.Side, _settings.EmacsControlSide);

    private EmacsSideFilter GetEmacsSideFilter()
    {
        var control = GetControlModifierSnapshot().Sides;
        var alt = ModifierSides.None;
        if (_leftAlt.IsDown || IsVirtualKeyDown(NativeMethods.VkLMenu)) alt |= ModifierSides.Left;
        if (_rightAlt.IsDown || IsVirtualKeyDown(NativeMethods.VkRMenu)) alt |= ModifierSides.Right;
        return new(control, alt, _settings.EmacsControlSide, _settings.EmacsAltSide);
    }

    private ControlModifierSnapshot GetControlModifierSnapshot() => new(
        _leftControl.IsDown,
        _rightControl.IsDown,
        IsVirtualKeyDown(NativeMethods.VkControl),
        IsVirtualKeyDown(NativeMethods.VkLControl),
        IsVirtualKeyDown(NativeMethods.VkRControl));

    private static bool IsShiftKey(uint key) => key is NativeMethods.VkShift or NativeMethods.VkLShift or NativeMethods.VkRShift;

    private bool CanDeferAltShift() => _settings.Enabled && _settings.EmacsEnabled && _emacsState.Active &&
        EmacsBindings.ShouldDeferAltShift(_settings, GetEmacsSideFilter().AltDown) &&
        !IsVirtualKeyDown(NativeMethods.VkControl) && !IsVirtualKeyDown(NativeMethods.VkLWin) && !IsVirtualKeyDown(NativeMethods.VkRWin);

    private void MarkDeferredShiftChord()
    {
        if (_leftAlt.IsDown && !_leftAlt.OriginalDownPassed && !_leftAlt.NativeDownSent) _leftAlt.DeferredShiftChord = true;
        if (_rightAlt.IsDown && !_rightAlt.OriginalDownPassed && !_rightAlt.NativeDownSent) _rightAlt.DeferredShiftChord = true;
    }

    private void SendNavigation(EmacsBinding binding)
    {
        var modifiers = new List<ushort>();
        if (IsVirtualKeyDown(NativeMethods.VkLControl) && !_leftControl.HiddenFromForeground)
            modifiers.Add((ushort)NativeMethods.VkLControl);
        if (IsVirtualKeyDown(NativeMethods.VkRControl) && !_rightControl.HiddenFromForeground)
            modifiers.Add((ushort)NativeMethods.VkRControl);
        if (IsVirtualKeyDown(NativeMethods.VkLShift)) modifiers.Add((ushort)NativeMethods.VkLShift);
        if (IsVirtualKeyDown(NativeMethods.VkRShift)) modifiers.Add((ushort)NativeMethods.VkRShift);
        if (_leftAlt.IsDown && (_leftAlt.OriginalDownPassed || _leftAlt.NativeDownSent))
            modifiers.Add((ushort)NativeMethods.VkLMenu);
        if (_rightAlt.IsDown && (_rightAlt.OriginalDownPassed || _rightAlt.NativeDownSent))
            modifiers.Add((ushort)NativeMethods.VkRMenu);

        var plan = NavigationInputPlan.Create(binding, modifiers);
        SendKeyboardInputs(plan.Select(stroke => CreateNavigationKeyInput(stroke.VirtualKey, stroke.KeyUp)).ToArray());
        DiagnosticLog.WriteVerbose($"Emacs operation: {binding.Shortcut}.");
    }

    private void EnsureNativeCapsLockOff(bool nativeCapsLockOn)
    {
        if (_settings.Enabled && _settings.EmacsEnabled && nativeCapsLockOn)
        {
            SendKeyboardInputs(
                CreateVirtualKeyInput((ushort)NativeMethods.VkCapsLock, false),
                CreateVirtualKeyInput((ushort)NativeMethods.VkCapsLock, true));
        }
    }

    private void NotifyEmacsModeChanged()
    {
        DiagnosticLog.Write($"Keyboard mode changed: {(_emacsState.Active ? "Emacs" : "Normal")}.");
        EmacsModeChanged?.Invoke(_emacsState.Active);
    }

    private void SendImeStateTap(AltSide side)
    {
        var virtualKey = side == AltSide.Left
            ? JapaneseImeKey.ImeOffVirtualKey
            : JapaneseImeKey.ImeOnVirtualKey;

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        var keyboardLayout = unchecked((nuint)NativeMethods.GetKeyboardLayout(foregroundThread));
        DiagnosticLog.WriteVerbose(
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

    private void SendControl(DeferredControlState state, bool keyUp)
    {
        SendKeyboardInputs(
            CreateScanCodeInput(state.ScanCode, state.IsExtended, keyUp));
    }

    private void SendKeyboardInputs(params NativeMethods.Input[] inputs)
    {
        if (NativeMethods.SendKeyboardInputs(inputs, out var errorCode))
        {
            if (DiagnosticLog.VerboseEnabled)
                DiagnosticLog.WriteVerbose($"SendInput succeeded for {inputs.Length} keyboard event(s).");
        }
        else
        {
            _inputInjectionFailures++;
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
                    Flags = (keyUp ? NativeMethods.KeyeventfKeyUp : 0) |
                        (IsExtendedVirtualKey(virtualKey)
                            ? NativeMethods.KeyeventfExtendedKey : 0),
                    ExtraInfo = OwnInputMarker
                }
            }
        };
    }

    private static NativeMethods.Input CreateNavigationKeyInput(ushort virtualKey, bool keyUp)
    {
        var mappedScanCode = NativeMethods.MapVirtualKey(virtualKey, NativeMethods.MapvkVkToVscEx);
        var scanCode = (ushort)(mappedScanCode & 0xFF);
        var prefix = mappedScanCode & 0xFF00;
        var isExtended = prefix is 0xE000 or 0xE100 || IsExtendedVirtualKey(virtualKey);

        return CreateScanCodeInput(scanCode, isExtended, keyUp);
    }

    private static bool IsExtendedVirtualKey(ushort virtualKey) =>
        virtualKey is (ushort)Keys.Left or (ushort)Keys.Right or (ushort)Keys.Up or (ushort)Keys.Down or
            (ushort)Keys.Home or (ushort)Keys.End or (ushort)Keys.Delete or
            (ushort)Keys.RControlKey or (ushort)Keys.RMenu;

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

    private void ResetActiveStates(bool resetEmacsTransient = true)
    {
        if (_leftAlt.IsDown || _rightAlt.IsDown || _leftControl.IsDown || _rightControl.IsDown ||
            _emacsState.GestureActive)
            WriteHookStatus("before active state reset");
        foreach (var state in new[] { _leftAlt, _rightAlt })
        {
            if (state.IsDown && state.NativeDownSent)
            {
                SendAlt(state, keyUp: true);
            }

            state.Reset();
        }

        foreach (var state in new[] { _leftControl, _rightControl })
        {
            if (state.IsDown && state.NativeDownSent)
            {
                SendControl(state, keyUp: true);
            }

            state.Reset();
        }

        _nonAltKeysDown.Clear();
        if (resetEmacsTransient) _emacsState.ResetTransient();
    }

    private void RequestRefresh(string reason)
    {
        _refreshRequested = true;
        DiagnosticLog.Write($"Hook renewal requested: {reason}.");
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend)
        {
            PostToHookThread(() => ResetActiveStates());
        }
        else if (args.Mode == PowerModes.Resume)
        {
            PostToHookThread(() => RequestRefresh("power resume"));
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionLock)
        {
            PostToHookThread(() => ResetActiveStates());
        }
        else if (args.Reason == SessionSwitchReason.SessionUnlock)
        {
            PostToHookThread(() => RequestRefresh("session unlock"));
        }
    }

    private void MaintainHooks()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // Record even when a retained gesture prevents renewal indefinitely.
            var now = Environment.TickCount64;
            if (now - _lastStatusAtMilliseconds >= 30_000)
            {
                _lastStatusAtMilliseconds = now;
                WriteHookStatus("periodic");
            }
            var info = new NativeMethods.LastInputInfo
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>()
            };
            if (!NativeMethods.GetLastInputInfo(ref info))
            {
                return;
            }

            var idleMilliseconds = HookMaintenancePolicy.CalculateIdleMilliseconds(
                unchecked((uint)Environment.TickCount), info.Time);
            var gestureActive = _leftAlt.IsDown || _rightAlt.IsDown ||
                _leftControl.IsDown || _rightControl.IsDown || _emacsState.GestureActive;
            if (idleMilliseconds < HookMaintenancePolicy.MinimumIdleMilliseconds || gestureActive)
            {
                return;
            }

            // Includes real keys, modifiers and mouse buttons; excludes Japanese IME width commands.
            var anyKeyDown = KeyHoldPolicy.AnyKeyOrMouseButtonDown(
                GetForegroundKeyboardLayout(), IsVirtualKeyDown);

            if (HookMaintenancePolicy.ShouldRefresh(
                Environment.TickCount64 - _lastRefreshAtMilliseconds,
                idleMilliseconds, anyKeyDown, gestureActive, _refreshRequested))
            {
                ResetActiveStates();
                RegisterHooks();
            }
        }
        catch (Exception exception)
        {
            _refreshRequested = true;
            DiagnosticLog.Write($"Hook renewal failed; will retry at safe idle: {exception}");
        }
    }

    private void CleanupHookThread()
    {
        _maintenanceTimer?.Stop();
        _maintenanceTimer?.Dispose();
        try
        {
            WriteHookStatus("shutdown");
            ResetActiveStates();
        }
        finally
        {
            UnregisterHooks();
        }
        DiagnosticLog.Write("Dedicated hook thread stopped.");
    }

    private nint SuppressKeyboardEvent()
    {
        _suppressedKeyboardEventCount++;
        return 1;
    }

    private void WriteHookStatus(string reason)
    {
        // No ordinary key codes, typed text, window titles or clipboard contents.
        var layout = GetForegroundKeyboardLayout();
        DiagnosticLog.Write($"Hook status ({reason}): generation={_hookGeneration}, " +
            $"enabled={_settings.Enabled}, emacsEnabled={_settings.EmacsEnabled}, emacsActive={_emacsState.Active}, " +
            $"keyboardCallbacks={_keyboardCallbackCount}, mouseCallbacks={_mouseCallbackCount}, " +
            $"suppressed={_suppressedKeyboardEventCount}, injectionFailures={_inputInjectionFailures}, " +
            $"refreshRequested={_refreshRequested}, sinceRefreshMs={Environment.TickCount64 - _lastRefreshAtMilliseconds}, " +
            $"leftAlt={_leftAlt.IsDown}/{_leftAlt.OriginalDownPassed}/{_leftAlt.NativeDownSent}, " +
            $"rightAlt={_rightAlt.IsDown}/{_rightAlt.OriginalDownPassed}/{_rightAlt.NativeDownSent}, " +
            $"leftCtrl={_leftControl.IsDown}/{_leftControl.OriginalDownPassed}/{_leftControl.NativeDownSent}, " +
            $"rightCtrl={_rightControl.IsDown}/{_rightControl.OriginalDownPassed}/{_rightControl.NativeDownSent}, " +
            $"asyncAlt={IsVirtualKeyDown(NativeMethods.VkLMenu)}/{IsVirtualKeyDown(NativeMethods.VkRMenu)}, " +
            $"asyncCtrl={IsVirtualKeyDown(NativeMethods.VkLControl)}/{IsVirtualKeyDown(NativeMethods.VkRControl)}, " +
            $"capturedKeys={_emacsState.CapturedKeyCount}, capsCaptured={_emacsState.CapsLockCaptured}, " +
            $"otherKeysCount={_nonAltKeysDown.Count}, foregroundHkl=0x{layout:X}, " +
            $"imeWidthEvents={_imeWidthCommandEvents}, " +
            $"asyncWidth={IsVirtualKeyDown(KeyHoldPolicy.ImeSingleByte)}/{IsVirtualKeyDown(KeyHoldPolicy.ImeDoubleByte)}, " +
            $"trackedWidth={_nonAltKeysDown.Contains(KeyHoldPolicy.ImeSingleByte)}/{_nonAltKeysDown.Contains(KeyHoldPolicy.ImeDoubleByte)}, " +
            $"droppedLogs={DiagnosticLog.DroppedEntries}.");
    }

    private void UnregisterHooks()
    {
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
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _hookThread.Dispose();
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

        public bool ConsumedByEmacs { get; set; }
        public bool DeferredShiftChord { get; set; }

        public long DownAtMilliseconds { get; set; }

        public ushort ScanCode { get; set; }

        public bool IsExtended { get; set; }

        public void Start(uint scanCode, bool isExtended)
        {
            IsDown = true;
            OriginalDownPassed = false;
            NativeDownSent = false;
            ConsumedByEmacs = false;
            DeferredShiftChord = false;
            DownAtMilliseconds = Environment.TickCount64;
            ScanCode = checked((ushort)scanCode);
            IsExtended = isExtended;
        }

        public void Reset()
        {
            IsDown = false;
            OriginalDownPassed = false;
            NativeDownSent = false;
            ConsumedByEmacs = false;
            DeferredShiftChord = false;
            DownAtMilliseconds = 0;
            ScanCode = 0;
            IsExtended = false;
        }
    }

    private sealed class DeferredControlState(ModifierSides side)
    {
        public ModifierSides Side { get; } = side;

        public bool IsDown { get; set; }

        public bool OriginalDownPassed { get; set; }

        public bool NativeDownSent { get; set; }

        public bool ConsumedByEmacs { get; set; }

        public ushort ScanCode { get; private set; }

        public bool IsExtended { get; private set; }

        public bool HiddenFromForeground =>
            IsDown && !OriginalDownPassed && !NativeDownSent;

        public void Start(uint scanCode, bool isExtended)
        {
            IsDown = true;
            OriginalDownPassed = false;
            NativeDownSent = false;
            ConsumedByEmacs = false;
            ScanCode = checked((ushort)scanCode);
            IsExtended = isExtended;
        }

        public void Reset()
        {
            IsDown = false;
            OriginalDownPassed = false;
            NativeDownSent = false;
            ConsumedByEmacs = false;
            ScanCode = 0;
            IsExtended = false;
        }
    }
}
