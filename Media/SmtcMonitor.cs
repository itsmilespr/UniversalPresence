using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using Windows.Media.Control;

namespace UniversalPresence.Media;

/// <summary>
/// Universal System Media Transport Controls reader.
///
/// It subscribes to the global manager and to every session that Windows exposes and
/// never looks at an allow-list: if Windows reports a Playing/Paused session, it is used.
/// </summary>
public sealed class SmtcMonitor : IDisposable
{
    private readonly AppOptions _options;
    private readonly Log _log;
    private readonly PlayerIdentityResolver _identity;
    private readonly ArtworkCache _artwork;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    /// <summary>
    /// Cada llamada a GetSessions/GetCurrentSession/TryGetMediaPropertiesAsync crea objetos
    /// WinRT nuevos. Con un heap gestionado diminuto casi nunca hay GC de gen2, así que sus
    /// referencias nativas se acumulaban hasta cientos de MB. Se cachean y solo se releen
    /// cuando el evento correspondiente avisa (o cada pocos segundos, como red de seguridad).
    /// </summary>
    // Los eventos son la vía normal; estos TTL son solo la red de seguridad para
    // reproductores que no los emiten. Cuanto más largos, menos objetos WinRT se crean.
    // TTLs más cortos: la latencia al cambiar de canción importa más que ahorrar
    // unos MB de objetos WinRT (el GC ya los limpia periódicamente).
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PlaybackTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TimelineTtl = TimeSpan.FromSeconds(2);

    private readonly object _watchGate = new();
    private readonly object _metadataGate = new();
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _watched =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MetadataCache> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaybackCache> _playback = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimelineCache> _timelines = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<GlobalSystemMediaTransportControlsSession> _sessions =
        Array.Empty<GlobalSystemMediaTransportControlsSession>();
    private int _sessionsVersion = 1;
    private int _sessionsSnapshotVersion = -1;
    private DateTimeOffset _sessionsReadAt = DateTimeOffset.MinValue;

    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private int _currentVersion = 1;
    private int _currentSnapshotVersion = -1;
    private DateTimeOffset _currentReadAt = DateTimeOffset.MinValue;

    private int _metadataVersion;
    private int _playbackVersion;
    private int _timelineVersion;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private int _consecutiveFailures;
    private bool _disposed;

    public SmtcMonitor(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
        _identity = new PlayerIdentityResolver(options, log);
        _artwork = new ArtworkCache(options, log);
    }

    /// <summary>Raised (from a WinRT thread) whenever Windows reports a session change.</summary>
    public event Action? Changed;

    public bool IsReady => _manager is not null;

    public string ArtworkDirectory => _artwork.Directory;

    /// <summary>
    /// Drops the manager and every session subscription so the next read re-creates them.
    /// Called on the engine loop (never concurrently with a read) after a settings change.
    /// </summary>
    public void Reset()
    {
        try
        {
            if (_manager is not null)
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }
        }
        catch
        {
            // ignore
        }

        _manager = null;
        _consecutiveFailures = 0;
        ClearWatches();
        ResetSnapshots();

