using System.Collections.Concurrent;
using System.Globalization;
using UniversalPresence.Configuration;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;
using UniversalPresence.Media;

namespace UniversalPresence.Core;

/// <summary>
/// The background loop: reads SMTC, builds the activity and publishes it to Discord.
/// Runs on a thread-pool task so the tray message loop is never blocked.
/// </summary>
public sealed class PresenceEngine : IAsyncDisposable, IDisposable
{
    private const string IdleKey = "<idle>";

    /// <summary>
    /// Ventana de asentamiento antes de enviar. Si el usuario salta varias canciones
    /// seguidas solo se manda la última: las intermedias no gastan huecos del límite de
    /// Discord (5 actualizaciones cada 20 s) ni llegan a verse.
    /// </summary>
    private static readonly TimeSpan PublishSettleWindow = TimeSpan.FromMilliseconds(140);

    /// <summary>Tiempo máximo para confirmar que una canción volvió de verdad al principio.</summary>
    private static readonly TimeSpan RepeatConfirmationWindow = TimeSpan.FromSeconds(5);

    private readonly AppOptions _options;
    private readonly Log _log;
    private readonly SmtcMonitor _monitor;
    private readonly DiscordPresenceService _discord;
    private readonly ActivityFactory _factory;
    private readonly ArtworkLookupService _artworkLookup;
    private readonly ArtworkUploadService _artworkUpload;
    private readonly DiscordTokenService _discordTokens;
    private readonly ExternalAssetsService _externalAssets;
    private readonly ProgramIconAssetService _programIcons;
    private readonly ChangeSignal _signal = new();
    private readonly ChangeSignal _publishSignal = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _publishGate = new();

    private Task? _loop;
    private Task? _publisher;
    private PublishRequest? _pendingPublish;
    private CancellationTokenSource? _inflightPublish;
    private volatile bool _paused;

    /// <summary>Última clave encolada (o enviada); el bucle compara la actividad contra ella.</summary>
    private volatile string? _desiredKey;

    /// <summary>Instante del último envío confirmado, en epoch ms (0 = todavía ninguno).</summary>
    private long _publishedAtUnixMs;

    /// <summary>Desde cuándo la sesión activa se queda sin metadatos a mitad de cambio de pista.</summary>
    private DateTimeOffset _metadataGapSince = DateTimeOffset.MinValue;
    private volatile MediaSnapshot? _lastSnapshot;
    private int _playCount = 0;

    // Confirmación de repetición: una sola lectura con la posición en 0 no basta, porque
    // mover la barra hacia atrás (o un fallo momentáneo del reproductor) también lo parece.
    private string _repeatCandidateKey = "";
    private DateTimeOffset _repeatCandidateAt = DateTimeOffset.MinValue;
    private TimeSpan _repeatCandidatePosition;
    private volatile DiscordActivity? _currentActivity;
    private volatile IReadOnlyList<MediaSnapshot> _sessions = Array.Empty<MediaSnapshot>();
    private volatile string _statusText = "Iniciando…";
    private volatile string? _lastLookupKey;
    private volatile string? _lastLookupUrl;
    private int _emptyReads;
    private int _forceRepublish;
    private int _restartMonitorRequested;
    private bool _disposed;

    // Resoluciones de portada en segundo plano: el bucle nunca espera a la red.
    // La clave queda reservada mientras hay una subida, búsqueda o conversión en curso.
    private readonly ConcurrentDictionary<string, byte> _resolvingCovers = new(StringComparer.Ordinal);

    public PresenceEngine(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
        _monitor = new SmtcMonitor(options, log);
        _discord = new DiscordPresenceService(options, log);
        _factory = new ActivityFactory(options);
        _artworkLookup = new ArtworkLookupService(
            log,
            Path.Combine(AppOptions.LocalDataDirectory, "lookup-cache.json"));
        _artworkUpload = new ArtworkUploadService(options, log);
        _discordTokens = new DiscordTokenService(options, log);
        _externalAssets = new ExternalAssetsService(options, _discordTokens, log);
        _programIcons = new ProgramIconAssetService(options, _artworkUpload, log);
    }

    /// <summary>Raised when the tray needs to repaint; fires on a worker thread.</summary>
    public event Action? Updated;

    public bool Paused => _paused;

    public MediaSnapshot? LastSnapshot => _lastSnapshot;

    public DiscordActivity? CurrentActivity => _currentActivity;

