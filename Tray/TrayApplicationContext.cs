using System.Diagnostics;
using System.Drawing;
using UniversalPresence.Configuration;
using UniversalPresence.Core;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;
using UniversalPresence.Localization;

namespace UniversalPresence.Tray;

/// <summary>
/// No main window: only a notification-area icon with a small menu. The presence engine
/// runs on its own task; UI updates are marshalled to the WinForms thread.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppOptions _options;
    private UiText _ui;
    private readonly Log _log;
    private readonly PresenceEngine _engine;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _autoUpdateItem;
    private readonly UpdateService _updater;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly Control _dispatcher;
    private bool _updateBusy;
    private TrayVisualState _visualState = TrayVisualState.Idle;
    private bool _closing;
    private bool _settingsOpen;
    private string _lastToastTrack = "";
    private HotkeyWindow? _hotkeys;
    private OverlayForm? _overlay;
    private System.Windows.Forms.Timer? _cmdTimer;

    public TrayApplicationContext(AppOptions options, Log log, bool openSettings = false)
    {
        _options = options;
        _ui = UiText.For(options.Language);
        _log = log;

        _dispatcher = new Control();
        _ = _dispatcher.Handle; // created on the WinForms thread, used to marshal

        _engine = new PresenceEngine(options, log);
        _updater = new UpdateService(log);

        _statusItem = new ToolStripMenuItem(_ui.Starting) { Enabled = false };

        _pauseItem = new ToolStripMenuItem(_ui.Pause);
        _pauseItem.Click += (_, _) => TogglePause();

        var settingsItem = new ToolStripMenuItem(_ui.Settings);
        settingsItem.Click += (_, _) => OpenSettings();

        var logsItem = new ToolStripMenuItem(_ui.OpenLogs);
        logsItem.Click += (_, _) => OpenPath(_log.Directory);

        _updateItem = new ToolStripMenuItem(_ui.CheckUpdates);
        _updateItem.Click += (_, _) =>
        {
            if (_updater.HasDownloadedUpdate)
            {
                BeginExit();
                return;
            }

            _ = CheckForUpdatesAsync(manual: true);
        };

        _autoUpdateItem = new ToolStripMenuItem(_ui.AutoUpdate)
        {
            Checked = _options.AutoUpdate,
            CheckOnClick = true,
        };
        _autoUpdateItem.CheckedChanged += (_, _) => SetAutoUpdate(_autoUpdateItem.Checked);

        var exitItem = new ToolStripMenuItem(_ui.Exit);
        exitItem.Click += (_, _) => BeginExit();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem(_ui.AppName)
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
        _menu.Items.Add(_updateItem);
        _menu.Items.Add(_autoUpdateItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIcons.Get(TrayVisualState.Idle),
            Text = _ui.AppName,
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _engine.Updated += OnEngineUpdated;
        _engine.Start();

        _log.Info("Tray icon created; waiting for SMTC sessions.");

        WindowsAutostart.Apply(_options.StartWithWindows);

        if (_options.GlobalHotkeys)
        {
            try
            {
                _hotkeys = new HotkeyWindow(id =>
                {
                    if (id == 1) TogglePause();
                    else if (id == 2) _dispatcher.BeginInvoke(new Action(OpenSettings));
                });
                // Ctrl+Alt+P = pause/resume, Ctrl+Alt+S = settings
                _hotkeys.Register(1, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.P);
                _hotkeys.Register(2, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.S);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not register global hotkeys: {ex.Message}");
            }
        }

        if (!_engine.DiscordConfigured)
        {
            _notifyIcon.ShowBalloonTip(
                6_000,
                _ui.AppName,
                _ui.NoDiscordClientId,
                ToolTipIcon.Info);
        }


        UpdateUi();

        // Primera comprobación 20 s después de arrancar (deja respirar a Discord/SMTC) y
        // luego cada 6 h. Sin `AutoUpdate` el tick no hace nada.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 20_000 };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 6 * 60 * 60 * 1000;
            await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();

        _cmdTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _cmdTimer.Tick += (_, _) => PollCommand();
        _cmdTimer.Start();

        if (!_options.FirstRunCompleted)
        {
            try
            {
                using var fr = new FirstRunForm(_options);
                if (fr.ShowDialog() == DialogResult.OK) fr.Apply();
            }
            catch (Exception ex) { _log.Warn($"First-run wizard failed: {ex.Message}"); }
        }

        if (_options.ShowOverlay)
        {
            try { _overlay = new OverlayForm(_engine, _options); _overlay.Show(); }
            catch (Exception ex) { _log.Warn($"Could not open the overlay: {ex.Message}"); }
        }

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
            using var form = new SettingsForm(_options, () =>
            {
                _engine.ApplySettings();
                _ui = UiText.For(_options.Language);
                ApplyHotkeys();
                ApplyOverlay();
            });
            form.ShowDialog();
            UpdateUi();
        }
        catch (Exception ex)
        {
            _log.Error("Could not open the settings.", ex);
            MessageBox.Show(
                string.Format(_ui.OpenSettingsFailed, ex.Message),
                _ui.AppName,
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

        // Toast al cambiar de pista.
        try
        {
            var snap = _engine.LastSnapshot;
            if (_options.ShowTrackChangeToast && snap is { IsActive: true })
            {
                var key = $"{snap.Title}|{snap.Artist}";
                if (!string.Equals(key, _lastToastTrack, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(snap.Title))
                {
                    _lastToastTrack = key;
                    var text = string.IsNullOrWhiteSpace(snap.Artist) ? snap.Title : $"{snap.Title} — {snap.Artist}";
                    _notifyIcon.ShowBalloonTip(4_000, _ui.AppName, text, ToolTipIcon.Info);
                }
            }
        }
        catch { }

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
        _pauseItem.Text = paused ? _ui.Resume : _ui.Pause;

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

    private void PollCommand()
    {
        try
        {
            var p = Path.Combine(AppOptions.LocalDataDirectory, "command.txt");
            if (!File.Exists(p)) return;
            var cmd = File.ReadAllText(p).Trim().ToLowerInvariant();
            File.Delete(p);
            switch (cmd)
            {
                case "pause": _engine.SetPaused(true); break;
                case "resume": _engine.SetPaused(false); break;
                case "toggle": TogglePause(); break;
                case "settings": OpenSettings(); break;
                case "exit": BeginExit(); break;
            }
            UpdateUi();
        }
        catch { }
    }

    private void TogglePause()
    {
        _engine.SetPaused(!_engine.Paused);
        UpdateUi();
    }

    private void ApplyHotkeys()
    {
        try
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            if (_options.GlobalHotkeys)
            {
                var hw = new HotkeyWindow(id =>
                {
                    if (id == 1) TogglePause();
                    else if (id == 2) _dispatcher.BeginInvoke(new Action(OpenSettings));
                });
                hw.Register(1, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.P);
                hw.Register(2, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.S);
                _hotkeys = hw;
            }
        }
        catch (Exception ex) { _log.Warn($"Could not register global hotkeys: {ex.Message}"); }
    }

    private void ApplyOverlay()
    {
        try
        {
            if (_options.ShowOverlay)
            {
                if (_overlay == null || _overlay.IsDisposed)
                {
                    _overlay = new OverlayForm(_engine, _options);
                    _overlay.Show();
                }
            }
            else
            {
                if (_overlay != null && !_overlay.IsDisposed)
                {
                    _overlay.Close();
                    _overlay.Dispose();
                }
                _overlay = null;
            }
        }
        catch (Exception ex) { _log.Warn($"Could not open the overlay: {ex.Message}"); }
    }

    private void SetAutoUpdate(bool enabled)
    {
        _options.AutoUpdate = enabled;

        try
        {
            _options.Save();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not save the update preference: {ex.Message}");
        }
    }

    /// <summary>
    /// Comprueba GitHub Releases y, si hay una versión nueva, la descarga en segundo plano.
    /// Se instala al cerrar (o al pulsar «Actualizar» en el menú).
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_closing || _updateBusy)
        {
            return;
        }

        if (!manual && !_options.AutoUpdate)
        {
            return;
        }

        _updateBusy = true;

        if (manual)
        {
            _updateItem.Text = _ui.CheckingUpdates;
            _updateItem.Enabled = false;
        }

        try
        {
            var info = await _updater.CheckAsync();

            if (_closing)
            {
                return;
            }

            if (info is null)
            {
                if (manual)
                {
                    _notifyIcon.ShowBalloonTip(
                        6_000,
                        _ui.AppName,
                        string.Format(_ui.UpToDate, _updater.CurrentVersion),
                        ToolTipIcon.Info);
                }

                return;
            }

            _updateItem.Text = string.Format(_ui.Downloading, info.Tag);
            _updateItem.Enabled = false;
            _notifyIcon.ShowBalloonTip(
                7_000,
                _ui.AppName,
                string.Format(_ui.UpdateAvailable, info.Tag),
                ToolTipIcon.Info);

            var path = await _updater.DownloadAsync(info);

            if (_closing)
            {
                return;
            }

            if (path is null)
            {
                _notifyIcon.ShowBalloonTip(
                    7_000,
                    _ui.AppName,
                    _ui.DownloadFailed,
                    ToolTipIcon.Warning);
                return;
            }

            _updateItem.Text = $"Actualizar a {info.Tag} y reiniciar";
            _updateItem.Enabled = true;
            _notifyIcon.ShowBalloonTip(
                8_000,
                _ui.AppName,
                string.Format(_ui.UpdateReady, info.Tag),
                ToolTipIcon.Info);
        }
        catch (OperationCanceledException)
        {
            // cerrando
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not check for updates: {ex.Message}");

            if (manual)
            {
                _notifyIcon.ShowBalloonTip(
                    7_000,
                    _ui.AppName,
                    _ui.UpdateCheckFailed,
                    ToolTipIcon.Warning);
            }
        }
        finally
        {
            _updateBusy = false;

            if (!_closing && !_updater.HasDownloadedUpdate)
            {
                _updateItem.Text = _ui.CheckUpdates;
                _updateItem.Enabled = true;
            }
        }
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
            _log.Warn($"Error while closing presence: {ex.Message}");
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
                _log.Warn($"Path does not exist: {path}");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open '{path}': {ex.Message}");
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
            _closing = true;
            _updateTimer.Stop();
            _updateTimer.Dispose();
            _cmdTimer?.Stop();
            _cmdTimer?.Dispose();

            _engine.Updated -= OnEngineUpdated;
            _engine.Dispose();

            // Cualquier cierre ordenado instala la actualización descargada: el script
            // espera a que este proceso desaparezca antes de reemplazar el .exe.
            if (_updater.HasDownloadedUpdate)
            {
                _updater.TryApplyAndRestart();
            }

            _updater.Dispose();
            _notifyIcon.Dispose();
            _menu.Dispose();
            _hotkeys?.Dispose();
            _overlay?.Dispose();
            _dispatcher.Dispose();
        }

        base.Dispose(disposing);
    }
}
