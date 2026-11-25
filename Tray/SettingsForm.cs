using System.Drawing;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using UniversalPresence.Localization;

namespace UniversalPresence.Tray;

/// <summary>
/// Modal configuration window opened from the notification-area menu.
/// It edits the live <see cref="AppOptions"/> instance, writes config.json and asks the
/// engine to re-apply everything (including an SMTC monitor restart) without quitting.
/// </summary>
public sealed class SettingsForm : Form
{
    private AppOptions _options;
    private Action _onSaved;
    private List<string> _ignored;
    private UiText _ui;
    private UiStrings _strings;
    private TabControl _tabs = null!;
    private Panel _headerPanel = null!;

    // --- Blacklist tab ----------------------------------------------------------
    private ListBox _ignoredList = null!;
    private TextBox _newPlayer = null!;

    // --- Behavior tab -----------------------------------------------------------
    private CheckBox _appendPlayer = null!;
    private CheckBox _usePlayerNameAsActivity = null!;
    private CheckBox _hideIfNoArtwork = null!;
    private CheckBox _startWithWindows = null!;
    private CheckBox _showProgress = null!;
    private NumericUpDown _pauseRefresh = null!;
    private CheckBox _darkMode = null!;
    private ComboBox _languageBox = null!;
    private string _selectedLanguage = "";
    private ComboBox _profileBox = null!;
    private TextBox _newProfileName = null!;

    // --- Paused tab -------------------------------------------------------------
    private CheckBox _showPausedMedia = null!;
    private CheckBox _pauseResetProgress = null!;
    private CheckBox _showPausedIcon = null!;
    private Control _pauseAssetKey = null!;
    private Control _appIconAssetKey = null!;

    // --- Presence tab -----------------------------------------------------------
    private CheckBox _showOverlay = null!;
    private CheckBox _gamingMode = null!;
    private CheckBox _showTrackChangeToast = null!;
    private CheckBox _globalHotkeys = null!;
    private CheckBox _listeningHistory = null!;
    private TextBox _detailsTemplateBox = null!;
    private TextBox _stateTemplateBox = null!;
    private TextBox _largeTextTemplateBox = null!;

    // --- Artwork tab ---
    private CheckBox _artworkEnabledCheck = null!;
    private ComboBox _artworkModeBox = null!;
    private ComboBox _uploadModeBox = null!;
    private TextBox _uploadUrlBox = null!;
    private TextBox _uploadApiKeyBox = null!;
    private CheckBox _iconOnlyCheck = null!;
    private TextBox _button1LabelBox = null!;
    private TextBox _button1UrlBox = null!;
    private TextBox _button2LabelBox = null!;
    private TextBox _button2UrlBox = null!;
    private NumericUpDown _historyDaysBox = null!;
    private NumericUpDown _historyEntriesBox = null!;

    // --- Last.fm tab ------------------------------------------------------------
    private TextBox _lastFmApiKeyBox = null!;
    private TextBox _lastFmSecretBox = null!;
    private TextBox _lastFmUserBox = null!;
    private TextBox _lastFmSessionKeyBox = null!;
    private CheckBox _lastFmNowPlayingBox = null!;

    // --- Avanzado ----------------------------------------------------------------
    private CheckBox _clearWhenIdle = null!;
    private NumericUpDown _idleSecondsBox = null!;
    private CheckBox _resyncDrift = null!;
    private ComboBox _channelBox = null!;
    private CheckBox _autoRollback = null!;
    private ListBox _overridesList = null!;
    private TextBox _overridePlayerBox = null!;
    private TextBox _overrideSourceBox = null!;
    private Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    // --- Portada propia por canción ----------------------------------------------
    private ListBox _trackOverridesList = null!;
    private TextBox _trackArtistBox = null!;
    private TextBox _trackTitleBox = null!;
    private TextBox _trackSourceBox = null!;
    private Dictionary<string, string> _trackOverrides = new(StringComparer.OrdinalIgnoreCase);

