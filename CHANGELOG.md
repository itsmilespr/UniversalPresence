# Changelog

All notable changes are documented here. Versions follow [SemVer](https://semver.org/).
The full release history (including binaries and SHA-256) lives on the
[GitHub Releases page](https://github.com/itsmilespr/UniversalPresence/releases).

## [1.8.6] - 2026-11-26

### Fixed
- A failed timeline read (player switching session, resume from sleep) no longer publishes
  position 0:00. The last known position of the same track is kept and projected, so the
  Discord bar stops flickering back to the start and no longer wastes an update.
- Clearing the presence is retried when Discord rejects it instead of being reported as sent.
- Connecting to Discord no longer republishes the activity that is already in flight.
- Cover URLs, the upload queue and the listening history are written atomically: a forced
  close can no longer leave a truncated cache that forced every cover to be uploaded again.
- The metadata cache also invalidates when the position moves backwards more than 2 s, which
  covers repeats of tracks with the same duration that never emit MediaPropertiesChanged.

### Changed
- Discord's rate limit is now prioritised by what changes. Content updates (track, artist,
  cover, player) may use all five slots per 20 s; progress-only resyncs and the heartbeat
  reserve two slots, so a track change never waits behind a bar resync.
- The artwork URL is part of the content key: a cover that arrives or is renewed is published
  as a visible change with content priority.
- Periodic bar resync tolerance is 400 ms, as documented (it was 1 s in practice).
- The transient metadata gap at a track change is covered for 1 s instead of 300 ms.
- The metadata cache lives 15 s instead of 30 s, and the SMTC timeline is read once per
  session and poll instead of twice.
- Default poll interval is 1500 ms; SMTC events still wake the loop instantly.

### Performance
- The listening history no longer parses the whole file on every track change; it is trimmed
  only when the entry count crosses the maximum plus a margin, or once a day.
- The retry queue pauses while the current track's cover is being uploaded, so the visible
  cover never waits behind pending ones.
- Cover optimisation skips the PNG encode unless the source can carry transparency.

## [1.8.5] - 2026-11-25

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
