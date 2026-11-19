using System.Diagnostics;
using System.Drawing;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using UniversalPresence.Localization;

namespace UniversalPresence.Tray;

/// <summary>
/// Visor del registro de actividad.
///
/// Antes, para saber por qué una portada no aparecía había que abrir la carpeta de logs con
/// el Bloc de notas y buscar a mano entre miles de líneas. Esta ventana muestra el final del
/// registro, permite filtrar por nivel y por texto, y copiar el resultado. También genera el
/// paquete de diagnóstico (con los secretos censurados) sin salir de la app.
/// </summary>
public sealed class LogViewerForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 120, 212);
    private static readonly Color PageBg = Color.FromArgb(243, 245, 249);

    private readonly AppOptions _options;
    private readonly UiStrings _ui;
    private readonly Log _log;
    private readonly string _path;

    private readonly ComboBox _levelBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 120,
        AccessibleName = "Log level",
    };

    private readonly TextBox _searchBox = new()
    {
        Width = 200,
        PlaceholderText = "…",
        AccessibleName = "Search",
    };

    private readonly CheckBox _followBox = new() { AutoSize = true };
    private readonly RichTextBox _output = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        WordWrap = false,
        BorderStyle = BorderStyle.None,
        ScrollBars = RichTextBoxScrollBars.Both,
        DetectUrls = false,
        Font = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point),
        BackColor = Color.White,
        AccessibleName = "Log",
    };

    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(20, 0, 0, 0),
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private long _lastLength = -1;
    private DateTime _lastWrite = DateTime.MinValue;

    public LogViewerForm(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
        _ui = UiStrings.For(options.Language);
        _path = log.FilePath;

        Text = _ui["LogTitle"];
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        ClientSize = new Size(900, 600);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = PageBg;

        _levelBox.Items.Add(new LevelItem(_ui["LogAll"], null));
        _levelBox.Items.Add(new LevelItem("DEBUG", LogLevel.Debug));
        _levelBox.Items.Add(new LevelItem("INFO", LogLevel.Info));
        _levelBox.Items.Add(new LevelItem("WARN", LogLevel.Warn));
        _levelBox.Items.Add(new LevelItem("ERROR", LogLevel.Error));
        _levelBox.SelectedIndex = 0;
        _levelBox.SelectedIndexChanged += (_, _) => Render(force: true);

        _searchBox.PlaceholderText = _ui["LogSearchHint"];
        _searchBox.TextChanged += (_, _) => Render(force: true);

        _followBox.Text = _ui["LogFollow"];
        _followBox.Checked = true;
        _followBox.CheckedChanged += (_, _) => UpdateTimer();

        var refreshButton = MakeButton(_ui["LogRefresh"], "Refresh");
        refreshButton.Click += (_, _) => Render(force: true);

        var copyButton = MakeButton(_ui["LogCopy"], "Copy");
        copyButton.Click += (_, _) => CopyLog();

        var folderButton = MakeButton(_ui["LogOpenFolder"], "OpenFolder");
        folderButton.Click += (_, _) => OpenFolder(_log.Directory);

        var diagnosticsButton = MakeButton(_ui["LogExportDiagnostics"], "ExportDiagnostics");
        diagnosticsButton.Click += (_, _) => ExportDiagnostics();

        var closeButton = MakeButton(_ui["StatsClose"], "Close");
        closeButton.Click += (_, _) => Close();

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(14, 10, 14, 6),
            BackColor = PageBg,
        };
        toolbar.Controls.Add(new Label
        {
            Text = _ui["LogLevel"],
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0),
        });
        toolbar.Controls.Add(_levelBox);
        toolbar.Controls.Add(new Label
        {
            Text = _ui["LogFilter"],
            AutoSize = true,
            Margin = new Padding(12, 8, 6, 0),
        });
        toolbar.Controls.Add(_searchBox);
        toolbar.Controls.Add(_followBox);
        toolbar.Controls.Add(refreshButton);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(14, 0, 14, 8),
            BackColor = PageBg,
        };
        actions.Controls.Add(copyButton);
        actions.Controls.Add(folderButton);
        actions.Controls.Add(diagnosticsButton);
        actions.Controls.Add(closeButton);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 0, 20, 0),
            BackColor = PageBg,
        };
        body.Controls.Add(_output);

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
            Text = _ui["LogTitle"],
            Font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(20, 14),
            BackColor = Color.Transparent,
        });
        header.Controls.Add(new Label
        {
            Text = _ui["LogSubtitle"],
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(220, 235, 255),
            AutoSize = true,
            Location = new Point(22, 40),
            BackColor = Color.Transparent,
        });

        Controls.Add(body);
        Controls.Add(_status);
        Controls.Add(actions);
        Controls.Add(toolbar);
        Controls.Add(header);

        CancelButton = closeButton;
        AcceptButton = null;

        _timer.Tick += (_, _) => Render(force: false);
        Load += (_, _) =>
        {
            Render(force: true);
            UpdateTimer();
        };
        FormClosed += (_, _) => _timer.Stop();
        Shown += (_, _) =>
        {
            _output.SelectionStart = _output.TextLength;
            _output.ScrollToCaret();
        };

        ApplyTheme();
    }

    private LogLevel? SelectedLevel =>
        _levelBox.SelectedItem is LevelItem item ? item.Level : null;

    private void UpdateTimer()
    {
        if (_followBox.Checked)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Render(bool force)
    {
        var info = new FileInfo(_path);
        var length = info.Exists ? info.Length : 0;
        var write = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;

        if (!force && length == _lastLength && write == _lastWrite)
        {
            return;
        }

        _lastLength = length;
        _lastWrite = write;

        var lines = ReadTail();
        var level = SelectedLevel;
        var search = _searchBox.Text.Trim();

        if (level is not null)
        {
            lines = lines.Where(line => LineLevel(line) == level).ToList();
        }

        if (search.Length > 0)
        {
            lines = lines.Where(line => line.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }

        var stickToEnd = _output.SelectionStart >= _output.TextLength - 1;
        _output.SuspendLayout();

        try
        {
            _output.Clear();

            if (lines.Count == 0)
            {
                _output.SelectionColor = Color.FromArgb(120, 120, 120);
                _output.AppendText(_ui["LogEmpty"]);
                _status.Text = _ui.T("LogLines", 0);
                return;
            }

            foreach (var line in lines)
            {
                _output.SelectionColor = ColorFor(line);
                _output.AppendText(line + Environment.NewLine);
            }

            _status.Text = _ui.T("LogLines", lines.Count);
        }
        finally
        {
            _output.ResumeLayout();

            if (_followBox.Checked && stickToEnd)
            {
                _output.SelectionStart = _output.TextLength;
                _output.ScrollToCaret();
            }
        }
    }

    private List<string> ReadTail()
    {
        // FileShare.ReadWrite: la app sigue escribiendo en el mismo archivo mientras se lee.
        try
        {
            if (!File.Exists(_path))
            {
                return new List<string>();
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;

            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }

            // 4000 líneas cubren de sobra un arranque completo sin congelar la ventana.
            return lines.Count > 4000 ? lines.GetRange(lines.Count - 4000, 4000) : lines;
        }
        catch (Exception ex)
        {
            return new List<string> { $"No se pudo leer el registro: {ex.Message}" };
        }
    }

    private static LogLevel LineLevel(string line)
    {
        if (line.Contains("[ERROR]", StringComparison.Ordinal)) return LogLevel.Error;
        if (line.Contains("[WARN ]", StringComparison.Ordinal)) return LogLevel.Warn;
        if (line.Contains("[DEBUG]", StringComparison.Ordinal)) return LogLevel.Debug;
        return LogLevel.Info;
    }

    private static Color ColorFor(string line)
    {
        return LineLevel(line) switch
        {
            LogLevel.Error => Color.FromArgb(170, 30, 30),
            LogLevel.Warn => Color.FromArgb(150, 95, 0),
            LogLevel.Debug => Color.FromArgb(120, 120, 120),
            _ => Color.FromArgb(30, 30, 30),
        };
    }

    private void CopyLog()
    {
        if (_output.TextLength == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_output.Text);
            _status.Text = _ui["LogCopied"];
        }
        catch
        {
            // Otro programa puede tener el portapapeles bloqueado un instante.
        }
    }

    private void ExportDiagnostics()
    {
        try
        {
            var path = CrashDiagnostics.Write(null, _log, _options);
            _status.Text = path;

            var result = MessageBox.Show(
                this,
                _ui.T("LogExportDone", path),
                _ui["LogTitle"],
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (result == DialogResult.Yes)
            {
                Start(path);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                _ui["LogTitle"],
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OpenFolder(string directory)
    {
        try
        {
            Start(directory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, _ui["LogTitle"], MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void Start(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

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
        _output.BackColor = dark ? Color.FromArgb(45, 46, 50) : Color.White;
        _output.ForeColor = foreground;
        _levelBox.BackColor = dark ? Color.FromArgb(55, 57, 61) : Color.White;
        _searchBox.BackColor = dark ? Color.FromArgb(55, 57, 61) : Color.White;
        _status.BackColor = pageBg;

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
                foreach (Control child in p.Controls)
                {
                    if (child is Label lbl) lbl.ForeColor = Color.White;
                }
                break;
            case Panel or TableLayoutPanel or FlowLayoutPanel:
                control.BackColor = cardBg;
                control.ForeColor = foreground;
                break;
            case Button button:
                button.BackColor = dark ? Color.FromArgb(55, 57, 61) : SystemColors.Control;
                button.ForeColor = foreground;
                button.FlatStyle = FlatStyle.Flat;
                break;
            case Label label:
                label.BackColor = Color.Transparent;
                label.ForeColor = foreground;
                break;
            case RichTextBox or TextBox or ComboBox:
                control.BackColor = dark ? Color.FromArgb(45, 46, 50) : Color.White;
                control.ForeColor = foreground;
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

    private sealed class LevelItem
    {
        public LevelItem(string text, LogLevel? level)
        {
            Text = text;
            Level = level;
        }

        public string Text { get; }

        public LogLevel? Level { get; }

        public override string ToString() => Text;
    }
}
