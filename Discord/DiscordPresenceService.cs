using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Discord;

/// <summary>
/// Qué cambia con este SET_ACTIVITY. Decide cuánto cupo del límite de Discord puede
/// gastar y con qué separación mínima, para que la información visible (canción, portada,
/// reproductor) nunca espere detrás de una resincronización de la barra.
/// </summary>
public enum PublishPriority
{
    /// <summary>Latido y reanclaje de pausa: solo mantienen viva la actividad.</summary>
    Background = 0,

    /// <summary>Solo timestamps (seek, deriva, resincronización periódica).</summary>
    Progress = 1,

    /// <summary>Cambió algo visible: pista, artista, portada, reproductor o estado.</summary>
    Content = 2,
}

/// <summary>
/// Keeps a Discord IPC connection alive: connects lazily, retries with exponential
/// backoff when Discord is closed, and throttles SET_ACTIVITY to Discord's limits.
/// </summary>
public sealed class DiscordPresenceService : IAsyncDisposable
{
    /// <summary>Discord acepta ~5 actualizaciones de actividad cada 20 s.</summary>
    private const int MaxSendsPerWindow = 5;

    private static readonly TimeSpan SendWindow = TimeSpan.FromSeconds(20);

    private readonly AppOptions _options;
    private readonly Log _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _retryGate = new();
    private readonly Queue<DateTimeOffset> _recentSends = new();

    private DiscordIpcClient? _client;
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private TimeSpan _backoff = TimeSpan.FromSeconds(1);
    private volatile DiscordConnectionState _state = DiscordConnectionState.Disconnected;
    private bool _disposed;

    public DiscordPresenceService(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
    }

