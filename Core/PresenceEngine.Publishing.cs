using UniversalPresence.Configuration;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;
using UniversalPresence.Media;

namespace UniversalPresence.Core;

/// <summary>
/// Cola de publicación: solo hay una actividad pendiente (la última gana), prioridades
/// para no gastar el cupo de Discord, anclas de la barra y resincronización automática.
/// </summary>
public sealed partial class PresenceEngine : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Ventana de asentamiento antes de enviar. Si el usuario salta varias canciones
    /// seguidas solo se manda la última: las intermedias no gastan huecos del límite de
    /// Discord (5 actualizaciones cada 20 s) ni llegan a verse.
    /// </summary>
    private static readonly TimeSpan PublishSettleWindow = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// Margen que se espera a que Discord confirme una pista nueva antes de forzar un
    /// reenvío. Cubre el caso de un envío que se perdió (o se descartó) sin que el bucle
    /// se enterara: la canción que suena vuelve a publicarse sola.
    /// </summary>
    private static readonly TimeSpan AutoResyncGrace = TimeSpan.FromSeconds(1);

    private readonly object _publishGate = new();

    private Task? _publisher;

    private PublishRequest? _pendingPublish;

    private CancellationTokenSource? _inflightPublish;

    /// <summary>Última clave encolada (o enviada); el bucle compara la actividad contra ella.</summary>
    private volatile string? _desiredKey;

    /// <summary>Instante del último envío confirmado, en epoch ms (0 = todavía ninguno).</summary>
    private long _publishedAtUnixMs;
    // Ancla de la última actividad enviada: sirve para detectar que la posición real se
    // alejó de la que Discord está dibujando y reenviar los timestamps.
    private TimeSpan _publishedPosition;

    private DateTimeOffset _publishedAnchorAt = DateTimeOffset.MinValue;

    private bool _hasPublishAnchor;
    // Clave de la última actividad CONFIRMADA por Discord (no la encolada). Es la base de
    // la resincronización automática: si la canción que suena no coincide con esta clave y
    // no hay nada pendiente en la cola, el envío se perdió y hay que forzarlo.
    private volatile string? _publishedKey;

    private volatile string? _publishedTrackKey;

    private string? _unconfirmedKey;

    private DateTimeOffset _unconfirmedSince = DateTimeOffset.MinValue;

    private DateTimeOffset _lastDriftResyncAt = DateTimeOffset.MinValue;

    private DateTimeOffset _nextPeriodicResync = DateTimeOffset.MinValue;

    /// <summary>
    /// Comprobación periódica de la barra: cualquier desviación de más de 400 ms se
    /// reenvía. Se llama cada <see cref="AppOptions.ResyncEverySeconds"/> segundos.
    /// </summary>
    private bool HasPeriodicProgressDrift(MediaSnapshot snapshot)
    {
        if (!_hasPublishAnchor
            || _publishedAtUnixMs == 0
            || snapshot.Duration <= TimeSpan.Zero
            || !string.Equals(_publishedTrackKey, TrackKey(snapshot), StringComparison.Ordinal))
        {
            return false;
        }

                return IsProgressDesync(
            _publishedPosition,
            _publishedAnchorAt,
            snapshot.Position,
            DateTimeOffset.UtcNow,
            snapshot.Status,
            ProgressResyncThreshold);
    }

    /// <summary>Desviación mínima de la barra que merece un reenvío de timestamps.</summary>
    internal static readonly TimeSpan ProgressResyncThreshold = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// True cuando la posición real se separó de la que Discord está dibujando. La barra se
    /// dibuja desde un par absoluto start/end, así que una desviación mayor que el umbral
    /// significa que el reloj publicado ya no coincide con el audio.
    /// </summary>
    internal static bool IsProgressDesync(
        TimeSpan publishedPosition,
        DateTimeOffset anchorAt,
        TimeSpan actualPosition,
        DateTimeOffset now,
        PlaybackState status,
        TimeSpan threshold)
    {
        var expected = status == PlaybackState.Playing
            ? publishedPosition + (now - anchorAt)
            : publishedPosition;
        var difference = (actualPosition - expected).Duration();

        return difference > threshold && difference < TimeSpan.FromMinutes(30);
    }

    /// True cuando la posición real se alejó más de 2,5 s de la que Discord está dibujando
    /// a partir del último envío. Es la red de seguridad para reproductores que no emiten
    /// eventos de timeline: la barra se resincroniza sin esperar al latido.
    /// </summary>
    private bool HasProgressDrift(MediaSnapshot? snapshot)
    {
        if (!_options.ResyncProgressDrift
            || !_hasPublishAnchor
            || _publishedAtUnixMs == 0
            || snapshot is not { Status: PlaybackState.Playing }
            || snapshot.Duration <= TimeSpan.Zero
            || !string.Equals(_publishedTrackKey, TrackKey(snapshot), StringComparison.Ordinal))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        // Red de seguridad para reproductores que no avisan de los seeks: se reenvía en
        // cuanto la barra se separa 2,5 s, como mucho una vez cada 5 s (un reproductor con
        // proyección mala no debe consumir el límite de Discord a base de reenvíos).
        if (now - _lastDriftResyncAt < TimeSpan.FromSeconds(5))
        {
            return false;
        }

        var expected = _publishedPosition + (now - _publishedAnchorAt);
        var difference = (snapshot.Position - expected).Duration();

        if (difference > TimeSpan.FromSeconds(2.5)
            && difference < TimeSpan.FromMinutes(30))
        {
            _lastDriftResyncAt = now;
            return true;
        }

        return false;
    }

    private void OnDiscordStateChanged()
    {
        // Lo que Discord muestra ya no es de fiar: se olvida la confirmación. Pero no se
        // invalida la actividad encolada si hay un envío en curso (al conectar): eso
        // provocaba un reenvío duplicado de la misma canción justo después de publicarla.
        _publishedKey = null;
        _publishedTrackKey = null;
        _publishedAtUnixMs = 0;
        _hasPublishAnchor = false;
        _unconfirmedKey = null;

        if (_discord.State != DiscordConnectionState.Connected || IsPublishIdle())
        {
            _desiredKey = null;
        }

        _signal.Set();
        Updated?.Invoke();
    }

    /// <summary>
    /// True cuando la pista que suena no es la confirmada por Discord y la cola está
    /// vacía: el envío se perdió y hay que forzarlo (resync automático).
    /// </summary>
    private bool ShouldAutoResync(MediaSnapshot? snapshot, string key)
    {
        if (snapshot is not { IsActive: true })
        {
            _unconfirmedKey = null;
            return false;
        }

        if (string.Equals(key, _publishedKey, StringComparison.Ordinal))
        {
            _unconfirmedKey = null;
            return false;
        }

        var now = DateTimeOffset.UtcNow;

        if (!string.Equals(key, _unconfirmedKey, StringComparison.Ordinal))
        {
            _unconfirmedKey = key;
            _unconfirmedSince = now;
            return false;
        }

        if (now - _unconfirmedSince < AutoResyncGrace
            || !string.Equals(key, _desiredKey, StringComparison.Ordinal)
            || !IsPublishIdle())
        {
            return false;
        }

        // Se reinicia el margen para no repetir el aviso en cada vuelta si el reenvío
        // tampoco se confirma (por ejemplo, con Discord cerrado).
        _unconfirmedSince = now;
        return true;
    }

    private bool IsPublishIdle()
    {
        lock (_publishGate)
        {
            return _pendingPublish is null && _inflightPublish is null;
        }
    }

    /// <summary>
    /// Encola la última actividad a publicar. Solo hay un hueco: lo que llegue después
    /// sustituye a lo anterior, porque en Discord solo se ve el estado final.
    /// </summary>
    private void RequestPublish(
        DiscordActivity? activity,
        string key,
        PublishPriority priority,
        TimeSpan position = default,
        DateTimeOffset capturedAt = default,
        string trackKey = "")
    {
        if (capturedAt == default)
        {
            capturedAt = DateTimeOffset.UtcNow;
        }

        CancellationTokenSource? inflight;

        lock (_publishGate)
        {
            _pendingPublish = new PublishRequest(activity, key, priority, DateTimeOffset.UtcNow, position, capturedAt, trackKey);
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

                var published = false;

                try
                {
                    published = await _discord
                        .PublishAsync(request.Activity, cancellationToken, request.Priority, supersede.Token)
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

                        // Se confirma bajo el mismo bloqueo que el estado de la cola: el
                        // bucle nunca ve «nada pendiente» con una clave antigua y no fuerza
                        // un reenvío duplicado.
                        if (published)
                        {
                            _publishedKey = request.Key;
                        }
                    }
                }

                if (published)
                {
                    _publishedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    if (request.Activity is not null)
                    {
                        _publishedPosition = request.Position;
                        _publishedTrackKey = request.TrackKey;
                        // Los timestamps de reproducción están anclados a CapturedAt; los de
                        // pausa se desplazan al instante real del envío (ShiftTimestampsToNow),
                        // así que el ancla de deriva debe usar ese mismo instante.
                        _publishedAnchorAt = request.Activity.FrozenTimestamps
                            ? DateTimeOffset.UtcNow
                            : request.CapturedAt;
                        _hasPublishAnchor = true;
                    }
                    else
                    {
                        _hasPublishAnchor = false;
                        _publishedTrackKey = null;
                    }
                }
                else if (!supersede.IsCancellationRequested)
                {
                    // Discord no estaba disponible: que el bucle lo vuelva a intentar.
                    // También para los borrados (Activity null): si no, un "quitar la
                    // presencia" fallido dejaría la canción anterior fija en Discord.
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

    private sealed record PublishRequest(
        DiscordActivity? Activity,
        string Key,
        PublishPriority Priority,
        DateTimeOffset QueuedAt,
        TimeSpan Position,
        DateTimeOffset CapturedAt,
        string TrackKey);
}
