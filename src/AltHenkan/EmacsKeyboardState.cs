namespace AltHenkan;

// Pure gesture state: no Windows API, disk I/O or UI operations.
internal sealed class EmacsKeyboardState
{
    private bool _capsLockCaptured;
    private bool _capsLockPassed;
    private readonly Dictionary<uint, EmacsShortcut> _capturedKeys = [];
    private readonly HashSet<uint> _passedKeys = [];

    public bool Active { get; private set; }
    public bool GestureActive => _capsLockCaptured || _capturedKeys.Count > 0;

    public bool Disable()
        => SetActive(false);

    public bool SetActive(bool active)
    {
        var changed = Active != active;
        Active = active;
        // Keep captured key-ups paired even if settings change while a key is held.
        return changed;
    }

    public bool HandleCapsLock(bool keyDown, bool enabled, out bool modeChanged)
    {
        modeChanged = false;
        if (!keyDown)
        {
            var captured = _capsLockCaptured;
            _capsLockCaptured = false;
            _capsLockPassed = false;
            return captured;
        }
        if (_capsLockCaptured)
        {
            return true;
        }
        if (_capsLockPassed) return false;
        if (!enabled)
        {
            _capsLockPassed = true;
            return false;
        }
        _capsLockCaptured = true;
        Active = !Active;
        modeChanged = true;
        return true;
    }

    public bool HandleKey(uint key, bool keyDown, bool enabled,
        NavigationModifiers modifiers, EmacsShortcutSettings settings, out EmacsBinding? binding,
        EmacsSideFilter? sideFilter = null)
    {
        binding = null;
        if (!EmacsBindings.IsSourceKey(key)) return false;
        if (!keyDown)
        {
            _passedKeys.Remove(key);
            return _capturedKeys.Remove(key);
        }
        if (_passedKeys.Contains(key)) return false;
        var captured = _capturedKeys.TryGetValue(key, out var originalShortcut);
        if (enabled && Active)
        {
            binding = EmacsBindings.Resolve(key, modifiers, settings, sideFilter);
            if (binding is not null && (!captured || originalShortcut == binding.Shortcut))
            {
                _capturedKeys[key] = binding.Shortcut;
                return true;
            }
        }
        binding = null;
        if (!captured) _passedKeys.Add(key);
        return captured;
    }

    public void ResetTransient()
    {
        _capsLockCaptured = false;
        _capsLockPassed = false;
        _capturedKeys.Clear();
        _passedKeys.Clear();
        // A periodic hook renewal must not change the user's selected mode.
    }

}
