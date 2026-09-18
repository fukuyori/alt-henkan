namespace AltHenkan;

internal sealed class ModeOverlayForm : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _hideTimer;
    private bool _resourcesDisposed;

    public ModeOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(35, 40, 48);
        ForeColor = Color.White;
        ClientSize = new Size(280, 64);
        AutoScaleMode = AutoScaleMode.Dpi;
        _label = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14, FontStyle.Bold)
        };
        Controls.Add(_label);
        _hideTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _hideTimer.Tick += (_, _) => Dismiss();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
            parameters.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080;
            return parameters;
        }
    }

    public void ShowMode(bool emacs)
    {
        _hideTimer.Stop();
        _label.Text = emacs ? "Emacsモード" : "通常モード";
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2,
            Math.Max(area.Top, area.Bottom - Height - 80));
        Show();
        _hideTimer.Start();
    }

    public void Dismiss()
    {
        _hideTimer.Stop();
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _hideTimer.Dispose();
            _label.Font.Dispose();
        }
        base.Dispose(disposing);
    }
}