    public DiscordConnectionState State => _state;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.DiscordClientId);

    public event Action? StateChanged;

    /// <summary>
    /// Publishes <paramref name="activity"/>, or clears the presence when it is null.
    /// Returns false when Discord is not reachable right now (the caller should retry).
    /// </summary>
    /// <param name="urgent">
    /// El contenido visible cambió (pista, reproductor o portada): se envía en cuanto la
    /// ventana de 5 actualizaciones / 20 s lo permite, sin esperar MinUpdateIntervalMs.
    /// </param>
    /// <param name="superseded">
    /// Se cancela cuando llega una actividad más nueva mientras esta espera hueco en el
    /// límite de Discord. En ese caso la actual se descarta sin gastar una actualización:
    /// solo importa la última (saltar canciones rápido no debe encolar estados viejos).
    /// </param>
    public async Task<bool> PublishAsync(
        DiscordActivity? activity,
        CancellationToken cancellationToken,
        PublishPriority priority = PublishPriority.Background,
        CancellationToken superseded = default)
    {
        if (!IsConfigured)
        {
            SetState(DiscordConnectionState.Disabled);
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (activity is null)
            {
                // Sin conexión no hay nada que limpiar: Discord borra la actividad al
                // cerrarse el pipe. Con conexión, un fallo sí debe reintentarse (antes se
                // daba por bueno y la canción anterior se quedaba fija).
                if (_client is not { IsConnected: true })
                {
                    return true;
                }

                try
                {
                    await _client.SetActivityAsync(null, cancellationToken).ConfigureAwait(false);
                    RecordSend();
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"No se pudo limpiar la actividad: {ex.Message}");
                    await DropClientAsync().ConfigureAwait(false);
                    SetState(DiscordConnectionState.Disconnected);
                    ScheduleRetry();
                    return false;
                }
            }

            if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            // Un cambio de pista / reproductor / portada sale al instante si queda hueco en
            // la ventana de Discord; el latido y el reanclaje de pausa respetan
            // MinUpdateIntervalMs. La espera usa un token aparte: si mientras tanto llega
            // una canción nueva, se abandona esta sin escribir nada en el pipe (nunca a
            // medias) y el bucle de publicación manda la última.
            using var slotCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                superseded);

            try
            {
                await WaitForSendSlotAsync(priority, slotCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Debug("Update discarded: a newer one arrived before it was sent.");
                return false;
            }

            // La actividad se construyó al leer SMTC. Si hubo espera, se desplazan los
            // timestamps absolutos para que el reloj de Discord coincida con el audio.
            ShiftTimestampsToNow(activity);

            try
            {
                await _client!.SetActivityAsync(activity, cancellationToken).ConfigureAwait(false);
                RecordSend();
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"No se pudo actualizar la actividad de Discord: {ex.Message}");
                await DropClientAsync().ConfigureAwait(false);
                SetState(DiscordConnectionState.Disconnected);
                ScheduleRetry();
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"Error hablando con Discord: {ex.Message}");
            ScheduleRetry();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Espera hasta que Discord acepte otra actualización: ventana deslizante de 20 s con
    /// 5 huecos. El contenido visible puede usar los cinco; el progreso y los envíos de
    /// fondo dejan siempre un par libres, así que un cambio de canción nunca se queda
    /// esperando detrás de una resincronización de la barra.
    /// </summary>
    private async Task WaitForSendSlotAsync(PublishPriority priority, CancellationToken cancellationToken)
    {
        var policy = PolicyFor(priority, _options.MinUpdateIntervalMs);

        while (true)
        {
            TimeSpan wait;

            lock (_recentSends)
            {
                var now = DateTimeOffset.UtcNow;

                while (_recentSends.Count > 0 && now - _recentSends.Peek() >= SendWindow)
                {
                    _recentSends.Dequeue();
                }

                if (_recentSends.Count >= policy.Limit)
                {
                    wait = _recentSends.Peek() + SendWindow - now;
                }
                else if (policy.Gap > TimeSpan.Zero
                    && _lastSentAt != DateTimeOffset.MinValue
                    && now - _lastSentAt < policy.Gap)
                {
                    wait = policy.Gap - (now - _lastSentAt);
                }
                else
                {
                    wait = TimeSpan.Zero;
                }
            }

            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Huecos de la ventana de Discord y separación mínima para un tipo de envío.</summary>
    internal readonly record struct SendPolicy(int Limit, TimeSpan Gap);

    /// <summary>
    /// Reparto del límite de Discord (5 actualizaciones / 20 s). El contenido usa los cinco
    /// huecos con un suelo de medio segundo; el progreso y el fondo reservan dos huecos
    /// para que un cambio de pista encuentre sitio al instante.
    /// </summary>
    internal static SendPolicy PolicyFor(PublishPriority priority, int minUpdateIntervalMs) => priority switch
    {
        PublishPriority.Content => new SendPolicy(
            MaxSendsPerWindow,
            TimeSpan.FromMilliseconds(500)),
        PublishPriority.Progress => new SendPolicy(
            Math.Max(1, MaxSendsPerWindow - 2),
            TimeSpan.FromMilliseconds(1500)),
        _ => new SendPolicy(
            Math.Max(1, MaxSendsPerWindow - 2),
            TimeSpan.FromMilliseconds(Math.Max(1000, minUpdateIntervalMs))),
    };

    private void RecordSend()
    {
        _lastSentAt = DateTimeOffset.UtcNow;

        lock (_recentSends)
        {
            _recentSends.Enqueue(_lastSentAt);
        }
    }

    private static void ShiftTimestampsToNow(DiscordActivity activity)
    {
        // Los timestamps de una pista en reproducción son absolutos: Discord avanza el
        // reloj solo. Desplazarlos por el tiempo de espera dejaría la barra exactamente
        // ese tiempo por detrás del audio (el desfase al saltar canciones). Solo el
        // anclaje congelado de pausa necesita reenviarse relativo al momento del envío.
        if (!activity.FrozenTimestamps || activity.Timestamps is not { } timestamps)
        {
            return;
        }

        var drift = (long)(DateTimeOffset.UtcNow - activity.BuiltAt).TotalMilliseconds;
        if (drift <= 0)
        {
            return;
        }

        if (timestamps.Start is long start)
        {
            timestamps.Start = start + drift;
        }

        if (timestamps.End is long end)
        {
            timestamps.End = end + drift;
        }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
        {
            return true;
        }

        if (_client is not null)
        {
            await DropClientAsync().ConfigureAwait(false);
        }

        DateTimeOffset nextAttemptAt;
        lock (_retryGate)
        {
            nextAttemptAt = _nextAttemptAt;
        }
        if (DateTimeOffset.UtcNow < nextAttemptAt)
        {
            return false;
        }

        SetState(DiscordConnectionState.Connecting);

        var client = await DiscordIpcClient.ConnectAsync(
            _options.DiscordClientId,
            TimeSpan.FromSeconds(5),
            _log,
            cancellationToken).ConfigureAwait(false);

        if (client is null)
        {
            SetState(DiscordConnectionState.Disconnected);
            ScheduleRetry();
            _log.Debug($"Discord no disponible; reintento en {_backoff.TotalSeconds:0} s.");
            return false;
        }

        client.Closed += OnClientClosed;
        _client = client;
        lock (_retryGate)
        {
            _backoff = TimeSpan.FromSeconds(1);
            _nextAttemptAt = DateTimeOffset.MinValue;
        }
        SetState(DiscordConnectionState.Connected);
        _log.Info("Conectado a Discord (RPC por IPC).");
        return true;
    }

    private void ScheduleRetry()
    {
        lock (_retryGate)
        {
            _nextAttemptAt = DateTimeOffset.UtcNow + _backoff;
            _backoff = TimeSpan.FromSeconds(Math.Min(_backoff.TotalSeconds * 2, 30));
        }
    }

    private void OnClientClosed(DiscordIpcClient client)
    {
        if (!ReferenceEquals(client, _client))
        {
            return;
        }

        _log.Info("Discord cerró la conexión; se reintentará automáticamente.");
        SetState(DiscordConnectionState.Disconnected);
        lock (_retryGate)
        {
            _nextAttemptAt = DateTimeOffset.UtcNow;
            _backoff = TimeSpan.FromSeconds(1);
        }
    }

    private async Task DropClientAsync()
    {
        var client = _client;
        _client = null;

        if (client is null)
        {
            return;
        }

        client.Closed -= OnClientClosed;

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private void SetState(DiscordConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_client is { IsConnected: true })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _client.SetActivityAsync(null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Closing the pipe clears the presence anyway.
        }

        await DropClientAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
