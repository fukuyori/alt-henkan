using System.Diagnostics;

namespace AltHenkan;

internal sealed partial class AltInputService
{
    private long _traceObservationSequence;
    private long _traceObservation;
    private long _traceParent;
    private long _traceBatchSequence;
    private long _traceBatch;
    private uint _traceToken;
    private InputTraceVerdict _traceVerdict;
    private InputTraceReason _traceReason;

    internal static bool IsOwnInput(nuint marker) => unchecked((uint)marker) == (uint)OwnInputMarker;
    internal static uint TraceToken(nuint marker) =>
        Environment.Is64BitProcess && IsOwnInput(marker) ? (uint)((ulong)marker >> 32) : 0;
    private nuint NextInputMarker()
    {
        if (!Environment.Is64BitProcess) return OwnInputMarker;
        if (++_traceToken == 0) ++_traceToken;
        return unchecked((nuint)(((ulong)_traceToken << 32) | (uint)OwnInputMarker));
    }

    private InputTraceSnapshot TraceSnapshot()
    {
        static uint Bits(bool down, bool passed, bool sent, bool consumed) =>
            (down ? 1u : 0) | (passed ? 2u : 0) | (sent ? 4u : 0) | (consumed ? 8u : 0);
        var state = Bits(_leftControl.IsDown, _leftControl.OriginalDownPassed, _leftControl.NativeDownSent, _leftControl.ConsumedByEmacs) |
            Bits(_rightControl.IsDown, _rightControl.OriginalDownPassed, _rightControl.NativeDownSent, _rightControl.ConsumedByEmacs) << 4 |
            Bits(_leftAlt.IsDown, _leftAlt.OriginalDownPassed, _leftAlt.NativeDownSent, _leftAlt.ConsumedByEmacs) << 8 |
            Bits(_rightAlt.IsDown, _rightAlt.OriginalDownPassed, _rightAlt.NativeDownSent, _rightAlt.ConsumedByEmacs) << 12 |
            (_leftAlt.DeferredShiftChord ? 1u << 16 : 0) | (_rightAlt.DeferredShiftChord ? 1u << 17 : 0) |
            (_emacsState.Active ? 1u << 18 : 0) | (_emacsState.CapsLockCaptured ? 1u << 19 : 0) |
            (_settings.Enabled ? 1u << 20 : 0) | (_settings.EmacsEnabled ? 1u << 21 : 0);
        // Raw samples, NOT proof of physical state or access reliability, and pre-event in hooks.
        byte async = 0;
        if (IsVirtualKeyDown(NativeMethods.VkLControl)) async |= 1;
        if (IsVirtualKeyDown(NativeMethods.VkRControl)) async |= 2;
        if (IsVirtualKeyDown(NativeMethods.VkLMenu)) async |= 4;
        if (IsVirtualKeyDown(NativeMethods.VkRMenu)) async |= 8;
        if (IsVirtualKeyDown(NativeMethods.VkShift)) async |= 16;
        if (IsVirtualKeyDown(NativeMethods.VkLWin) || IsVirtualKeyDown(NativeMethods.VkRWin)) async |= 32;
        return new(state, async, _emacsState.CapturedKeyCount, _nonAltKeysDown.Count);
    }

    private long Trace(InputTraceEvent entry)
    {
        if (!InputTrace.Enabled) return 0;
        try
        {
            return InputTrace.Record(entry with
            {
                Observation = _traceObservation, Parent = _traceParent, Batch = _traceBatch,
                Depth = _inputInjectionDepth,
                Snapshot = entry.Kind == InputTraceKind.SendItem ? default : TraceSnapshot()
            });
        }
        catch { InputTrace.RecordFailure(); return 0; }
    }

    private void TraceState(InputTraceReason reason, bool after = false) =>
        Trace(new() { Kind = InputTraceKind.State, Reason = reason, Index = after ? 1 : 0 });

    private void TraceSettings(bool after)
    {
        if (!InputTrace.Enabled) return;
        uint shortcuts = 0;
        foreach (var binding in EmacsBindings.All)
            if (_settings.EmacsShortcuts.IsEnabled(binding.Shortcut)) shortcuts |= 1u << (int)binding.Shortcut;
        var bits = (uint)_settings.EmacsControlSide | (uint)_settings.EmacsAltSide << 2 |
            (uint)_settings.EmacsInitialMode << 4 | (_settings.LeftAltLongPressEnabled ? 1u << 5 : 0) |
            (_settings.RightAltLongPressEnabled ? 1u << 6 : 0);
        Trace(new() { Kind = InputTraceKind.Settings, Reason = InputTraceReason.Settings, Index = after ? 1 : 0,
            SettingsBits = bits, LongPressMilliseconds = _settings.LongPressMilliseconds, Shortcuts = shortcuts });
    }

