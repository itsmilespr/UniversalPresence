using System.Drawing;
using UniversalPresence.Configuration;
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
    private TabControl _tabs;

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

    // --- Last.fm tab ------------------------------------------------------------
    private TextBox _lastFmApiKeyBox = null!;
    private TextBox _lastFmSecretBox = null!;
    private TextBox _lastFmUserBox = null!;
    private TextBox _lastFmSessionKeyBox = null!;

    private bool _cancelled;
    private bool _saved;

    private static readonly Color Accent = Color.FromArgb(0, 120, 212);
    private static readonly Color CardBg = Color.White;
    private static readonly Color PageBg = Color.FromArgb(243, 245, 249);

    public SettingsForm(AppOptions options, Action onSaved)
    {
        _options = options;
        _onSaved = onSaved;
        _ignored = options.IgnoredPlayers.ToList();
        _ui = UiText.For(options.Language);

        Text = _ui.SettingsTitle;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = PageBg;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 740);
        MinimumSize = new Size(560, 600);
        Icon = TrayIcons.Get(TrayVisualState.Playing);

        // ---- Tab control -------------------------------------------------------
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            Padding = new Point(12, 6),
        };
        _tabs.TabPages.Add(MakeTab("Jugadores", BuildPlayersTab()));
        _tabs.TabPages.Add(MakeTab("Comportamiento", BuildBehaviorTab()));
        _tabs.TabPages.Add(MakeTab("Pausa", BuildPausedTab()));
        _tabs.TabPages.Add(MakeTab("Presencia", BuildPresenceTab()));
        _tabs.TabPages.Add(MakeTab("Last.fm", BuildLastFmTab()));

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

        Controls.Add(_tabs);
        Controls.Add(hint);
        Controls.Add(buttons);

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
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 0, 8),
        };
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

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        panel.Controls.Add(_ignoredList);
        panel.Controls.Add(addRow);
        panel.Controls.Add(hint);
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
        var current = string.IsNullOrWhiteSpace(_options.Language) ? "en" : _options.Language.Trim().ToLowerInvariant();
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

        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = CardBg };
        var header = new Label { Text = _ui.BehaviorGroup, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point), Margin = new Padding(0, 0, 0, 8) };
        panel.Controls.Add(header);
        panel.Controls.Add(langLabel);
        panel.Controls.Add(_languageBox);
        panel.Controls.Add(_appendPlayer);
        panel.Controls.Add(_usePlayerNameAsActivity);
        panel.Controls.Add(_hideIfNoArtwork);
        panel.Controls.Add(_startWithWindows);
        panel.Controls.Add(_showProgress);
        panel.Controls.Add(_darkMode);
        panel.Controls.Add(pauseRow);
        return panel;
    }

    private Control BuildPausedTab()
    {
        _showPausedMedia = MakeCheck(_ui.ShowPausedMedia, _options.ShowPausedMedia, "ShowPausedMediaCheck");
        _pauseResetProgress = MakeCheck(_ui.PauseResetProgress, _options.PauseResetProgress, "PauseResetProgressCheck");
        _showPausedIcon = MakeCheck(_ui.ShowPausedIcon, _options.ShowPausedIcon, "ShowPausedIconCheck");

        _pauseAssetKey = CreateAssetKeyBox(_ui.PauseAssetKeyLabel, _options.PauseAssetKey, "PauseAssetKeyBox");
        _appIconAssetKey = CreateAssetKeyBox(_ui.AppIconAssetKeyLabel, _options.AppIconAssetKey, "AppIconAssetKeyBox");

        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = CardBg };
        var header = new Label { Text = _ui.PausedGroup, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point), Margin = new Padding(0, 0, 0, 8) };
        panel.Controls.Add(header);
        panel.Controls.Add(_showPausedMedia);
        panel.Controls.Add(_pauseResetProgress);
        panel.Controls.Add(_showPausedIcon);
        panel.Controls.Add(_pauseAssetKey);
        panel.Controls.Add(_appIconAssetKey);
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

        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = CardBg };
        var header = new Label { Text = "Funciones nuevas", AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point), Margin = new Padding(0, 0, 0, 8) };
        panel.Controls.Add(header);
        panel.Controls.Add(_showOverlay);
        panel.Controls.Add(_gamingMode);
        panel.Controls.Add(_showTrackChangeToast);
        panel.Controls.Add(_globalHotkeys);
        panel.Controls.Add(_listeningHistory);

        panel.Controls.Add(new Label { Text = "Plantilla Details (usa {title}, {artist}, {album}, {player}):", AutoSize = true, Margin = new Padding(0, 8, 0, 2), Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point) });
        panel.Controls.Add(_detailsTemplateBox);
        panel.Controls.Add(new Label { Text = "Plantilla State:", AutoSize = true, Margin = new Padding(0, 6, 0, 2), Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point) });
        panel.Controls.Add(_stateTemplateBox);
        panel.Controls.Add(new Label { Text = "Plantilla Large text:", AutoSize = true, Margin = new Padding(0, 6, 0, 2), Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point) });
        panel.Controls.Add(_largeTextTemplateBox);
        return panel;
    }

    private Control BuildLastFmTab()
    {
        _lastFmApiKeyBox = new TextBox { Text = _options.LastFmApiKey, Dock = DockStyle.Top, Name = "LastFmApiKeyBox", PlaceholderText = "API key", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        _lastFmSecretBox = new TextBox { Text = _options.LastFmSecret, Dock = DockStyle.Top, Name = "LastFmSecretBox", PlaceholderText = "Shared secret", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        _lastFmUserBox = new TextBox { Text = _options.LastFmUser, Dock = DockStyle.Top, Name = "LastFmUserBox", PlaceholderText = "Usuario", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };
        _lastFmSessionKeyBox = new TextBox { Text = _options.LastFmSessionKey, Dock = DockStyle.Top, Name = "LastFmSessionKeyBox", PlaceholderText = "Session key", Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point) };

        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = CardBg };
        var header = new Label { Text = "Last.fm (scrobbling)", AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point), Margin = new Padding(0, 0, 0, 8) };
        panel.Controls.Add(header);
        panel.Controls.Add(new Label { Text = "API key:", AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        panel.Controls.Add(_lastFmApiKeyBox);
        panel.Controls.Add(new Label { Text = "Shared secret:", AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
        panel.Controls.Add(_lastFmSecretBox);
        panel.Controls.Add(new Label { Text = "Usuario:", AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
        panel.Controls.Add(_lastFmUserBox);
        panel.Controls.Add(new Label { Text = "Session key:", AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
        panel.Controls.Add(_lastFmSessionKeyBox);
        panel.Controls.Add(new Label
        {
            Text = "Deja vacío para desactivar el scrobbling. Necesitas una cuenta Last.fm y una API key.",
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 90, 90),
            Margin = new Padding(0, 10, 0, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Italic, GraphicsUnit.Point),
        });
        return panel;
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

    private void ApplyTheme()
    {
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
        else if (c is Panel p)
        {
            p.BackColor = cardBg;
            p.ForeColor = fg;
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
        _options.DetailsTemplate = _detailsTemplateBox.Text.Trim();
        _options.StateTemplate = _stateTemplateBox.Text.Trim();
        _options.LargeTextTemplate = _largeTextTemplateBox.Text.Trim();
        _options.LastFmApiKey = _lastFmApiKeyBox.Text.Trim();
        _options.LastFmSecret = _lastFmSecretBox.Text.Trim();
        _options.LastFmUser = _lastFmUserBox.Text.Trim();
        _options.LastFmSessionKey = _lastFmSessionKeyBox.Text.Trim();
        if (_languageBox.SelectedItem is LanguageItem li)
        {
            _options.Language = li.Code;
        }
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
}