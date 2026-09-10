namespace AltHenkan;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore _settingsStore = new();
    private readonly AltInputService _inputService;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _enabledMenuItem;
    private AppSettings _settings;
    private bool _disposed;
    private long _lastInputErrorAtMilliseconds;

    public TrayApplicationContext()
    {
        _settings = _settingsStore.Load();
        _inputService = new AltInputService(_settings);

        _enabledMenuItem = new ToolStripMenuItem("有効")
        {
            Checked = _settings.Enabled,
            CheckOnClick = true
        };
        _enabledMenuItem.Click += (_, _) => SetEnabled(_enabledMenuItem.Checked);

        var settingsMenuItem = new ToolStripMenuItem("設定...");
        settingsMenuItem.Click += (_, _) => ShowSettings();

        var exitMenuItem = new ToolStripMenuItem("終了");
        exitMenuItem.Click += (_, _) => ExitThread();

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.AddRange([
            _enabledMenuItem,
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
        _inputService.InputInjectionFailed += ShowInputInjectionError;
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
        SaveSettings();
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

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _notifyIcon.Visible = false;
            _inputService.InputInjectionFailed -= ShowInputInjectionError;
            _notifyIcon.Dispose();
            _inputService.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
