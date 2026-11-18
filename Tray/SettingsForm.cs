using System.Drawing;
using UniversalPresence.Configuration;

namespace UniversalPresence.Tray;

/// <summary>
/// Modal configuration window opened from the notification-area menu.
/// It edits the live <see cref="AppOptions"/> instance, writes config.json and asks the
/// engine to re-apply everything (including an SMTC monitor restart) without quitting.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppOptions _options;
    private readonly Action _onSaved;
    private readonly List<string> _ignored;
    private readonly ListBox _ignoredList;
    private readonly TextBox _newPlayer;
    private readonly CheckBox _appendPlayer;
    private readonly CheckBox _usePlayerNameAsActivity;
    private readonly CheckBox _hideIfNoArtwork;
    private readonly CheckBox _startWithWindows;
    private readonly CheckBox _showProgress;
    private readonly NumericUpDown _pauseRefresh;
    private readonly CheckBox _showPausedMedia;
    private readonly CheckBox _pauseResetProgress;
    private readonly CheckBox _showPausedIcon;
    private readonly Control _pauseAssetKey;
    private readonly Control _appIconAssetKey;
    private bool _cancelled;
    private bool _saved;

    public SettingsForm(AppOptions options, Action onSaved)
    {
        _options = options;
        _onSaved = onSaved;
        _ignored = options.IgnoredPlayers.ToList();

        Text = "Universal Presence — Configuración";
        Font = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(248, 249, 252);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 640);
        Padding = new Padding(14);
        Icon = TrayIcons.Get(TrayVisualState.Playing);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Name = "SettingsRoot",
        };

        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // --- Ignored players ---------------------------------------------------------
        var playersGroup = new GroupBox
        {
            Text = "Reproductores ignorados (lista negra)",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Margin = new Padding(0),
            Name = "PlayersGroup",
        };

        _ignoredList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            SelectionMode = SelectionMode.One,
            Name = "IgnoredList",
        };

        _newPlayer = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "p. ej. vlc",
            Margin = new Padding(0),
            Name = "NewPlayerBox",
        };

        var addButton = new Button
        {
            Text = "Añadir",
            AutoSize = true,
            Name = "AddButton",
            Margin = new Padding(6, 0, 0, 0),
        };
        addButton.Click += (_, _) => AddTypedPlayer();

        var removeButton = new Button
        {
            Text = "Eliminar",
            AutoSize = true,
            Name = "RemoveButton",
            Margin = new Padding(6, 0, 0, 0),
        };
        removeButton.Click += (_, _) => RemoveSelectedPlayer();

        var addRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.Controls.Add(_newPlayer, 0, 0);
        addRow.Controls.Add(addButton, 1, 0);
        addRow.Controls.Add(removeButton, 2, 0);

        var playersLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        playersLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        playersLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        playersLayout.Controls.Add(_ignoredList, 0, 0);
        playersLayout.Controls.Add(addRow, 0, 1);

        playersGroup.Controls.Add(playersLayout);

        _newPlayer.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter)
            {
                return;
            }

            // Enter adds the typed entry instead of triggering the form's AcceptButton.
            eventArgs.SuppressKeyPress = true;
            AddTypedPlayer();
        };

        // --- Behavior ----------------------------------------------------------------
        var behaviorGroup = new GroupBox
        {
            Text = "Apariencia y barra de progreso",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            Padding = new Padding(10),
            Margin = new Padding(0, 10, 0, 0),
            Name = "BehaviorGroup",
        };

        _appendPlayer = new CheckBox
        {
            Text = "Mostrar el reproductor en la segunda línea (Artista · Reproductor)",
            AutoSize = true,
            Checked = _options.AppendPlayerToState,
            Name = "AppendPlayerCheck",
            Margin = new Padding(0, 0, 0, 6),
        };

        _usePlayerNameAsActivity = new CheckBox
        {
            Text = "Mostrar el nombre del reproductor arriba en Discord (Reproduciendo foobar2000)",
            AutoSize = true,
            Checked = _options.UsePlayerNameAsActivityName,
            Name = "UsePlayerNameAsActivityCheck",
            Margin = new Padding(0, 0, 0, 6),
        };

        _hideIfNoArtwork = new CheckBox
        {
            Text = "No publicar si la canción no tiene portada",
            AutoSize = true,
            Checked = _options.HideIfNoArtwork,
            Name = "HideIfNoArtworkCheck",
            Margin = new Padding(0, 0, 0, 6),
        };

        _startWithWindows = new CheckBox
        {
            Text = "Iniciar con Windows",
            AutoSize = true,
            Checked = _options.StartWithWindows,
            Name = "StartWithWindowsCheck",
            Margin = new Padding(0, 0, 0, 6),
        };

        _showProgress = new CheckBox
        {
            Text = "Mostrar barra de progreso (timestamps)",
            AutoSize = true,
            Checked = _options.ShowProgress,
            Name = "ShowProgressCheck",
            Margin = new Padding(0, 0, 0, 4),
        };

        var pauseLabel = new Label
        {
            Text = "Reanclar la barra en pausa cada (segundos, 0 = desactivado):",
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
            Margin = new Padding(0, 6, 0, 0),
            Name = "PauseRefreshBox",
        };

        var pauseRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
            Name = "PauseRow",
        };
        pauseRow.Controls.Add(pauseLabel);
        pauseRow.Controls.Add(_pauseRefresh);

        var behavior = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        behavior.Controls.Add(_appendPlayer);
        behavior.Controls.Add(_usePlayerNameAsActivity);
        behavior.Controls.Add(_hideIfNoArtwork);
        behavior.Controls.Add(_startWithWindows);
        behavior.Controls.Add(_showProgress);
        behavior.Controls.Add(pauseRow);
        behaviorGroup.Controls.Add(behavior);

        // --- Paused media -------------------------------------------------------------
        var pausedGroup = new GroupBox
        {
            Text = "Comportamiento al pausar",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            Padding = new Padding(10),
            Margin = new Padding(0, 10, 0, 0),
            Name = "PausedGroup",
        };

        _showPausedMedia = new CheckBox
        {
            Text = "Mantener la canción visible al pausar",
            AutoSize = true,
            Checked = _options.ShowPausedMedia,
            Name = "ShowPausedMediaCheck",
            Margin = new Padding(0, 0, 0, 6),
        };

        _pauseResetProgress = new CheckBox
        {
            Text = "Reiniciar la barra de progreso a 0:00 mientras está pausada",
            AutoSize = true,
            Checked = _options.PauseResetProgress,
            Name = "PauseResetProgressCheck",
            Margin = new Padding(0, 0, 0, 6),
        };

        _showPausedIcon = new CheckBox
        {
            Text = "Mostrar icono de pausa sobre la portada al pausar",
            AutoSize = true,
            Checked = _options.ShowPausedIcon,
            Name = "ShowPausedIconCheck",
            Margin = new Padding(0, 0, 0, 10),
        };

        _pauseAssetKey = CreateAssetKeyBox(
            "Clave del asset de pausa (portal de Discord):",
            _options.PauseAssetKey,
            "PauseAssetKeyBox");

        _appIconAssetKey = CreateAssetKeyBox(
            "Clave del icono de UniversalPresence (portal de Discord):",
            _options.AppIconAssetKey,
            "AppIconAssetKeyBox");

        var pausedLayout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        pausedLayout.Controls.Add(_showPausedMedia);
        pausedLayout.Controls.Add(_pauseResetProgress);
        pausedLayout.Controls.Add(_showPausedIcon);
        pausedLayout.Controls.Add(_pauseAssetKey);
        pausedLayout.Controls.Add(_appIconAssetKey);
        pausedGroup.Controls.Add(pausedLayout);

        var hint = new Label
        {
            Text = "Los cambios se guardan en config.json y se aplican al instante, sin cerrar la aplicación.",
            Dock = DockStyle.Fill,
            Height = 34,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(2, 8, 0, 4),
            Name = "HintLabel",
        };

        // --- Buttons -----------------------------------------------------------------
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
        };

        var saveButton = new Button
        {
            Text = "Guardar",
            AutoSize = true,
            MinimumSize = new Size(96, 30),
            Name = "SaveButton",
        };

        var cancelButton = new Button
        {
            Text = "Cancelar",
            AutoSize = true,
            MinimumSize = new Size(96, 30),
            Margin = new Padding(0, 0, 8, 0),
            Name = "CancelButton",
        };

        saveButton.Click += (_, _) =>
        {
            if (!SaveSettings())
            {
                return;
            }

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

        root.Controls.Add(playersGroup, 0, 0);
        root.Controls.Add(behaviorGroup, 0, 1);
        root.Controls.Add(pausedGroup, 0, 2);
        root.Controls.Add(hint, 0, 3);
        root.Controls.Add(buttons, 0, 4);
        Controls.Add(root);

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        RefreshIgnoredList();
        ActiveControl = _ignoredList;
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        // Closing with X (or Alt+F4) also persists the current values, as requested.
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
            Margin = new Padding(0, 0, 0, 6),
            Name = name + "Row",
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250f));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 8, 0),
        }, 0, 0);
        panel.Controls.Add(new TextBox
        {
            Text = value,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
            Name = name,
        }, 1, 0);
        return panel;
    }

    private static string AssetKeyText(Control row)
    {
        foreach (Control child in row.Controls)
        {
            if (child is TextBox box)
            {
                return box.Text;
            }
        }

        return "";
    }

    private void AddTypedPlayer()
    {
        var value = _newPlayer.Text.Trim();

        if (value.Length == 0)
        {
            _newPlayer.Focus();
            return;
        }

        if (!AddPlayer(value))
        {
            MessageBox.Show(
                this,
                $"«{value}» ya está en la lista.",
                "Universal Presence",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _newPlayer.Clear();
        _newPlayer.Focus();
    }

    private bool AddPlayer(string value)
    {
        if (_ignored.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _ignored.Add(value);
        RefreshIgnoredList(value);
        return true;
    }

    private void RemoveSelectedPlayer()
    {
        if (_ignoredList.SelectedItem is not string selected)
        {
            return;
        }

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
            {
                _ignoredList.Items.Add(value);
            }
        }
        finally
        {
            _ignoredList.EndUpdate();
        }

        if (!string.IsNullOrEmpty(select))
        {
            _ignoredList.SelectedItem = select;
        }
    }

    private bool SaveSettings()
    {
        var pending = _newPlayer.Text.Trim();
        if (pending.Length > 0)
        {
            AddPlayer(pending);
            _newPlayer.Clear();
        }

        _options.IgnoredPlayers = _ignored.ToList();
        _options.AppendPlayerToState = _appendPlayer.Checked;
        _options.UsePlayerNameAsActivityName = _usePlayerNameAsActivity.Checked;
        _options.HideIfNoArtwork = _hideIfNoArtwork.Checked;
        _options.StartWithWindows = _startWithWindows.Checked;
        _options.ShowProgress = _showProgress.Checked;
        _options.PauseRefreshSeconds = (int)_pauseRefresh.Value;
        _options.ShowPausedMedia = _showPausedMedia.Checked;
        _options.PauseResetProgress = _pauseResetProgress.Checked;
        _options.ShowPausedIcon = _showPausedIcon.Checked;
        _options.PauseAssetKey = AssetKeyText(_pauseAssetKey);
        _options.AppIconAssetKey = AssetKeyText(_appIconAssetKey);
        _options.Normalize();

        try
        {
            _options.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "No se pudo guardar la configuración:\n" + ex.Message,
                "Universal Presence",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        WindowsAutostart.Apply(_options.StartWithWindows);
        _onSaved();
        return true;
    }
}