        _log.Info("SMTC monitor restarted (settings updated).");
    }

    public async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_manager is not null)
        {
            return true;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_manager is not null)
            {
                return true;
            }

            _log.Debug("Solicitando GlobalSystemMediaTransportControlsSessionManager…");
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.SessionsChanged += OnSessionsChanged;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager = manager;
            _log.Info("SMTC ready: universal detection active (no player whitelist).");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            // El monitor fue disposed; no intentar de nuevo.
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn($"SMTC not available yet: {ex.Message}");
            return false;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Every session Windows currently exposes, including stopped ones (diagnostics).</summary>
    public async Task<IReadOnlyList<MediaSnapshot>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<MediaSnapshot>();
        var manager = _manager;

        if (manager is null)
        {
            return result;
        }

        var sessions = GetSessionsCached();
        _consecutiveFailures = 0;
        var currentAumid = GetCurrentSessionCached()?.SourceAppUserModelId ?? string.Empty;

        PruneWatches(sessions);

        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshot = await ReadSessionAsync(session, currentAumid, cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    result.Add(snapshot);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Debug($"Could not read an SMTC session: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Best session to publish: Playing beats Paused beats Stopped; the system's current
    /// session wins ties, then the most recently updated one. No player is preferred by name.
    /// </summary>
    public static MediaSnapshot? SelectBest(IEnumerable<MediaSnapshot> sessions)
    {
        return sessions
            .OrderByDescending(Score)
            .ThenByDescending(snapshot => snapshot.SourceUpdatedAt)
            .FirstOrDefault();
    }

    private static int Score(MediaSnapshot snapshot)
    {
        var score = snapshot.Status switch
        {
            PlaybackState.Playing => 300,
            PlaybackState.Paused => 200,
            PlaybackState.Stopped => 100,
            _ => 0,
        };

        if (snapshot.IsCurrentSession)
        {
            score += 40;
        }

        return score;
    }

    /// <summary>
    /// Re-reads one session right now, taking the timeline exactly as the player reports it.
    ///
    /// Used when a track resumes: the player has just restarted its clock, but
    /// <c>LastUpdatedTime</c> still points at the pause instant, so projecting the position
    /// forward would add the whole pause duration and desynchronise the bar. Here the fresh
    /// timeline is used as-is.
    /// </summary>
    public async Task<MediaSnapshot?> ReadFreshAsync(string appUserModelId, CancellationToken cancellationToken)
    {
        var manager = _manager;

        if (manager is null || string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSession? match;
        var currentAumid = string.Empty;

        try
        {
            currentAumid = GetCurrentSessionCached()?.SourceAppUserModelId ?? string.Empty;
            match = GetSessionsCached().FirstOrDefault(candidate => string.Equals(
                (candidate.SourceAppUserModelId ?? string.Empty).Trim(),
                appUserModelId.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            HandleManagerFailure(ex);
            return null;
        }

        if (match is null)
        {
            return null;
        }

        try
        {
            return await ReadSessionAsync(match, currentAumid, cancellationToken, projectPosition: false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not re-read the session on resume: {ex.Message}");
            return null;
        }
    }

    private async Task<MediaSnapshot?> ReadSessionAsync(
        GlobalSystemMediaTransportControlsSession session,
        string currentAumid,
        CancellationToken cancellationToken,
        bool projectPosition = true)
    {
        var aumid = string.Empty;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                aumid = (session.SourceAppUserModelId ?? string.Empty).Trim();

                var status = GetStatusCached(session, aumid);
                if (status == PlaybackState.Closed)
                {
                    return null;
                }

                var identity = _identity.Resolve(aumid);

                // Blacklist check happens before any metadata/artwork read: a browser playing
                // YouTube never reaches the activity builder and behaves as "no music".
                if (_options.IsIgnoredPlayer(aumid, identity.Name, identity.ExecutablePath, identity.FileName))
                {
                    _log.Debug(
                        $"Session ignored by IgnoredPlayers: AUMID '{aumid}', player '{identity.Name}'.");
                    return null;
                }

                var media = await GetMediaPropertiesAsync(session, aumid, cancellationToken).ConfigureAwait(false);

                // Los navegadores (Brave, Chrome, Edge, Firefox…) exponen como miniatura
                // de SMTC su propio icono o el favicon del sitio, no la portada real del
                // álbum. Si usamos esa miniatura, Discord muestra el icono del navegador
                // en lugar de la carátula. Para ellos se ignora la miniatura y se deja
                // que la búsqueda en catálogo (iTunes/Deezer) ponga la portada real.
                var isBrowser = IsBrowserPlayer(aumid, identity);

                var title = Clean(media?.Title);
                var artist = Clean(media?.Artist);
                var albumArtist = Clean(media?.AlbumArtist);
                var album = Clean(media?.AlbumTitle);
                var subtitle = Clean(media?.Subtitle);

                if (title.Length == 0)
                {
                    title = subtitle;
                }

                if (artist.Length == 0)
                {
                    artist = albumArtist;
                }

                if (status != PlaybackState.Playing && title.Length == 0 && artist.Length == 0 && album.Length == 0)
                {
                    // Nothing worth showing.
                    return null;
                }

                var (position, duration, lastUpdated) = ReadTimelineCached(
                    session, aumid, status, projectPosition, force: !projectPosition);
                var mediaKey = $"{aumid}|{title}|{artist}|{album}";
                var thumbnail = isBrowser ? null : media?.Thumbnail;
                var artwork = await _artwork.GetAsync(thumbnail, mediaKey, cancellationToken).ConfigureAwait(false);

                if (aumid.Length > 0)
                {
                    EnsureWatched(session, aumid);
                }

                return new MediaSnapshot
                {
                    AppUserModelId = aumid,
                    PlayerName = identity.Name,
                    PlayerExecutable = identity.ExecutablePath,
                    PlayerNameSource = identity.Source,
                    Title = title,
                    Artist = artist,
                    Album = album,
                    AlbumArtist = albumArtist,
                    Position = position,
                    Duration = duration,
                    Status = status,
                    CapturedAt = DateTimeOffset.UtcNow,
                    SourceUpdatedAt = lastUpdated,
                    IsCurrentSession = aumid.Length > 0 && string.Equals(aumid, currentAumid, StringComparison.OrdinalIgnoreCase),
                    Artwork = artwork,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt == 0)
            {
                // SMTC reads race with the player updating its metadata; one retry is
                // enough in practice and avoids dropping the session from the activity.
                _log.Debug($"SMTC read failed, retrying ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Debug(
                    $"SMTC session '{aumid}' unreadable ({ex.GetType().Name}, 0x{ex.HResult:X8}): '{ex.Message}'");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Estado de reproducción cacheado: cambiar de estado dispara PlaybackInfoChanged, así
    /// que leerlo cada segundo solo creaba un objeto WinRT más por vuelta.
    /// </summary>
    private PlaybackState GetStatusCached(
        GlobalSystemMediaTransportControlsSession session,
        string aumid)
    {
        if (aumid.Length == 0)
        {
            return MapStatus(session.GetPlaybackInfo()?.PlaybackStatus);
        }

        var now = DateTimeOffset.UtcNow;
        var version = Volatile.Read(ref _playbackVersion);

        lock (_metadataGate)
        {
            if (_playback.TryGetValue(aumid, out var cached)
                && cached.Version == version
                && now - cached.At < PlaybackTtl)
            {
                return cached.Status;
            }
        }

        var status = MapStatus(session.GetPlaybackInfo()?.PlaybackStatus);

        lock (_metadataGate)
        {
            _playback[aumid] = new PlaybackCache { Status = status, Version = version, At = now };
        }

        return status;
    }

    /// <summary>
    /// Timeline cacheada: se guarda la posición cruda y el LastUpdated del reproductor, y la
    /// posición se proyecta hacia delante en cada vuelta. Así la barra sigue exacta sin
    /// crear un objeto WinRT por segundo (un seek real dispara el evento y refresca ya).
    /// </summary>
    private (TimeSpan Position, TimeSpan Duration, DateTimeOffset LastUpdated) ReadTimelineCached(
        GlobalSystemMediaTransportControlsSession session,
        string aumid,
        PlaybackState status,
        bool projectPosition,
        bool force)
    {
        var now = DateTimeOffset.UtcNow;
        var version = Volatile.Read(ref _timelineVersion);
        TimelineCache? cache = null;

        if (aumid.Length > 0)
        {
            lock (_metadataGate)
            {
                _timelines.TryGetValue(aumid, out cache);
            }
        }

        var valid = cache is not null
            && !force
            && cache.Version == version
            && now - cache.At < TimelineTtl;

        if (!valid)
        {
            try
            {
                var timeline = session.GetTimelineProperties();
                if (timeline is null)
                {
                    return (TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue);
                }

                var duration = timeline.EndTime > timeline.StartTime
                    ? timeline.EndTime - timeline.StartTime
                    : TimeSpan.Zero;

                if (duration < TimeSpan.Zero)
                {
                    duration = TimeSpan.Zero;
                }

                cache = new TimelineCache
                {
                    Position = timeline.Position,
                    Duration = duration,
                    LastUpdated = timeline.LastUpdatedTime,
                    Version = version,
                    At = now,
                };

                if (aumid.Length > 0)
                {
                    lock (_metadataGate)
                    {
                        _timelines[aumid] = cache;
                    }
                }
            }
            catch
            {
                return (TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue);
            }
        }

        var position = cache!.Position;

        if (projectPosition && status == PlaybackState.Playing)
        {
            var drift = now - cache.LastUpdated.ToUniversalTime();
            if (drift > TimeSpan.Zero && drift < TimeSpan.FromMinutes(10))
            {
                position += drift;
            }
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (cache.Duration > TimeSpan.Zero && position > cache.Duration)
        {
            position = cache.Duration;
        }

        return (position, cache.Duration, cache.LastUpdated);
    }

    /// <summary>
    /// Detecta si la sesión proviene de un navegador web. Los navegadores exponen como
    /// miniatura SMTC su propio icono o el favicon del sitio, no la portada real del álbum;
    /// en ese caso se ignora la miniatura para dejar que la búsqueda en catálogo ponga
    /// la portada correcta.
    /// </summary>
    private static bool IsBrowserPlayer(string aumid, PlayerIdentity identity)
    {
        var aumidLower = (aumid ?? string.Empty).ToLowerInvariant();
        var nameLower = (identity.Name ?? string.Empty).ToLowerInvariant();
        var execLower = (identity.ExecutablePath ?? string.Empty).ToLowerInvariant();
        var fileLower = (identity.FileName ?? string.Empty).ToLowerInvariant();

        string[] browserKeywords =
        {
            "brave", "chrome", "chromium", "msedge", "edge", "firefox", "opera",
            "vivaldi", "arc", "safari", "waterfox", "librewolf", "tor", "yandex",
            "duckduckgo", "whale", "naver"
        };

        foreach (var kw in browserKeywords)
        {
            if (aumidLower.Contains(kw, StringComparison.Ordinal)
                || nameLower.Contains(kw, StringComparison.Ordinal)
                || execLower.Contains(kw, StringComparison.Ordinal)
                || fileLower.Contains(kw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // AUMIDs típicos de navegadores empaquetados.
        if (aumidLower.Contains("microsoft.microsoftedge", StringComparison.Ordinal)
            || aumidLower.Contains("google.chrome", StringComparison.Ordinal)
            || aumidLower.Contains("mozilla.firefox", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private sealed class PlaybackCache
    {
        public PlaybackState Status { get; init; }

        public int Version { get; init; }

        public DateTimeOffset At { get; init; }
    }

    private sealed class TimelineCache
    {
        public TimeSpan Position { get; init; }

        public TimeSpan Duration { get; init; }

        public DateTimeOffset LastUpdated { get; init; }

        public int Version { get; init; }

        public DateTimeOffset At { get; init; }
    }

    private static PlaybackState MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => PlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackState.Stopped,
        _ => PlaybackState.Closed,
    };

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var cleaned = value.Replace('\0', ' ').Trim();

        // Some integrations double-encode whitespace.
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned;
    }

    private void EnsureWatched(GlobalSystemMediaTransportControlsSession session, string aumid)
    {
        lock (_watchGate)
        {
            if (_watched.ContainsKey(aumid))
            {
                return;
            }

            try
            {
                session.MediaPropertiesChanged += OnSessionMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnSessionPlaybackInfoChanged;
                session.TimelinePropertiesChanged += OnSessionTimelinePropertiesChanged;
                _watched[aumid] = session;
                _log.Debug($"Suscrito a los eventos de la sesión '{aumid}'.");
            }
            catch (Exception ex)
            {
                _log.Debug($"No se pudieron suscribir los eventos de '{aumid}': {ex.Message}");
            }
        }
    }

    private void PruneWatches(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in sessions)
        {
            try
            {
                var aumid = session.SourceAppUserModelId;
                if (!string.IsNullOrWhiteSpace(aumid))
                {
                    live.Add(aumid);
                }
            }
            catch
            {
                // ignore
            }
        }

        lock (_watchGate)
        {
            foreach (var pair in _watched.Where(pair => !live.Contains(pair.Key)).ToList())
            {
                try
                {
                    pair.Value.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
                    pair.Value.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
                    pair.Value.TimelinePropertiesChanged -= OnSessionTimelinePropertiesChanged;
                }
                catch
                {
                    // ignore
                }

                _watched.Remove(pair.Key);
            }
        }

        lock (_metadataGate)
        {
            foreach (var key in _metadata.Keys.Where(key => !live.Contains(key)).ToList())
            {
                _metadata.Remove(key);
            }

            foreach (var key in _playback.Keys.Where(key => !live.Contains(key)).ToList())
            {
                _playback.Remove(key);
            }

            foreach (var key in _timelines.Keys.Where(key => !live.Contains(key)).ToList())
            {
                _timelines.Remove(key);
            }
        }
    }

    private void HandleManagerFailure(Exception exception)
    {
        _consecutiveFailures++;
        _log.Warn($"GetSessions() falló ({_consecutiveFailures}): {exception.Message}");

        if (_consecutiveFailures < 3)
        {
            return;
        }

        // The manager can go stale after a user session change or a resume from sleep.
        _log.Warn("Reiniciando el gestor SMTC.");
        _consecutiveFailures = 0;

        try
        {
            if (_manager is not null)
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }
        }
        catch
        {
            // ignore
        }

        _manager = null;
        ClearWatches();
        ResetSnapshots();
    }

    private void OnManagerChanged() => Changed?.Invoke();

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        Interlocked.Increment(ref _sessionsVersion);
        OnManagerChanged();
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        Interlocked.Increment(ref _currentVersion);
        OnManagerChanged();
    }

    private void OnSessionMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        Interlocked.Increment(ref _metadataVersion);
        OnManagerChanged();
    }

    private void OnSessionPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        Interlocked.Increment(ref _playbackVersion);
        OnManagerChanged();
    }

    private void OnSessionTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        Interlocked.Increment(ref _timelineVersion);
        OnManagerChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_manager is not null)
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }
        }
        catch
        {
            // ignore
        }

        ClearWatches();
        ResetSnapshots();

        _manager = null;
        _startGate.Dispose();
    }

    private void ClearWatches()
    {
        lock (_watchGate)
        {
            foreach (var session in _watched.Values)
            {
                try
                {
                    session.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
                    session.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
                    session.TimelinePropertiesChanged -= OnSessionTimelinePropertiesChanged;
                }
                catch
                {
                    // ignore
                }
            }

            _watched.Clear();
        }
    }

    private void ResetSnapshots()
    {
        _sessions = Array.Empty<GlobalSystemMediaTransportControlsSession>();
        _sessionsSnapshotVersion = -1;
        _sessionsReadAt = DateTimeOffset.MinValue;
        _currentSession = null;
        _currentSnapshotVersion = -1;
        _currentReadAt = DateTimeOffset.MinValue;

        // Fuerza releer en la próxima vuelta aunque los eventos no hayan llegado.
        Interlocked.Increment(ref _sessionsVersion);
        Interlocked.Increment(ref _currentVersion);
        Interlocked.Increment(ref _metadataVersion);
        Interlocked.Increment(ref _playbackVersion);
        Interlocked.Increment(ref _timelineVersion);

        lock (_metadataGate)
        {
            _metadata.Clear();
            _playback.Clear();
            _timelines.Clear();
        }
    }

    /// <summary>GetSessions cacheado por 5 s y por evento; evita un RCW nuevo cada segundo.</summary>
    private IReadOnlyList<GlobalSystemMediaTransportControlsSession> GetSessionsCached()
    {
        var manager = _manager;
        if (manager is null)
        {
            return Array.Empty<GlobalSystemMediaTransportControlsSession>();
        }

        var now = DateTimeOffset.UtcNow;
        var version = Volatile.Read(ref _sessionsVersion);

        if (version == _sessionsSnapshotVersion && now - _sessionsReadAt < SnapshotTtl)
        {
            return _sessions;
        }

        try
        {
            _sessions = manager.GetSessions();
            _sessionsSnapshotVersion = version;
            _sessionsReadAt = now;
            return _sessions;
        }
        catch (Exception ex)
        {
            HandleManagerFailure(ex);
            return Array.Empty<GlobalSystemMediaTransportControlsSession>();
        }
    }

    private GlobalSystemMediaTransportControlsSession? GetCurrentSessionCached()
    {
        var manager = _manager;
        if (manager is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var version = Volatile.Read(ref _currentVersion);

        if (version == _currentSnapshotVersion && now - _currentReadAt < SnapshotTtl)
        {
            return _currentSession;
        }

        try
        {
            _currentSession = manager.GetCurrentSession();
            _currentSnapshotVersion = version;
            _currentReadAt = now;
            return _currentSession;
        }
        catch
        {
            return _currentSession;
        }
    }

    /// <summary>
    /// Metadatos (incluida la miniatura) cacheados hasta que el reproductor avise de un
    /// cambio. Sin esto se creaba un objeto WinRT con la imagen cada segundo.
    /// </summary>
    private async Task<GlobalSystemMediaTransportControlsSessionMediaProperties?> GetMediaPropertiesAsync(
        GlobalSystemMediaTransportControlsSession session,
        string aumid,
        CancellationToken cancellationToken)
    {
        if (aumid.Length == 0)
        {
            return await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var version = Volatile.Read(ref _metadataVersion);

        lock (_metadataGate)
        {
            if (_metadata.TryGetValue(aumid, out var cached)
                && cached.Media is not null
                && cached.Version == version
                && now - cached.At < MetadataTtl)
            {
                return cached.Media;
            }
        }

        var media = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);

        lock (_metadataGate)
        {
            _metadata[aumid] = new MetadataCache { Media = media, Version = version, At = now };
        }

        return media;
    }

    private sealed class MetadataCache
    {
        public GlobalSystemMediaTransportControlsSessionMediaProperties? Media { get; init; }

        public int Version { get; init; }

        public DateTimeOffset At { get; init; }
    }
}
