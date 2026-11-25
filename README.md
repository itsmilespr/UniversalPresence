# Universal Presence

**Discord Rich Presence for Windows.** Shows what you are listening to —in any player— with
cover art, a live progress bar and the player icon, by reading the system media sessions
(SMTC). No allow-lists, no account, no setup.

[English](README.md) | [Español](README.es.md)

[![Latest release](https://img.shields.io/github/v/release/itsmilespr/UniversalPresence?label=download&color=6d5efc)](https://github.com/itsmilespr/UniversalPresence/releases/latest)
[![MIT license](https://img.shields.io/badge/license-MIT-6ee7b7)](LICENSE)
[![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-4f8cff)](#requirements)

**[Download the latest version](https://github.com/itsmilespr/UniversalPresence/releases/latest)** ·
[Website](https://itsmilespr.github.io/UniversalPresence/) ·
[Technical notes (Spanish)](TECNICO.md)

---

## What it does

Run the executable, play music, and your Discord profile shows the song with its cover, artist,
album, a synced progress bar and the player icon. If you replay the same song, it adds a
`×2`, `×3`… counter.

You do not need to create a Discord application, paste tokens or pick your player from a list:
if Windows exposes the session, it is published.

## Features

- **No allow-list.** Spotify, foobar2000 (Gatefold), VLC, Apple Music, MusicBee, TIDAL,
  Windows Media Player… any player that uses SMTC.
- **Real cover art.** Taken from the player itself; if it does not expose one, iTunes and Deezer
  are searched in parallel, with a disk cache so requests are not repeated.
- **Synced progress bar.** Absolute timestamps, re-anchoring on pause and the real position on
  resume: the bar follows the audio even when you skip tracks.
- **Instant changes.** The app listens to Windows events and publishes on a separate thread,
  with a publisher that only keeps the latest activity: Discord's rate limit (5 updates per
  20 s) never blocks detection or skips.
- **Repeat counter.** Detects that you restarted the same song and shows `×N` in the state,
  without counting seeks or player glitches.
- **Sharp player icon.** Extracted at 256 px from the player's own `.exe` and cached.
- **Careful pause.** The cover never disappears, the bar freezes and an optional pause badge
  can be shown.
- **Self-updating.** Checks GitHub Releases on startup and every 6 h; downloads the new build,
  verifies its SHA-256 and installs it on exit (or immediately from the menu). Can be disabled
  from the tray menu. Optional **beta** channel (includes pre-releases) and a one-click
  **restore of the previous version** if an update does not behave.
- **Player controls from the tray.** Play/pause, next and previous are forwarded to the active
  player over SMTC, and the current song can be copied to the clipboard.
- **Away detection.** While the PC is idle or locked the activity is hidden, and it comes back
  on its own when you return. The idle threshold is configurable.
- **Listening statistics.** Optional local history (`history.jsonl`) with plays, hours, top
  artists and tracks, the last seven days and a CSV export.
- **Custom cover per app.** A cover of your own — an https URL or a local image — can be pinned
  to one player, overriding whatever it exposes.
- **Last.fm.** Optional scrobbling, plus “now playing” on your profile, authorized from the
  settings window without copying tokens by hand.
- **Share the app from your profile.** The activity carries two buttons, “Download” and “How it
  works”, so anyone who sees what you are playing can reach the project in one click.
- **Multi-language presence.** The text published to Discord is available in 18 languages and
  defaults to English: `"Language": "en"` (also `es`, `pt`, `fr`, `de`, `it`, `nl`, `pl`, `ru`,
  `uk`, `tr`, `ja`, `ko`, `zh-CN`, `zh-TW`, `id`, `sv`, `cs`).
- **Local by default.** No account, no telemetry and no own servers: only Discord's local IPC.
- **A single `.exe`.** Self-contained, no .NET install required. Optional start with Windows
  (enabled by default) from the settings window.

## Requirements

- Windows 10 (1809 or later) or Windows 11, **64-bit**.
- Discord running for the activity to appear.
- To use the published executable: **nothing else** (the .NET 8 runtime is bundled).
- To build: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

## Installation

1. Download `UniversalPresence.exe` from
   [Releases](https://github.com/itsmilespr/UniversalPresence/releases/latest).
2. Run it. It appears in the notification area, with no main window.
3. Play music and open Discord.

Nothing to install and no administrator rights are needed. Windows may show a SmartScreen
warning because the binary is not code-signed: “More info” → “Run anyway”.

## Usage

The tray menu has the essentials:

| Option | What it does |
| --- | --- |
| Status | Current song, player and Discord connection state. |
| Pause / Resume | Stops publishing (or clears the presence) without closing the app. |
| Settings… | Window with the options: start with Windows, ignored players, progress bar, pause and artwork. |
| Open logs folder | `%LOCALAPPDATA%\UniversalPresence\logs`. |
| Exit | Closes the app and clears the Discord activity. |

Settings are stored in `%APPDATA%\UniversalPresence\config.json` and applied live when saved.
Every key is documented in [TECNICO.md](TECNICO.md#configuración) (Spanish).

> The settings window itself is currently in Spanish. The text published to Discord, the
> activity buttons and the README are localized; the UI translation is the next step.

## Build from source

```bash
# Build
dotnet build UniversalPresence.csproj -c Release

# Self-contained single-file executable
dotnet publish UniversalPresence.csproj -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=None -o publish
```

The project uses .NET 8 (WinForms) and has **no NuGet dependencies**: the Discord IPC protocol
is implemented by hand. Every push is built with GitHub Actions, and pushing a `v*` tag
publishes the release with the `.exe` attached.

## Privacy

Universal Presence has no servers and no accounts. It only writes on your machine and talks to
Discord over its local channel. So Discord can display the extracted cover, it is uploaded to
Litterbox (catbox.moe), Imgur or a self-hosted permanent cover host (`"UploadMode": "SelfHosted"`); this can be disabled with `"Artwork": { "UploadMode": "None" }`. To find
a cover when the player does not provide one, title/artist/album are sent to the public iTunes
and Deezer APIs; this can be disabled with `"Artwork": { "Mode": "None" }`. To update itself, it
queries the public GitHub API on startup and every 6 h (no account or token); this can be
disabled from the tray menu or with `"AutoUpdate": false`.

## Known limitations

- One activity at a time: if two players are playing, the one that is playing (or the one
  Windows reports as the current session) is published.
- Browsers are ignored by default so a YouTube tab does not appear as music; this can be
  changed in “Ignored players”.
- Some players do not expose cover art or duration over SMTC: the fields they do provide are
  shown.
- The settings window is only in Spanish for now.
- The executable is not code-signed, so SmartScreen may warn on first run.

## Roadmap

- Translate the settings window (currently Spanish only).
- Add unit tests for the pure logic in `Core/` and `Media/`.
- Code-sign the published executable to avoid the SmartScreen warning.

## License

[MIT](LICENSE). You can use, modify and redistribute it while keeping the copyright notice.