    public IReadOnlyList<MediaSnapshot> Sessions => _sessions;

    public DiscordConnectionState DiscordState => _discord.State;

    public bool DiscordConfigured => _discord.IsConfigured;

    public string StatusText => _statusText;

    /// <summary>
    /// Motivo por el que Discord no puede mostrar carátulas/iconos propios con el token
    /// actual (token de bot o ausente), o null cuando todo está bien.
    /// </summary>
    public string? ImageProxyHint => _externalAssets.ImageProxyHint;

    private readonly object _startLock = new();

    public void Start()
    {
        lock (_startLock)
        {
            if (_loop is not null)
            {
                return;
            }

            _monitor.Changed += OnMediaChanged;
            _discord.StateChanged += OnDiscordStateChanged;
            _paused = _options.StartPaused;
            _statusText = _paused ? "Pausado" : "Iniciando…";
            _loop = Task.Run(() => LoopAsync(_cts.Token));

            // Publicador independiente: esperar por el límite de Discord nunca frena la
            // lectura de SMTC, el estado de la bandeja ni el siguiente cambio de pista.
            _publisher = Task.Run(() => PublishLoopAsync(_cts.Token));
        }
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;
        _log.Info(paused ? "Presencia pausada." : "Presencia reanudada.");
        _signal.Set();
        Updated?.Invoke();
    }

