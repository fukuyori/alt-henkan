namespace AltHenkan;

internal sealed class SettingsForm : Form
{
    private readonly CheckBox _leftAltLongPressCheckBox;
    private readonly CheckBox _rightAltLongPressCheckBox;
    private readonly NumericUpDown _longPressMillisecondsInput;
    private readonly CheckBox _emacsEnabledCheckBox;
    private readonly CheckBox _showModeOverlayCheckBox;
    private readonly Dictionary<EmacsShortcut, CheckBox> _shortcutCheckBoxes = [];

    public SettingsForm(AppSettings settings)
    {
        settings = settings.Normalize();
        Text = "Alt Henkan 設定";
        ClientSize = new Size(570, 490);
        MinimumSize = new Size(480, 430);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var altPage = AddPage(tabs, "Alt設定");
        var emacsPage = AddPage(tabs, "Emacs設定");
        var displayPage = AddPage(tabs, "表示設定");

        altPage.Controls.Add(Description("短押し：左Alt＝IMEオフ、右Alt＝IMEオン\n長押し：有効にした側でWindows本来のAlt単独操作を実行"));
        _leftAltLongPressCheckBox = Option("左Altの長押しで本来のAlt単独操作を実行する", settings.LeftAltLongPressEnabled);
        _rightAltLongPressCheckBox = Option("右Altの長押しで本来のAlt単独操作を実行する", settings.RightAltLongPressEnabled);
        altPage.Controls.Add(_leftAltLongPressCheckBox);
        altPage.Controls.Add(_rightAltLongPressCheckBox);
        var threshold = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        threshold.Controls.Add(new Label { Text = "長押し判定時間：", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        _longPressMillisecondsInput = new NumericUpDown
        {
            Minimum = AppSettings.MinimumLongPressMilliseconds,
            Maximum = AppSettings.MaximumLongPressMilliseconds,
            Increment = 50,
            Value = settings.LongPressMilliseconds,
            Width = 90,
            TextAlign = HorizontalAlignment.Right
        };
        threshold.Controls.Add(_longPressMillisecondsInput);
        threshold.Controls.Add(new Label { Text = "ミリ秒", AutoSize = true, Margin = new Padding(8, 6, 0, 0) });
        altPage.Controls.Add(threshold);

        _emacsEnabledCheckBox = Option("CapsLockによるEmacsモード切り替えを有効にする", settings.EmacsEnabled);
        emacsPage.Controls.Add(_emacsEnabledCheckBox);
        emacsPage.Controls.Add(Description("CapsLockで通常／Emacsを切り替えます。文字入力は通常どおりです。\n機能OFF時はCapsLock本来の大文字固定に戻ります。\nEmacsモード中、ONの操作だけを変換します。OFFの操作はそのまま渡します。"));
        foreach (var binding in EmacsBindings.All)
        {
            var checkBox = Option(binding.Label, settings.EmacsShortcuts.IsEnabled(binding.Shortcut));
            _shortcutCheckBoxes.Add(binding.Shortcut, checkBox);
            emacsPage.Controls.Add(checkBox);
        }
        emacsPage.Controls.Add(Description("例：Ctrl+Aの全選択、Ctrl+Pの印刷などは移動操作に置き換わります。\nShiftや他の修飾キーを追加した組み合わせは変換しません。"));
        _emacsEnabledCheckBox.CheckedChanged += (_, _) => UpdateShortcutControls();
        UpdateShortcutControls();

        _showModeOverlayCheckBox = Option("モード切り替え時に画面へ状態を表示する", settings.ShowModeChangeOverlay);
        displayPage.Controls.Add(_showModeOverlayCheckBox);
        displayPage.Controls.Add(Description("「通常モード」「Emacsモード」を約1.2秒間表示します。\n非表示にしても、通知領域のメニューで現在のモードを確認できます。"));

        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 30) };
        var cancelButton = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 30) };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0)
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(okButton);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(tabs, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private static FlowLayoutPanel AddPage(TabControl tabs, string title)
    {
        var page = new TabPage(title);
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(16)
        };
        page.Controls.Add(content);
        tabs.TabPages.Add(page);
        return content;
    }

    private static Label Description(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 14)
    };

    private static CheckBox Option(string text, bool enabled) => new()
    {
        Text = text, Checked = enabled, AutoSize = true, Margin = new Padding(0, 0, 0, 10)
    };

    private void UpdateShortcutControls()
    {
        foreach (var checkBox in _shortcutCheckBoxes.Values)
        {
            checkBox.Enabled = _emacsEnabledCheckBox.Checked;
        }
    }

    public AppSettings CreateSettings(AppSettings current)
    {
        var shortcuts = current.Normalize().EmacsShortcuts;
        foreach (var (shortcut, checkBox) in _shortcutCheckBoxes)
        {
            shortcuts = shortcuts.WithEnabled(shortcut, checkBox.Checked);
        }
        return current with
        {
            LeftAltLongPressEnabled = _leftAltLongPressCheckBox.Checked,
            RightAltLongPressEnabled = _rightAltLongPressCheckBox.Checked,
            LongPressMilliseconds = decimal.ToInt32(_longPressMillisecondsInput.Value),
            EmacsEnabled = _emacsEnabledCheckBox.Checked,
            ShowModeChangeOverlay = _showModeOverlayCheckBox.Checked,
            EmacsShortcuts = shortcuts
        };
    }
}
