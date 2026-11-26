using System.Collections.Concurrent;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using UniversalPresence.Media;

namespace UniversalPresence.Core;

/// <summary>
/// Estado de la pista: identidad, seeks, reanudación, contador de repeticiones, hueco de
/// metadatos al cambiar de canción y artistas invitados de Deezer.
/// </summary>
public sealed partial class PresenceEngine : IAsyncDisposable, IDisposable
{
    /// <summary>Tiempo máximo para confirmar que una canción volvió de verdad al principio.</summary>
    private static readonly TimeSpan RepeatConfirmationWindow = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ArtistDisplayNegativeTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, ArtistDisplayEntry> _artistDisplayCache = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _artistLookupsInFlight = new(StringComparer.Ordinal);
    // Seeks detectados por SMTC y último reenvío de la red de seguridad por deriva.
    private long _seenSeekRevision;

    /// <summary>Desde cuándo la sesión activa se queda sin metadatos a mitad de cambio de pista.</summary>
    private DateTimeOffset _metadataGapSince = DateTimeOffset.MinValue;

    private int _playCount;
    // Confirmación de repetición: una sola lectura con la posición en 0 no basta, porque
    // mover la barra hacia atrás (o un fallo momentáneo del reproductor) también lo parece.
    private string _repeatCandidateKey = "";

    private DateTimeOffset _repeatCandidateAt = DateTimeOffset.MinValue;

    private TimeSpan _repeatCandidatePosition;

    /// <summary>
    /// Spotify solo entrega el artista principal en SMTC. Se completa la línea de artista
    /// con la lista de Deezer sin tocar los campos de identidad (historial, portada,
    /// de-duplicación), que siguen usando el artista reportado por Windows.
    /// </summary>
    private MediaSnapshot? ApplyArtistDisplay(MediaSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsActive || !IsSpotify(snapshot))
        {
            return snapshot;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ArtistDisplay))
        {
            return snapshot;
        }

        var key = TrackKey(snapshot);
        if (_artistDisplayCache.TryGetValue(key, out var cached))
        {
            if (!string.IsNullOrWhiteSpace(cached.Display))
            {
                return snapshot with { ArtistDisplay = cached.Display };
            }

            // Una respuesta negativa se respeta un rato, pero no para siempre: un fallo
            // puntual de Deezer no debe dejar la pista sin artistas invitados hasta
            // reiniciar la aplicación.
            if (DateTimeOffset.UtcNow - cached.At < ArtistDisplayNegativeTtl)
            {
                return snapshot;
            }

            _artistDisplayCache.TryRemove(key, out _);
        }

        if (_artistLookupsInFlight.TryAdd(key, 0))
        {
            _ = Task.Run(() => ResolveArtistDisplayAsync(snapshot, key));
        }

        return snapshot;
    }

    private async Task ResolveArtistDisplayAsync(MediaSnapshot snapshot, string key)
    {
        try
        {
            var display = await _artistLookup
                .ResolveAsync(snapshot.Title, snapshot.Artist, _cts.Token)
                .ConfigureAwait(false);

            if (_artistDisplayCache.Count >= 256)
            {
                _artistDisplayCache.Clear();
            }

            _artistDisplayCache[key] = new ArtistDisplayEntry(display ?? "", DateTimeOffset.UtcNow);

            if (string.IsNullOrWhiteSpace(display))
            {
                return;
            }

            // Si mientras tanto empezó otra canción, el resultado se guarda para esa
            // pista pero no se republica la que ya no suena.
            if (_lastSnapshot is not null
                && string.Equals(TrackKey(_lastSnapshot), key, StringComparison.Ordinal))
            {
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
            _log.Debug($"No se pudieron completar los artistas de la pista: {ex.Message}");
        }
        finally
        {
            _artistLookupsInFlight.TryRemove(key, out _);
        }
    }

    private static bool IsSpotify(MediaSnapshot snapshot) =>
        snapshot.PlayerName.Contains("Spotify", StringComparison.OrdinalIgnoreCase)
        || snapshot.AppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase);

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
            && (previous.Duration <= TimeSpan.Zero
                || current.Duration <= TimeSpan.Zero
                || (previous.Duration - current.Duration).Duration() <= TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Detecta el instante en que un reproductor cambia de pista pero todavía no ha
    /// publicado título/artista (durante unos milisegundos la sesión queda vacía).
    /// Se mantiene la presencia anterior un máximo de 1 s para no enseñar un hueco.
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

        return DateTimeOffset.UtcNow - _metadataGapSince < TimeSpan.FromMilliseconds(1000);
    }

    /// <summary>
    /// Mantiene el contador de reproducciones consecutivas de la misma canción.
    ///
    /// Una repetición se cuenta cuando la pista vuelve al principio después de haber sonado
    /// de verdad, y solo tras confirmarla en la lectura siguiente: así mover la barra hacia
    /// atrás, un salto de posición o un dato momentáneo en 0:00 no suman una reproducción
    /// que nunca ocurrió (el bug de las canciones que solo habían sonado una vez).
    /// </summary>
    private void UpdatePlayCount(MediaSnapshot? previous, MediaSnapshot? current, bool seeked)
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
            if (seeked)
            {
                // Arrastrar la barra hasta el principio no es una reproducción nueva:
                // el seek lo confirma SMTC y no debe inflar el contador «×N».
                ClearRepeatCandidate();
                return;
            }

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

    private sealed record ArtistDisplayEntry(string Display, DateTimeOffset At);
}