    private nint ForwardKeyboard(int code, nint wParam, nint lParam, InputTraceReason reason = InputTraceReason.None)
    {
        _traceVerdict = InputTraceVerdict.Forwarded;
        _traceReason = reason;
        return _nextKeyboardHook(code, wParam, lParam);
    }

    private nint TraceKeyboard(int code, nint wParam, nint lParam)
    {
        var oldObservation = _traceObservation;
        var oldParent = _traceParent;
        var oldVerdict = _traceVerdict;
        var oldReason = _traceReason;
        _traceParent = oldObservation;
        _traceObservation = ++_traceObservationSequence;
        _traceVerdict = InputTraceVerdict.None;
        _traceReason = InputTraceReason.None;
        var started = Stopwatch.GetTimestamp();
        var result = (nint)0;
        var entry = new InputTraceEvent { Kind = InputTraceKind.KeyBegin };
        try
        {
            try
            {
                if (code >= 0 && !_disposed)
                {
                    var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
                    entry = entry with
                    {
                        Key = InputTrace.Classify(data.VirtualKeyCode, data.Flags), Flags = data.Flags,
                        Up = (int)wParam is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp,
                        EventTime = data.Time, Own = IsOwnInput(data.ExtraInfo), Token = TraceToken(data.ExtraInfo)
                    };
                    var foreground = _platform.Foreground();
                    entry = entry with { Foreground = foreground.Window, ForegroundProcess = foreground.Process, ForegroundThread = foreground.Thread };
                }
            }
            catch { InputTrace.RecordFailure(); }
            Trace(entry);
            result = KeyboardHookCore(code, wParam, lParam);
            return result;
        }
        catch (Exception exception)
        {
            _traceVerdict = InputTraceVerdict.Exception;
            _refreshRequested = true;
            DiagnosticLog.Write($"Keyboard callback failed; renewal requested: obs={_traceObservation}, {exception}");
            Trace(new() { Kind = InputTraceKind.Fallback, Reason = InputTraceReason.CallbackFailed });
            var fallback = _nextKeyboardHook(code, wParam, lParam);
            Trace(new() { Kind = InputTraceKind.Fallback, Reason = InputTraceReason.CallbackFailed,
                Index = 1, Verdict = InputTraceVerdict.Forwarded, Result = (long)fallback });
            return fallback;
        }
        finally
        {
            Trace(entry with { Kind = InputTraceKind.KeyEnd, Verdict = _traceVerdict, Reason = _traceReason,
                Result = (long)result, ElapsedTicks = Stopwatch.GetTimestamp() - started });
            _traceObservation = oldObservation;
            _traceParent = oldParent;
            _traceVerdict = oldVerdict;
            _traceReason = oldReason;
        }
    }

    private nint TraceMouse(int code, nint wParam, nint lParam)
    {
        // Mouse movement is deliberately not recorded. Buttons/wheel can promote modifiers.
        if (code < 0 || _disposed || !IsMouseInteraction((int)wParam)) return MouseHookCore(code, wParam, lParam);
        var oldObservation = _traceObservation;
        var oldParent = _traceParent;
        _traceParent = oldObservation;
        _traceObservation = ++_traceObservationSequence;
        var result = (nint)0;
        var verdict = InputTraceVerdict.Exception;
        var started = Stopwatch.GetTimestamp();
        var entry = new InputTraceEvent { Kind = InputTraceKind.MouseBegin, Index = (int)wParam };
        try
        {
            try
            {
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MouseLlHookStruct>(lParam);
                entry = entry with { Flags = data.Flags, EventTime = data.Time };
                var foreground = _platform.Foreground();
                entry = entry with { Foreground = foreground.Window, ForegroundProcess = foreground.Process, ForegroundThread = foreground.Thread };
            }
            catch { InputTrace.RecordFailure(); }
            Trace(entry);
            result = MouseHookCore(code, wParam, lParam);
            verdict = InputTraceVerdict.Forwarded;
            return result;
        }
        catch (Exception exception)
        {
            _refreshRequested = true;
            DiagnosticLog.Write($"Mouse callback failed; renewal requested: obs={_traceObservation}, {exception}");
            Trace(new() { Kind = InputTraceKind.Fallback, Reason = InputTraceReason.CallbackFailed });
            var fallback = _platform.NextMouse(_mouseHook, code, wParam, lParam);
            Trace(new() { Kind = InputTraceKind.Fallback, Reason = InputTraceReason.CallbackFailed,
                Index = 1, Verdict = InputTraceVerdict.Forwarded, Result = (long)fallback });
            return fallback;
        }
        finally
        {
            Trace(entry with { Kind = InputTraceKind.MouseEnd, Verdict = verdict, Result = (long)result,
                ElapsedTicks = Stopwatch.GetTimestamp() - started });
            _traceObservation = oldObservation;
            _traceParent = oldParent;
        }
    }
}
