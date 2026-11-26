using System.Drawing;
using System.IO;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using Windows.Media.Control;
using Windows.Storage.Streams;

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
    private PlayerIdentityResolver _identity;
    private ArtworkCache _artwork;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    /// <summary>
    /// Cada llamada a GetSessions/GetCurrentSession/TryGetMediaPropertiesAsync crea objetos
    /// WinRT nuevos. Con un heap gestionado diminuto casi nunca hay GC de gen2, así que sus
    /// referencias nativas se acumulaban hasta cientos de MB. Se cachean y solo se releen
    /// cuando el evento correspondiente avisa (o cada pocos segundos, como red de seguridad).
    /// </summary>
    // Los eventos son la vía normal e invalidan estas cachés al instante (cada handler
    // incrementa su versión). Estos TTL son solo la red de seguridad para reproductores
    // que no emiten eventos: cuanto más largos, menos objetos WinRT con la miniatura se
    // crean y menos crece la memoria nativa. Un cambio real no espera al TTL.
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlaybackTtl = TimeSpan.FromSeconds(2);

    private readonly object _watchGate = new();
    private readonly object _metadataGate = new();
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _watched =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MetadataCache> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlaybackCache> _playback = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimelineCache> _timelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _coverDecisionCache = new(StringComparer.Ordinal);

    // Una sesión ignorada se sigue leyendo en cada vuelta (hay que saber si desaparece),
    // pero escribir su línea de log cada segundo solo genera I/O y ruido: se registra una
    // vez por AUMID hasta que se reinicie el monitor.
    private readonly HashSet<string> _ignoredLogged = new(StringComparer.OrdinalIgnoreCase);

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

    // Detección de seek real (arrastrar la barra / saltar ±10 s) en la pista actual.
    private long _seekRevision;
    private DateTimeOffset _lastSeekDetectedAt = DateTimeOffset.MinValue;

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

    /// <summary>
    /// Se incrementa cuando el reproductor mueve la posición de la pista actual (seek o
    /// arrastre de la barra). El motor lo usa para reenviar los timestamps al instante.
    /// </summary>
    public long SeekRevision => Volatile.Read(ref _seekRevision);

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

        // Estos dos servicios guardan una instantánea de las opciones. Al cambiar de perfil
        // o de ajustes hay que recrearlos: si no, el nombre del reproductor y la decisión
        // de leer la carátula siguen siendo los del perfil anterior hasta reiniciar.
        _identity = new PlayerIdentityResolver(_options, _log);
        _artwork = new ArtworkCache(_options, _log);

        lock (_metadataGate)
        {
            _ignoredLogged.Clear();
            _coverDecisionCache.Clear();
        }
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
                    var ignoredKey = aumid.Length > 0 ? aumid : identity.Name;
                    bool firstTime;
                    lock (_metadataGate)
                    {
                        if (_ignoredLogged.Count > 64)
                        {
                            _ignoredLogged.Clear();
                        }

                        firstTime = _ignoredLogged.Add(ignoredKey);
                    }

                    if (firstTime)
                    {
                        _log.Debug(
                            $"Session ignored by IgnoredPlayers: AUMID '{aumid}', player '{identity.Name}'.");
                    }

                    return null;
                }

                // El timeline se lee UNA vez por sesión y vuelta: su firma invalida los
                // metadatos cuando cambia la pista y su posición alimenta la barra, sin
                // crear un segundo objeto WinRT en la caché de metadatos.
                GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = null;
                try
                {
                    timeline = session.GetTimelineProperties();
                }
                catch
                {
                    timeline = null;
                }

                var media = await GetMediaPropertiesAsync(session, aumid, timeline, cancellationToken)
                    .ConfigureAwait(false);

                // Los navegadores (Brave, Chrome, Edge, Firefox…) exponen como miniatura
                // de SMTC su propio icono o el favicon del sitio, no la portada real del
                // álbum. Si usamos esa miniatura, Discord muestra el icono del navegador
                // en lugar de la carátula. Para ellos se ignora la miniatura y se deja
                // que la búsqueda en catálogo (iTunes/Deezer) ponga la portada real.
                var isBrowser = IsBrowserPlayer(aumid, identity);
                var isTikTok = IsTikTokPlayer(aumid, identity);

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

                // Se ancla el instante de captura a la lectura del timeline, no al final del
                // read (tras extraer/subir la portada): así la posición y CapturedAt quedan
                // sincronizados y la barra no se adelanta/atrasa por el tiempo de artwork.
                var capturedAt = DateTimeOffset.UtcNow;
                var mediaKey = $"{aumid}|{title}|{artist}|{album}";
                var (position, duration, lastUpdated) = ReadTimelineCached(
                    aumid, status, projectPosition, mediaKey, timeline);
                // Los navegadores exponen como miniatura SMTC su propio icono o el favicon del
                // sitio, no la portada real. Se ignora esa miniatura por defecto, pero si la
                // imagen es lo bastante grande (>= 200 px) se asume que es una portada real
                // (p. ej. TikTok u otros sitios que sí rellenan el artwork del Media Session)
                // y se conserva para que se suba/muestre.
                IRandomAccessStreamReference? thumbnail;
                string artworkDecision;
                if (isTikTok)
                {
                    // TikTok no está en los catálogos: se confía en su miniatura SMTC.
                    thumbnail = media?.Thumbnail;
                    artworkDecision = "tiktok";
                }
                else if (isBrowser)
                {
                    var cacheKey = $"{aumid}|{mediaKey}";
                    bool isReal;
                    lock (_metadataGate)
                    {
                        if (!_coverDecisionCache.TryGetValue(cacheKey, out isReal))
                        {
                            isReal = false;
                        }
                    }

                    if (!_coverDecisionCache.ContainsKey(cacheKey))
                    {
                        var browserCoverTask = IsRealCoverThumbnailAsync(media?.Thumbnail, cancellationToken);
                        var browserTimeout = Task.Delay(150, cancellationToken);
                        var browserCompleted = await Task.WhenAny(browserCoverTask, browserTimeout).ConfigureAwait(false);
                        if (browserCompleted == browserCoverTask)
                        {
                            isReal = await browserCoverTask.ConfigureAwait(false);
                        }
                        else
                        {
                            isReal = false;
                            _log.Debug($"Verificación de carátula de navegador tardó más de 100 ms para '{aumid}'; se asume no real.");
                            _ = browserCoverTask.ContinueWith(t =>
                            {
                                if (t.IsFaulted)
                                {
                                    _log.Debug($"Verificación de carátula falló: {t.Exception?.GetBaseException().Message}");
                                }
                            }, TaskContinuationOptions.OnlyOnFaulted);
                        }
                        lock (_metadataGate)
                        {
                            if (_coverDecisionCache.Count > 128) _coverDecisionCache.Clear();
                            _coverDecisionCache[cacheKey] = isReal;
                        }
                        _log.Debug($"Portada de navegador para '{aumid}': {(isReal ? "real (>=200 px)" : "ignorada (icono/favicon)")}");
                    }

                    thumbnail = isReal ? media?.Thumbnail : null;
                    artworkDecision = isReal ? "browser-real" : "browser-ignored";
                }
                else
                {
                    thumbnail = media?.Thumbnail;
                    artworkDecision = "smtc";
                }
                // La lectura de la miniatura (stream + resize + disco) puede tardar varios
                // segundos en la primera vez; mientras tanto el bucle no puede publicar el
                // título ni la barra. Se limita a 180 ms: si no llega, se publica ya el
                // título y la siguiente vuelta toma la carátula cacheada (ArtworkCache
                // deduplica la extracción en curso, así que no se decodifica dos veces).
                ArtworkInfo? artwork = null;
                if (thumbnail is not null)
                {
                    var artworkTask = _artwork.GetAsync(thumbnail, mediaKey, cancellationToken);
                    var timeoutTask = Task.Delay(250, cancellationToken);
                    var completed = await Task.WhenAny(artworkTask, timeoutTask).ConfigureAwait(false);
                    if (completed == artworkTask)
                    {
                        artwork = await artworkTask.ConfigureAwait(false);
                    }
                    else
                    {
                        _log.Debug($"Lectura de carátula tardó más de 180 ms para '{mediaKey}'; se reintenta en la siguiente vuelta.");
                        _ = artworkTask.ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                _log.Debug($"Lectura de carátula falló: {t.Exception?.GetBaseException().Message}");
                            }
                        }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }

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
                    CapturedAt = capturedAt,
                    SourceUpdatedAt = lastUpdated,
                    IsCurrentSession = aumid.Length > 0 && string.Equals(aumid, currentAumid, StringComparison.OrdinalIgnoreCase),
                    Artwork = artwork,
                    ArtworkDecision = artworkDecision,
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
        string aumid,
        PlaybackState status,
        bool projectPosition,
        string mediaKey,
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline)
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

        var previous = cache;

        if (timeline is null)
        {
            // Una lectura fallida (o un timeline nulo) no puede publicar 0:00: se mantiene
            // la última posición conocida de la MISMA pista, proyectada, en lugar de
            // reiniciar la barra y forzar un envío inútil a Discord.
            if (previous is null
                || !string.Equals(previous.MediaKey, mediaKey, StringComparison.Ordinal))
            {
                return (TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue);
            }

            cache = new TimelineCache
            {
                Position = previous.Position,
                Duration = previous.Duration,
                LastUpdated = previous.LastUpdated,
                Version = version,
                At = now,
                MediaKey = mediaKey,
            };
        }
        else
        {
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
                MediaKey = mediaKey,
            };

            // Solo se compara con la lectura anterior de la MISMA pista: un cambio de
            // canción no es un seek (y además ya cambia la clave de actividad).
            if (previous is not null
                && string.Equals(previous.MediaKey, mediaKey, StringComparison.Ordinal))
            {
                DetectSeek(
                    previous,
                    timeline.Position,
                    timeline.LastUpdatedTime,
                    status,
                    now);
            }

            if (aumid.Length > 0)
            {
                lock (_metadataGate)
                {
                    _timelines[aumid] = cache;
                }
            }

            // Diagnóstico para reproductores que reportan timelines anómalos
            // (p. ej. Mornye/media_kit): se registra la lectura cruda para poder
            // correlacionarla con la barra de Discord.
            if (timeline.LastUpdatedTime == DateTimeOffset.MinValue
                || timeline.LastUpdatedTime > now.AddSeconds(1))
            {
                _log.Debug(
                    $"Timeline crudo '{aumid}': status={status} position={timeline.Position} " +
                    $"duration={duration} lastUpdated={timeline.LastUpdatedTime:O} now={now:O}");
            }
        }

        var position = cache!.Position;

        if (projectPosition && status == PlaybackState.Playing)
        {
            // Algunos reproductores (Mornye/media_kit) reportan LastUpdatedTime inválido
            // (MinValue o un instante futuro). Proyectar en esos casos congelaba o
            // adelantaba la barra, así que solo se proyecta con un instante válido.
            var lastUpdatedValid = cache.LastUpdated != DateTimeOffset.MinValue
                && cache.LastUpdated <= now.AddSeconds(1);
            var drift = lastUpdatedValid
                ? now - cache.LastUpdated.ToUniversalTime()
                : TimeSpan.Zero;
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
    /// Detecta un salto real dentro de la misma pista (arrastrar la barra, saltar ±10 s).
    /// Se apoya en que el reproductor actualiza <c>LastUpdatedTime</c> al moverse: así una
    /// posición rezagada de un reproductor lento no se confunde con un seek en cada vuelta.
    /// </summary>
    private void DetectSeek(
        TimelineCache previous,
        TimeSpan reportedPosition,
        DateTimeOffset reportedLastUpdated,
        PlaybackState status,
        DateTimeOffset now)
    {
        if (previous.LastUpdated == DateTimeOffset.MinValue
            || previous.LastUpdated > now.AddSeconds(1)
            || reportedLastUpdated == DateTimeOffset.MinValue
            || reportedLastUpdated > now.AddSeconds(1))
        {
            return;
        }

        // Un seek reescribe el instante del timeline. Si no cambió, es avance normal (o el
        // reproductor no informa de la posición en tiempo real): no se toca nada.
        if ((reportedLastUpdated - previous.LastUpdated).Duration()
            < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        var expected = previous.Position;

        if (status == PlaybackState.Playing)
        {
            var elapsed = now - previous.LastUpdated.ToUniversalTime();
            if (elapsed <= TimeSpan.Zero || elapsed >= TimeSpan.FromMinutes(10))
            {
                return;
            }

            expected += elapsed;
        }

        var delta = reportedPosition - expected;

        if (delta.Duration() <= TimeSpan.FromSeconds(2)
            || delta.Duration() >= TimeSpan.FromMinutes(30)
            || now - _lastSeekDetectedAt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastSeekDetectedAt = now;
        Interlocked.Increment(ref _seekRevision);
        _log.Debug(
            $"Seek detectado: posición {reportedPosition} (esperada {expected}), " +
            $"status={status}; se resincronizará la barra.");
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

    /// <summary>
    /// Detecta si la sesión proviene de TikTok (app nativa o pestaña del navegador).
    /// TikTok no suele estar en los catálogos de música, así que su portada solo puede
    /// venir de la miniatura SMTC (el vídeo) y se confía en ella aunque sea pequeña.
    /// </summary>
    private static bool IsTikTokPlayer(string aumid, PlayerIdentity identity)
    {
        var aumidLower = (aumid ?? string.Empty).ToLowerInvariant();
        var nameLower = (identity.Name ?? string.Empty).ToLowerInvariant();
        var execLower = (identity.ExecutablePath ?? string.Empty).ToLowerInvariant();
        var fileLower = (identity.FileName ?? string.Empty).ToLowerInvariant();

        foreach (var value in new[] { aumidLower, nameLower, execLower, fileLower })
        {
            if (value.Contains("tiktok", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Comprueba si la miniatura SMTC de un navegador es una portada real (y no un icono
    /// o favicon pequeño). Los navegadores que rellenan el artwork del Media Session (p. ej.
    /// TikTok) suelen entregar imágenes grandes; los favicons/iconos del navegador son
    /// pequeños. Si no se puede leer o es pequeña, se descarta.
    /// </summary>
    private static async Task<bool> IsRealCoverThumbnailAsync(
        IRandomAccessStreamReference? reference,
        CancellationToken cancellationToken)
    {
        if (reference is null)
        {
            return false;
        }

        try
        {
            using var stream = await reference.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (stream is null || stream.Size == 0 || stream.Size > 8 * 1024 * 1024)
            {
                return false;
            }

            stream.Seek(0);
            var size = (uint)stream.Size;
            var bytes = new byte[size];

            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                var loaded = await reader.LoadAsync(size).AsTask(cancellationToken).ConfigureAwait(false);
                if (loaded == 0)
                {
                    return false;
                }

                reader.ReadBytes(bytes);
            }

            using var ms = new MemoryStream(bytes);
            using var image = Image.FromStream(ms);
            return image.Width >= 200 && image.Height >= 200;
        }
        catch
        {
            return false;
        }
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

        /// <summary>Identidad de la pista a la que pertenece esta lectura.</summary>
        public string MediaKey { get; init; } = "";
    }

    internal static PlaybackState MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => PlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackState.Stopped,
        _ => PlaybackState.Closed,
    };

    internal static string Clean(string? value)
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

    /// <summary>
    /// Envía una orden de transporte al reproductor activo (o al primero que esté sonando).
    /// Windows expone estas acciones por SMTC, así que funciona con cualquier reproductor
    /// que ya aparezca en el sistema, sin integraciones propias.
    /// </summary>
    public async Task<bool> TryControlAsync(MediaControlAction action, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var session = GetControlTargetSession();
        if (session is null)
        {
            return false;
        }

        try
        {
            var ok = action switch
            {
                MediaControlAction.TogglePlayPause => await session.TryTogglePlayPauseAsync(),
                MediaControlAction.NextTrack => await session.TrySkipNextAsync(),
                MediaControlAction.PreviousTrack => await session.TrySkipPreviousAsync(),
                _ => false,
            };

            if (ok)
            {
                // El reproductor publicará el cambio por sus eventos; se avisa igualmente
                // para que la bandeja responda al instante.
                OnManagerChanged();
            }

            return ok;
        }
        catch (Exception ex)
        {
            _log.Debug($"SMTC control '{action}' failed: {ex.Message}");
            return false;
        }
    }

    private GlobalSystemMediaTransportControlsSession? GetControlTargetSession()
    {
        try
        {
            var current = GetCurrentSessionCached();
            if (current is not null)
            {
                return current;
            }

            foreach (var candidate in GetSessionsCached())
            {
                var status = MapStatus(candidate.GetPlaybackInfo()?.PlaybackStatus);
                if (status is PlaybackState.Playing or PlaybackState.Paused)
                {
                    return candidate;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo elegir la sesión para controlar: {ex.Message}");
        }

        return null;
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
            _consecutiveFailures = 0;
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
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline,
        CancellationToken cancellationToken)
    {
        if (aumid.Length == 0)
        {
            return await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var version = Volatile.Read(ref _metadataVersion);

        // La carátula/título se cachean, pero un cambio de pista puede no disparar el
        // evento MediaPropertiesChanged en algunos reproductores. Para no quedarnos con
        // los metadatos de la canción anterior se invalida la caché cuando cambia la
        // firma del timeline (duración + LastUpdated) o cuando la posición vuelve atrás
        // más de dos segundos (repetición de una pista de la misma duración).
        var currentDuration = timeline is null
            ? TimeSpan.Zero
            : timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
        var currentLastUpdated = timeline?.LastUpdatedTime ?? DateTimeOffset.MinValue;
        var currentPosition = timeline?.Position ?? TimeSpan.Zero;

        lock (_metadataGate)
        {
            if (_metadata.TryGetValue(aumid, out var cached)
                && cached.Media is not null
                && cached.Version == version
                && now - cached.At < MetadataTtl
                && cached.TimelineDuration == currentDuration
                && cached.TimelineLastUpdated == currentLastUpdated
                && !(timeline is not null
                    && currentPosition < cached.TimelinePosition - TimeSpan.FromSeconds(2)))
            {
                return cached.Media;
            }
        }

        var media = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);

        lock (_metadataGate)
        {
            if (_metadata.Count > 64) _metadata.Clear();
            _metadata[aumid] = new MetadataCache
            {
                Media = media,
                Version = version,
                At = now,
                TimelineDuration = currentDuration,
                TimelineLastUpdated = currentLastUpdated,
                TimelinePosition = currentPosition,
            };
        }

        return media;
    }

    /// <summary>Órdenes de transporte que Windows expone por SMTC.</summary>
    public enum MediaControlAction
    {
        TogglePlayPause,
        NextTrack,
        PreviousTrack,
    }

    private sealed class MetadataCache
    {
        public GlobalSystemMediaTransportControlsSessionMediaProperties? Media { get; init; }

        public int Version { get; init; }

        public DateTimeOffset At { get; init; }

        public TimeSpan TimelineDuration { get; init; }

        public DateTimeOffset TimelineLastUpdated { get; init; }

        /// <summary>Posición leída al guardar; si retrocede, la pista se reinició.</summary>
        public TimeSpan TimelinePosition { get; init; }
    }
}
