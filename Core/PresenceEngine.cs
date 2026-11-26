using System.Collections.Concurrent;
using System.Globalization;
using UniversalPresence.Configuration;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;
using UniversalPresence.Media;
using UniversalPresence.Plugins;

namespace UniversalPresence.Core;

/// <summary>
/// Ciclo de vida del motor: campos compartidos, arranque, parada y el bucle que lee SMTC
/// y encola la actividad a publicar. Empieza por aquí para entender el flujo.
/// </summary>
public sealed partial class PresenceEngine : IAsyncDisposable, IDisposable
{
    private const string IdleKey = "<idle>";

    private readonly AppOptions _options;

    private readonly Log _log;

    private readonly SmtcMonitor _monitor;

    private readonly DiscordPresenceService _discord;

    private readonly ActivityFactory _factory;

    private readonly ArtworkLookupService _artworkLookup;

    private readonly TrackArtistLookupService _artistLookup;

    private readonly ListeningHistoryService _history;

    private readonly LastFmService _lastfm;

    private readonly ArtworkUploadService _artworkUpload;

    private readonly DiscordTokenService _discordTokens;

    private readonly ExternalAssetsService _externalAssets;

    private readonly ProgramIconAssetService _programIcons;

    private readonly PluginHost _plugins;

    private readonly ChangeSignal _signal = new();

    private readonly ChangeSignal _publishSignal = new();

    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;

    private volatile bool _paused;

    private volatile bool _gamingPaused;

    private volatile bool _idleCleared;

    private volatile MediaSnapshot? _lastSnapshot;

    private volatile DiscordActivity? _currentActivity;

    private volatile IReadOnlyList<MediaSnapshot> _sessions = Array.Empty<MediaSnapshot>();

    private volatile string _statusText = "Iniciando…";

    private int _emptyReads;

    private int _forceRepublish;

    private int _restartMonitorRequested;

    private bool _disposed;

