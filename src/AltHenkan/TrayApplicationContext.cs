namespace AltHenkan;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore _settingsStore = new();
    private readonly AltInputService _inputService;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _enabledMenuItem;
    private readonly ToolStripMenuItem _modeMenuItem;
    private readonly ModeOverlayForm _modeOverlay = new();
    private readonly Control _uiDispatcher;
    private AppSettings _settings;
    private bool _disposed;
    private bool _emacsActive;
    private long _lastInputErrorAtMilliseconds;

    public TrayApplicationContext()
    {
        _settings = _settingsStore.Load();
        _inputService = new AltInputService(_settings);
        _uiDispatcher = new Control();
        _ = _uiDispatcher.Handle;

        _enabledMenuItem = new ToolStripMenuItem("有効")
        {
            Checked = _settings.Enabled,
            CheckOnClick = true
        };
        _enabledMenuItem.Click += (_, _) => SetEnabled(_enabledMenuItem.Checked);
        _modeMenuItem = new ToolStripMenuItem { Enabled = false };

        var settingsMenuItem = new ToolStripMenuItem("設定...");
        settingsMenuItem.Click += (_, _) => ShowSettings();

        var exitMenuItem = new ToolStripMenuItem("終了");
        exitMenuItem.Click += (_, _) => ExitThread();

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.AddRange([
            _enabledMenuItem,
            _modeMenuItem,
            settingsMenuItem,
            new ToolStripSeparator(),
            exitMenuItem
        ]);

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Alt Henkan",
            ContextMenuStrip = contextMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettings();
        _inputService.InputInjectionFailed += QueueInputInjectionError;
        _inputService.EmacsModeChanged += QueueModeChanged;
        UpdateModeDisplay();
    }

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("AltHenkan.ico");
        if (stream is null)
        {
            return SystemIcons.Application;
        }

        // Pick the size the shell uses for tray icons on the current DPI.
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    private void SetEnabled(bool enabled)
    {
        _settings = _settings with { Enabled = enabled };
        _inputService.UpdateSettings(_settings);
        UpdateModeDisplay();
        SaveSettings();
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        _settings = form.CreateSettings(_settings);
        _inputService.UpdateSettings(_settings);
        _enabledMenuItem.Checked = _settings.Enabled;
        if (!_settings.ShowModeChangeOverlay)
        {
            _modeOverlay.Dismiss();
        }
        UpdateModeDisplay();
        SaveSettings();
    }

    private void UpdateModeDisplay()
    {
        var featureEnabled = _settings.Enabled && _settings.EmacsEnabled;
        if (!featureEnabled)
        {
            _emacsActive = false;
            _modeOverlay.Dismiss();
        }
        var mode = !featureEnabled ? "Emacs機能：オフ" : _emacsActive ? "モード：Emacs" : "モード：通常";
        _modeMenuItem.Text = mode;
        _notifyIcon.Text = $"Alt Henkan — {mode}";
    }

    private void QueueModeChanged(bool active)
    {
        // Queue only: the input callback must never wait for rendering on the UI thread.
        try
        {
            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                _emacsActive = active;
                UpdateModeDisplay();
                if (_settings.Enabled && _settings.EmacsEnabled && _settings.ShowModeChangeOverlay)
                {
                    _modeOverlay.ShowMode(_emacsActive);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // UI shutdown raced with an input callback.
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"設定を保存できませんでした。\n\n{exception.Message}",
                "Alt Henkan",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ShowInputInjectionError(int errorCode)
    {
        var now = Environment.TickCount64;
        if (now - _lastInputErrorAtMilliseconds < 5000)
        {
            return;
        }

        _lastInputErrorAtMilliseconds = now;
        _notifyIcon.BalloonTipTitle = "Alt Henkan";
        _notifyIcon.BalloonTipText = errorCode == 0
            ? "キー入力を送信できませんでした。対象アプリとの権限差を確認してください。"
            : $"キー入力を送信できませんでした。（Windowsエラー {errorCode}）";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private void QueueInputInjectionError(int errorCode)
    {
        // Called on the hook thread. It must never wait for the UI or show a balloon here.
        try
        {
            _uiDispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                {
                    ShowInputInjectionError(errorCode);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // UI shutdown raced with an in-flight hook callback.
        }
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _notifyIcon.Visible = false;
            _inputService.InputInjectionFailed -= QueueInputInjectionError;
            _inputService.EmacsModeChanged -= QueueModeChanged;
            _inputService.Dispose();
            _modeOverlay.Dispose();
            _uiDispatcher.Dispose();
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
