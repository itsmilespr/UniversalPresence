using UniversalPresence.Configuration;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;
using UniversalPresence.Media;

namespace UniversalPresence.Core;

/// <summary>
/// Historial de escucha y Last.fm: cada pista se registra una sola vez, y se hace scrobble
/// de la anterior cuando empieza una nueva, siempre que cumpla la regla de Last.fm.
/// </summary>
public sealed partial class PresenceEngine : IAsyncDisposable, IDisposable
{
    // Última pista registrada en el historial / Last.fm: pausar o reanudar la misma
    // canción no debe contarla como otra reproducción.
    private string? _lastLoggedKey;

    // Datos de la pista anterior, para scrobblearla cuando empieza la siguiente.
    private string _prevScrobbleTitle = "";
    private string _prevScrobbleArtist = "";
    private string _prevScrobbleAlbum = "";
    private DateTimeOffset _prevScrobbleAt = DateTimeOffset.MinValue;
    private TimeSpan _prevScrobbleDuration;

    // Tiempo reproducido de esa pista: es lo que decide si el scrobble cumple la regla de
    // Last.fm (mitad de la duración o 4 minutos), así que se acumula vuelta a vuelta.
    private TimeSpan _prevScrobbleListened;
    private DateTimeOffset _lastListenedTickAt = DateTimeOffset.MinValue;

    /// <summary>Un salto mayor que esto es una suspensión del equipo, no música escuchada.</summary>
    private static readonly TimeSpan MaxListenedGap = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Registra en el historial la pista que acaba de empezar, cierra el scrobble de la
    /// anterior en Last.fm y actualiza «escuchando ahora». Se llama en cada vuelta del bucle;
    /// la clave evita que pausar y reanudar la misma canción cuente dos veces.
    /// </summary>
    private void RecordListening(MediaSnapshot? snapshot, DiscordActivity? activity)
    {
        AccumulateListened(snapshot);

        // Se compara por identidad de pista (sin el estado): pausar o reanudar la misma
        // canción no cuenta como otra reproducción ni la vuelve a scrobblear en Last.fm.
        var historyKey = snapshot is null
            ? ""
            : $"{TrackKey(snapshot)}|{snapshot.PlayCount}";

        if (!_options.ListeningHistory
            || snapshot is not { Status: PlaybackState.Playing }
            || activity is null
            || string.Equals(historyKey, _lastLoggedKey, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _history.Record(
                snapshot.Title,
                snapshot.Artist,
                snapshot.Album,
                snapshot.PlayerName,
                DateTimeOffset.UtcNow,
                (long)snapshot.Duration.TotalMilliseconds);
        }
        catch { }

        // Scrobble de la pista anterior, ya con el tiempo que sonó de verdad: la regla de
        // Last.fm decide dentro del servicio si merece la pena enviarlo.
        var previousTitle = _prevScrobbleTitle;
        var previousArtist = _prevScrobbleArtist;
        var previousAlbum = _prevScrobbleAlbum;
        var previousStart = _prevScrobbleAt;
        var previousDuration = _prevScrobbleDuration;
        var previousListened = _prevScrobbleListened;

        if (_lastfm.IsEnabled && !string.IsNullOrWhiteSpace(previousTitle))
        {
            _ = Task.Run(() => _lastfm.ScrobbleAsync(
                previousTitle,
                previousArtist,
                previousAlbum,
                previousStart,
                previousDuration,
                previousListened,
                CancellationToken.None));
        }

        // «Escuchando ahora» para el perfil de Last.fm.
        if (_lastfm.IsEnabled && _options.LastFmNowPlaying)
        {
            var title = snapshot.Title;
            var artist = snapshot.Artist;
            var album = snapshot.Album;
            var durationSeconds = (long)snapshot.Duration.TotalSeconds;

            _ = Task.Run(() => _lastfm.UpdateNowPlayingAsync(
                title, artist, album, durationSeconds, CancellationToken.None));
        }

        _prevScrobbleTitle = snapshot.Title;
        _prevScrobbleArtist = snapshot.Artist;
        _prevScrobbleAlbum = snapshot.Album;
        _prevScrobbleAt = DateTimeOffset.UtcNow;
        _prevScrobbleDuration = snapshot.Duration;
        _prevScrobbleListened = TimeSpan.Zero;
        _lastListenedTickAt = _prevScrobbleAt;
        _lastLoggedKey = historyKey;
    }

    /// <summary>
    /// Suma a la pista en curso el tiempo transcurrido desde la vuelta anterior del bucle.
    /// Se llama siempre, antes de las salidas tempranas de <see cref="RecordListening"/>,
    /// porque el scrobble depende de cuánto se ha escuchado y no de cuándo empezó.
    /// </summary>
    private void AccumulateListened(MediaSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(_prevScrobbleTitle)
            || snapshot is not { Status: PlaybackState.Playing })
        {
            // Pausa o reproductor cerrado: ese hueco no cuenta como escucha.
            _lastListenedTickAt = DateTimeOffset.MinValue;
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (_lastListenedTickAt != DateTimeOffset.MinValue)
        {
            var elapsed = now - _lastListenedTickAt;
            if (elapsed > TimeSpan.Zero && elapsed <= MaxListenedGap)
            {
                _prevScrobbleListened += elapsed;
            }
        }

        _lastListenedTickAt = now;
    }
}