    /// <summary>
    /// Applies a settings change without restarting the app: the SMTC monitor is re-created
    /// on the next loop iteration and the current activity is republished immediately.
    /// </summary>
    public void ApplySettings()
    {
        // Tras conectar la cuenta, los fallos de conversión cacheados (sin token) ya no
        // valen: se descartan para que la portada y el icono se reintenten al instante.
        _externalAssets.InvalidateFailures();
        _programIcons.InvalidateFailures();

        Interlocked.Exchange(ref _forceRepublish, 1);
        Interlocked.Exchange(ref _restartMonitorRequested, 1);
        _signal.Set();
        Updated?.Invoke();
        _log.Info("Configuración aplicada: se reinicia el monitor SMTC y se republica la actividad.");
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (Interlocked.Exchange(ref _restartMonitorRequested, 0) == 1)
                    {
                        _monitor.Reset();
                        _lastLookupKey = null;
                        _lastLookupUrl = null;
                    }

                    var forceRepublish = Interlocked.Exchange(ref _forceRepublish, 0) == 1;

                    if (_paused && !_options.ShowPausedMedia)
                    {
                        _lastSnapshot = null;
                        _currentActivity = null;
                        SetStatus("Pausado");

                        if (!string.Equals(_desiredKey, IdleKey, StringComparison.Ordinal))
                        {
                            RequestPublish(null, IdleKey, urgent: true);
                        }

                        await _signal.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await _monitor.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

                    var sessions = await _monitor.ReadAllAsync(cancellationToken).ConfigureAwait(false);

                    if (sessions.Count == 0 && _lastSnapshot is not null && ++_emptyReads < 2)
                    {
                        // Una lectura vacía aislada no debe hacer parpadear la presencia; se
                        // reintenta en 250 ms (no en el sondeo completo) para que parar la
                        // música limpie Discord casi al instante.
                        SetStatus(Describe(_lastSnapshot));
                        await _signal.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    _emptyReads = 0;
                    _sessions = sessions;

                    var best = SmtcMonitor.SelectBest(sessions);

                    if (IsTransientMetadataGap(best, _lastSnapshot))
                    {
                        // El reproductor todavía no publicó los datos de la nueva canción.
                        // Se mantiene la presencia anterior un momento en lugar de enseñar
                        // «Reproduciendo» y volver a cambiar medio segundo después.
                        await _signal.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    // Sin esperas de red: la portada y el icono se resuelven en segundo
                    // plano y la actividad se republica en cuanto estén listos, así el
                    // título, el artista y la barra salen al instante.
                    var snapshot = ApplyArtwork(best, _lastSnapshot);

                    snapshot = ApplyProgramIcon(snapshot);

                    // Discord renders its own clock from the absolute start/end pair it already
                    // has; a paused track keeps that pair almost unchanged, so resuming it must
                    // re-anchor the bar explicitly. On Paused -> Playing of the same track we
                    // rewind to the paused position, invalidate the published key and republish,
                    // otherwise the bar stays where the pause left it.
                    var resumed = IsResumeOfSameTrack(_lastSnapshot, snapshot);

                    if (resumed)
                    {
                        // The cached paused position is stale: the player may have adjusted it,
                        // or taken a moment to actually restart the audio. Read the session again
                        // right now and anchor to the position it reports, so the bar starts in
                        // sync with the real audio instead of being extrapolated from the pause.
                        var fresh = await _monitor
                            .ReadFreshAsync(snapshot!.AppUserModelId, cancellationToken)
                            .ConfigureAwait(false);

                        if (fresh is { Status: PlaybackState.Playing })
                        {
                            // The fresh read may lack the thumbnail/URL the resolved
                            // snapshot already had; re-resolve so the cover survives.
                            snapshot = ApplyArtwork(fresh, snapshot);
                            snapshot = ApplyProgramIcon(snapshot);
                        }

                        _desiredKey = null;
                        forceRepublish = true;
                        _log.Info(
                            $"Reanudación detectada en «{snapshot!.Title}»; timestamps anclados a la " +
                            $"posición real ({FormatPosition(snapshot.Position)}).");
                    }

                    // Misma canción reiniciada desde 0 (o un salto en la barra): la clave
                    // de actividad no cambia, así que sin esto Discord seguiría con el reloj
                    // viejo. Se republica con los timestamps de la posición real.
                    var jumped = PositionJumped(_lastSnapshot, snapshot);

                    // Conteo de reproducciones consecutivas de la misma canción. Solo sube
                    // cuando vuelve de verdad al principio: arrastrar la barra hacia atrás o
                    // una lectura momentánea en 0 no cuentan como otra reproducción.
                    UpdatePlayCount(_lastSnapshot, snapshot);

                    if (snapshot is not null && snapshot.IsActive)
                    {
                        snapshot = snapshot with { PlayCount = _playCount <= 0 ? 1 : _playCount };
                    }

                    if (!string.IsNullOrWhiteSpace(snapshot?.PublicArtworkUrl)
                        && !string.Equals(snapshot.PublicArtworkUrl, _lastSnapshot?.PublicArtworkUrl, StringComparison.Ordinal))
                    {
                        _log.Debug($"Portada publicada: {snapshot.PublicArtworkUrl}");
                    }

                    _lastSnapshot = snapshot;

                    if (jumped && _desiredKey is not null && _desiredKey != IdleKey)
                    {
                        forceRepublish = true;
                        _desiredKey = null;
                        _log.Debug(
                            $"Salto de posición detectado en «{snapshot!.Title}» " +
                            $"({FormatPosition(snapshot.Position)}); republicando con timestamps frescos.");
                    }

                    var activity = snapshot is null ? null : _factory.Create(snapshot);
                    _currentActivity = activity;

                    if (resumed)
                    {
                        _log.Debug(
                            $"Timestamps de reanudación: start {activity?.Timestamps?.Start}, " +
                            $"end {activity?.Timestamps?.End}.");
                    }

                    var key = activity is null
                        ? IdleKey
                        : $"{snapshot!.ActivityKey}|{activity.Details}|{activity.State}";

                    var publishedAge = _publishedAtUnixMs == 0
                        ? TimeSpan.MaxValue
                        : TimeSpan.FromMilliseconds(
                            Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _publishedAtUnixMs));

                    // El latido solo cuenta desde un envío confirmado: antes del primero ya
                    // hay una actividad encolada y no hace falta repetirla.
                    var heartbeat = _options.HeartbeatSeconds > 0
                        && _publishedAtUnixMs != 0
                        && publishedAge >= TimeSpan.FromSeconds(_options.HeartbeatSeconds);

                    // Discord has no paused flag and animates timestamps in its client, so a
                    // paused activity is re-anchored periodically to keep the bar at the
                    // paused position (same behaviour as Music Presence's frozen bar).
                    var pausedAnchorDue = snapshot is { Status: PlaybackState.Paused }
                        && _options.PauseRefreshSeconds > 0
                        && _publishedAtUnixMs != 0
                        && publishedAge >= TimeSpan.FromSeconds(_options.PauseRefreshSeconds);

                    var urgent = forceRepublish || !string.Equals(key, _desiredKey, StringComparison.Ordinal);

                    var shouldPublish = urgent
                        || pausedAnchorDue
                        || (heartbeat && !string.Equals(_desiredKey, IdleKey, StringComparison.Ordinal));

                    if (pausedAnchorDue)
                    {
                        _log.Debug(
                            $"Reanclando timestamps de pausa en la posición actual " +
                            $"(start {activity?.Timestamps?.Start}).");
                    }

                    if (shouldPublish)
                    {
                        // Encolar nunca bloquea: si Discord está en su límite, la actividad
                        // espera fuera de este bucle y un cambio más nuevo la reemplaza. Así
                        // el sondeo, la bandeja y los siguientes saltos siguen al instante.
                        RequestPublish(activity, key, urgent);
                    }

                    SetStatus(Describe(snapshot));

                    await _signal.WaitAsync(TimeSpan.FromMilliseconds(_options.PollIntervalMs), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error("Error en el bucle de presencia.", ex);
                    SetStatus("Error: " + ex.Message);
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    /// <summary>
    /// Resuelve la portada sin bloquear el bucle.
    ///
    /// Antes se esperaba aquí a Catbox + external-assets (segundos en cada cambio de
    /// pista) y por eso el título, la barra y la portada aparecían tarde. Ahora una URL
    /// ya resuelta se reutiliza al instante y, si hay que subir o buscar la carátula, se
    /// lanza en segundo plano y la actividad se republica en cuanto está lista.
    /// </summary>
    private MediaSnapshot? ApplyArtwork(MediaSnapshot? snapshot, MediaSnapshot? previous)
    {
        // Some players drop the SMTC thumbnail while paused. Carry the previous
        // snapshot's artwork over for the same track so the cover never disappears.
        snapshot = CarryOverArtwork(snapshot, previous);

        if (snapshot is null)
        {
            _lastLookupKey = null;
            _lastLookupUrl = null;
            return null;
        }

        // Same track: la URL anterior es solo un respaldo (pausa / relectura). Si la
        // caché ya tiene una URL nueva por una renovación, esa manda.
        var carriedUrl = SameMediaTrack(previous, snapshot) ? previous?.PublicArtworkUrl : null;

        var artwork = _options.Artwork;

        if (artwork.IsDisabled)
        {
            return snapshot;
        }

        if (artwork.UsesUpload && snapshot.Artwork is not null)
        {
            var cachedCover = _artworkUpload.GetCachedCover(snapshot.Artwork);

            if (cachedCover is not null)
            {
                // La URL se revalida a las 24 h y se renueva a las 60 h: Litterbox borra el
                // archivo a las 72 h y antes se guardaba la URL durante días.
                if (cachedCover.NeedsRevalidation)
                {
                    StartCoverRevalidation(snapshot.Artwork, cachedCover.Url!);
                }

                if (cachedCover.NeedsRefresh)
                {
                    StartCoverUpload(snapshot.Artwork, force: true);
                }

                if (!string.IsNullOrWhiteSpace(cachedCover.Url))
                {
                    // Discord hace proxy automáticamente de las URLs HTTPS públicas en el
                    // RPC de escritorio, así que se envía la URL cruda (más fiable que el
                    // mp:, que en este cliente no se resolvía).
                    return snapshot with { PublicArtworkUrl = cachedCover.Url };
                }
            }
            else
            {
                StartCoverUpload(snapshot.Artwork);
            }

            // Sin URL nueva (subida en curso o fallo reciente): se conserva la anterior de
            // la misma pista mientras siga siendo válida.
            if (!string.IsNullOrWhiteSpace(carriedUrl))
            {
                return snapshot with { PublicArtworkUrl = carriedUrl };
            }

            // Si la subida aún no está disponible, intentar también la búsqueda en
            // catálogo en paralelo: así las pistas que no están en el host público siguen
            // mostrando una portada (iTunes/Deezer) en lugar de quedarse sin imagen.
            if (artwork.UsesLookup)
            {
                // cae al bloque de búsqueda de más abajo
            }
            else
            {
                return snapshot;
            }
        }

        if (!string.IsNullOrWhiteSpace(carriedUrl))
        {
            snapshot = snapshot with { PublicArtworkUrl = carriedUrl };
        }

        if (!artwork.UsesLookup)
        {
            _lastLookupKey = null;
            _lastLookupUrl = null;
            return snapshot;
        }

        var key = $"{snapshot.Title}|{snapshot.Artist}|{snapshot.Album}";

        if (string.Equals(key, _lastLookupKey, StringComparison.Ordinal))
        {
            return _lastLookupUrl is null ? snapshot : snapshot with { PublicArtworkUrl = _lastLookupUrl };
        }

        _lastLookupKey = key;
        _lastLookupUrl = null;
        StartLookup(snapshot, key);
        return snapshot;
    }

    /// <summary>
    /// Icono del reproductor cacheado por AUMID. Antes se extraía, se subía y se convertía
    /// en cada vuelta del bucle (una vez por segundo) aunque nada hubiera cambiado; ahora
    /// se hace una sola vez por reproductor y el cambio de reproductor lo muestra al
    /// instante.
    /// </summary>
    private MediaSnapshot? ApplyProgramIcon(MediaSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var url = _programIcons.GetOrRequest(
            snapshot.AppUserModelId,
            snapshot.PlayerExecutable,
            OnProgramIconReady);

        return string.IsNullOrWhiteSpace(url) ? snapshot : snapshot with { ProgramIconUrl = url };
    }

    /// <summary>Se llama desde un hilo de fondo cuando el icono ya tiene URL pública.</summary>
    private void OnProgramIconReady()
    {
        // El icono ya tiene URL pública (cruda): se republica al instante para que el
        // badge aparezca sin esperar a ningún proxy.
        Interlocked.Exchange(ref _forceRepublish, 1);
        _signal.Set();
    }

    private static MediaSnapshot? CarryOverArtwork(MediaSnapshot? snapshot, MediaSnapshot? previous)
    {
        if (snapshot is { Artwork: null }
            && previous?.Artwork is not null
            && SameMediaTrack(previous, snapshot))
        {
            return snapshot with { Artwork = previous.Artwork };
        }

        return snapshot;
    }

    private static bool SameMediaTrack(MediaSnapshot? a, MediaSnapshot? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(a.AppUserModelId, b.AppUserModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Title, b.Title, StringComparison.Ordinal)
            && string.Equals(a.Artist, b.Artist, StringComparison.Ordinal)
            && string.Equals(a.Album, b.Album, StringComparison.Ordinal);
    }

    private static string CoverKey(ArtworkInfo artwork) =>
        string.IsNullOrWhiteSpace(artwork.Hash) ? artwork.FileName : artwork.Hash;

    /// <summary>Sube la carátula al host público en segundo plano y republica al terminar.</summary>
    private void StartCoverUpload(ArtworkInfo artwork, bool force = false)
    {
        var coverKey = CoverKey(artwork);

        if (!_resolvingCovers.TryAdd(coverKey, 0))
        {
            return;
        }

        _ = Task.Run(() => ResolveCoverAsync(coverKey, artwork, force));
    }

    private async Task ResolveCoverAsync(string coverKey, ArtworkInfo artwork, bool force)
    {
        try
        {
            var url = await _artworkUpload.UploadAsync(artwork, _cts.Token, force).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            _log.Info($"Portada lista: {artwork.FileName} → {url}");

            // La siguiente vuelta del bucle la toma de la caché y la publica al momento.
            Interlocked.Exchange(ref _forceRepublish, 1);
            _signal.Set();
        }
        catch (OperationCanceledException)
        {
            // cerrando
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo preparar la portada: {ex.Message}");
        }
        finally
        {
            _resolvingCovers.TryRemove(coverKey, out _);
        }
    }

    /// <summary>
    /// Comprueba en segundo plano que una URL de portada sigue viva. Si el host ya la borró,
    /// se sube otra y se republica; si solo hay dudas (red, 5xx), no se toca.
    /// </summary>
    private void StartCoverRevalidation(ArtworkInfo artwork, string url)
    {
        var coverKey = CoverKey(artwork);

        if (!_resolvingCovers.TryAdd(coverKey, 0))
        {
            return;
        }

        _ = Task.Run(() => RevalidateCoverAsync(coverKey, artwork, url));
    }

    private async Task RevalidateCoverAsync(string coverKey, ArtworkInfo artwork, string url)
    {
        try
        {
            var alive = await _artworkUpload.IsUrlAliveAsync(url, _cts.Token).ConfigureAwait(false);
            _artworkUpload.MarkRevalidated(url);

            if (alive != false)
            {
                return;
            }

            _log.Info("La URL pública de la portada ya no existe; se sube otra.");
            _artworkUpload.Invalidate(artwork);

            var fresh = await _artworkUpload
                .UploadAsync(artwork, _cts.Token, force: true)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(fresh))
            {
                return;
            }

            _log.Info($"Portada renovada: {artwork.FileName} → {fresh}");
            Interlocked.Exchange(ref _forceRepublish, 1);
            _signal.Set();
        }
        catch (OperationCanceledException)
        {
            // cerrando
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo revalidar la portada: {ex.Message}");
        }
        finally
        {
            _resolvingCovers.TryRemove(coverKey, out _);
        }
    }

    /// <summary>Convierte una portada ya subida en una URL mp: sin bloquear el bucle.</summary>
    private void StartExternalAssetConversion(string imageUrl)
    {
        if (!_resolvingCovers.TryAdd(imageUrl, 0))
        {
            return;
        }

        _ = Task.Run(() => ConvertCoverAsync(imageUrl));
    }

    private async Task ConvertCoverAsync(string imageUrl)
    {
        try
        {
            var mpUrl = await _externalAssets.ConvertAsync(imageUrl, _cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(mpUrl))
            {
                var ready = await _externalAssets.WaitForProxyAsync(mpUrl, _cts.Token).ConfigureAwait(false);
                if (!ready)
                {
                    _externalAssets.Invalidate(imageUrl);
                    return;
                }

                Interlocked.Exchange(ref _forceRepublish, 1);
                _signal.Set();
            }
        }
        catch (OperationCanceledException)
        {
            // cerrando
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo convertir la portada a mp: {ex.Message}");
        }
        finally
        {
            _resolvingCovers.TryRemove(imageUrl, out _);
        }
    }

    /// <summary>Busca una portada pública (iTunes + Deezer) en segundo plano y republica al terminar.</summary>
    private void StartLookup(MediaSnapshot snapshot, string key)
    {
        if (!_resolvingCovers.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(() => LookupCoverAsync(snapshot, key));
    }

    private async Task LookupCoverAsync(MediaSnapshot snapshot, string key)
    {
        try
        {
            var rawUrl = await _artworkLookup
                .ResolveAsync(snapshot.Title, snapshot.Artist, snapshot.Album, _cts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return;
            }

            // Si mientras tanto empezó otra canción, el resultado ya no sirve.
            if (!string.Equals(_lastLookupKey, key, StringComparison.Ordinal))
            {
                return;
            }

            // Discord hace proxy automáticamente de las URLs HTTPS públicas (Apple/Deezer)
            // en el RPC de escritorio, así que se envía la URL cruda: la conversión a mp:
            // no renderizaba en el cliente y la portada no aparecía.
            _lastLookupUrl = rawUrl;

            Interlocked.Exchange(ref _forceRepublish, 1);
            _signal.Set();
        }
        catch (OperationCanceledException)
        {
            // cerrando
        }
        catch (Exception ex)
        {
            _log.Debug($"Búsqueda de portada fallida: {ex.Message}");
        }
        finally
        {
            _resolvingCovers.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// True cuando la misma pista sigue sonando pero la posición pegó un salto: reinicio
    /// desde 0 (la misma canción otra vez) o un salto en la barra. Discord conserva el
    /// reloj que ya tiene, así que hay que reenviar los timestamps.
    /// </summary>
    private static bool PositionJumped(MediaSnapshot? previous, MediaSnapshot? current)
    {
        if (previous is null || current is null)
        {
            return false;
        }

        if (previous.Status != PlaybackState.Playing || current.Status != PlaybackState.Playing)
        {
            return false;
        }

        if (!SameMediaTrack(previous, current))
        {
            return false;
        }

        var delta = current.Position - previous.Position;

        // Hacia atrás más de 1,5 s = reinicio; hacia adelante más de 5 s = salto manual.
        return delta < TimeSpan.FromSeconds(-1.5) || delta > TimeSpan.FromSeconds(5);
    }

    private bool IsResumeOfSameTrack(MediaSnapshot? previous, MediaSnapshot? current)
    {
        if (previous is null || current is null)
        {
            return false;
        }

        var wasPaused = previous.Status == PlaybackState.Paused
            || (previous.Status == PlaybackState.Stopped && _options.TreatStoppedAsPaused);

        return wasPaused
            && current.Status == PlaybackState.Playing
            && string.Equals(previous.AppUserModelId, current.AppUserModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.Title, current.Title, StringComparison.Ordinal)
            && previous.Duration == current.Duration;
    }

    private static string FormatPosition(TimeSpan position) =>
        position.ToString(position.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private string Describe(MediaSnapshot? snapshot)
    {
        if (_paused)
        {
            return "Pausado";
        }

        if (snapshot is null)
        {
            return _discord.State switch
            {
                DiscordConnectionState.Disabled => "Sin reproducción · Discord sin configurar",
                DiscordConnectionState.Connected => "Sin reproducción",
                DiscordConnectionState.Connecting => "Sin reproducción · conectando a Discord…",
                _ => "Sin reproducción · Discord desconectado",
            };
        }

        var discord = _discord.State switch
        {
            DiscordConnectionState.Connected => "Discord conectado",
            DiscordConnectionState.Connecting => "conectando a Discord…",
            DiscordConnectionState.Disabled => "Discord sin configurar",
            _ => "Discord desconectado",
        };

        return $"{snapshot.TrackSummary} · {snapshot.PlayerName} | {discord}";
    }

    private void SetStatus(string status)
    {
        if (string.Equals(_statusText, status, StringComparison.Ordinal))
        {
            return;
        }

        _statusText = status;
        Updated?.Invoke();
    }

    private void OnMediaChanged() => _signal.Set();

    private void OnDiscordStateChanged()
    {
        // Force a republish after a reconnect.
        _desiredKey = null;
        _publishedAtUnixMs = 0;
        _signal.Set();
        Updated?.Invoke();
    }

    /// <summary>
    /// Encola la última actividad a publicar. Solo hay un hueco: lo que llegue después
    /// sustituye a lo anterior, porque en Discord solo se ve el estado final.
    /// </summary>
    private void RequestPublish(DiscordActivity? activity, string key, bool urgent)
    {
        CancellationTokenSource? inflight;

        lock (_publishGate)
        {
            _pendingPublish = new PublishRequest(activity, key, urgent, DateTimeOffset.UtcNow);
            inflight = _inflightPublish;
        }

        _desiredKey = key;

        // Si había un envío esperando hueco en el límite de Discord se abandona: ya hay una
        // actividad más nueva. La cancelación solo afecta a la espera, nunca a una escritura
        // a medias del pipe.
        try
        {
            inflight?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // El publicador ya terminó ese envío.
        }

        _publishSignal.Set();
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PublishRequest? request;

                lock (_publishGate)
                {
                    request = _pendingPublish;
                }

                if (request is null)
                {
                    await _publishSignal.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                // Ventana de asentamiento: al saltar canciones en rápida sucesión solo se
                // envía la última, y las intermedias no consumen el límite de Discord.
                var remaining = PublishSettleWindow - (DateTimeOffset.UtcNow - request.QueuedAt);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using var supersede = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                lock (_publishGate)
                {
                    _inflightPublish = supersede;
                }

                bool published;

                try
                {
                    published = await _discord
                        .PublishAsync(request.Activity, cancellationToken, request.Urgent, supersede.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_publishGate)
                    {
                        _inflightPublish = null;

                        if (ReferenceEquals(_pendingPublish, request))
                        {
                            _pendingPublish = null;
                        }
                    }
                }

                if (published)
                {
                    _publishedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
                else if (!supersede.IsCancellationRequested && request.Activity is not null)
                {
                    // Discord no estaba disponible: que el bucle lo vuelva a intentar.
                    _desiredKey = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn($"Error publicando la actividad: {ex.Message}");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Detecta el instante en que un reproductor cambia de pista pero todavía no ha
    /// publicado título/artista (durante unos milisegundos la sesión queda vacía).
    /// Se mantiene la presencia anterior un máximo de 2 s para no enseñar un hueco.
    /// </summary>
    private bool IsTransientMetadataGap(MediaSnapshot? current, MediaSnapshot? previous)
    {
        if (current is null
            || current.Status != PlaybackState.Playing
            || !string.IsNullOrWhiteSpace(current.Title))
        {
            _metadataGapSince = DateTimeOffset.MinValue;
            return false;
        }

        if (previous is null
            || string.IsNullOrWhiteSpace(previous.Title)
            || !string.Equals(previous.AppUserModelId, current.AppUserModelId, StringComparison.OrdinalIgnoreCase))
        {
            _metadataGapSince = DateTimeOffset.MinValue;
            return false;
        }

        if (_metadataGapSince == DateTimeOffset.MinValue)
        {
            _metadataGapSince = DateTimeOffset.UtcNow;
        }

        return DateTimeOffset.UtcNow - _metadataGapSince < TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Mantiene el contador de reproducciones consecutivas de la misma canción.
    ///
    /// Una repetición se cuenta cuando la pista vuelve al principio después de haber sonado
    /// de verdad, y solo tras confirmarla en la lectura siguiente: así mover la barra hacia
    /// atrás, un salto de posición o un dato momentáneo en 0:00 no suman una reproducción
    /// que nunca ocurrió (el bug de las canciones que solo habían sonado una vez).
    /// </summary>
    private void UpdatePlayCount(MediaSnapshot? previous, MediaSnapshot? current)
    {
        if (current is null || !current.IsActive)
        {
            // Sin música se conserva el conteo por si vuelve la misma canción.
            return;
        }

        if (previous is null || !SameTrackForCounting(previous, current))
        {
            _playCount = 1;
            ClearRepeatCandidate();
            return;
        }

        if (current.Status != PlaybackState.Playing)
        {
            return;
        }

        if (StartedOver(previous, current))
        {
            // Todavía no se cuenta: se confirma con la lectura siguiente.
            _repeatCandidateKey = current.ActivityKey;
            _repeatCandidateAt = DateTimeOffset.UtcNow;
            _repeatCandidatePosition = current.Position;
            return;
        }

        if (_repeatCandidateKey.Length == 0
            || !string.Equals(_repeatCandidateKey, current.ActivityKey, StringComparison.Ordinal))
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _repeatCandidateAt;

        if (elapsed > RepeatConfirmationWindow)
        {
            ClearRepeatCandidate();
            return;
        }

        if (elapsed < TimeSpan.FromMilliseconds(800))
        {
            // Se espera a la lectura siguiente para no dar por buena una posición que aún
            // no ha demostrado que avanza (y para que el reproductor termine de publicar
            // los metadatos de la pista nueva).
            return;
        }

        // Sigue sonando desde el principio y la posición avanzó al ritmo real: era una
        // repetición de verdad. Si volvió a la posición anterior, era un fallo del reproductor.
        var expected = _repeatCandidatePosition + elapsed;
        if ((current.Position - expected).Duration() <= TimeSpan.FromSeconds(2))
        {
            _playCount++;
            ClearRepeatCandidate();
            _log.Debug($"Repetición confirmada de «{current.Title}»: ×{_playCount}.");
        }
    }

    private static bool StartedOver(MediaSnapshot previous, MediaSnapshot current)
    {
        var restartWindow = TimeSpan.FromSeconds(3);

        if (current.Position > restartWindow)
        {
            return false;
        }

        // Hay que haber escuchado una parte real de la canción antes de que vuelva a
        // empezar; si no, cualquier corrección de posición sumaría una reproducción.
        var minimumListened = previous.Duration > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(Math.Min(10_000, previous.Duration.TotalMilliseconds / 2))
            : TimeSpan.FromSeconds(10);

        if (previous.Position < minimumListened)
        {
            return false;
        }

        if (previous.Status == PlaybackState.Playing)
        {
            return true;
        }

        // El reproductor dejó la pista al final (pausa o parada tras terminar): volver a
        // empezar sí es otra reproducción.
        return previous.Duration > TimeSpan.Zero
            && previous.Position >= previous.Duration - TimeSpan.FromSeconds(5);
    }

    private static bool SameTrackForCounting(MediaSnapshot previous, MediaSnapshot current)
    {
        if (!SameMediaTrack(previous, current))
        {
            return false;
        }

        // Dos versiones distintas pueden compartir título, artista y álbum (directo,
        // remaster, extended…). La duración las separa.
        return previous.Duration <= TimeSpan.Zero
            || current.Duration <= TimeSpan.Zero
            || (previous.Duration - current.Duration).Duration() <= TimeSpan.FromSeconds(2);
    }

    private void ClearRepeatCandidate()
    {
        _repeatCandidateKey = "";
        _repeatCandidateAt = DateTimeOffset.MinValue;
        _repeatCandidatePosition = TimeSpan.Zero;
    }

    private sealed record PublishRequest(
        DiscordActivity? Activity,
        string Key,
        bool Urgent,
        DateTimeOffset QueuedAt);

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        if (_publisher is not null)
        {
            try
            {
                await _publisher.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        _monitor.Changed -= OnMediaChanged;
        _discord.StateChanged -= OnDiscordStateChanged;

        try
        {
            // DiscordPresenceService.DisposeAsync clears the activity (single clear frame).
            await _discord.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        try
        {
            _monitor.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _cts.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public void Dispose()
    {
        try
        {
            StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // ignore
        }
    }
}