    // --- Textos de presencia por reproductor --------------------------------------
    private ListBox _templatesList = null!;
    private TextBox _templatePlayerBox = null!;
    private TextBox _templateDetailsBox = null!;
    private TextBox _templateStateBox = null!;
    private Dictionary<string, PresenceTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);

    // --- Portadas: subida ---------------------------------------------------------
    private CheckBox _optimizeUploads = null!;
    private CheckBox _fallbackHost = null!;

    private bool _cancelled;
    private bool _saved;

    private static readonly Color Accent = Color.FromArgb(0, 120, 212);
    private static readonly Color CardBg = Color.White;
    private static readonly Color PageBg = Color.FromArgb(243, 245, 249);

    public SettingsForm(AppOptions options, Action onSaved)
    {
        _options = options;
        _selectedLanguage = string.IsNullOrWhiteSpace(options.Language) ? "en" : options.Language.Trim();
        _onSaved = onSaved;
        _ignored = options.IgnoredPlayers.ToList();
        _ui = UiText.For(options.Language);
        _strings = UiStrings.For(options.Language);
        _overrides = new Dictionary<string, string>(
            options.CoverOverrides ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);

        _trackOverrides = new Dictionary<string, string>(
            options.TrackCoverOverrides ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);

        _templates = new Dictionary<string, PresenceTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.PlayerTemplates ?? new Dictionary<string, PresenceTemplate>())
        {
            var template = pair.Value;
            if (template is not null && !string.IsNullOrWhiteSpace(pair.Key))
            {
                _templates[pair.Key] = PresenceTemplate.Create(template.Details, template.State, template.LargeText);
            }
        }

        Text = _ui.SettingsTitle;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = PageBg;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 800);
        MinimumSize = new Size(620, 640);
        Icon = TrayIcons.Get(TrayVisualState.Playing);

        // ---- Tab control -------------------------------------------------------
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
            Padding = new Point(18, 8),
            Appearance = TabAppearance.FlatButtons,
            ItemSize = new Size(0, 30),
            SizeMode = TabSizeMode.FillToRight,
        };
        _tabs.TabPages.Add(MakeTab(_ui.PlayersTab, BuildPlayersTab()));
        _tabs.TabPages.Add(MakeTab(_ui.BehaviorTab, BuildBehaviorTab()));
        _tabs.TabPages.Add(MakeTab(_ui.PausedTab, BuildPausedTab()));
        _tabs.TabPages.Add(MakeTab(_ui.PresenceTab, BuildPresenceTab()));
        _tabs.TabPages.Add(MakeTab("Portadas", BuildArtworkTab()));
        _tabs.TabPages.Add(MakeTab(_ui.LastFmTab, BuildLastFmTab()));
        _tabs.TabPages.Add(MakeTab(_strings["AdvancedTab"], BuildAdvancedTab()));

        // ---- Bottom buttons ----------------------------------------------------
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(14, 8, 14, 14),
            BackColor = PageBg,
        };

        var saveButton = new Button
        {
            Name = "SaveButton",
            Text = _ui.Save,
            AutoSize = true,
            MinimumSize = new Size(104, 34),
            BackColor = Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 0, 0),
        };
        saveButton.FlatAppearance.BorderSize = 0;

        var cancelButton = new Button
        {
            Text = _ui.Cancel,
            AutoSize = true,
            MinimumSize = new Size(104, 34),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 8, 0),
        };

        saveButton.Click += (_, _) =>
        {
            if (!SaveSettings()) return;
            _saved = true;
            DialogResult = DialogResult.OK;
            Close();
        };

        cancelButton.Click += (_, _) =>
        {
            _cancelled = true;
            DialogResult = DialogResult.Cancel;
            Close();
        };

        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);

        var hint = new Label
        {
            Text = _ui.Hint,
            Dock = DockStyle.Bottom,
            Height = 30,
            ForeColor = Color.FromArgb(90, 90, 90),
            Padding = new Padding(14, 4, 14, 0),
            BackColor = PageBg,
        };

        // ---- Cabecera moderna --------------------------------------------------
        _headerPanel = new Panel
        {
            Name = "HeaderPanel",
            Dock = DockStyle.Top,
            Height = 78,
            BackColor = Accent,
            Padding = new Padding(20, 14, 20, 14),
        };
        var headerTitle = new Label
        {
            Text = _ui.AppName,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(20, 12),
            BackColor = Color.Transparent,
        };
        var headerSubtitle = new Label
        {
            Text = $"{_ui.Settings.TrimEnd('…').Trim()} · v{typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(220, 235, 255),
            AutoSize = true,
            Location = new Point(22, 46),
            BackColor = Color.Transparent,
        };
        _headerPanel.Controls.Add(headerTitle);
        _headerPanel.Controls.Add(headerSubtitle);
        RoundCorners(_headerPanel, 0);

        // El panel Fill se añade al final para que los paneles inferiores (botones/pista)
        // queden por encima y no tapen los controles de las pestañas.
        Controls.Add(buttons);
        Controls.Add(hint);
        Controls.Add(_tabs);
        Controls.Add(_headerPanel);
        _tabs.BringToFront();

        RoundCorners(saveButton, 8);
        RoundCorners(cancelButton, 8);

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        RefreshIgnoredList();
        ApplyTheme();
    }

    private static TabPage MakeTab(string title, Control content)
    {
        var page = new TabPage(title)
        {
            BackColor = PageBg,
            Padding = new Padding(8),
            AutoScroll = true,
        };
        var card = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = CardBg,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 0, 8),
            BorderStyle = BorderStyle.FixedSingle,
        };
        RoundCorners(card, 10);
        content.Dock = DockStyle.Top;
        card.Controls.Add(content);
        page.Controls.Add(card);
        return page;
    }

    private static GroupBox MakeGroup(string title) => new GroupBox
    {
        Text = title,
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(12, 14, 12, 12),
        Margin = new Padding(0, 0, 0, 12),
        Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
        BackColor = CardBg,
    };

    private static CheckBox MakeCheck(string text, bool value, string name) => new CheckBox
    {
        Text = text,
        AutoSize = true,
        Checked = value,
        Name = name,
        Margin = new Padding(0, 0, 0, 8),
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
    };

    private Control BuildPlayersTab()
    {
        _ignoredList = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 260,
            IntegralHeight = false,
            SelectionMode = SelectionMode.One,
            Name = "IgnoredList",
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            BorderStyle = BorderStyle.FixedSingle,
        };

        _newPlayer = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = _ui.NewPlayerPlaceholder,
            Name = "NewPlayerBox",
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        };

        var addButton = new Button { Text = _ui.Add, AutoSize = true, Name = "AddButton" };
        var removeButton = new Button { Text = _ui.Remove, AutoSize = true, Name = "RemoveButton" };
        addButton.Click += (_, _) => AddTypedPlayer();
        removeButton.Click += (_, _) => RemoveSelectedPlayer();

        var addLabel = new Label
        {
            Text = _ui.AddPlayerLabel + ":",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 0),
        };

        var addRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
        };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.Controls.Add(addLabel, 0, 0);
        addRow.Controls.Add(_newPlayer, 1, 0);
        addRow.Controls.Add(addButton, 2, 0);
        addRow.Controls.Add(removeButton, 3, 0);

        var hint = new Label
        {
            Text = "Escribe el nombre del reproductor y pulsa Añadir (o Enter). Para quitarlo, selecciónalo y pulsa Quitar.",
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 90, 90),
            Margin = new Padding(0, 10, 0, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Italic, GraphicsUnit.Point),
        };

        _newPlayer.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter) return;
            eventArgs.SuppressKeyPress = true;
            AddTypedPlayer();
        };

        var panel = Stack();
        AddRow(panel, _ignoredList);
        AddRow(panel, addRow);
        AddRow(panel, hint);
        return panel;
    }

    private Control BuildBehaviorTab()
    {
        _appendPlayer = MakeCheck(_ui.AppendPlayer, _options.AppendPlayerToState, "AppendPlayerCheck");
        _usePlayerNameAsActivity = MakeCheck(_ui.UsePlayerNameAsActivity, _options.UsePlayerNameAsActivityName, "UsePlayerNameAsActivityCheck");
        _hideIfNoArtwork = MakeCheck(_ui.HideIfNoArtwork, _options.HideIfNoArtwork, "HideIfNoArtworkCheck");
        _startWithWindows = MakeCheck(_ui.StartWithWindows, _options.StartWithWindows, "StartWithWindowsCheck");
        _showProgress = MakeCheck(_ui.ShowProgress, _options.ShowProgress, "ShowProgressCheck");
        _darkMode = MakeCheck("Modo oscuro (dark mode)", _options.DarkMode, "DarkModeCheck");
        _darkMode.CheckedChanged += (_, _) => ApplyTheme();

        var pauseLabel = new Label
        {
            Text = _ui.PauseRefreshLabel,
            AutoSize = true,
            Margin = new Padding(0, 8, 10, 0),
        };
        _pauseRefresh = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 120,
            Value = Math.Clamp(_options.PauseRefreshSeconds, 0, 120),
            Width = 70,
            Anchor = AnchorStyles.Left,
            Name = "PauseRefreshBox",
        };
        var pauseRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        pauseRow.Controls.Add(pauseLabel);
        pauseRow.Controls.Add(_pauseRefresh);

        // Idioma de la presencia y de la interfaz.
        var langLabel = new Label
        {
            Text = "Idioma (presencia y ventana):",
            AutoSize = true,
            Margin = new Padding(0, 0, 10, 4),
        };
        _languageBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            Name = "LanguageBox",
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 0, 8),
        };
        foreach (var (code, name) in LanguageOptions())
        {
            _languageBox.Items.Add(new LanguageItem(code, name));
        }
        var current = string.IsNullOrWhiteSpace(_selectedLanguage) ? "en" : _selectedLanguage.Trim().ToLowerInvariant();
        for (var i = 0; i < _languageBox.Items.Count; i++)
        {
            if (_languageBox.Items[i] is LanguageItem li
                && string.Equals(li.Code, current, StringComparison.OrdinalIgnoreCase))
            {
                _languageBox.SelectedIndex = i;
                break;
            }
        }
        if (_languageBox.SelectedIndex < 0) _languageBox.SelectedIndex = 0;

        _languageBox.SelectedIndexChanged += (_, _) =>
        {
            if (_languageBox.SelectedItem is not LanguageItem li) return;
            _selectedLanguage = li.Code;
            _ui = UiText.For(li.Code);
            Text = _ui.SettingsTitle;
            BeginInvoke(new Action(RebuildTabs));
        };

        var panel = Stack();
        var header = new Label { Text = _ui.BehaviorGroup, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point), Margin = new Padding(0, 0, 0, 8) };
        AddRow(panel, header);
        AddRow(panel, langLabel);
        AddRow(panel, _languageBox);
        AddRow(panel, _appendPlayer);
        AddRow(panel, _usePlayerNameAsActivity);
        AddRow(panel, _hideIfNoArtwork);
        AddRow(panel, _startWithWindows);
        AddRow(panel, _showProgress);
        AddRow(panel, _darkMode);
        AddRow(panel, pauseRow);

        // Perfiles de configuración.
        var profileLabel = new Label { Text = "Profile:", AutoSize = true, Margin = new Padding(0, 8, 10, 4) };
        _profileBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, Name = "ProfileBox" };
        _newProfileName = new TextBox { Width = 140, PlaceholderText = "new profile", Name = "NewProfileBox" };
        var switchButton = new Button { Text = "Switch", AutoSize = true, Name = "SwitchProfileButton" };
        var newButton = new Button { Text = "New", AutoSize = true, Name = "NewProfileButton" };
        var deleteButton = new Button { Text = "Delete", AutoSize = true, Name = "DeleteProfileButton" };

        RefreshProfileList();

        switchButton.Click += (_, _) =>
        {
            if (_profileBox.SelectedItem is not string name) return;
            if (ProfileService.Switch(_options, name))
            {
                ReloadFromOptions();
                _onSaved();
                MessageBox.Show(this, $"Switched to profile '{name}'.", _ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        newButton.Click += (_, _) =>
        {
            var name = _newProfileName.Text.Trim();
            if (name.Length == 0) return;
            try
            {
                ProfileService.Save(_options, name);
                ProfileService.Switch(_options, name);
                _newProfileName.Clear();
                ReloadFromOptions();
                _onSaved();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, _ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        deleteButton.Click += (_, _) =>
        {
            if (_profileBox.SelectedItem is not string name || name == "default") return;
            ProfileService.Delete(name);
            RefreshProfileList();
        };

        var profileRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
        profileRow.Controls.Add(profileLabel);
        profileRow.Controls.Add(_profileBox);
        profileRow.Controls.Add(_newProfileName);
        profileRow.Controls.Add(switchButton);
        profileRow.Controls.Add(newButton);
        profileRow.Controls.Add(deleteButton);
        AddRow(panel, profileRow);

        return panel;
    }

    private Control BuildPausedTab()
    {
        _showPausedMedia = MakeCheck(_ui.ShowPausedMedia, _options.ShowPausedMedia, "ShowPausedMediaCheck");
        _pauseResetProgress = MakeCheck(_ui.PauseResetProgress, _options.PauseResetProgress, "PauseResetProgressCheck");
        _showPausedIcon = MakeCheck(_ui.ShowPausedIcon, _options.ShowPausedIcon, "ShowPausedIconCheck");

        _pauseAssetKey = CreateAssetKeyBox(_ui.PauseAssetKeyLabel, _options.PauseAssetKey, "PauseAssetKeyBox");
        _appIconAssetKey = CreateAssetKeyBox(_ui.AppIconAssetKeyLabel, _options.AppIconAssetKey, "AppIconAssetKeyBox");

        var panel = Stack();
        var header = new Label { Text = _ui.PausedGroup, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point), Margin = new Padding(0, 0, 0, 8) };
        AddRow(panel, header);
        AddRow(panel, _showPausedMedia);
        AddRow(panel, _pauseResetProgress);
        AddRow(panel, _showPausedIcon);
        AddRow(panel, _pauseAssetKey);
        AddRow(panel, _appIconAssetKey);
        return panel;
    }

    private Control BuildPresenceTab()
    {
        _showOverlay = MakeCheck("Mostrar overlay flotante (now playing)", _options.ShowOverlay, "ShowOverlayCheck");
        _gamingMode = MakeCheck("Pausar presencia en juegos a pantalla completa", _options.GamingMode, "GamingModeCheck");
        _showTrackChangeToast = MakeCheck("Mostrar toast al cambiar de pista", _options.ShowTrackChangeToast, "ShowTrackChangeToastCheck");
        _globalHotkeys = MakeCheck("Atajos globales (Ctrl+Alt+P / S)", _options.GlobalHotkeys, "GlobalHotkeysCheck");
        _listeningHistory = MakeCheck("Guardar historial de escucha (history.jsonl)", _options.ListeningHistory, "ListeningHistoryCheck");

        _detailsTemplateBox = new TextBox { Text = _options.DetailsTemplate, Dock = DockStyle.Top, Name = "DetailsTemplateBox", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        _stateTemplateBox = new TextBox { Text = _options.StateTemplate, Dock = DockStyle.Top, Name = "StateTemplateBox", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        _largeTextTemplateBox = new TextBox { Text = _options.LargeTextTemplate, Dock = DockStyle.Top, Name = "LargeTextTemplateBox", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };

        var panel = Stack();
        var header = new Label { Text = "Funciones nuevas", AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point), Margin = new Padding(0, 0, 0, 8) };
        AddRow(panel, header);
        AddRow(panel, _showOverlay);
        AddRow(panel, _gamingMode);
        AddRow(panel, _showTrackChangeToast);
        AddRow(panel, _globalHotkeys);
        AddRow(panel, _listeningHistory);

        AddRow(panel, new Label { Text = "Plantilla Details (usa {title}, {artist}, {album}, {player}):", AutoSize = true, Margin = new Padding(0, 8, 0, 2), Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point) });
        AddRow(panel, _detailsTemplateBox);
        AddRow(panel, new Label { Text = "Plantilla State:", AutoSize = true, Margin = new Padding(0, 6, 0, 2), Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point) });
        AddRow(panel, _stateTemplateBox);
        AddRow(panel, new Label { Text = "Plantilla Large text:", AutoSize = true, Margin = new Padding(0, 6, 0, 2), Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point) });
        AddRow(panel, _largeTextTemplateBox);

        // --- Textos propios de un reproductor concreto --------------------------
        _templatesList = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 100,
            IntegralHeight = false,
            SelectionMode = SelectionMode.One,
            Name = "PlayerTemplatesList",
            AccessibleName = _strings["PlayerTemplatesGroup"],
            BorderStyle = BorderStyle.FixedSingle,
        };
        _templatesList.DoubleClick += (_, _) => LoadSelectedTemplate();

        _templatePlayerBox = new TextBox
        {
            Width = 240,
            PlaceholderText = _strings["PlayerTemplatePlayer"],
            Name = "PlayerTemplatePlayerBox",
            AccessibleName = _strings["PlayerTemplatePlayer"],
        };
        _templateDetailsBox = new TextBox
        {
            Width = 300,
            PlaceholderText = _strings["PlayerTemplateDetails"],
            Name = "PlayerTemplateDetailsBox",
            AccessibleName = _strings["PlayerTemplateDetails"],
        };
        _templateStateBox = new TextBox
        {
            Width = 300,
            PlaceholderText = _strings["PlayerTemplateState"],
            Name = "PlayerTemplateStateBox",
            AccessibleName = _strings["PlayerTemplateState"],
        };

        var addTemplate = new Button { Text = _ui.Add, AutoSize = true, Name = "AddTemplateButton" };
        addTemplate.Click += (_, _) => AddTemplate();

        var removeTemplate = new Button { Text = _ui.Remove, AutoSize = true, Name = "RemoveTemplateButton" };
        removeTemplate.Click += (_, _) => RemoveTemplate();

        var templateRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        templateRow.Controls.Add(_templatePlayerBox);
        templateRow.Controls.Add(_templateDetailsBox);

        var templateRow2 = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
        templateRow2.Controls.Add(_templateStateBox);
        templateRow2.Controls.Add(addTemplate);
        templateRow2.Controls.Add(removeTemplate);

        AddRow(panel, Header(_strings["PlayerTemplatesGroup"]));
        AddRow(panel, _templatesList);
        AddRow(panel, templateRow);
        AddRow(panel, templateRow2);
        AddRow(panel, new Label
        {
            Text = _strings["PlayerTemplatesHint"],
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.FromArgb(90, 90, 90),
            Margin = new Padding(0, 8, 0, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Italic, GraphicsUnit.Point),
        });

        RefreshTemplates();
        return panel;
    }

    private Control BuildArtworkTab()
    {
        _artworkEnabledCheck = MakeCheck("Leer y publicar carátulas (SMTC)", _options.Artwork.Enabled, "ArtworkEnabledCheck");
        _iconOnlyCheck = MakeCheck("Solo icono (no subir/buscar portadas)", _options.Artwork.IconOnly, "IconOnlyCheck");
        _optimizeUploads = MakeCheck(_strings["OptimizeUploads"], _options.Artwork.OptimizeUploads, "OptimizeUploadsCheck");
        _fallbackHost = MakeCheck(_strings["FallbackHost"], _options.Artwork.AllowFallbackUploadHost, "FallbackHostCheck");

        var modeLabel = new Label { Text = "Modo de portada:", AutoSize = true, Margin = new Padding(0, 8, 10, 4) };
        _artworkModeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Name = "ArtworkModeBox" };
        foreach (var m in new[] { "Auto", "ExternalUrl", "Lookup", "DiscordAsset", "None" }) _artworkModeBox.Items.Add(m);
        _artworkModeBox.SelectedItem = _options.Artwork.Mode;
        if (_artworkModeBox.SelectedIndex < 0) _artworkModeBox.SelectedIndex = 0;

        var uploadLabel = new Label { Text = "Modo de subida:", AutoSize = true, Margin = new Padding(0, 8, 10, 4) };
        _uploadModeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Name = "UploadModeBox" };
        foreach (var m in new[] { "None", "Catbox", "Imgur", "SelfHosted" }) _uploadModeBox.Items.Add(m);
        _uploadModeBox.SelectedItem = _options.Artwork.UploadMode;
        if (_uploadModeBox.SelectedIndex < 0) _uploadModeBox.SelectedIndex = 0;

        _uploadUrlBox = new TextBox { Text = _options.Artwork.UploadUrl, Dock = DockStyle.Top, Name = "UploadUrlBox", PlaceholderText = "https://tu-host/covers" };
        _uploadApiKeyBox = new TextBox { Text = _options.Artwork.UploadApiKey, Dock = DockStyle.Top, Name = "UploadApiKeyBox", PlaceholderText = "token / Client-ID" };

        _button1LabelBox = new TextBox { Text = _options.Button1Label, Dock = DockStyle.Top, Name = "Button1LabelBox", PlaceholderText = "Etiqueta botón 1 (vacío = Descargar)" };
        _button1UrlBox = new TextBox { Text = _options.Button1Url, Dock = DockStyle.Top, Name = "Button1UrlBox", PlaceholderText = "URL botón 1" };
        _button2LabelBox = new TextBox { Text = _options.Button2Label, Dock = DockStyle.Top, Name = "Button2LabelBox", PlaceholderText = "Etiqueta botón 2 (vacío = Cómo funciona)" };
        _button2UrlBox = new TextBox { Text = _options.Button2Url, Dock = DockStyle.Top, Name = "Button2UrlBox", PlaceholderText = "URL botón 2" };

        var daysLabel = new Label { Text = "Retención historial (días, 0 = sin límite):", AutoSize = true, Margin = new Padding(0, 8, 10, 4) };
        _historyDaysBox = new NumericUpDown { Minimum = 0, Maximum = 3650, Value = Math.Clamp(_options.ListeningHistoryRetentionDays, 0, 3650), Width = 80, Name = "HistoryDaysBox" };
        var entriesLabel = new Label { Text = "Entradas máximas (0 = sin límite):", AutoSize = true, Margin = new Padding(0, 8, 10, 4) };
        _historyEntriesBox = new NumericUpDown { Minimum = 0, Maximum = 100000, Value = Math.Clamp(_options.ListeningHistoryMaxEntries, 0, 100000), Width = 90, Name = "HistoryEntriesBox" };

        var panel = Stack();
        AddRow(panel, _artworkEnabledCheck);
        AddRow(panel, _iconOnlyCheck);
        AddRow(panel, modeLabel);
        AddRow(panel, _artworkModeBox);
        AddRow(panel, uploadLabel);
        AddRow(panel, _uploadModeBox);
        AddRow(panel, new Label { Text = "URL del host propio:", AutoSize = true, Margin = new Padding(0, 8, 0, 2) });
        AddRow(panel, _uploadUrlBox);
        AddRow(panel, new Label { Text = "Token / Client-ID:", AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
        AddRow(panel, _uploadApiKeyBox);
        AddRow(panel, _optimizeUploads);
        AddRow(panel, _fallbackHost);
        AddRow(panel, new Label { Text = "Botón 1:", AutoSize = true, Margin = new Padding(0, 10, 0, 2), Font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point) });
        AddRow(panel, _button1LabelBox);
        AddRow(panel, _button1UrlBox);
        AddRow(panel, new Label { Text = "Botón 2:", AutoSize = true, Margin = new Padding(0, 8, 0, 2), Font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point) });
        AddRow(panel, _button2LabelBox);
        AddRow(panel, _button2UrlBox);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
        row.Controls.Add(daysLabel);
        row.Controls.Add(_historyDaysBox);
        row.Controls.Add(entriesLabel);
        row.Controls.Add(_historyEntriesBox);
        AddRow(panel, row);
        return panel;
    }

    private Control BuildLastFmTab()
    {
        _lastFmApiKeyBox = new TextBox { Text = _options.LastFmApiKey, Dock = DockStyle.Top, Name = "LastFmApiKeyBox", PlaceholderText = "API key", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        _lastFmSecretBox = new TextBox { Text = _options.LastFmSecret, Dock = DockStyle.Top, Name = "LastFmSecretBox", PlaceholderText = "Shared secret", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        _lastFmUserBox = new TextBox { Text = _options.LastFmUser, Dock = DockStyle.Top, Name = "LastFmUserBox", PlaceholderText = "Usuario", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        _lastFmSessionKeyBox = new TextBox { Text = _options.LastFmSessionKey, Dock = DockStyle.Top, Name = "LastFmSessionKeyBox", PlaceholderText = "Session key", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };

        var panel = Stack();
        var header = new Label { Text = "Last.fm (scrobbling)", AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point), Margin = new Padding(0, 0, 0, 8) };
        AddRow(panel, header);
        AddRow(panel, new Label { Text = "API key:", AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        AddRow(panel, _lastFmApiKeyBox);
        AddRow(panel, new Label { Text = "Shared secret:", AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
        AddRow(panel, _lastFmSecretBox);
        AddRow(panel, new Label { Text = "Usuario:", AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
        AddRow(panel, _lastFmUserBox);
        AddRow(panel, new Label { Text = "Session key:", AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
        AddRow(panel, _lastFmSessionKeyBox);

        _lastFmNowPlayingBox = MakeCheck(_strings["LastFmNowPlaying"], _options.LastFmNowPlaying, "LastFmNowPlayingCheck");
        _lastFmNowPlayingBox.Margin = new Padding(0, 10, 0, 4);
        AddRow(panel, _lastFmNowPlayingBox);

        var connectButton = new Button
        {
            Text = _strings["LastFmConnect"],
            AutoSize = true,
            MinimumSize = new Size(180, 32),
            Name = "LastFmConnectButton",
            AccessibleName = _strings["LastFmConnect"],
            Margin = new Padding(0, 6, 0, 6),
        };
        connectButton.Click += async (_, _) => await ConnectLastFmAsync();
        AddRow(panel, connectButton);

        AddRow(panel, new Label
        {
            Text = _strings["LastFmConnectHint"],
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.FromArgb(90, 90, 90),
            Margin = new Padding(0, 6, 0, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Italic, GraphicsUnit.Point),
        });
        return panel;
    }

    /// <summary>
    /// Autorización de Last.fm en dos pasos: se pide un token, se abre el navegador y, cuando
    /// el usuario confirma, se cambia el token por la session key. Nada que copiar a mano.
    /// </summary>
    private async Task ConnectLastFmAsync()
    {
        var apiKey = _lastFmApiKeyBox.Text.Trim();
        var secret = _lastFmSecretBox.Text.Trim();

        if (apiKey.Length == 0 || secret.Length == 0)
        {
            MessageBox.Show(this, _strings["LastFmMissingKeys"], _ui.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _options.LastFmApiKey = apiKey;
        _options.LastFmSecret = secret;

        using var log = Log.Create(_options, console: false);
        var service = new LastFmService(_options, log);

        try
        {
            var token = await service.RequestTokenAsync(CancellationToken.None);
            if (string.IsNullOrWhiteSpace(token))
            {
                MessageBox.Show(this, _strings.T("LastFmConnectFailed", "token"), _ui.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    LastFmService.AuthUrl(apiKey, token)) { UseShellExecute = true });
            }
            catch
            {
                // Si no hay navegador, el usuario puede copiar la URL del aviso.
            }

            var proceed = MessageBox.Show(this, _strings["LastFmAuthorize"], _ui.AppName,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information);

            if (proceed != DialogResult.OK)
            {
                return;
            }

            var session = await service.CompleteSessionAsync(token, CancellationToken.None);
            if (session is null)
            {
                MessageBox.Show(this, _strings.T("LastFmConnectFailed", "auth.getSession"), _ui.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _lastFmUserBox.Text = session.Value.User;
            _lastFmSessionKeyBox.Text = session.Value.SessionKey;
            _options.LastFmUser = session.Value.User;
            _options.LastFmSessionKey = session.Value.SessionKey;

            MessageBox.Show(this, _strings.T("LastFmConnected", session.Value.User), _ui.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, _strings.T("LastFmConnectFailed", ex.Message), _ui.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private Control BuildAdvancedTab()
    {
        _clearWhenIdle = MakeCheck(_strings["ClearWhenIdle"], _options.ClearWhenIdle, "ClearWhenIdleCheck");

        var idleLabel = new Label
        {
            Text = _strings["IdleSecondsLabel"],
            AutoSize = true,
            Margin = new Padding(0, 2, 10, 0),
        };

        _idleSecondsBox = new NumericUpDown
        {
            Minimum = 30,
            Maximum = 86_400,
            Increment = 30,
            Value = Math.Clamp(_options.IdleSeconds, 30, 86_400),
            Width = 90,
            Name = "IdleSecondsBox",
            AccessibleName = _strings["IdleSecondsLabel"],
        };

        var idleRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 2, 0, 8),
        };
        idleRow.Controls.Add(idleLabel);
        idleRow.Controls.Add(_idleSecondsBox);

        _resyncDrift = MakeCheck(_strings["ResyncDrift"], _options.ResyncProgressDrift, "ResyncDriftCheck");

        var channelLabel = new Label
        {
            Text = _strings["UpdateChannelLabel"],
            AutoSize = true,
            Margin = new Padding(0, 4, 10, 0),
        };

        _channelBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            Name = "UpdateChannelBox",
            AccessibleName = _strings["UpdateChannelLabel"],
        };
        _channelBox.Items.Add(new ChannelItem("stable", _strings["ChannelStable"]));
        _channelBox.Items.Add(new ChannelItem("beta", _strings["ChannelBeta"]));
        _channelBox.SelectedIndex = _options.UpdateChannel == "beta" ? 1 : 0;

        var channelRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 2, 0, 8),
        };
        channelRow.Controls.Add(channelLabel);
        channelRow.Controls.Add(_channelBox);

        _autoRollback = MakeCheck(_strings["AutoRollback"], _options.AutoRollback, "AutoRollbackCheck");

        // --- Portada propia por aplicación ------------------------------------
        _overridesList = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 110,
            IntegralHeight = false,
            SelectionMode = SelectionMode.One,
            Name = "OverridesList",
            AccessibleName = _strings["OverridesGroup"],
            BorderStyle = BorderStyle.FixedSingle,
        };
        _overridesList.DoubleClick += (_, _) => LoadSelectedOverride();

        _overridePlayerBox = new TextBox
        {
            Width = 190,
            PlaceholderText = _strings["OverridePlayer"],
            Name = "OverridePlayerBox",
            AccessibleName = _strings["OverridePlayer"],
        };

        _overrideSourceBox = new TextBox
        {
            Width = 250,
            PlaceholderText = _strings["OverrideSource"],
            Name = "OverrideSourceBox",
            AccessibleName = _strings["OverrideSource"],
        };

        var addOverride = new Button
        {
            Text = _strings["OverrideAdd"],
            AutoSize = true,
            Name = "AddOverrideButton",
            AccessibleName = _strings["OverrideAdd"],
        };
        addOverride.Click += (_, _) => AddOverride();

        var removeOverride = new Button
        {
            Text = _strings["OverrideRemove"],
            AutoSize = true,
            Name = "RemoveOverrideButton",
            AccessibleName = _strings["OverrideRemove"],
        };
        removeOverride.Click += (_, _) => RemoveOverride();

        var overrideRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 6, 0, 0),
        };
        overrideRow.Controls.Add(_overridePlayerBox);
        overrideRow.Controls.Add(_overrideSourceBox);
        overrideRow.Controls.Add(addOverride);
        overrideRow.Controls.Add(removeOverride);

        var overrideHint = new Label
        {
            Text = _strings["OverrideHint"],
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.FromArgb(90, 90, 90),
            Margin = new Padding(0, 8, 0, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Italic, GraphicsUnit.Point),
        };

        // --- Portada propia por canción ----------------------------------------
        _trackOverridesList = new ListBox
        {
            Dock = DockStyle.Top,
            Height = 100,
            IntegralHeight = false,
            SelectionMode = SelectionMode.One,
            Name = "TrackOverridesList",
            AccessibleName = _strings["TrackOverridesGroup"],
            BorderStyle = BorderStyle.FixedSingle,
        };
        _trackOverridesList.DoubleClick += (_, _) => LoadSelectedTrackOverride();

        _trackArtistBox = new TextBox
        {
            Width = 150,
            PlaceholderText = _strings["TrackArtist"],
            Name = "TrackArtistBox",
            AccessibleName = _strings["TrackArtist"],
        };
        _trackTitleBox = new TextBox
        {
            Width = 170,
            PlaceholderText = _strings["TrackTitle"],
            Name = "TrackTitleBox",
            AccessibleName = _strings["TrackTitle"],
        };
        _trackSourceBox = new TextBox
        {
            Width = 240,
            PlaceholderText = _strings["OverrideSource"],
            Name = "TrackSourceBox",
            AccessibleName = _strings["OverrideSource"],
        };

        var addTrackOverride = new Button
        {
            Text = _strings["OverrideAdd"],
            AutoSize = true,
            Name = "AddTrackOverrideButton",
            AccessibleName = _strings["OverrideAdd"],
        };
        addTrackOverride.Click += (_, _) => AddTrackOverride();

        var removeTrackOverride = new Button
        {
            Text = _strings["OverrideRemove"],
            AutoSize = true,
            Name = "RemoveTrackOverrideButton",
            AccessibleName = _strings["OverrideRemove"],
        };
        removeTrackOverride.Click += (_, _) => RemoveTrackOverride();

        var trackOverrideRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 6, 0, 0),
        };
        trackOverrideRow.Controls.Add(_trackArtistBox);
        trackOverrideRow.Controls.Add(_trackTitleBox);
        trackOverrideRow.Controls.Add(_trackSourceBox);
        trackOverrideRow.Controls.Add(addTrackOverride);
        trackOverrideRow.Controls.Add(removeTrackOverride);

        // --- Herramientas -------------------------------------------------------
        var statsButton = new Button
        {
            Text = _strings["OpenStats"],
            AutoSize = true,
            Name = "OpenStatsButton",
            AccessibleName = _strings["OpenStats"],
            Margin = new Padding(0, 4, 8, 0),
        };
        statsButton.Click += (_, _) =>
        {
            using var form = new StatsForm(_options);
            form.ShowDialog(this);
        };

        var restoreButton = new Button
        {
            Text = _strings["OpenBackup"],
            AutoSize = true,
            Name = "RestoreBackupButton",
            AccessibleName = _strings["OpenBackup"],
            Margin = new Padding(0, 4, 0, 0),
        };
        restoreButton.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(this, _strings["BackupConfirm"], _ui.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes)
            {
                return;
            }

            try
            {
                var path = Path.Combine(AppOptions.LocalDataDirectory, "command.txt");
                var temp = path + ".tmp";
                Directory.CreateDirectory(AppOptions.LocalDataDirectory);
                File.WriteAllText(temp, "restore-now");
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, _strings.T("BackupFailed", ex.Message), _ui.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var toolsRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 6, 0, 0),
        };
        var logButton = new Button
        {
            Text = _strings["LogOpen"],
            AutoSize = true,
            Name = "OpenLogButton",
            AccessibleName = _strings["LogOpen"],
            Margin = new Padding(0, 4, 8, 0),
        };
        logButton.Click += (_, _) =>
        {
            using var form = new LogViewerForm(_options, Log.ForReading());
            form.ShowDialog(this);
        };

        toolsRow.Controls.Add(statsButton);
        toolsRow.Controls.Add(logButton);
        toolsRow.Controls.Add(restoreButton);

        var panel = Stack();

        AddRow(panel, Header(_strings["IdleGroup"]));
        AddRow(panel, _clearWhenIdle);
        AddRow(panel, idleRow);
        AddRow(panel, _resyncDrift);
        AddRow(panel, channelRow);
        AddRow(panel, _autoRollback);
        AddRow(panel, Header(_strings["OverridesGroup"]));
        AddRow(panel, _overridesList);
        AddRow(panel, overrideRow);
        AddRow(panel, overrideHint);
        AddRow(panel, Header(_strings["TrackOverridesGroup"]));
        AddRow(panel, _trackOverridesList);
        AddRow(panel, trackOverrideRow);
        AddRow(panel, new Label
        {
            Text = _strings["TrackOverrideHint"],
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.FromArgb(90, 90, 90),
            Margin = new Padding(0, 8, 0, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Italic, GraphicsUnit.Point),
        });
        AddRow(panel, Header(_strings["DiagnosticsGroup"]));
        AddRow(panel, toolsRow);

        RefreshOverrides();
        RefreshTrackOverrides();
        return panel;
    }

    /// <summary>
    /// Contenedor de una sola columna para las filas de una tarjeta. A diferencia de un
    /// FlowLayoutPanel, calcula bien su alto y estira los campos al ancho disponible.
    /// </summary>
    private static TableLayoutPanel Stack()
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            Margin = new Padding(0),
            BackColor = CardBg,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        return stack;
    }

    private static void AddRow(TableLayoutPanel stack, Control control)
    {
        // Los campos de texto, listas y filas compuestas ocupan todo el ancho de la tarjeta;
        // casillas, etiquetas y botones conservan su tamaño natural.
        if (control is TextBox or ListBox or FlowLayoutPanel or TableLayoutPanel)
        {
            control.Dock = DockStyle.Top;
        }

        var margin = control.Margin;
        control.Margin = new Padding(margin.Left, margin.Top, margin.Right, Math.Max(margin.Bottom, 6));

        stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(control, 0, stack.RowCount - 1);
    }

    private static Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
        Margin = new Padding(0, 8, 0, 6),
    };

    private void AddOverride()
    {
        var player = _overridePlayerBox.Text.Trim();
        var source = _overrideSourceBox.Text.Trim();

        var isUrl = Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps;

        if (player.Length == 0 || source.Length == 0 || (!isUrl && !File.Exists(source)))
        {
            MessageBox.Show(this, _strings["OverrideInvalid"], _ui.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _overrides[player] = source;
        _overridePlayerBox.Clear();
        _overrideSourceBox.Clear();
        RefreshOverrides();
    }

    private void RemoveOverride()
    {
        if (_overridesList.SelectedItem is not OverrideItem item)
        {
            return;
        }

        _overrides.Remove(item.Player);
        RefreshOverrides();
    }

    private void RefreshOverrides()
    {
        if (_overridesList is null)
        {
            return;
        }

        _overridesList.BeginUpdate();
        try
        {
            _overridesList.Items.Clear();
            foreach (var pair in _overrides.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                _overridesList.Items.Add(new OverrideItem(pair.Key, pair.Value));
            }
        }
        finally
        {
            _overridesList.EndUpdate();
        }
    }

    private void LoadSelectedOverride()
    {
        if (_overridesList.SelectedItem is not OverrideItem item)
        {
            return;
        }

        _overridePlayerBox.Text = item.Player;
        _overrideSourceBox.Text = item.Source;
    }

    private void AddTrackOverride()
    {
        var title = _trackTitleBox.Text.Trim();
        var artist = _trackArtistBox.Text.Trim();
        var source = _trackSourceBox.Text.Trim();

        var isUrl = Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps;

        if (title.Length == 0 || source.Length == 0 || (!isUrl && !File.Exists(source)))
        {
            MessageBox.Show(this, _strings["TrackOverrideInvalid"], _ui.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Sin artista la portada vale para ese título con cualquier intérprete.
        var key = artist.Length == 0 ? TextMatch.TitleKey(title) : TextMatch.TrackKey(artist, title);
        _trackOverrides[key] = source;
        _trackArtistBox.Clear();
        _trackTitleBox.Clear();
        _trackSourceBox.Clear();
        RefreshTrackOverrides();
    }

    private void RemoveTrackOverride()
    {
        if (_trackOverridesList.SelectedItem is not TrackOverrideItem item)
        {
            return;
        }

        _trackOverrides.Remove(item.Key);
        RefreshTrackOverrides();
    }

    private void LoadSelectedTrackOverride()
    {
        if (_trackOverridesList.SelectedItem is not TrackOverrideItem item)
        {
            return;
        }

        // Las entradas sin artista se guardan solo con el título: hay que distinguirlas o
        // al reeditar se cargaría el título en la casilla del artista.
        var parts = item.Key.Split('\u001f');
        _trackArtistBox.Text = parts.Length > 1 ? parts[0] : "";
        _trackTitleBox.Text = parts.Length > 1 ? parts[1] : parts[0];
        _trackSourceBox.Text = item.Source;
    }

    private void RefreshTrackOverrides()
    {
        if (_trackOverridesList is null)
        {
            return;
        }

        _trackOverridesList.BeginUpdate();
        try
        {
            _trackOverridesList.Items.Clear();
            foreach (var pair in _trackOverrides.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                _trackOverridesList.Items.Add(new TrackOverrideItem(pair.Key, pair.Value));
            }
        }
        finally
        {
            _trackOverridesList.EndUpdate();
        }
    }

    private void AddTemplate()
    {
        var player = _templatePlayerBox.Text.Trim();
        var details = _templateDetailsBox.Text.Trim();
        var state = _templateStateBox.Text.Trim();

        if (player.Length == 0 || (details.Length == 0 && state.Length == 0))
        {
            MessageBox.Show(this, _strings["PlayerTemplateInvalid"], _ui.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var existing = _templates.TryGetValue(player, out var previous) ? previous : null;

        _templates[player] = PresenceTemplate.Create(details, state, existing?.LargeText ?? "");
        _templatePlayerBox.Clear();
        _templateDetailsBox.Clear();
        _templateStateBox.Clear();
        RefreshTemplates();
    }

    private void RemoveTemplate()
    {
        if (_templatesList.SelectedItem is not TemplateItem item)
        {
            return;
        }

        _templates.Remove(item.Player);
        RefreshTemplates();
    }

    private void LoadSelectedTemplate()
    {
        if (_templatesList.SelectedItem is not TemplateItem item)
        {
            return;
        }

        _templatePlayerBox.Text = item.Player;
        _templateDetailsBox.Text = item.Template.Details;
        _templateStateBox.Text = item.Template.State;
    }

    private void RefreshTemplates()
    {
        if (_templatesList is null)
        {
            return;
        }

        _templatesList.BeginUpdate();
        try
        {
            _templatesList.Items.Clear();
            foreach (var pair in _templates.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                _templatesList.Items.Add(new TemplateItem(pair.Key, pair.Value));
            }
        }
        finally
        {
            _templatesList.EndUpdate();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (!_saved && !_cancelled && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            if (!SaveSettings())
            {
                eventArgs.Cancel = true;
                return;
            }
            _saved = true;
            DialogResult = DialogResult.OK;
        }
        base.OnFormClosing(eventArgs);
    }

    private static Control CreateAssetKeyBox(string label, string value, string name)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 8),
            Name = name + "Row",
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230f));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 8, 0),
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        }, 0, 0);
        panel.Controls.Add(new TextBox
        {
            Text = value,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
            Name = name,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
        }, 1, 0);
        return panel;
    }

    private static string AssetKeyText(Control row)
    {
        foreach (Control child in row.Controls)
        {
            if (child is TextBox box) return box.Text;
        }
        return "";
    }

    private void AddTypedPlayer()
    {
        var value = _newPlayer.Text.Trim();
        if (value.Length == 0) { _newPlayer.Focus(); return; }
        if (!AddPlayer(value))
        {
            MessageBox.Show(this, string.Format(_ui.AlreadyInList, value), _ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _newPlayer.Clear();
        _newPlayer.Focus();
    }

    private bool AddPlayer(string value)
    {
        if (_ignored.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            return false;
        _ignored.Add(value);
        RefreshIgnoredList(value);
        return true;
    }

    private void RemoveSelectedPlayer()
    {
        if (_ignoredList.SelectedItem is not string selected) return;
        _ignored.RemoveAll(value => string.Equals(value, selected, StringComparison.OrdinalIgnoreCase));
        RefreshIgnoredList();
    }

    private void RefreshIgnoredList(string? select = null)
    {
        _ignoredList.BeginUpdate();
        try
        {
            _ignoredList.Items.Clear();
            foreach (var value in _ignored.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
                _ignoredList.Items.Add(value);
        }
        finally { _ignoredList.EndUpdate(); }
        if (!string.IsNullOrEmpty(select)) _ignoredList.SelectedItem = select;
    }

    private static void RoundCorners(Control control, int radius)
    {
        if (radius <= 0) return;
        Action update = () =>
        {
            if (control.Width <= 0 || control.Height <= 0) return;
            int d = radius * 2;
            if (d > control.Width) d = control.Width;
            if (d > control.Height) d = control.Height;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(control.Width - d, 0, d, d, 270, 90);
            path.AddArc(control.Width - d, control.Height - d, d, d, 0, 90);
            path.AddArc(0, control.Height - d, d, d, 90, 90);
            path.CloseFigure();
            control.Region = new Region(path);
        };
        update();
        control.Resize += (_, _) => update();
    }

    private void ApplyTheme()
    {
        // Con alto contraste del sistema mandan los colores del sistema: no se pinta nada.
        if (SystemInformation.HighContrast)
        {
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;
            if (_headerPanel != null)
            {
                _headerPanel.BackColor = SystemColors.Control;
                foreach (Control c in _headerPanel.Controls)
                {
                    c.ForeColor = SystemColors.WindowText;
                }
            }
            return;
        }

        bool dark = _darkMode != null && _darkMode.Checked;
        Color pageBg = dark ? Color.FromArgb(32, 33, 36) : PageBg;
        Color cardBg = dark ? Color.FromArgb(41, 42, 45) : Color.White;
        Color fg = dark ? Color.FromArgb(232, 234, 237) : Color.Black;
        Color accent = dark ? Color.FromArgb(138, 180, 248) : Accent;

        BackColor = pageBg;
        ForeColor = fg;

        foreach (Control c in Controls)
        {
            ApplyThemeTo(c, pageBg, cardBg, fg, accent, dark);
        }
    }

    private static void ApplyThemeTo(Control c, Color pageBg, Color cardBg, Color fg, Color accent, bool dark)
    {
        if (c is TabControl tc)
        {
            tc.BackColor = pageBg;
            tc.ForeColor = fg;
        }
        else if (c is TabPage tp)
        {
            tp.BackColor = pageBg;
            tp.ForeColor = fg;
        }
        else if (c is GroupBox gb)
        {
            gb.BackColor = cardBg;
            gb.ForeColor = fg;
        }
        else if (c is Panel p && p.Name == "HeaderPanel")
        {
            p.BackColor = accent;
            p.ForeColor = Color.White;
        }
        else if (c is Panel p2)
        {
            p2.BackColor = cardBg;
            p2.ForeColor = fg;
        }
        else if (c is FlowLayoutPanel flp)
        {
            flp.BackColor = cardBg;
            flp.ForeColor = fg;
        }
        else if (c is TableLayoutPanel tlp)
        {
            tlp.BackColor = cardBg;
            tlp.ForeColor = fg;
        }
        else if (c is TextBox tb)
        {
            tb.BackColor = dark ? Color.FromArgb(55, 57, 61) : Color.White;
            tb.ForeColor = fg;
            tb.BorderStyle = BorderStyle.FixedSingle;
        }
        else if (c is ListBox lb)
        {
            lb.BackColor = dark ? Color.FromArgb(55, 57, 61) : Color.White;
            lb.ForeColor = fg;
        }
        else if (c is NumericUpDown nud)
        {
            nud.BackColor = dark ? Color.FromArgb(55, 57, 61) : Color.White;
            nud.ForeColor = fg;
        }
        else if (c is CheckBox cb)
        {
            cb.ForeColor = fg;
            cb.BackColor = Color.Transparent;
        }
        else if (c is Label lbl)
        {
            lbl.ForeColor = fg;
            lbl.BackColor = Color.Transparent;
        }
        else if (c is Button btn)
        {
            if (btn.Name == "SaveButton")
            {
                btn.BackColor = accent;
                btn.ForeColor = dark ? Color.FromArgb(32, 33, 36) : Color.White;
            }
            else
            {
                btn.BackColor = dark ? Color.FromArgb(55, 57, 61) : SystemColors.Control;
                btn.ForeColor = fg;
                btn.FlatStyle = FlatStyle.Flat;
            }
        }
        else
        {
            c.BackColor = pageBg;
            c.ForeColor = fg;
        }

        foreach (Control child in c.Controls)
        {
            ApplyThemeTo(child, pageBg, cardBg, fg, accent, dark);
        }
    }

    private void RefreshProfileList()
    {
        if (_profileBox is null) return;
        _profileBox.Items.Clear();
        foreach (var name in ProfileService.List())
        {
            _profileBox.Items.Add(name);
        }
        var current = string.IsNullOrWhiteSpace(_options.ActiveProfile) ? "default" : _options.ActiveProfile;
        for (var i = 0; i < _profileBox.Items.Count; i++)
        {
            if (string.Equals(_profileBox.Items[i]?.ToString(), current, StringComparison.OrdinalIgnoreCase))
            {
                _profileBox.SelectedIndex = i;
                break;
            }
        }
        if (_profileBox.SelectedIndex < 0 && _profileBox.Items.Count > 0)
        {
            _profileBox.SelectedIndex = 0;
        }
    }

    private void RebuildTabs()
    {
        var index = _tabs.SelectedIndex;
        _tabs.TabPages.Clear();
        _tabs.TabPages.Add(MakeTab(_ui.PlayersTab, BuildPlayersTab()));
        _tabs.TabPages.Add(MakeTab(_ui.BehaviorTab, BuildBehaviorTab()));
        _tabs.TabPages.Add(MakeTab(_ui.PausedTab, BuildPausedTab()));
        _tabs.TabPages.Add(MakeTab(_ui.PresenceTab, BuildPresenceTab()));
        _tabs.TabPages.Add(MakeTab("Portadas", BuildArtworkTab()));
        _tabs.TabPages.Add(MakeTab(_ui.LastFmTab, BuildLastFmTab()));
        _tabs.TabPages.Add(MakeTab(_strings["AdvancedTab"], BuildAdvancedTab()));
        if (index >= 0 && index < _tabs.TabPages.Count) _tabs.SelectedIndex = index;
    }

    /// <summary>
    /// Reconstruye la ventana desde las opciones cargadas (cambio de perfil). Sin esto los
    /// controles seguían mostrando el perfil anterior y, al cerrar, lo sobrescribían con
    /// valores obsoletos.
    /// </summary>
    private void ReloadFromOptions()
    {
        _selectedLanguage = string.IsNullOrWhiteSpace(_options.Language) ? "en" : _options.Language.Trim();
        _ignored = _options.IgnoredPlayers.ToList();
        _overrides = new Dictionary<string, string>(_options.CoverOverrides, StringComparer.OrdinalIgnoreCase);
        _trackOverrides = new Dictionary<string, string>(_options.TrackCoverOverrides, StringComparer.OrdinalIgnoreCase);
        _templates = new Dictionary<string, PresenceTemplate>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in _options.PlayerTemplates)
        {
            if (pair.Value is not null && !string.IsNullOrWhiteSpace(pair.Key))
            {
                _templates[pair.Key] = PresenceTemplate.Create(
                    pair.Value.Details,
                    pair.Value.State,
                    pair.Value.LargeText);
            }
        }

        _ui = UiText.For(_selectedLanguage);
        _strings = UiStrings.For(_selectedLanguage);
        Text = _ui.SettingsTitle;
        RebuildTabs();
        ApplyTheme();
    }

    private bool SaveSettings()
    {
        var pending = _newPlayer.Text.Trim();
        if (pending.Length > 0) { AddPlayer(pending); _newPlayer.Clear(); }

        _options.IgnoredPlayers = _ignored.ToList();
        _options.AppendPlayerToState = _appendPlayer.Checked;
        _options.UsePlayerNameAsActivityName = _usePlayerNameAsActivity.Checked;
        _options.HideIfNoArtwork = _hideIfNoArtwork.Checked;
        _options.StartWithWindows = _startWithWindows.Checked;
        _options.ShowProgress = _showProgress.Checked;
        _options.DarkMode = _darkMode.Checked;
        _options.PauseRefreshSeconds = (int)_pauseRefresh.Value;
        _options.ShowPausedMedia = _showPausedMedia.Checked;
        _options.PauseResetProgress = _pauseResetProgress.Checked;
        _options.ShowPausedIcon = _showPausedIcon.Checked;
        _options.PauseAssetKey = AssetKeyText(_pauseAssetKey);
        _options.AppIconAssetKey = AssetKeyText(_appIconAssetKey);
        _options.ShowOverlay = _showOverlay.Checked;
        _options.GamingMode = _gamingMode.Checked;
        _options.ShowTrackChangeToast = _showTrackChangeToast.Checked;
        _options.GlobalHotkeys = _globalHotkeys.Checked;
        _options.ListeningHistory = _listeningHistory.Checked;
        _options.Artwork.Enabled = _artworkEnabledCheck.Checked;
        _options.Artwork.IconOnly = _iconOnlyCheck.Checked;
        _options.Artwork.Mode = _artworkModeBox.SelectedItem?.ToString() ?? _options.Artwork.Mode;
        _options.Artwork.UploadMode = _uploadModeBox.SelectedItem?.ToString() ?? _options.Artwork.UploadMode;
        _options.Artwork.UploadUrl = _uploadUrlBox.Text.Trim();
        _options.Artwork.UploadApiKey = _uploadApiKeyBox.Text.Trim();
        _options.Button1Label = _button1LabelBox.Text.Trim();
        _options.Button1Url = _button1UrlBox.Text.Trim();
        _options.Button2Label = _button2LabelBox.Text.Trim();
        _options.Button2Url = _button2UrlBox.Text.Trim();
        _options.ListeningHistoryRetentionDays = (int)_historyDaysBox.Value;
        _options.ListeningHistoryMaxEntries = (int)_historyEntriesBox.Value;
        _options.DetailsTemplate = _detailsTemplateBox.Text.Trim();
        _options.StateTemplate = _stateTemplateBox.Text.Trim();
        _options.LargeTextTemplate = _largeTextTemplateBox.Text.Trim();
        _options.LastFmApiKey = _lastFmApiKeyBox.Text.Trim();
        _options.LastFmSecret = _lastFmSecretBox.Text.Trim();
        _options.LastFmUser = _lastFmUserBox.Text.Trim();
        _options.LastFmSessionKey = _lastFmSessionKeyBox.Text.Trim();
        _options.LastFmNowPlaying = _lastFmNowPlayingBox.Checked;
        _options.ClearWhenIdle = _clearWhenIdle.Checked;
        _options.IdleSeconds = (int)_idleSecondsBox.Value;
        _options.ResyncProgressDrift = _resyncDrift.Checked;
        _options.UpdateChannel = (_channelBox.SelectedItem as ChannelItem)?.Code ?? "stable";
        _options.AutoRollback = _autoRollback.Checked;
        _options.CoverOverrides = new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase);
        _options.TrackCoverOverrides = new Dictionary<string, string>(_trackOverrides, StringComparer.OrdinalIgnoreCase);
        _options.PlayerTemplates = _templates.ToDictionary(
            pair => pair.Key,
            pair => PresenceTemplate.Create(pair.Value.Details, pair.Value.State, pair.Value.LargeText),
            StringComparer.OrdinalIgnoreCase);
        _options.Artwork.OptimizeUploads = _optimizeUploads.Checked;
        _options.Artwork.AllowFallbackUploadHost = _fallbackHost.Checked;
        _options.Language = string.IsNullOrWhiteSpace(_selectedLanguage) ? _options.Language : _selectedLanguage;
        _options.Normalize();

        try { _options.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format(_ui.SaveFailed, ex.Message), _ui.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        WindowsAutostart.Apply(_options.StartWithWindows);
        _onSaved();
        return true;
    }

    private static IEnumerable<(string Code, string Name)> LanguageOptions()
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
        public string Code { get; }
        public string Name { get; }
        public LanguageItem(string code, string name) { Code = code; Name = name; }
        public override string ToString() => Name;
    }

    private sealed class ChannelItem
    {
        public ChannelItem(string code, string name) { Code = code; Name = name; }
        public string Code { get; }
        public string Name { get; }
        public override string ToString() => Name;
    }

    private sealed class OverrideItem
    {
        public OverrideItem(string player, string source) { Player = player; Source = source; }
        public string Player { get; }
        public string Source { get; }
        public override string ToString() => $"{Player}  →  {Source}";
    }

    private sealed class TrackOverrideItem
    {
        public TrackOverrideItem(string key, string source) { Key = key; Source = source; }
        public string Key { get; }
        public string Source { get; }

        public override string ToString()
        {
            var parts = Key.Split('\u001f');

            // Clave solo por título: no hay separador y parts[0] es el título.
            var artist = parts.Length > 1 ? parts[0] : "";
            var title = parts.Length > 1 ? parts[1] : parts[0];
            var label = string.IsNullOrWhiteSpace(artist) ? title : $"{title} — {artist}";
            return $"{label}  →  {Source}";
        }
    }

    private sealed class TemplateItem
    {
        public TemplateItem(string player, PresenceTemplate template) { Player = player; Template = template; }
        public string Player { get; }
        public PresenceTemplate Template { get; }
        public override string ToString() => $"{Player}  →  {Template}";
    }
}