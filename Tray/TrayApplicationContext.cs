using System.Diagnostics;
using System.Drawing;
using UniversalPresence.Configuration;
using UniversalPresence.Core;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;
using UniversalPresence.Localization;
using UniversalPresence.Media;

namespace UniversalPresence.Tray;

/// <summary>
/// No main window: only a notification-area icon with a small menu. The presence engine
/// runs on its own task; UI updates are marshalled to the WinForms thread.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppOptions _options;
    private UiText _ui;
    private UiStrings _strings;
    private readonly Log _log;
    private readonly PresenceEngine _engine;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _resyncItem;
    private readonly ToolStripMenuItem _refreshCoverItem;
    private readonly ToolStripMenuItem _copyTrackItem;
    private readonly ToolStripMenuItem _playPauseItem;
    private readonly ToolStripMenuItem _nextTrackItem;
    private readonly ToolStripMenuItem _previousTrackItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _autoUpdateItem;
    private readonly ToolStripMenuItem _restoreItem;
    private readonly ToolStripMenuItem _channelStableItem;
    private readonly ToolStripMenuItem _channelBetaItem;
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
        _strings = UiStrings.For(options.Language);
        _log = log;

        _dispatcher = new Control();
        _ = _dispatcher.Handle; // created on the WinForms thread, used to marshal

        _engine = new PresenceEngine(options, log);
        _updater = new UpdateService(log);

        _statusItem = new ToolStripMenuItem(_ui.Starting) { Enabled = false };

        _copyTrackItem = new ToolStripMenuItem(_strings["CopyTrack"]) { Enabled = false };
        _copyTrackItem.Click += (_, _) => CopyCurrentTrack();

        _playPauseItem = new ToolStripMenuItem(_strings["PlayPause"]);
        _playPauseItem.Click += (_, _) => RunControl(SmtcMonitor.MediaControlAction.TogglePlayPause);

        _nextTrackItem = new ToolStripMenuItem(_strings["NextTrack"]);
        _nextTrackItem.Click += (_, _) => RunControl(SmtcMonitor.MediaControlAction.NextTrack);

        _previousTrackItem = new ToolStripMenuItem(_strings["PreviousTrack"]);
        _previousTrackItem.Click += (_, _) => RunControl(SmtcMonitor.MediaControlAction.PreviousTrack);

        _pauseItem = new ToolStripMenuItem(_ui.Pause);
        _pauseItem.Click += (_, _) => TogglePause();

        var settingsItem = new ToolStripMenuItem(_ui.Settings);
        settingsItem.Click += (_, _) => OpenSettings();

        var logsItem = new ToolStripMenuItem(_ui.OpenLogs);
        logsItem.Click += (_, _) => OpenPath(_log.Directory);

        _resyncItem = new ToolStripMenuItem(_strings["ResyncNow"]);
        _resyncItem.Click += (_, _) =>
        {
            _engine.RequestResync();
            UpdateUi();
        };

        _refreshCoverItem = new ToolStripMenuItem(_strings["RefreshCover"]) { Enabled = false };
        _refreshCoverItem.Click += (_, _) =>
        {
            if (_engine.RefreshCurrentCover())
            {
                _log.Info("Portada actual descartada; se busca otra.");
                _notifyIcon?.ShowBalloonTip(3_000, _ui.AppName, _strings["CoverSearchStarted"], ToolTipIcon.Info);
            }
            else
            {
                _notifyIcon?.ShowBalloonTip(3_000, _ui.AppName, _strings["TrackUnknown"], ToolTipIcon.Info);
            }

            UpdateUi();
        };

        var logViewerItem = new ToolStripMenuItem(_strings["LogOpen"]);
        logViewerItem.Click += (_, _) => OpenLogViewer();

        var statsItem = new ToolStripMenuItem(_strings["Stats"]);
        statsItem.Click += (_, _) => OpenStats();

        _restoreItem = new ToolStripMenuItem(_strings["RestorePrevious"])
        {
            Enabled = _updater.HasPreviousVersion,
        };
        _restoreItem.Click += (_, _) => RestorePreviousVersion(confirm: true);

        _channelStableItem = new ToolStripMenuItem(_strings["ChannelStable"])
        {
            Checked = _options.UpdateChannel != "beta",
        };
        _channelStableItem.Click += (_, _) => SetUpdateChannel("stable");

        _channelBetaItem = new ToolStripMenuItem(_strings["ChannelBeta"])
        {
            Checked = _options.UpdateChannel == "beta",
        };
        _channelBetaItem.Click += (_, _) => SetUpdateChannel("beta");

        var channelMenu = new ToolStripMenuItem(_strings["ChannelMenu"]);
        channelMenu.DropDownItems.Add(_channelStableItem);
        channelMenu.DropDownItems.Add(_channelBetaItem);

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

        var playbackMenu = new ToolStripMenuItem(_strings["PlaybackMenu"]);
        playbackMenu.DropDownItems.Add(_playPauseItem);
        playbackMenu.DropDownItems.Add(_nextTrackItem);
        playbackMenu.DropDownItems.Add(_previousTrackItem);

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
        _menu.Items.Add(_copyTrackItem);
        _menu.Items.Add(playbackMenu);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_resyncItem);
        _menu.Items.Add(_refreshCoverItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(statsItem);
        _menu.Items.Add(logViewerItem);
        _menu.Items.Add(logsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_updateItem);
        _menu.Items.Add(_autoUpdateItem);
        _menu.Items.Add(channelMenu);
        _menu.Items.Add(_restoreItem);
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
                    else if (id == 3) RunControl(SmtcMonitor.MediaControlAction.NextTrack);
                    else if (id == 4) RunControl(SmtcMonitor.MediaControlAction.PreviousTrack);
                });
                // Ctrl+Alt+P = pausa, S = ajustes, → / ← = siguiente / anterior
                _hotkeys.Register(1, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.P);
                _hotkeys.Register(2, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.S);
                _hotkeys.Register(3, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.Right);
                _hotkeys.Register(4, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.Left);
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
                using var fr = new FirstRunForm(_options, _engine.DiscordState == DiscordConnectionState.Connected);
                if (fr.ShowDialog() == DialogResult.OK)
                {
                    fr.Apply();
                    _ui = UiText.For(_options.Language);
                    _strings = UiStrings.For(_options.Language);
                    _engine.ApplySettings();
                    UpdateUi();

                    if (fr.OpenSettingsRequested)
                    {
                        _dispatcher.BeginInvoke(new Action(OpenSettings));
                    }
                }
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
                _strings = UiStrings.For(_options.Language);
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

        // Controles rápidos: solo activos cuando hay una sesión que los acepte.
        var track = _engine.CurrentTrackText;
        var hasTrack = track is not null;
        _copyTrackItem.Enabled = hasTrack;
        _copyTrackItem.Text = hasTrack
            ? $"{_strings["CopyTrack"]} · {Shorten(track!, 48)}"
            : _strings["TrackUnknown"];
        _playPauseItem.Enabled = hasTrack;
        _nextTrackItem.Enabled = hasTrack;
        _previousTrackItem.Enabled = hasTrack;
        _refreshCoverItem.Enabled = hasTrack;
        _restoreItem.Enabled = _updater.HasPreviousVersion;
        _channelStableItem.Checked = _options.UpdateChannel != "beta";
        _channelBetaItem.Checked = _options.UpdateChannel == "beta";

        var status = _engine.StatusText;
        _statusItem.Text = Shorten(status, 90);
        _notifyIcon.Text = Shorten($"Universal Presence — {status}", 63);

        try
        {
            File.WriteAllText(Path.Combine(AppOptions.LocalDataDirectory, "status.txt"), status);
        }
        catch { }

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
                case "stats": OpenStats(); break;
                case "logs": OpenLogViewer(); break;
                case "resync": _engine.RequestResync(); break;
                case "cover": _engine.RefreshCurrentCover(); break;
                case "restore": RestorePreviousVersion(confirm: true); break;
                case "restore-now": RestorePreviousVersion(confirm: false); break;
                case "exit": BeginExit(); break;
                case "reload":
                    _engine.ApplySettings();
                    _ui = UiText.For(_options.Language);
                    _strings = UiStrings.For(_options.Language);
                    ApplyHotkeys();
                    ApplyOverlay();
                    UpdateUi();
                    break;
                default:
                    if (cmd.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
                    {
                        var profileName = cmd["profile:".Length..].Trim();
                        if (profileName.Length > 0 && ProfileService.Switch(_options, profileName))
                        {
                            _engine.ApplySettings();
                            _ui = UiText.For(_options.Language);
                            _strings = UiStrings.For(_options.Language);
                            ApplyHotkeys();
                            ApplyOverlay();
                            UpdateUi();
                        }
                    }
                    break;
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

    private void CopyCurrentTrack()
    {
        var track = _engine.CurrentTrackText;
        if (string.IsNullOrWhiteSpace(track))
        {
            return;
        }

        try
        {
            Clipboard.SetText(track);
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo copiar la canción: {ex.Message}");
        }
    }

    /// <summary>
    /// Reenvía una orden de transporte por SMTC. Windows la aplica al reproductor activo,
    /// así que sirve para cualquier app que ya aparezca en el panel de medios.
    /// </summary>
    private void RunControl(SmtcMonitor.MediaControlAction action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await _engine.ControlPlaybackAsync(action).ConfigureAwait(false);
                if (!ok)
                {
                    _log.Debug($"El reproductor rechazó la orden {action}.");
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"No se pudo enviar la orden {action}: {ex.Message}");
            }
        });
    }

    private void OpenLogViewer()
    {
        try
        {
            using var form = new LogViewerForm(_options, _log);
            form.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.Error("No se pudo abrir el registro.", ex);
        }
    }

    private void OpenStats()
    {
        try
        {
            using var form = new StatsForm(_options);
            form.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.Error("No se pudo abrir las estadísticas de escucha.", ex);
        }
    }

    private void SetUpdateChannel(string channel)
    {
        _options.UpdateChannel = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";

        try
        {
            _options.Save();
        }
        catch (Exception ex)
        {
            _log.Warn($"No se pudo guardar el canal de actualizaciones: {ex.Message}");
        }

        UpdateUi();
    }

    private void RestorePreviousVersion(bool confirm)
    {
        if (!_updater.HasPreviousVersion)
        {
            _notifyIcon.ShowBalloonTip(6_000, _ui.AppName, _strings["BackupNone"], ToolTipIcon.Info);
            return;
        }

        if (confirm)
        {
            var answer = MessageBox.Show(
                _strings["BackupConfirm"],
                _ui.AppName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        if (!_updater.TryRestorePreviousVersion())
        {
            MessageBox.Show(
                string.Format(_strings["BackupFailed"], ""),
                _ui.AppName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        BeginExit();
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
                    else if (id == 3) RunControl(SmtcMonitor.MediaControlAction.NextTrack);
                    else if (id == 4) RunControl(SmtcMonitor.MediaControlAction.PreviousTrack);
                });
                hw.Register(1, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.P);
                hw.Register(2, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.S);
                hw.Register(3, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.Right);
                hw.Register(4, HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (uint)Keys.Left);
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
            var info = await _updater.CheckAsync(_options.UpdateChannel == "beta");

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
