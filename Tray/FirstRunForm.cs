using UniversalPresence.Configuration;

namespace UniversalPresence.Tray;

public sealed class FirstRunForm : Form
{
    private readonly AppOptions _options;
    private readonly ComboBox _lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly CheckBox _autostart = new() { Text = "Start with Windows", Checked = true };
    private readonly CheckBox _toasts = new() { Text = "Show track change toasts", Checked = true };
    private readonly CheckBox _history = new() { Text = "Keep listening history", Checked = true };

    public FirstRunForm(AppOptions options)
    {
        _options = options;
        Text = "Welcome to Universal Presence";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 220);
        MaximizeBox = false; MinimizeBox = false;

        var y = 12;
        var l = new Label { Text = "Language:", Location = new Point(12, y + 3), AutoSize = true };
        _lang.Location = new Point(100, y);
        foreach (var c in new[] { "en", "es", "pt", "fr", "de", "it", "nl", "pl", "ru", "uk", "tr", "ja", "ko", "zh-CN", "zh-TW", "id", "sv", "cs" }) _lang.Items.Add(c);
        _lang.SelectedItem = string.IsNullOrWhiteSpace(options.Language) ? "en" : options.Language;
        Controls.Add(l); Controls.Add(_lang);

        y += 36;
        _autostart.Location = new Point(12, y); Controls.Add(_autostart); y += 26;
        _toasts.Location = new Point(12, y); Controls.Add(_toasts); y += 26;
        _history.Location = new Point(12, y); Controls.Add(_history); y += 36;

        var ok = new Button { Text = "Get started", DialogResult = DialogResult.OK, Location = new Point(12, y), Width = 120 };
        Controls.Add(ok);
        AcceptButton = ok;
    }

    public void Apply()
    {
        _options.Language = _lang.SelectedItem?.ToString() ?? "en";
        _options.StartWithWindows = _autostart.Checked;
        _options.ShowTrackChangeToast = _toasts.Checked;
        _options.ListeningHistory = _history.Checked;
        _options.FirstRunCompleted = true;
        try { _options.Save(); } catch { }
        WindowsAutostart.Apply(_options.StartWithWindows);
    }
}