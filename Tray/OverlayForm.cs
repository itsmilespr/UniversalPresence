using System.Drawing;
using UniversalPresence.Configuration;
using UniversalPresence.Core;
using UniversalPresence.Localization;

namespace UniversalPresence.Tray;

public sealed class OverlayForm : Form
{
    private readonly Label _title = new() { Dock = DockStyle.Top, Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = Color.White, AutoSize = false, Height = 22 };
    private readonly Label _artist = new() { Dock = DockStyle.Top, Font = new Font("Segoe UI", 9f), ForeColor = Color.LightGray, AutoSize = false, Height = 18 };
    private readonly PresenceEngine _engine;
    private readonly AppOptions _options;

    public OverlayForm(PresenceEngine engine, AppOptions options)
    {
        _engine = engine;
        _options = options;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(320, 52);
        BackColor = options.DarkMode ? Color.FromArgb(30, 30, 30) : Color.FromArgb(30, 30, 30);
        Opacity = 0.85;
        var screen = Screen.PrimaryScreen!;
        Location = new Point(screen.WorkingArea.Right - Width - 12, screen.WorkingArea.Bottom - Height - 12);

        Controls.Add(_artist);
        Controls.Add(_title);
        _engine.Updated += RefreshOverlay;
        RefreshOverlay();
    }

    private void RefreshOverlay()
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshOverlay)); return; }
            var snap = _engine.LastSnapshot;
            _title.Text = snap?.Title ?? "Universal Presence";
            _artist.Text = snap is { IsActive: true } ? (snap.Artist ?? "") : (_engine.Paused ? "Paused" : "No playback");
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _engine.Updated -= RefreshOverlay;
        base.Dispose(disposing);
    }
}