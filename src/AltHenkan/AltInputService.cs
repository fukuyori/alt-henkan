using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace AltHenkan;

internal sealed partial class AltInputService : IDisposable
{
    private const nuint OwnInputMarker = 0x414C5448;

    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly NativeMethods.HookProc _nextKeyboardHook;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly HashSet<uint> _nonAltKeysDown = [];
    private readonly AltState _leftAlt = new(AltSide.Left);
    private readonly AltState _rightAlt = new(AltSide.Right);
    private readonly DeferredControlState _leftControl = new(ModifierSides.Left);
    private readonly DeferredControlState _rightControl = new(ModifierSides.Right);
    private readonly EmacsKeyboardState _emacsState = new();
    private readonly bool _initialNativeCapsLockOn;
    private readonly DedicatedMessageLoop? _hookThread;
    private readonly InputPlatform _platform;
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
    private int _inputInjectionDepth;
    private long _inputInjectionCalls;
    private int _maxInputInjectionDepth;
    private long _keyboardCallbacksDuringInjection;
    private long _altReconcileChecks;
    private long _altReconcileInjectionSkips;
    private long _altReconcileInjectionSkipsWithAlt;
    private int _maxAltReconcileInjectionDepth;
    private long _altStateRecoveries;
    private long _controlStateRecoveries;

    public event Action<int>? InputInjectionFailed;
    public event Action<bool>? EmacsModeChanged;

