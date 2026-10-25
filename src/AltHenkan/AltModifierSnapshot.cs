namespace AltHenkan;

internal readonly record struct AltModifierSnapshot(
    bool TrackedDown, bool OriginalDownPassed, bool NativeDownSent, bool AsyncDown)
{
    public bool NativeVisible => OriginalDownPassed || NativeDownSent;

    // A deferred Alt is intentionally invisible to Windows. Native Alt, however,
    // must not be kept active solely by our possibly stale tracked state.
    public bool Active => AsyncDown || (TrackedDown && !NativeVisible);
    public bool ShouldRestore => TrackedDown && NativeVisible && AsyncDown;
}

internal sealed class ModifierReleaseMonitor
{
    public const int ConfirmationMilliseconds = 1000;
    public const int MaximumSampleGapMilliseconds = 2500;
    private long? _releasedSince;
    private long _lastSample;
    private nint _foreground;

    public bool Observe(AltModifierSnapshot state, bool reliable, nint foreground, long now)
        => Observe(state.TrackedDown, state.NativeVisible, state.AsyncDown, reliable, foreground, now);

    public bool Observe(bool trackedDown, bool nativeVisible, bool asyncDown, bool reliable, nint foreground, long now)
    {
        if (!reliable || foreground == 0 || !trackedDown || !nativeVisible || asyncDown)
        {
            Reset();
            return false;
        }
        if (_releasedSince is null || _foreground != foreground || now < _lastSample ||
            now - _lastSample > MaximumSampleGapMilliseconds)
        {
            _releasedSince = now;
            _foreground = foreground;
        }
        _lastSample = now;
        return now - _releasedSince.Value >= ConfirmationMilliseconds;
    }

    public void Reset()
    {
        _releasedSince = null;
        _lastSample = 0;
        _foreground = 0;
    }
}
