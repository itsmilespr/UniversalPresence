using System.Drawing;
using UniversalPresence.Configuration;
using UniversalPresence.Localization;

namespace UniversalPresence.Tray;

/// <summary>
/// Asistente de primera ejecución: idioma, arranque con Windows y un resumen de qué hace la
/// aplicación. Todo cabe en una ventana y se puede volver a cambiar en Configuración.
/// </summary>
public sealed class FirstRunForm : Form
{
    private readonly AppOptions _options;
    private readonly bool _discordRunning;
    private UiStrings _strings;

    private readonly Label _title = new();
    private readonly Label _intro = new();
    private readonly Label _languageLabel = new();
    private readonly Label _optionsLabel = new();
    private readonly Label _coversLabel = new();
    private readonly Label _privacyLabel = new();
    private readonly Label _discordLabel = new();
    private readonly CheckBox _openSettings = new();

    private readonly ComboBox _language = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "Language",
    };

    private readonly CheckBox _autostart = new() { AutoSize = true };
    private readonly CheckBox _toasts = new() { AutoSize = true };
    private readonly CheckBox _history = new() { AutoSize = true };
    private readonly CheckBox _overlay = new() { AutoSize = true };
    private readonly Button _startButton = new() { AutoSize = true };

    public FirstRunForm(AppOptions options, bool discordRunning)
    {
        _options = options;
        _discordRunning = discordRunning;
        _strings = UiStrings.For(options.Language);

        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 588);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(243, 245, 249);
        Icon = TrayIcons.Get(TrayVisualState.Playing);

        foreach (var (code, name) in LanguageCatalog())
        {
            _language.Items.Add(new LanguageItem(code, name));
        }

        SelectLanguage(string.IsNullOrWhiteSpace(options.Language) ? "en" : options.Language);

        _language.SelectedIndexChanged += (_, _) =>
        {
            if (_language.SelectedItem is LanguageItem item)
            {
                _strings = UiStrings.For(item.Code);
                RefreshTexts();
            }
        };

        _autostart.Checked = options.StartWithWindows;
        _autostart.AccessibleName = "Start with Windows";
        _toasts.Checked = options.ShowTrackChangeToast;
        _toasts.AccessibleName = "Track change notifications";
        _history.Checked = options.ListeningHistory;
        _history.AccessibleName = "Listening history";
        _overlay.Checked = options.ShowOverlay;
        _overlay.AccessibleName = "Floating overlay";

        _openSettings.Checked = false;
        _openSettings.AutoSize = true;

        _startButton.DialogResult = DialogResult.OK;
        _startButton.MinimumSize = new Size(140, 36);
        _startButton.BackColor = Color.FromArgb(0, 120, 212);
        _startButton.ForeColor = Color.White;
        _startButton.FlatStyle = FlatStyle.Flat;
        _startButton.FlatAppearance.BorderSize = 0;
        _startButton.Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(22, 18, 22, 18),
            BackColor = Color.White,
        };

        _title.Font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Point);
        _title.AutoSize = true;
        _title.Margin = new Padding(0, 0, 0, 8);

        foreach (var label in new[] { _intro, _coversLabel, _privacyLabel })
        {
            label.AutoSize = true;
            label.MaximumSize = new Size(456, 0);
            label.Margin = new Padding(0, 0, 0, 10);
        }

        _privacyLabel.ForeColor = Color.FromArgb(100, 100, 100);

        _languageLabel.AutoSize = true;
        _languageLabel.Margin = new Padding(0, 6, 0, 4);
        _language.Margin = new Padding(0, 0, 0, 12);

        _optionsLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
        _optionsLabel.AutoSize = true;
        _optionsLabel.Margin = new Padding(0, 4, 0, 6);

        _discordLabel.AutoSize = true;
        _discordLabel.MaximumSize = new Size(456, 0);
        _discordLabel.Margin = new Padding(0, 4, 0, 12);

        body.Controls.Add(_title);
        body.Controls.Add(_intro);
        body.Controls.Add(_languageLabel);
        body.Controls.Add(_language);
        body.Controls.Add(_optionsLabel);
        body.Controls.Add(_autostart);
        body.Controls.Add(_toasts);
        body.Controls.Add(_history);
        body.Controls.Add(_overlay);
        body.Controls.Add(_discordLabel);
        body.Controls.Add(_coversLabel);
        body.Controls.Add(_privacyLabel);
        body.Controls.Add(_openSettings);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(22, 10, 22, 14),
            BackColor = Color.FromArgb(243, 245, 249),
        };
        footer.Controls.Add(_startButton);

        Controls.Add(body);
        Controls.Add(footer);

        AcceptButton = _startButton;

        RefreshTexts();
    }

    /// <summary>El usuario pidió abrir la configuración al terminar el asistente.</summary>
    public bool OpenSettingsRequested => _openSettings.Checked;

    private void RefreshTexts()
    {
        Text = _strings["WizardTitle"];
        _title.Text = _strings["WizardTitle"];
        _intro.Text = _strings["WizardIntro"];
        _languageLabel.Text = _strings["WizardLanguage"] + ":";
        _optionsLabel.Text = _strings["WizardOptions"] + ":";
        _autostart.Text = UiText.For(CurrentLanguage()).StartWithWindows;
        _toasts.Text = ToastText(CurrentLanguage());
        _history.Text = HistoryText(CurrentLanguage());
        _overlay.Text = OverlayText(CurrentLanguage());
        _coversLabel.Text = _strings["WizardCovers"];
        _privacyLabel.Text = _strings["WizardPrivacy"];
        _openSettings.Text = _strings["WizardOpenSettings"];
        _startButton.Text = _strings["WizardStart"];

        if (_options.DiscordClientId.Length == 0)
        {
            _discordLabel.Text = _strings["WizardDiscordDisabled"];
            _discordLabel.ForeColor = Color.FromArgb(180, 90, 0);
        }
        else if (_discordRunning)
        {
            _discordLabel.Text = _strings["WizardDiscordOk"];
            _discordLabel.ForeColor = Color.FromArgb(30, 130, 60);
        }
        else
        {
            _discordLabel.Text = _strings["WizardDiscordMissing"];
            _discordLabel.ForeColor = Color.FromArgb(150, 110, 0);
        }
    }

    private string CurrentLanguage() =>
        _language.SelectedItem is LanguageItem item ? item.Code : "en";

    // Tres textos que ya existen en UiText (18 idiomas) se reaprovechan en el asistente.
    private static string ToastText(string language) => language switch
    {
        "es" => "Mostrar un aviso al cambiar de canción",
        "pt" => "Mostrar aviso ao mudar de faixa",
        "fr" => "Afficher une notification au changement de piste",
        "de" => "Bei Titelwechsel benachrichtigen",
        "it" => "Mostra un avviso al cambio brano",
        "nl" => "Melding tonen bij een nieuw nummer",
        "pl" => "Pokazuj powiadomienie przy zmianie utworu",
        "ru" => "Показывать уведомление при смене трека",
        "uk" => "Показувати сповіщення про зміну треку",
        "tr" => "Şarkı değişince bildirim göster",
        "ja" => "曲が変わったら通知を表示",
        "ko" => "곡이 바뀌면 알림 표시",
        "id" => "Tampilkan notifikasi saat lagu berganti",
        "sv" => "Visa avis när låten byts",
        "cs" => "Zobrazit oznámení při změně skladby",
        _ when language.StartsWith("zh", StringComparison.Ordinal) => "换歌时显示提示",
        _ => "Show a notification when the track changes",
    };

    private static string HistoryText(string language) => language switch
    {
        "es" => "Guardar historial de escucha",
        "pt" => "Guardar histórico de audição",
        "fr" => "Conserver l'historique d'écoute",
        "de" => "Hörverlauf speichern",
        "it" => "Salva la cronologia di ascolto",
        "nl" => "Luistergeschiedenis bewaren",
        "pl" => "Zapisuj historię słuchania",
        "ru" => "Сохранять историю прослушивания",
        "uk" => "Зберігати історію прослуховування",
        "tr" => "Dinleme geçmişini kaydet",
        "ja" => "再生履歴を保存",
        "ko" => "청취 기록 저장",
        "id" => "Simpan riwayat mendengarkan",
        "sv" => "Spara lyssningshistorik",
        "cs" => "Ukládat historii poslechu",
        _ when language.StartsWith("zh", StringComparison.Ordinal) => "保存收听记录",
        _ => "Keep a listening history",
    };

    private static string OverlayText(string language) => language switch
    {
        "es" => "Mostrar la ventana flotante «now playing»",
        "pt" => "Mostrar a janela flutuante «now playing»",
        "fr" => "Afficher la fenêtre flottante « lecture en cours »",
        "de" => "Schwebendes „Now playing“-Fenster anzeigen",
        "it" => "Mostra la finestra flottante «now playing»",
        "nl" => "Zwevend „now playing“-venster tonen",
        "pl" => "Pokaż pływające okno „teraz grane”",
        "ru" => "Показывать плавающее окно «сейчас играет»",
        "uk" => "Показувати плаваюче вікно «зараз грає»",
        "tr" => "Yüzen «şimdi çalıyor» penceresini göster",
        "ja" => "フローティング「再生中」ウィンドウを表示",
        "ko" => "플로팅 «재생 중» 창 표시",
        "id" => "Tampilkan jendela melayang «now playing»",
        "sv" => "Visa flytande «spelas nu»-fönster",
        "cs" => "Zobrazit plovoucí okno „právě hraje“",
        _ when language.StartsWith("zh", StringComparison.Ordinal) => "显示悬浮「正在播放」窗口",
        _ => "Show a floating “now playing” window",
    };

    private void SelectLanguage(string code)
    {
        var wanted = code.Trim().ToLowerInvariant();
        for (var i = 0; i < _language.Items.Count; i++)
        {
            if (_language.Items[i] is LanguageItem item
                && string.Equals(item.Code, wanted, StringComparison.OrdinalIgnoreCase))
            {
                _language.SelectedIndex = i;
                return;
            }
        }

        _language.SelectedIndex = 0;
    }

    public void Apply()
    {
        _options.Language = CurrentLanguage();
        _options.StartWithWindows = _autostart.Checked;
        _options.ShowTrackChangeToast = _toasts.Checked;
        _options.ListeningHistory = _history.Checked;
        _options.ShowOverlay = _overlay.Checked;
        _options.FirstRunCompleted = true;
        _options.Normalize();

        try { _options.Save(); } catch { }

        WindowsAutostart.Apply(_options.StartWithWindows);
    }

    private static IEnumerable<(string Code, string Name)> LanguageCatalog()
    {
        yield return ("en", "English");
        yield return ("es", "Español");
        yield return ("pt", "Português");
        yield return ("fr", "Français");
        yield return ("de", "Deutsch");
        yield return ("it", "Italiano");
        yield return ("nl", "Nederlands");
        yield return ("pl", "Polski");
        yield return ("ru", "Русский");
        yield return ("uk", "Українська");
        yield return ("tr", "Türkçe");
        yield return ("ja", "日本語");
        yield return ("ko", "한국어");
        yield return ("id", "Bahasa Indonesia");
        yield return ("sv", "Svenska");
        yield return ("cs", "Čeština");
        yield return ("zh", "简体中文");
        yield return ("zh-TW", "繁體中文");
    }

    private sealed class LanguageItem
    {
        public LanguageItem(string code, string name)
        {
            Code = code;
            Name = name;
        }

        public string Code { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }
}