    public AltInputService(AppSettings settings) : this(settings, new InputPlatform())
    {
        _initialNativeCapsLockOn = (NativeMethods.GetKeyState((int)NativeMethods.VkCapsLock) & 1) != 0;
        _hookThread = new DedicatedMessageLoop(InitializeHookThread, CleanupHookThread);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    internal AltInputService(AppSettings settings, InputPlatform platform)
    {
        _platform = platform;
        _settings = settings.Normalize();
        _emacsState.SetActive(_settings.InitialEmacsActive);
        _keyboardCallback = KeyboardHookCallback;
        _nextKeyboardHook = (code, wParam, lParam) =>
            _platform.NextKeyboard(_keyboardHook, code, wParam, lParam);
        _mouseCallback = MouseHookCallback;
    }

    private void InitializeHookThread()
    {
        DiagnosticLog.Write($"Dedicated hook thread started: managed thread {Environment.CurrentManagedThreadId}.");
        DiagnosticLog.Write($"Initial keyboard mode: {(_emacsState.Active ? "Emacs" : "Normal")}.");
        RegisterHooks();
        _lastStatusAtMilliseconds = Environment.TickCount64;
        WriteHookStatus("startup");
        TraceSettings(after: true);
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
        Trace(new() { Kind = InputTraceKind.Boundary, Reason = InputTraceReason.Registered, Index = _hookGeneration });
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
            TraceSettings(after: false);
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
                ResetActiveStates(resetEmacsTransient: false, reason: InputTraceReason.Disabled);
            }
            else if (!wasEnabled && _settings.Enabled)
            {
                RequestRefresh("enabled");
            }
            TraceSettings(after: true);
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
            if (_hookThread is not null) await _hookThread.InvokeAsync(action).ConfigureAwait(false);
            else action(); // The test platform has no dedicated message loop.
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
        if (_inputInjectionDepth > 0) _keyboardCallbacksDuringInjection++;
        try
        {
            return InputTrace.Enabled ? TraceKeyboard(code, wParam, lParam) : KeyboardHookCore(code, wParam, lParam);
        }
        catch (Exception exception)
        {
            _refreshRequested = true;
            DiagnosticLog.Write($"Keyboard callback failed; renewal requested: {exception}");
            return _nextKeyboardHook(code, wParam, lParam);
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
            return ForwardKeyboard(code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        if (IsOwnInput(data.ExtraInfo))
        {
            if (DiagnosticLog.VerboseEnabled) DiagnosticLog.WriteVerbose(
                $"Injected keyboard event observed: message=0x{unchecked((int)wParam):X4}, vk=0x{data.VirtualKeyCode:X2}, scan=0x{data.ScanCode:X2}, flags=0x{data.Flags:X2}.");
            return ForwardKeyboard(code, wParam, lParam, InputTraceReason.OwnInput);
        }

        var message = unchecked((int)wParam);
        var isKeyDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
        var isKeyUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
        if (!isKeyDown && !isKeyUp)
        {
            return ForwardKeyboard(code, wParam, lParam);
        }

        // These IME commands can remain set in GetAsyncKeyState. Pass them through,
        // but do not retain them as held keys or promote pending Alt/Ctrl gestures.
        if (KeyHoldPolicy.IsWidthKeyCode(data.VirtualKeyCode) &&
            KeyHoldPolicy.IsImeWidthCommand(data.VirtualKeyCode, GetForegroundKeyboardLayout()))
        {
            _nonAltKeysDown.Remove(data.VirtualKeyCode);
            _imeWidthCommandEvents++;
            return ForwardKeyboard(code, wParam, lParam, InputTraceReason.ImeWidth);
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
            return SuppressKeyboardEvent(InputTraceReason.CapsLock);
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

        var inspectModifiers = isKeyDown && emacsEnabled && _emacsState.Active && EmacsBindings.IsSourceKey(data.VirtualKeyCode);
        var controlSnapshot = inspectModifiers ? GetControlModifierSnapshot() : default;
        var modifiers = inspectModifiers ? GetNavigationModifiers(controlSnapshot) : NavigationModifiers.None;
        if (_emacsState.HandleKey(data.VirtualKeyCode, isKeyDown, emacsEnabled,
            modifiers, _settings.EmacsShortcuts, out var binding,
            modifiers == NavigationModifiers.None ? null : GetEmacsSideFilter(controlSnapshot)))
        {
            if (binding is not null)
            {
                if ((binding.Modifiers & NavigationModifiers.Alt) != 0)
                {
                    ConsumePendingAltGestures();
                }
                if ((binding.Modifiers & NavigationModifiers.Control) != 0)
                {
                    ConsumePendingControlGestures(controlSnapshot.Sides);
                }
                SendNavigation(binding);
            }
            return SuppressKeyboardEvent(InputTraceReason.Emacs);
        }

        if (!_settings.Enabled)
        {
            return ForwardKeyboard(code, wParam, lParam, InputTraceReason.Inactive);
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

        return ForwardKeyboard(code, wParam, lParam);
    }

    private nint HandleControlEvent(
        DeferredControlState state,
        int code, nint wParam, nint lParam,
        NativeMethods.KbdLlHookStruct data, bool isKeyDown)
    {
        var downVersion = state.ReleaseAudit.Begin(isKeyDown);
        try { return HandleControlEventCore(state, code, wParam, lParam, data, isKeyDown); }
        finally
        {
            if (!isKeyDown && state.ReleaseAudit.DownVersion == downVersion)
                CheckModifierRelease(ref state.ReleaseAudit,
                    state.Side == ModifierSides.Left ? InputTraceKey.LeftControl : InputTraceKey.RightControl,
                    ModifierResidual(state.IsDown, state.OriginalDownPassed, state.NativeDownSent, state.ConsumedByEmacs), data);
        }
    }

    private nint HandleControlEventCore(
        DeferredControlState state,
        int code,
        nint wParam,
        nint lParam,
        NativeMethods.KbdLlHookStruct data,
        bool isKeyDown)
    {
        if (isKeyDown)
        {
            state.ReleaseMonitor.Reset();
            if (state.IsDown)
            {
                if (state.OriginalDownPassed || state.NativeDownSent)
                {
                    // This can be a new press after a missed up, as with native Alt.
                    state.OriginalDownPassed = true;
                    state.NativeDownSent = false;
                    return ForwardKeyboard(code, wParam, lParam, InputTraceReason.ControlRepeatNative);
                }
                return SuppressKeyboardEvent(InputTraceReason.ControlRepeat);
            }

            state.Start(data.ScanCode, (data.Flags & NativeMethods.LlkhfExtended) != 0);
            var canDefer = CanDeferControl(state);
            if (!canDefer || IsAnyOtherInputDownForControl())
            {
                state.OriginalDownPassed = true;
                return ForwardKeyboard(code, wParam, lParam, canDefer
                    ? InputTraceReason.ControlNativeOtherInput : InputTraceReason.ControlNativeNotDeferrable);
            }

            var otherControl = state.Side == ModifierSides.Left ? _rightControl : _leftControl;
            if (otherControl.IsDown)
            {
                PromotePendingControlToNative(otherControl);
                PromotePendingControlToNative(state);
            }
            return SuppressKeyboardEvent(InputTraceReason.ControlDeferred);
        }

        if (!state.IsDown)
        {
            return ForwardKeyboard(code, wParam, lParam, InputTraceReason.ControlUnknownUp);
        }

        state.IsDown = false;
        if (state.OriginalDownPassed)
        {
            state.Reset();
            return ForwardKeyboard(code, wParam, lParam, InputTraceReason.ControlPassedUp);
        }

        if (state.NativeDownSent)
        {
            SendControl(state, keyUp: true);
            state.Reset();
            return SuppressKeyboardEvent(InputTraceReason.ControlSentUp);
        }

        if (!state.ConsumedByEmacs)
        {
            SendControl(state, keyUp: false);
            SendControl(state, keyUp: true);
        }
        state.Reset();
        return SuppressKeyboardEvent(InputTraceReason.ControlTapOrConsumed);
    }

    private nint HandleAltEvent(
        AltState state,
        int code, nint wParam, nint lParam,
        NativeMethods.KbdLlHookStruct data, bool isKeyDown)
    {
        var downVersion = state.ReleaseAudit.Begin(isKeyDown);
        try { return HandleAltEventCore(state, code, wParam, lParam, data, isKeyDown); }
        finally
        {
            if (!isKeyDown && state.ReleaseAudit.DownVersion == downVersion)
                CheckModifierRelease(ref state.ReleaseAudit,
                    state.Side == AltSide.Left ? InputTraceKey.LeftAlt : InputTraceKey.RightAlt,
                    ModifierResidual(state.IsDown, state.OriginalDownPassed, state.NativeDownSent, state.ConsumedByEmacs,
                        state.DeferredShiftChord), data);
        }
    }

    private nint HandleAltEventCore(
        AltState state,
        int code,
        nint wParam,
        nint lParam,
        NativeMethods.KbdLlHookStruct data,
        bool isKeyDown)
    {
        if (isKeyDown)
        {
            // A down event invalidates release observations, even when it looks like a repeat.
            state.ReleaseMonitor.Reset();
            if (state.IsDown)
            {
                if (state.OriginalDownPassed || state.NativeDownSent)
                {
                    // This may be a new press after a missed up. Forward native Alt repeats
                    // too: swallowing this down would leave Windows up while the user holds Alt.
                    // Its physical up can now follow the physical down through the hook chain.
                    state.OriginalDownPassed = true;
                    state.NativeDownSent = false;
                    return ForwardKeyboard(code, wParam, lParam, InputTraceReason.AltRepeatNative);
                }
                return SuppressKeyboardEvent(InputTraceReason.AltRepeatHidden);
            }

            state.Start(data.ScanCode, (data.Flags & NativeMethods.LlkhfExtended) != 0);

            var otherAlt = state.Side == AltSide.Left ? _rightAlt : _leftAlt;
            if (otherAlt.IsDown)
            {
                PromotePendingAltToNative(otherAlt);
                PromotePendingAltToNative(state);
                return SuppressKeyboardEvent(InputTraceReason.AltDeferred);
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
                    return ForwardKeyboard(code, wParam, lParam, InputTraceReason.AltNative);
                }
            }

            return SuppressKeyboardEvent(InputTraceReason.AltDeferred);
        }

        if (!state.IsDown)
        {
            return ForwardKeyboard(code, wParam, lParam, InputTraceReason.AltUnknownUp);
        }

        var heldMilliseconds = Environment.TickCount64 - state.DownAtMilliseconds;
        state.IsDown = false;

        if (state.OriginalDownPassed)
        {
            state.Reset();
            return ForwardKeyboard(code, wParam, lParam, InputTraceReason.AltPassedUp);
        }

        if (state.NativeDownSent)
        {
            DiagnosticLog.WriteVerbose($"{state.Side} Alt used with another input; sending native Alt up.");
            SendAlt(state, keyUp: true);
            state.Reset();
            return SuppressKeyboardEvent(InputTraceReason.AltSentUp);
        }

        if (state.ConsumedByEmacs)
        {
            state.Reset();
            return SuppressKeyboardEvent(InputTraceReason.AltConsumed);
        }

        if (state.DeferredShiftChord)
        {
            // No angle shortcut followed: preserve the native Alt+Shift chord.
            SendAltTap(state);
            state.Reset();
            return SuppressKeyboardEvent(InputTraceReason.AltTap);
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
        return SuppressKeyboardEvent(InputTraceReason.AltTap);
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        var startedAt = Stopwatch.GetTimestamp();
        _mouseCallbackCount++;
        try
        {
            return InputTrace.Enabled ? TraceMouse(code, wParam, lParam) : MouseHookCore(code, wParam, lParam);
        }
        catch (Exception exception)
        {
            _refreshRequested = true;
            DiagnosticLog.Write($"Mouse callback failed; renewal requested: {exception}");
            return _platform.NextMouse(_mouseHook, code, wParam, lParam);
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

        return _platform.NextMouse(_mouseHook, code, wParam, lParam);
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

        TraceState(InputTraceReason.PromoteControl);
        SendControl(state, keyUp: false);
        state.NativeDownSent = true;
        TraceState(InputTraceReason.PromoteControl, after: true);
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

        TraceState(InputTraceReason.PromoteAlt);
        SendAlt(state, keyUp: false);
        state.NativeDownSent = true;
        TraceState(InputTraceReason.PromoteAlt, after: true);
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

    private bool IsVirtualKeyDown(uint virtualKey)
    {
        return _platform.IsDown((int)virtualKey);
    }

    private static nuint GetForegroundKeyboardLayout()
    {
        var window = NativeMethods.GetForegroundWindow();
        // An unknown foreground must not silently select the hook thread's layout.
        if (window == 0) return 0;
        var thread = NativeMethods.GetWindowThreadProcessId(window, out _);
        return thread == 0 ? 0 : unchecked((nuint)NativeMethods.GetKeyboardLayout(thread));
    }

    private NavigationModifiers GetNavigationModifiers(ControlModifierSnapshot control)
    {
        var result = NavigationModifiers.None;
        if (control.AnyDown) result |= NavigationModifiers.Control;
        if (GetAltSnapshot(_leftAlt).Active || GetAltSnapshot(_rightAlt).Active ||
            IsVirtualKeyDown(NativeMethods.VkMenu)) result |= NavigationModifiers.Alt;
        if (IsVirtualKeyDown(NativeMethods.VkShift)) result |= NavigationModifiers.Shift;
        if (IsVirtualKeyDown(NativeMethods.VkLWin) || IsVirtualKeyDown(NativeMethods.VkRWin)) result |= NavigationModifiers.Windows;
        return result;
    }

    private void ConsumePendingAltGestures()
    {
        if (_leftAlt.IsDown) _leftAlt.ConsumedByEmacs = true;
        if (_rightAlt.IsDown) _rightAlt.ConsumedByEmacs = true;
    }

    private void ConsumePendingControlGestures(ModifierSides activeSides)
    {
        // Do not turn an excluded stale side into hidden Ctrl when the OTHER
        // side invokes a shortcut. Hidden state is eligible for reinjection.
        if ((activeSides & ModifierSides.Left) != 0) ConsumePendingControlGesture(_leftControl);
        if ((activeSides & ModifierSides.Right) != 0) ConsumePendingControlGesture(_rightControl);
    }

    private void ConsumePendingControlGesture(DeferredControlState state)
    {
        if (!state.IsDown)
        {
            return;
        }

        TraceState(InputTraceReason.ConsumeControl);
        if (state.OriginalDownPassed || state.NativeDownSent)
        {
            var downVersion = state.ReleaseAudit.DownVersion;
            SendControl(state, keyUp: true);
            // SendInput can reenter the hook. Do not hide a new native press,
            // or overwrite a gesture that a reentrant up has already ended.
            if (!state.IsDown || state.ReleaseAudit.DownVersion != downVersion)
            {
                TraceState(InputTraceReason.ConsumeControl, after: true);
                return;
            }
            DiagnosticLog.WriteVerbose($"{state.Side} Ctrl returned to deferred state for Emacs input.");
        }

        state.OriginalDownPassed = false;
        state.NativeDownSent = false;
        state.ConsumedByEmacs = true;
        state.ReleaseMonitor.Reset();
        TraceState(InputTraceReason.ConsumeControl, after: true);
    }

    private bool CanDeferControl(DeferredControlState state) =>
        _settings.Enabled && _settings.EmacsEnabled && _emacsState.Active &&
        EmacsSideFilter.Allows(state.Side, _settings.EmacsControlSide);

    private EmacsSideFilter GetEmacsSideFilter(ControlModifierSnapshot controlSnapshot)
    {
        var control = controlSnapshot.Sides;
        var alt = ModifierSides.None;
        if (GetAltSnapshot(_leftAlt).Active) alt |= ModifierSides.Left;
        if (GetAltSnapshot(_rightAlt).Active) alt |= ModifierSides.Right;
        return new(control, alt, _settings.EmacsControlSide, _settings.EmacsAltSide);
    }

    private ControlModifierSnapshot GetControlModifierSnapshot() => new(
        _leftControl.IsDown,
        _rightControl.IsDown,
        IsVirtualKeyDown(NativeMethods.VkControl),
        IsVirtualKeyDown(NativeMethods.VkLControl),
        IsVirtualKeyDown(NativeMethods.VkRControl),
        _leftControl.OriginalDownPassed || _leftControl.NativeDownSent,
        _rightControl.OriginalDownPassed || _rightControl.NativeDownSent);

    private static bool IsShiftKey(uint key) => key is NativeMethods.VkShift or NativeMethods.VkLShift or NativeMethods.VkRShift;

    private bool CanDeferAltShift() => _settings.Enabled && _settings.EmacsEnabled && _emacsState.Active &&
        EmacsBindings.ShouldDeferAltShift(_settings, GetEmacsSideFilter(GetControlModifierSnapshot()).AltDown) &&
        !IsVirtualKeyDown(NativeMethods.VkControl) && !IsVirtualKeyDown(NativeMethods.VkLWin) && !IsVirtualKeyDown(NativeMethods.VkRWin);

    private void MarkDeferredShiftChord()
    {
        if (_leftAlt.IsDown && !_leftAlt.OriginalDownPassed && !_leftAlt.NativeDownSent) _leftAlt.DeferredShiftChord = true;
        if (_rightAlt.IsDown && !_rightAlt.OriginalDownPassed && !_rightAlt.NativeDownSent) _rightAlt.DeferredShiftChord = true;
    }

    private void SendNavigation(EmacsBinding binding)
    {
        TraceState(InputTraceReason.Navigation);
        var modifiers = new List<ushort>();
        if (IsVirtualKeyDown(NativeMethods.VkLControl) && !_leftControl.HiddenFromForeground)
            modifiers.Add((ushort)NativeMethods.VkLControl);
        if (IsVirtualKeyDown(NativeMethods.VkRControl) && !_rightControl.HiddenFromForeground)
            modifiers.Add((ushort)NativeMethods.VkRControl);
        if (IsVirtualKeyDown(NativeMethods.VkLShift)) modifiers.Add((ushort)NativeMethods.VkLShift);
        if (IsVirtualKeyDown(NativeMethods.VkRShift)) modifiers.Add((ushort)NativeMethods.VkRShift);
        if (GetAltSnapshot(_leftAlt).ShouldRestore)
            modifiers.Add((ushort)NativeMethods.VkLMenu);
        if (GetAltSnapshot(_rightAlt).ShouldRestore)
            modifiers.Add((ushort)NativeMethods.VkRMenu);

        var plan = NavigationInputPlan.Create(binding, modifiers);
        SendKeyboardInputsFor(InputTraceReason.Navigation,
            plan.Select(stroke => CreateNavigationKeyInput(stroke.VirtualKey, stroke.KeyUp)).ToArray());
        TraceState(InputTraceReason.Navigation, after: true);
        DiagnosticLog.WriteVerbose($"Emacs operation: {binding.Shortcut}.");
    }

    private void EnsureNativeCapsLockOff(bool nativeCapsLockOn)
    {
        if (_settings.Enabled && _settings.EmacsEnabled && nativeCapsLockOn)
        {
            SendKeyboardInputsFor(InputTraceReason.CapsReset,
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

        SendKeyboardInputsFor(InputTraceReason.ImeTap,
            CreateVirtualKeyInput(virtualKey, keyUp: false),
            CreateVirtualKeyInput(virtualKey, keyUp: true));
    }

    private void SendAltTap(AltState state)
    {
        SendKeyboardInputsFor(InputTraceReason.AltTap,
            CreateScanCodeInput(state.ScanCode, state.IsExtended, keyUp: false),
            CreateScanCodeInput(state.ScanCode, state.IsExtended, keyUp: true));
    }

    private void SendAlt(AltState state, bool keyUp)
    {
        SendKeyboardInputsFor(InputTraceReason.AltSend,
            CreateScanCodeInput(state.ScanCode, state.IsExtended, keyUp));
    }

    private void SendControl(DeferredControlState state, bool keyUp)
    {
        SendKeyboardInputsFor(InputTraceReason.ControlSend,
            CreateScanCodeInput(state.ScanCode, state.IsExtended, keyUp));
    }

    private void SendKeyboardInputs(params NativeMethods.Input[] inputs)
        => SendKeyboardInputsFor(InputTraceReason.None, inputs);

    private void SendKeyboardInputsFor(InputTraceReason reason, params NativeMethods.Input[] inputs)
    {
        var parentBatch = _traceBatch;
        _traceBatch = ++_traceBatchSequence;
        var started = Stopwatch.GetTimestamp();
        var result = new InputSendResult(0, 0);
        var completed = false;
        _inputInjectionDepth++;
        _inputInjectionCalls++;
        _maxInputInjectionDepth = Math.Max(_maxInputInjectionDepth, _inputInjectionDepth);
        try
        {
            Trace(new() { Kind = InputTraceKind.SendBegin, Reason = reason, ParentBatch = parentBatch, Requested = inputs.Length });
            for (var index = 0; index < inputs.Length; index++)
            {
                inputs[index].Data.Keyboard.ExtraInfo = NextInputMarker();
                var key = inputs[index].Data.Keyboard;
                Trace(new() { Kind = InputTraceKind.SendItem, Reason = reason, Index = index,
                    Token = TraceToken(key.ExtraInfo), SentScan = key.ScanCode, SentVirtualKey = key.VirtualKey,
                    Up = (key.Flags & NativeMethods.KeyeventfKeyUp) != 0, Flags = key.Flags, Own = true });
            }
            result = _platform.Send(inputs);
            completed = true;
            if (result.Sent == (uint)inputs.Length)
            {
                if (DiagnosticLog.VerboseEnabled)
                    DiagnosticLog.WriteVerbose($"SendInput succeeded for {inputs.Length} keyboard event(s).");
            }
            else
            {
                _inputInjectionFailures++;
                DiagnosticLog.Write(
                    $"SendInput failed: requested={inputs.Length}, sent={result.Sent}; Windows error={result.Error}.");
                InputInjectionFailed?.Invoke(result.Error);
            }
        }
        finally
        {
            Trace(new() { Kind = InputTraceKind.SendEnd, Reason = reason, ParentBatch = parentBatch,
                Requested = inputs.Length, Sent = result.Sent, Error = result.Error, Index = completed ? 1 : 0,
                ElapsedTicks = Stopwatch.GetTimestamp() - started });
            _inputInjectionDepth--;
            _traceBatch = parentBatch;
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

    private void ResetActiveStates(bool resetEmacsTransient = true, InputTraceReason reason = InputTraceReason.Renewal)
    {
        Trace(new() { Kind = InputTraceKind.Boundary, Reason = reason });
        TraceState(InputTraceReason.ResetBegin);
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
        TraceState(InputTraceReason.ResetEnd, after: true);
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
            PostToHookThread(() => ResetActiveStates(reason: InputTraceReason.Suspend));
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
            PostToHookThread(() => ResetActiveStates(reason: InputTraceReason.SessionLock));
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
            ReconcileReleasedModifiers();
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
            ResetActiveStates(reason: InputTraceReason.Shutdown);
        }
        finally
        {
            UnregisterHooks();
            TraceState(InputTraceReason.Unregistered, after: true);
        }
        DiagnosticLog.Write("Dedicated hook thread stopped.");
    }

    private nint SuppressKeyboardEvent(InputTraceReason reason = InputTraceReason.None)
    {
        _traceVerdict = InputTraceVerdict.Suppressed;
        _traceReason = reason;
        _suppressedKeyboardEventCount++;
        return 1;
    }

    private AltModifierSnapshot GetAltSnapshot(AltState state) => new(
        state.IsDown, state.OriginalDownPassed, state.NativeDownSent,
        IsVirtualKeyDown(state.Side == AltSide.Left ? NativeMethods.VkLMenu : NativeMethods.VkRMenu));

    private void ReconcileReleasedModifiers()
    {
        _altReconcileChecks++;
        // Do not interpret our temporary releases during SendInput as user releases.
        if (_inputInjectionDepth != 0)
        {
            _altReconcileInjectionSkips++;
            if (_leftAlt.IsDown || _rightAlt.IsDown) _altReconcileInjectionSkipsWithAlt++;
            _maxAltReconcileInjectionDepth = Math.Max(_maxAltReconcileInjectionDepth, _inputInjectionDepth);
            _leftAlt.ReleaseMonitor.Reset();
            _rightAlt.ReleaseMonitor.Reset();
            _leftControl.ReleaseMonitor.Reset();
            _rightControl.ReleaseMonitor.Reset();
            return;
        }
        // Cheap prefilter: no token/process/desktop queries while idle, hidden,
        // or visibly held. A raw "up" only requests the guarded read; it never
        // authorizes recovery by itself. Evaluate all sides to reset monitors.
        var leftAlt = NeedsReleaseCheck(_leftAlt.IsDown, _leftAlt.OriginalDownPassed || _leftAlt.NativeDownSent,
            NativeMethods.VkLMenu, _leftAlt.ReleaseMonitor);
        var rightAlt = NeedsReleaseCheck(_rightAlt.IsDown, _rightAlt.OriginalDownPassed || _rightAlt.NativeDownSent,
            NativeMethods.VkRMenu, _rightAlt.ReleaseMonitor);
        var leftControl = NeedsReleaseCheck(_leftControl.IsDown, _leftControl.OriginalDownPassed || _leftControl.NativeDownSent,
            NativeMethods.VkLControl, _leftControl.ReleaseMonitor);
        var rightControl = NeedsReleaseCheck(_rightControl.IsDown, _rightControl.OriginalDownPassed || _rightControl.NativeDownSent,
            NativeMethods.VkRControl, _rightControl.ReleaseMonitor);
        if (!(leftAlt || rightAlt || leftControl || rightControl)) return;
        var sample = _platform.ReadModifierState();
        var now = _platform.MonotonicMilliseconds;
        if (leftAlt) ReconcileReleasedAlt(_leftAlt, sample.Reliable, sample.Foreground, sample.LeftAltDown, now);
        if (rightAlt) ReconcileReleasedAlt(_rightAlt, sample.Reliable, sample.Foreground, sample.RightAltDown, now);
        if (leftControl) ReconcileReleasedControl(_leftControl, sample.Reliable, sample.Foreground, sample.LeftControlDown, now);
        if (rightControl) ReconcileReleasedControl(_rightControl, sample.Reliable, sample.Foreground, sample.RightControlDown, now);
    }

    private bool NeedsReleaseCheck(bool tracked, bool visible, uint key, ModifierReleaseMonitor monitor)
    {
        if (tracked && visible && !IsVirtualKeyDown(key)) return true;
        monitor.Reset();
        return false;
    }

    private void ReconcileReleasedControl(DeferredControlState state, bool reliable, nint foreground, bool asyncDown, long now)
    {
        if (!state.ReleaseMonitor.Observe(state.IsDown, state.OriginalDownPassed || state.NativeDownSent,
            asyncDown, reliable, foreground, now)) return;
        var key = state.Side == ModifierSides.Left ? InputTraceKey.LeftControl : InputTraceKey.RightControl;
        var sequence = Trace(new() { Kind = InputTraceKind.State, Reason = InputTraceReason.RecoveredControl, Key = key });
        DiagnosticLog.Write($"Recovered released {state.Side} Ctrl: originalPassed={state.OriginalDownPassed}, " +
            $"nativeSent={state.NativeDownSent}, heldMs={Environment.TickCount64 - state.DownAtMilliseconds}, " +
            $"traceSession={InputTrace.Session:N}, traceSeq={sequence}.");
        state.Reset(); // Windows already reports up: never invent a tap or a release.
        _controlStateRecoveries++;
        Trace(new() { Kind = InputTraceKind.State, Reason = InputTraceReason.RecoveredControl, Key = key, Index = 1 });
        RequestRefresh("released Ctrl state reconciled");
    }

    private void ReconcileReleasedAlt(AltState state, bool reliable, nint foreground, bool asyncDown, long now)
    {
        var snapshot = new AltModifierSnapshot(state.IsDown, state.OriginalDownPassed, state.NativeDownSent, asyncDown);
        if (!state.ReleaseMonitor.Observe(snapshot, reliable, foreground, now)) return;
        DiagnosticLog.Write($"Recovered released {state.Side} Alt: originalPassed={state.OriginalDownPassed}, " +
            $"nativeSent={state.NativeDownSent}, heldMs={Environment.TickCount64 - state.DownAtMilliseconds}.");
        // Windows already reports up. Clear bookkeeping without inventing a tap or injecting input.
        TraceState(InputTraceReason.RecoveredAlt);
        state.Reset();
        _altStateRecoveries++;
        TraceState(InputTraceReason.RecoveredAlt, after: true);
        RequestRefresh("released Alt state reconciled");
    }

    private void WriteHookStatus(string reason)
    {
        var traceSequence = Trace(new() { Kind = InputTraceKind.Status,
            Reason = reason switch
            {
                "startup" => InputTraceReason.Startup,
                "settings changed" => InputTraceReason.Settings,
                "before active state reset" => InputTraceReason.BeforeReset,
                "shutdown" => InputTraceReason.Shutdown,
                _ => InputTraceReason.Maintenance
            }, Index = _hookGeneration, KeyboardCallbacks = _keyboardCallbackCount, MouseCallbacks = _mouseCallbackCount });
        // No ordinary key codes, typed text, window titles or clipboard contents.
        var layout = GetForegroundKeyboardLayout();
        DiagnosticLog.Write($"Hook status ({reason}): generation={_hookGeneration}, traceSession={InputTrace.Session:N}, traceSeq={traceSequence}, " +
            $"enabled={_settings.Enabled}, emacsEnabled={_settings.EmacsEnabled}, emacsActive={_emacsState.Active}, " +
            $"keyboardCallbacks={_keyboardCallbackCount}, mouseCallbacks={_mouseCallbackCount}, " +
            $"suppressed={_suppressedKeyboardEventCount}, injectionFailures={_inputInjectionFailures}, " +
            $"injectionCalls={_inputInjectionCalls}, injectionDepth={_inputInjectionDepth}, " +
            $"maxInjectionDepth={_maxInputInjectionDepth}, keyboardCallbacksDuringInjection={_keyboardCallbacksDuringInjection}, " +
            $"altReconcileChecks={_altReconcileChecks}, altReconcileInjectionSkips={_altReconcileInjectionSkips}, " +
            $"altReconcileInjectionSkipsWithAlt={_altReconcileInjectionSkipsWithAlt}, " +
            $"maxAltReconcileInjectionDepth={_maxAltReconcileInjectionDepth}, " +
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
            $"altStateRecoveries={_altStateRecoveries}, controlStateRecoveries={_controlStateRecoveries}, droppedLogs={DiagnosticLog.DroppedEntries}, " +
            $"traceDropped={InputTrace.Dropped}, traceRecordFailures={InputTrace.RecordFailures}, " +
            $"traceWriteError={InputTrace.LastError?.GetType().Name ?? "none"}.");
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
        _hookThread?.Dispose();
    }

    private enum AltSide
    {
        Left,
        Right
    }

    private sealed class AltState(AltSide side)
    {
        public ModifierReleaseAudit ReleaseAudit;
        public AltSide Side { get; } = side;
        public ModifierReleaseMonitor ReleaseMonitor { get; } = new();

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
            ReleaseMonitor.Reset();
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
            ReleaseMonitor.Reset();
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
        public ModifierReleaseAudit ReleaseAudit;
        public ModifierReleaseMonitor ReleaseMonitor { get; } = new();
        public long DownAtMilliseconds { get; private set; }
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
            ReleaseMonitor.Reset();
            DownAtMilliseconds = Environment.TickCount64;
            IsDown = true;
            OriginalDownPassed = false;
            NativeDownSent = false;
            ConsumedByEmacs = false;
            ScanCode = checked((ushort)scanCode);
            IsExtended = isExtended;
        }

        public void Reset()
        {
            ReleaseMonitor.Reset();
            DownAtMilliseconds = 0;
            IsDown = false;
            OriginalDownPassed = false;
            NativeDownSent = false;
            ConsumedByEmacs = false;
            ScanCode = 0;
            IsExtended = false;
        }
    }
}
