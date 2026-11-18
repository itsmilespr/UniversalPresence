using System.Diagnostics;
using System.Drawing;
using UniversalPresence.Configuration;
using UniversalPresence.Core;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Tray;

/// <summary>
/// No main window: only a notification-area icon with a small menu. The presence engine
/// runs on its own task; UI updates are marshalled to the WinForms thread.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppOptions _options;
    private readonly Log _log;
    private readonly PresenceEngine _engine;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Control _dispatcher;
    private TrayVisualState _visualState = TrayVisualState.Idle;
    private bool _closing;
    private bool _settingsOpen;

    public TrayApplicationContext(AppOptions options, Log log, bool openSettings = false)
    {
        _options = options;
        _log = log;

        _dispatcher = new Control();
        _ = _dispatcher.Handle; // created on the WinForms thread, used to marshal

        _engine = new PresenceEngine(options, log);

        _statusItem = new ToolStripMenuItem("Iniciando…") { Enabled = false };

        _pauseItem = new ToolStripMenuItem("Pausar");
        _pauseItem.Click += (_, _) => TogglePause();

        var settingsItem = new ToolStripMenuItem("Configuración…");
        settingsItem.Click += (_, _) => OpenSettings();

        var logsItem = new ToolStripMenuItem("Abrir carpeta de registros");
        logsItem.Click += (_, _) => OpenPath(_log.Directory);

        var exitItem = new ToolStripMenuItem("Salir");
        exitItem.Click += (_, _) => BeginExit();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem("Universal Presence")
        {
            Enabled = false,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        });
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(logsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIcons.Get(TrayVisualState.Idle),
            Text = "Universal Presence",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _engine.Updated += OnEngineUpdated;
        _engine.Start();

        _log.Info("Icono de bandeja creado; esperando sesiones SMTC.");

        WindowsAutostart.Apply(_options.StartWithWindows);

        if (!_engine.DiscordConfigured)
        {
            _notifyIcon.ShowBalloonTip(
                6_000,
                "Universal Presence",
                "Aún no hay DiscordClientId. Ábreme desde el icono → «Configuración…» y añade el id de tu aplicación de Discord.",
                ToolTipIcon.Info);
        }


        UpdateUi();

        if (openSettings)
        {
            _dispatcher.BeginInvoke(new Action(OpenSettings));
        }
    }

    /// <summary>Opens the modal settings window; saves apply instantly, without restarting.</summary>
    private void OpenSettings()
    {
        if (_closing || _settingsOpen)
        {
            return;
        }

        _settingsOpen = true;

        try
        {
            using var form = new SettingsForm(_options, () => _engine.ApplySettings());
            form.ShowDialog();
            UpdateUi();
        }
        catch (Exception ex)
        {
            _log.Error("No se pudo abrir la configuración.", ex);
            MessageBox.Show(
                "No se pudo abrir la configuración:\n" + ex.Message,
                "Universal Presence",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private void OnEngineUpdated()
    {
        if (_closing)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(new Action(UpdateUi));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher already gone (ObjectDisposedException derives from this): shutting down.
        }
    }

    private void UpdateUi()
    {
        if (_closing)
        {
            return;
        }

        var paused = _engine.Paused;
        _pauseItem.Text = paused ? "Reanudar" : "Pausar";

        var status = _engine.StatusText;
        _statusItem.Text = Shorten(status, 90);
        _notifyIcon.Text = Shorten($"Universal Presence — {status}", 63);

        var visual = paused
            ? TrayVisualState.Paused
            : _engine.LastSnapshot is { IsActive: true }
                ? TrayVisualState.Playing
                : TrayVisualState.Idle;

        if (visual != _visualState)
        {
            _visualState = visual;
            _notifyIcon.Icon = TrayIcons.Get(visual);
        }
    }

    private void TogglePause()
    {
        _engine.SetPaused(!_engine.Paused);
        UpdateUi();
    }

    private void BeginExit()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _notifyIcon.Visible = false;
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            await _engine.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn($"Error al cerrar la presencia: {ex.Message}");
        }

        try
        {
            _dispatcher.BeginInvoke(new Action(ExitThread));
        }
        catch
        {
            ExitThread();
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                _log.Warn($"No existe la ruta: {path}");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"No se pudo abrir '{path}': {ex.Message}");
        }
    }

    private static string Shorten(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.Updated -= OnEngineUpdated;
            _engine.Dispose();
            _notifyIcon.Dispose();
            _menu.Dispose();
            _dispatcher.Dispose();
        }

        base.Dispose(disposing);
    }
}
