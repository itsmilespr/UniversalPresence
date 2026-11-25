# Changelog

All notable changes are documented here. Versions follow [SemVer](https://semver.org/).
The full release history (including binaries and SHA-256) lives on the
[GitHub Releases page](https://github.com/itsmilespr/UniversalPresence/releases).

## [Unreleased] (local, not published yet)

### Changed
- Memory watchdog now runs every 5 s with an 80 MB threshold (was 30 s / 90 MB),
  keeping the private-memory peak near 90 MB instead of 200 MB+.
- SMTC caches live longer (snapshot 10 s, metadata 30 s, playback 5 s, timeline 3 s);
  invalidation is driven by SMTC events, so freshness is unaffected.
- Polling drops to every 2 s while paused or with no active session.
- "Ignored by IgnoredPlayers" is logged once per AUMID instead of every poll.

### Fixed
- Spotify reports only the main artist in SMTC; the presence now completes the artist
  line with Deezer's full track credits (main + featured) when the catalog match is
  unambiguous. If there is no match, the original artist is kept.

### Performance
- Measured private memory 39–101 MB (peak ~92–101 MB) and a track change confirmed
  in Discord in ~98 ms.

## [1.8.4] - 2026-11-20

### Progress bar
- Automatic resync every 5 s: compares the published bar with the real position and
  re-sends timestamps if it drifted more than 400 ms (`ResyncEverySeconds`; 0 disables).
- Instant seeks: dragging or jumping inside the same track is detected through the SMTC
  timeline event and re-published immediately.
- A new track is published at once; if Discord does not confirm it, it retries on its own
  after 2 s.
- Dragging to the start no longer counts as a repeat (×2).

### Covers
- An upload failure is remembered for 60 s instead of 10 minutes.
- Thumbnail read failures are retried after 45 s instead of being memoized.
- Pending uploads are retried at the start of each track and every 2 minutes.
- The current track's cover takes priority over older uploads.
- Changing host/token clears cached failures and retries immediately.

### Install
- Download `UniversalPresence.exe`, close the app if it is running, and run it. SmartScreen
  may warn because the binary is unsigned.

## [1.8.2] and earlier

See the [GitHub Releases page](https://github.com/itsmilespr/UniversalPresence/releases)
for notes and binaries for v1.4.1 through v1.8.2.