    public PresenceEngine(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
        _monitor = new SmtcMonitor(options, log);
        _discord = new DiscordPresenceService(options, log);
        _factory = new ActivityFactory(options);
        _artworkLookup = new ArtworkLookupService(
            options,
            log,
            Path.Combine(AppOptions.LocalDataDirectory, "lookup-cache.json"));
        _artistLookup = new TrackArtistLookupService(log);
        _lastfm = new LastFmService(options, log);
        _artworkUpload = new ArtworkUploadService(options, log);
        _discordTokens = new DiscordTokenService(options, log);
        _externalAssets = new ExternalAssetsService(options, _discordTokens, log);
        _programIcons = new ProgramIconAssetService(options, _artworkUpload, log);
        _plugins = new PluginHost(options, log);
        _history = new ListeningHistoryService(options.ListeningHistoryRetentionDays, options.ListeningHistoryMaxEntries);
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

    /// <summary>«Título — Artista» de lo que suena ahora, o null si no hay nada activo.</summary>
    public string? CurrentTrackText
    {
        get
        {
            if (_lastSnapshot is not { IsActive: true } snapshot)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(snapshot.DisplayArtist)
                ? snapshot.Title
                : $"{snapshot.Title} — {snapshot.DisplayArtist}";
        }
    }

    /// <summary>Manda una orden de transporte al reproductor activo por SMTC.</summary>
    public Task<bool> ControlPlaybackAsync(SmtcMonitor.MediaControlAction action) =>
        _monitor.TryControlAsync(action, _cts.Token);

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

        // Al arrancar se reintentan las subidas que fallaron la última vez (sin red, host
        // caído). Se esperan unos segundos para no competir con la conexión a Discord.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), _cts.Token).ConfigureAwait(false);
                StartQueueDrain();
            }
            catch (OperationCanceledException)
            {
                // cerrando
            }
        });
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
        _artworkUpload.InvalidateFailures();

        Interlocked.Exchange(ref _forceRepublish, 1);
        Interlocked.Exchange(ref _restartMonitorRequested, 1);
        _signal.Set();
        Updated?.Invoke();
        _log.Info("Settings applied: restarting the SMTC monitor and republishing the activity.");
    }

    /// <summary>Identidad estable de una pista (sin el estado de reproducción).</summary>
    private static string TrackKey(MediaSnapshot snapshot) =>
        string.Join(
            '|',
            snapshot.AppUserModelId,
            snapshot.Title,
            snapshot.Artist,
            snapshot.Album,
            ((long)snapshot.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture));
    /// Fuerza un reenvío inmediato de la actividad (menú de bandeja, <c>command.txt</c>).
    /// Útil cuando Discord se quedó con una portada o un título viejo tras reconectar.
    /// </summary>
    public void RequestResync()
    {
        _desiredKey = null;
        Interlocked.Exchange(ref _forceRepublish, 1);
        _signal.Set();
        _log.Info("Resincronización manual solicitada.");
        Updated?.Invoke();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    MaybeTrimMemory();
                    Telemetry.MaybeLog(_log);

                    // Modo juego: si hay una app a pantalla completa en primer plano, pausar presencia.
                    if (_options.GamingMode)
                    {
                        var fs = IsForegroundFullscreen();
                        if (fs && !_gamingPaused)
                        {
                            _gamingPaused = true;
                            _signal.Set();
                        }
                        else if (!fs && _gamingPaused)
                        {
                            _gamingPaused = false;
                            _signal.Set();
                        }
                    }
                    else if (_gamingPaused)
                    {
                        _gamingPaused = false;
                    }

                    // Ausencia: si el equipo está bloqueado o el usuario lleva un rato sin
                    // tocar nada, se retira la presencia en lugar de dejar "escuchando" una
                    // canción que ya nadie oye. Al volver se republica sola.
                    if (_options.ClearWhenIdle)
                    {
                        var away = UserIdle.IsAway(_options.IdleSeconds);

                        if (away != _idleCleared)
                        {
                            _idleCleared = away;
                            _desiredKey = null;
                            _hasPublishAnchor = false;
                            _log.Info(away
                                ? "Usuario ausente o sesión bloqueada: se oculta la presencia."
                                : "Usuario de vuelta: se republica la presencia.");

                            if (!away)
                            {
                                Interlocked.Exchange(ref _forceRepublish, 1);
                            }
                        }

                        if (_idleCleared)
                        {
                            _lastSnapshot = null;
                            _currentActivity = null;
                            SetStatus("Away");

                            if (!string.Equals(_desiredKey, IdleKey, StringComparison.Ordinal))
                            {
                                RequestPublish(null, IdleKey, PublishPriority.Content);
                            }

                            await _signal.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                                .ConfigureAwait(false);
                            continue;
                        }
                    }
                    else if (_idleCleared)
                    {
                        _idleCleared = false;
                        _desiredKey = null;
                    }

                    if (Interlocked.Exchange(ref _restartMonitorRequested, 0) == 1)
                    {
                        _monitor.Reset();
                        _lastLookupKey = null;
                        _lastLookupUrl = null;
                    }

                    var forceRepublish = Interlocked.Exchange(ref _forceRepublish, 0) == 1;

                    if ((_paused || _gamingPaused) && !_options.ShowPausedMedia)
                    {
                        _lastSnapshot = null;
                        _currentActivity = null;
                        SetStatus("Paused");

                        if (!string.Equals(_desiredKey, IdleKey, StringComparison.Ordinal))
                        {
                            RequestPublish(null, IdleKey, PublishPriority.Content);
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

                    snapshot = ApplyCoverOverride(snapshot);
                    snapshot = ApplyProgramIcon(snapshot);

                    // Seek/arrastre de la barra en la MISMA pista: SMTC avisa con
                    // TimelinePropertiesChanged. En cuanto llega, se comparan la posición
                    // real y la que Discord está dibujando y se reenvían los timestamps en
                    // el acto (antes había que esperar a superar 2,5 s de deriva).
                    var seekedCurrentTrack = false;
                    var seekRevision = _monitor.SeekRevision;
                    if (seekRevision != _seenSeekRevision)
                    {
                        _seenSeekRevision = seekRevision;

                        if (snapshot is { IsActive: true })
                        {
                            // La lectura del bucle pudo empezar antes del seek: se relee la
                            // sesión para anclar los timestamps a la posición exacta que el
                            // reproductor reporta ahora (sin proyectarla).
                            var afterSeek = await _monitor
                                .ReadFreshAsync(snapshot.AppUserModelId, cancellationToken)
                                .ConfigureAwait(false);

                            if (afterSeek is { IsActive: true })
                            {
                                snapshot = ApplyArtwork(afterSeek, snapshot);
                                snapshot = ApplyCoverOverride(snapshot);
                                snapshot = ApplyProgramIcon(snapshot);
                            }

                            if (_hasPublishAnchor
                                && string.Equals(
                                    _publishedTrackKey,
                                    TrackKey(snapshot!),
                                    StringComparison.Ordinal))
                            {
                                var expected = snapshot!.Status == PlaybackState.Playing
                                    ? _publishedPosition + (DateTimeOffset.UtcNow - _publishedAnchorAt)
                                    : _publishedPosition;
                                var seekDelta = (snapshot.Position - expected).Duration();

                                // Un seek de menos de 350 ms no se nota; cualquier salto
                                // mayor se reenvía ya, sin esperar al latido ni a la deriva.
                                if (seekDelta > TimeSpan.FromMilliseconds(350))
                                {
                                    seekedCurrentTrack = true;
                                    forceRepublish = true;
                                    _desiredKey = null;
                                    _log.Debug(
                                        $"Seek detectado en «{snapshot.Title}» " +
                                        $"({FormatPosition(snapshot.Position)}); timestamps reenviados al instante.");
                                }
                            }
                        }
                    }

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
                            snapshot = ApplyCoverOverride(snapshot);
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
                    UpdatePlayCount(_lastSnapshot, snapshot, seekedCurrentTrack);

                    if (snapshot is not null && snapshot.IsActive)
                    {
                        snapshot = snapshot with { PlayCount = _playCount <= 0 ? 1 : _playCount };
                    }

                    if (!string.IsNullOrWhiteSpace(snapshot?.PublicArtworkUrl)
                        && !string.Equals(snapshot.PublicArtworkUrl, _lastSnapshot?.PublicArtworkUrl, StringComparison.Ordinal))
                    {
                        _log.Debug($"Portada publicada: {snapshot.PublicArtworkUrl}");
                    }

                    snapshot = ApplyArtistDisplay(snapshot);
                    _lastSnapshot = snapshot;
                    _plugins.Notify(snapshot);

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

                    // La portada y el icono forman parte de la clave: cuando su URL llega
                    // o se renueva es un cambio visible, no un simple ajuste de la barra.
                    var key = activity is null
                        ? IdleKey
                        : $"{snapshot!.ActivityKey}|{activity.Details}|{activity.State}" +
                          $"|{snapshot.PublicArtworkUrl}|{snapshot.ProgramIconUrl}";

                    // Resincronización automática: si esta pista no es la que Discord tiene
                    // confirmada, no hay nada en la cola y aun así no se ha publicado en
                    // AutoResyncGrace, el envío se perdió. Se fuerza el reenvío en lugar de
                    // esperar al latido (hasta 60 s mostrando la canción anterior).
                    if (ShouldAutoResync(snapshot, key))
                    {
                        forceRepublish = true;
                        _desiredKey = null;
                        _log.Info(
                            $"«{snapshot!.Title}» aún no está confirmada en Discord; " +
                            "resincronización automática.");
                    }

                    // Empezó una pista nueva: se reintentan las subidas de portadas que
                    // quedaron pendientes (como mucho una vez cada 30 s). Se salta si la
                    // portada de ESTA pista aún se está preparando: es más importante que
                    // una subida antigua no le quite el turno.
                    var currentCoverPending = snapshot is { Artwork: not null }
                        && _resolvingCovers.ContainsKey(CoverKey(snapshot.Artwork));

                    if (!currentCoverPending
                        && snapshot is { IsActive: true }
                        && !string.Equals(key, _publishedKey, StringComparison.Ordinal))
                    {
                        MaybeKickUploadQueue();
                    }

                    RecordListening(snapshot, activity);

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

                    // Deriva de la barra: si la posición real se separó de la que Discord está
                    // dibujando (reproductor sin eventos, un seek silencioso, un ajuste del
                    // reloj), se reenvían los timestamps en lugar de esperar al latido.
                    var driftResync = HasProgressDrift(snapshot);

                    if (driftResync)
                    {
                        _log.Debug(
                            $"Deriva de progreso detectada en «{snapshot!.Title}» " +
                            $"({FormatPosition(snapshot.Position)}); resincronizando la barra.");
                    }

                    // Comprobación periódica (5 s por defecto): aunque no llegue ningún
                    // evento, si la barra publicada se separó más de 400 ms de la posición
                    // real se reenvía. En reproducción normal la diferencia es ~0 y no se
                    // envía nada, así que no gasta huecos del límite de Discord.
                    if (_options.ResyncEverySeconds > 0
                        && snapshot is { IsActive: true }
                        && DateTimeOffset.UtcNow >= _nextPeriodicResync)
                    {
                        _nextPeriodicResync = DateTimeOffset.UtcNow.AddSeconds(_options.ResyncEverySeconds);

                        if (HasPeriodicProgressDrift(snapshot))
                        {
                            forceRepublish = true;
                            _desiredKey = null;
                            _log.Debug(
                                $"Resincronización periódica en «{snapshot.Title}» " +
                                $"({FormatPosition(snapshot.Position)}); reenviando timestamps.");
                        }
                    }

                    var urgent = forceRepublish
                        || driftResync
                        || !string.Equals(key, _desiredKey, StringComparison.Ordinal);

                    var shouldPublish = urgent
                        || pausedAnchorDue
                        || (heartbeat && !string.Equals(_desiredKey, IdleKey, StringComparison.Ordinal));

                    if (pausedAnchorDue)
                    {
                        _log.Debug(
                            $"Reanclando timestamps de pausa en la posición actual " +
                            $"(start {activity?.Timestamps?.Start}).");
                    }

                    // Prioridad: el contenido visible (pista, portada, reproductor) puede
                    // usar todo el cupo de Discord; los reenvíos de solo-progreso reservan
                    // siempre un par de huecos para que el siguiente cambio de canción no
                    // se quede esperando detrás de ellos.
                    var priority = !string.Equals(key, _publishedKey, StringComparison.Ordinal)
                        ? PublishPriority.Content
                        : forceRepublish || driftResync || jumped
                            ? PublishPriority.Progress
                            : PublishPriority.Background;

                    if (shouldPublish)
                    {
                        // Encolar nunca bloquea: si Discord está en su límite, la actividad
                        // espera fuera de este bucle y un cambio más nuevo la reemplaza. Así
                        // el sondeo, la bandeja y los siguientes saltos siguen al instante.
                        RequestPublish(
                            activity,
                            key,
                            priority,
                            snapshot?.Position ?? TimeSpan.Zero,
                            snapshot?.CapturedAt ?? DateTimeOffset.UtcNow,
                            snapshot is null ? "" : TrackKey(snapshot));
                    }

                    SetStatus(Describe(snapshot));

                    // Cada dos minutos se reintentan las subidas pendientes mientras la
                    // app sigue viva (la red puede volver sin reiniciar nada).
                    if (DateTimeOffset.UtcNow >= _nextQueueDrain)
                    {
                        _nextQueueDrain = DateTimeOffset.UtcNow.AddMinutes(2);
                        StartQueueDrain();
                    }

                    // Con reproducción activa se sondea al ritmo configurado; en pausa o sin
                    // sesión, cada 2 s. Los eventos de SMTC despiertan el bucle al instante, así
                    // que esto solo recorta lecturas WinRT inútiles sin añadir latencia visible.
                    var pollDelay = snapshot is { IsActive: true, Status: PlaybackState.Playing }
                        ? _options.PollIntervalMs
                        : Math.Min(2000, Math.Max(500, _options.PollIntervalMs * 2));

                    await _signal.WaitAsync(TimeSpan.FromMilliseconds(pollDelay), cancellationToken)
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
