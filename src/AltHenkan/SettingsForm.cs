namespace AltHenkan;

internal sealed class SettingsForm : Form
{
    private readonly CheckBox _leftAltLongPressCheckBox;
    private readonly CheckBox _rightAltLongPressCheckBox;
    private readonly NumericUpDown _longPressMillisecondsInput;

    public SettingsForm(AppSettings settings)
    {
        Text = "Alt Henkan 設定";
        ClientSize = new Size(500, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var explanation = new Label
        {
            AutoSize = true,
            Location = new Point(20, 18),
            Text = "短押し: 左Alt＝IMEオフ、右Alt＝IMEオン\n長押し: 有効にした側でWindows本来のAlt単独操作を実行"
        };

        _leftAltLongPressCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(24, 72),
            Text = "左Altの長押しで本来のAlt単独操作を実行する",
            Checked = settings.LeftAltLongPressEnabled
        };

        _rightAltLongPressCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(24, 104),
            Text = "右Altの長押しで本来のAlt単独操作を実行する",
            Checked = settings.RightAltLongPressEnabled
        };

        var thresholdLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 146),
            Text = "長押し判定時間:"
        };

        _longPressMillisecondsInput = new NumericUpDown
        {
            Location = new Point(150, 142),
            Minimum = AppSettings.MinimumLongPressMilliseconds,
            Maximum = AppSettings.MaximumLongPressMilliseconds,
            Increment = 50,
            Value = settings.LongPressMilliseconds,
            Width = 90,
            TextAlign = HorizontalAlignment.Right
        };

        var millisecondsLabel = new Label
        {
            AutoSize = true,
            Location = new Point(247, 146),
            Text = "ミリ秒"
        };

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(318, 203),
            Size = new Size(75, 29)
        };

        var cancelButton = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(401, 203),
            Size = new Size(79, 29)
        };

        AcceptButton = okButton;
        CancelButton = cancelButton;
        Controls.AddRange([
            explanation,
            _leftAltLongPressCheckBox,
            _rightAltLongPressCheckBox,
            thresholdLabel,
            _longPressMillisecondsInput,
            millisecondsLabel,
            okButton,
            cancelButton
        ]);
    }

    public AppSettings CreateSettings(AppSettings current)
    {
        return current with
        {
            LeftAltLongPressEnabled = _leftAltLongPressCheckBox.Checked,
            RightAltLongPressEnabled = _rightAltLongPressCheckBox.Checked,
            LongPressMilliseconds = decimal.ToInt32(_longPressMillisecondsInput.Value)
        };
    }
}

