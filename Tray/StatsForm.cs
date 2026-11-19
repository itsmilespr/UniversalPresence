using System.Drawing;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using UniversalPresence.Localization;

namespace UniversalPresence.Tray;

/// <summary>
/// Panel de estadísticas de escucha: totales, artistas/canciones más escuchados y los
/// últimos siete días, todo calculado desde history.jsonl (nada sale del equipo).
/// </summary>
public sealed class StatsForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 120, 212);
    private static readonly Color PageBg = Color.FromArgb(243, 245, 249);

    private readonly AppOptions _options;
    private readonly UiStrings _ui;
    private readonly ListeningHistoryService _history;

    private readonly ComboBox _rangeBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 180,
        AccessibleName = "Period",
    };

    private readonly Label _playsValue = KpiValue("Plays");
    private readonly Label _hoursValue = KpiValue("Hours");
    private readonly Label _artistsValue = KpiValue("Artists");
    private readonly Label _tracksValue = KpiValue("Tracks");

    private readonly ListView _artistsList = MakeList("Top artists");
    private readonly ListView _tracksList = MakeList("Top tracks");
    private readonly ListView _daysList = MakeList("Last 7 days");

    private readonly Label _empty = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(110, 110, 110),
        Visible = false,
    };

    private readonly Label _weeklyValue = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = false,
        AutoEllipsis = true,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
    };

    public StatsForm(AppOptions options)
    {
        _options = options;
        _ui = UiStrings.For(options.Language);
        _history = new ListeningHistoryService(
            options.ListeningHistoryRetentionDays,
            options.ListeningHistoryMaxEntries);

        Text = _ui["StatsTitle"];
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(680, 480);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = PageBg;
        Icon = TrayIcons.Get(TrayVisualState.Playing);

        _rangeBox.Items.Add(new RangeItem(_ui["StatsAllTime"], 0));
        _rangeBox.Items.Add(new RangeItem(_ui.T("StatsDays", 7), 7));
        _rangeBox.Items.Add(new RangeItem(_ui.T("StatsDays", 30), 30));
        _rangeBox.Items.Add(new RangeItem(_ui.T("StatsDays", 90), 90));
        _rangeBox.Items.Add(new RangeItem(_ui.T("StatsDays", 365), 365));
        _rangeBox.SelectedIndex = 1;
        _rangeBox.SelectedIndexChanged += (_, _) => RefreshStats();
        _rangeBox.AccessibleDescription = _ui["StatsRange"];

        var refreshButton = MakeButton(_ui["StatsRefresh"], "Refresh");
        refreshButton.Click += (_, _) => RefreshStats();

        var exportButton = MakeButton(_ui["StatsExport"], "Export");
        exportButton.Click += (_, _) => Export(json: false);

        var exportJsonButton = MakeButton(_ui["StatsExportJson"], "ExportJson");
        exportJsonButton.Click += (_, _) => Export(json: true);

        var closeButton = MakeButton(_ui["StatsClose"], "Close");
        closeButton.DialogResult = DialogResult.Cancel;
        closeButton.Click += (_, _) => Close();

        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(14, 10, 14, 4),
            BackColor = PageBg,
        };
        topBar.Controls.Add(new Label
        {
            Text = _ui["StatsRange"],
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0),
        });
        topBar.Controls.Add(_rangeBox);
        topBar.Controls.Add(refreshButton);
        topBar.Controls.Add(exportButton);
        topBar.Controls.Add(exportJsonButton);
        topBar.Controls.Add(closeButton);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 86,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(14, 4, 14, 8),
            BackColor = PageBg,
        };
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        for (var i = 0; i < 4; i++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        }

        cards.Controls.Add(MakeCard(_ui["StatsTotalPlays"], _playsValue), 0, 0);
        cards.Controls.Add(MakeCard(_ui["StatsTotalHours"], _hoursValue), 1, 0);
        cards.Controls.Add(MakeCard(_ui["StatsArtists"], _artistsValue), 2, 0);
        cards.Controls.Add(MakeCard(_ui["StatsTracks"], _tracksValue), 3, 0);

        _artistsList.Columns.Add("#", 40);
        _artistsList.Columns.Add(_ui["StatsArtists"], 240);
        _artistsList.Columns.Add(_ui["StatsTotalPlays"], 70);

        _tracksList.Columns.Add("#", 40);
        _tracksList.Columns.Add(_ui["StatsTracks"], 240);
        _tracksList.Columns.Add(_ui["StatsTotalPlays"], 70);

        _daysList.Columns.Add(_ui["StatsLast7Days"], 220);
        _daysList.Columns.Add(_ui["StatsTotalPlays"], 70);

        var lists = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(14, 0, 14, 14),
            BackColor = PageBg,
        };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 67f));
        lists.RowCount = 1;
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var daysCard = MakeListCard(_ui["StatsLast7Days"], _daysList);
        var topCard = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = PageBg,
            Margin = new Padding(10, 0, 0, 0),
        };
        topCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        topCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        topCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        topCard.Controls.Add(MakeListCard(_ui["StatsTopArtists"], _artistsList), 0, 0);
        topCard.Controls.Add(MakeListCard(_ui["StatsTopTracks"], _tracksList), 1, 0);

        lists.Controls.Add(daysCard, 0, 0);
        lists.Controls.Add(topCard, 1, 0);

        _empty.Text = _ui["StatsEmpty"];

        lists.Controls.Add(_empty, 0, 0);
        lists.SetColumnSpan(_empty, 2);
        _empty.BringToFront();

        var header = new Panel
        {
            Name = "HeaderPanel",
            Dock = DockStyle.Top,
            Height = 62,
            BackColor = Accent,
            Padding = new Padding(20, 14, 20, 12),
        };
        header.Controls.Add(new Label
        {
            Text = _ui["StatsTitle"],
            Font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(20, 14),
            BackColor = Color.Transparent,
        });
        header.Controls.Add(new Label
        {
            Text = _ui["StatsSubtitle"],
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(220, 235, 255),
            AutoSize = true,
            Location = new Point(22, 40),
            BackColor = Color.Transparent,
        });

        var weeklyPanel = new Panel
        {
            Name = "WeeklyPanel",
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(20, 6, 20, 6),
            BackColor = Color.White,
        };
        weeklyPanel.Controls.Add(_weeklyValue);
        RoundCorners(weeklyPanel, 10);

        Controls.Add(lists);
        Controls.Add(weeklyPanel);
        Controls.Add(cards);
        Controls.Add(topBar);
        Controls.Add(header);

        AcceptButton = null;
        CancelButton = closeButton;

        ApplyTheme();
        RefreshStats();
    }

    private static Label KpiValue(string name) => new()
    {
        Text = "0",
        AutoSize = true,
        Font = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Point),
        Margin = new Padding(0, 0, 0, 2),
        AccessibleName = name,
    };

    private static ListView MakeList(string name) => new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        AccessibleName = name,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
    };

    private static Button MakeButton(string text, string name) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(96, 30),
        Name = name,
        AccessibleName = text,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(8, 2, 0, 4),
    };

    private static Panel MakeCard(string caption, Label value)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(12, 8, 12, 8),
        };

        var captionLabel = new Label
        {
            Text = caption,
            Dock = DockStyle.Top,
            AutoSize = true,
            Height = 18,
            ForeColor = Color.FromArgb(110, 110, 110),
            Tag = "muted",
        };

        value.Dock = DockStyle.Fill;

        card.Controls.Add(value);
        card.Controls.Add(captionLabel);
        RoundCorners(card, 10);
        return card;
    }

    private static GroupBox MakeListCard(string title, ListView list)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 8, 0),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point),
        };
        group.Controls.Add(list);
        RoundCorners(group, 10);
        return group;
    }

    private int SelectedDays =>
        _rangeBox.SelectedItem is RangeItem item ? item.Days : 7;

    private void RefreshStats()
    {
        var stats = _history.Compute(SelectedDays);

        _playsValue.Text = stats.Plays.ToString("N0");
        _hoursValue.Text = stats.Hours >= 10 ? stats.Hours.ToString("N0") : stats.Hours.ToString("0.0");
        _artistsValue.Text = stats.Artists.ToString("N0");
        _tracksValue.Text = stats.Tracks.ToString("N0");

        Fill(_artistsList, stats.TopArtists.Select(item => (item.Name, item.Count)));
        Fill(_tracksList, stats.TopTracks.Select(item => (item.Name, item.Count)));
        Fill(_daysList, stats.PerDay
            .OrderByDescending(day => day.Day)
            .Select(day => (day.Day.ToString("ddd d MMM"), day.Count)));

        _empty.Visible = stats.Plays == 0;

        var weekly = _history.ComputeWeekly();

        if (weekly.Plays == 0)
        {
            _weeklyValue.Text = _ui["StatsWeekEmpty"];
            return;
        }

        var line = _ui.T(
            "StatsWeekLine",
            weekly.Plays,
            weekly.Hours >= 10 ? weekly.Hours.ToString("N0") : weekly.Hours.ToString("0.0"),
            weekly.ActiveDays,
            string.IsNullOrWhiteSpace(weekly.TopTrack) ? weekly.TopArtist : weekly.TopTrack);

        // Comparar con la semana anterior solo tiene sentido si había algo con lo que comparar.
        if (weekly.PreviousPlays > 0)
        {
            var change = weekly.ChangePercent;
            line += " · " + _ui.T("StatsWeekChange", $"{change:+0;-0;0} %");
        }

        _weeklyValue.Text = line;
    }

    private static void Fill(ListView list, IEnumerable<(string Name, int Count)> rows)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            var index = 1;
            foreach (var (name, count) in rows)
            {
                var item = new ListViewItem(index.ToString());
                item.SubItems.Add(name);
                item.SubItems.Add(count.ToString("N0"));
                list.Items.Add(item);
                index++;
            }
        }
        finally
        {
            list.EndUpdate();
        }
    }

    private void Export(bool json)
    {
        using var dialog = new SaveFileDialog
        {
            Title = json ? _ui["StatsExportJson"] : _ui["StatsExport"],
            Filter = json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv",
            FileName = $"universal-presence-history-{DateTime.Now:yyyyMMdd}" + (json ? ".json" : ".csv"),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            if (json)
            {
                _history.ExportJson(dialog.FileName);
            }
            else
            {
                _history.ExportCsv(dialog.FileName);
            }
            MessageBox.Show(
                this,
                _ui.T("StatsExportDone", dialog.FileName),
                _ui["StatsTitle"],
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                _ui.T("StatsExportFailed", ex.Message),
                _ui["StatsTitle"],
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
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
        if (SystemInformation.HighContrast)
        {
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;
            return;
        }

        var dark = _options.DarkMode;
        var pageBg = dark ? Color.FromArgb(32, 33, 36) : PageBg;
        var cardBg = dark ? Color.FromArgb(41, 42, 45) : Color.White;
        var foreground = dark ? Color.FromArgb(232, 234, 237) : Color.Black;

        BackColor = pageBg;
        ForeColor = foreground;

        foreach (Control control in Controls)
        {
            ThemeControl(control, pageBg, cardBg, foreground, dark);
        }
    }

    private static void ThemeControl(Control control, Color pageBg, Color cardBg, Color foreground, bool dark)
    {
        switch (control)
        {
            case Panel p when p.Name == "HeaderPanel":
                p.BackColor = dark ? Color.FromArgb(138, 180, 248) : Accent;
                p.ForeColor = Color.White;
                foreach (Control child in p.Controls)
                {
                    if (child is Label lbl) lbl.ForeColor = Color.White;
                }
                break;
            case GroupBox or Panel or TableLayoutPanel or FlowLayoutPanel:
                control.BackColor = cardBg;
                control.ForeColor = foreground;
                break;
            case ListView list:
                list.BackColor = dark ? Color.FromArgb(45, 46, 50) : Color.White;
                list.ForeColor = foreground;
                break;
            case ComboBox or TextBox or NumericUpDown:
                control.BackColor = dark ? Color.FromArgb(55, 57, 61) : Color.White;
                control.ForeColor = foreground;
                break;
            case Button button:
                button.BackColor = dark ? Color.FromArgb(55, 57, 61) : SystemColors.Control;
                button.ForeColor = foreground;
                button.FlatStyle = FlatStyle.Flat;
                break;
            case Label label:
                label.BackColor = Color.Transparent;
                if (!Equals(label.Tag, "muted"))
                {
                    label.ForeColor = foreground;
                }
                break;
            default:
                control.BackColor = pageBg;
                control.ForeColor = foreground;
                break;
        }

        foreach (Control child in control.Controls)
        {
            ThemeControl(child, pageBg, cardBg, foreground, dark);
        }
    }

    private sealed class RangeItem
    {
        public RangeItem(string text, int days)
        {
            Text = text;
            Days = days;
        }

        public string Text { get; }

        public int Days { get; }

        public override string ToString() => Text;
    }
}
