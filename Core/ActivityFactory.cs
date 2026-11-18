using UniversalPresence.Configuration;
using UniversalPresence.Discord;
using UniversalPresence.Localization;
using UniversalPresence.Media;

namespace UniversalPresence.Core;

/// <summary>
/// Builds the Discord activity from an SMTC snapshot.
///
/// Layout (as agreed with the product spec):
///   details    = song title        (line 1)
///   state      = artist            (line 2)
///   large_text = album, else artist
///   large_image= cover URL, else uploaded fallback asset (Discord's app icon when empty)
///   small_text = real player/process name
///   timestamps = start = now - position, end = now + (duration - position)
/// </summary>
public sealed class ActivityFactory
{
    private const int MaxFieldLength = 128;

    // Botones de la actividad: quien ve el perfil puede descargar la app o entender qué es.
    private const string DownloadUrl =
        "https://github.com/itsmilespr/UniversalPresence/releases/latest";

    private const string HowItWorksUrl =
        "https://itsmilespr.github.io/UniversalPresence/#como-funciona";

    private readonly AppOptions _options;

    public ActivityFactory(AppOptions options)
    {
        _options = options;
    }

    public DiscordActivity? Create(MediaSnapshot snapshot)
    {
        var status = snapshot.Status;
        var text = PresenceText.For(_options.Language);

        if (status == PlaybackState.Stopped && _options.TreatStoppedAsPaused)
        {
            status = PlaybackState.Paused;
        }

        if (status == PlaybackState.Paused && !_options.ShowPausedMedia)
        {
            return null;
        }

        if (status is not (PlaybackState.Playing or PlaybackState.Paused))
        {
            return null;
        }

        var artist = snapshot.Artist;
        var album = _options.ShowAlbum ? snapshot.Album : "";
        var player = snapshot.PlayerName;

        var largeImage = ResolveLargeImage(snapshot);
        if (_options.HideIfNoArtwork && string.IsNullOrWhiteSpace(largeImage))
        {
            return null;
        }

        // Layout tipo Music Presence:
        //   Details   = Título de la canción (línea principal)
        //   State     = Artista (se muestra en el member list como "Listening to <artista>")
        //   LargeText = Álbum (tooltip al pasar el mouse sobre la portada)
        //   SmallImage = Icono del reproductor (si está disponible)
        //   SmallText = Nombre del reproductor + contador de repeticiones

        var detailsValue = !string.IsNullOrWhiteSpace(_options.DetailsTemplate)
            ? ApplyTemplate(_options.DetailsTemplate, snapshot, status, text)
            : FirstNonEmpty(snapshot.Title, artist, player, text.Playing);

        var stateValue = !string.IsNullOrWhiteSpace(_options.StateTemplate)
            ? ApplyTemplate(_options.StateTemplate, snapshot, status, text)
            : FirstNonEmpty(artist, album, player);

        if (_options.AppendPlayerToState
            && !string.IsNullOrWhiteSpace(player)
            && !string.Equals(player, artist, StringComparison.OrdinalIgnoreCase))
        {
            stateValue = string.IsNullOrWhiteSpace(stateValue) ? player : $"{stateValue} · {player}";
        }

        // Conteo de reproducciones consecutivas (solo a partir de la 2ª).
        if (snapshot.PlayCount >= 2)
        {
            var counter = $"×{snapshot.PlayCount}";
            stateValue = string.IsNullOrWhiteSpace(stateValue) ? counter : $"{stateValue} · {counter}";
        }

        // Si hay álbum, mostrarlo con prefijo "En" para que quede más bonito en el tooltip
        string largeText;
        if (!string.IsNullOrWhiteSpace(_options.LargeTextTemplate))
        {
            largeText = ApplyTemplate(_options.LargeTextTemplate, snapshot, status, text);
        }
        else
        {
            largeText = !string.IsNullOrWhiteSpace(album) && _options.ShowAlbum
                ? string.Format(text.InAlbum, album)
                : artist;
        }

        var smallText = ResolveSmallText(status, player, text);

        var activityName = _options.UsePlayerNameAsActivityName && !string.IsNullOrWhiteSpace(player)
            ? Clip(player)
            : "Universal Presence";

        return new DiscordActivity
        {
            Name = activityName,
            Type = MapType(_options.ActivityType),

            // Ancla de construcción: el servicio de Discord desplaza start/end hasta el
            // momento exacto del envío si tuvo que esperar por el límite de updates.
            BuiltAt = snapshot.CapturedAt,

            // Solo la pausa necesita ese desplazamiento; en reproducción el reloj de
            // Discord avanza por su cuenta y reanclar la barra la retrasaría.
            FrozenTimestamps = status == PlaybackState.Paused,

            // 1 = STATE: the member list shows "Listening to <artist>" instead of the
            // application name. 0 = NAME, 2 = DETAILS.
            StatusDisplayType = 1,
            Details = Clip(detailsValue),
            State = ClipNullable(stateValue),
            Timestamps = BuildTimestamps(snapshot, status),
            Buttons = new List<DiscordButton>
            {
                new() { Label = text.Download, Url = DownloadUrl },
                new() { Label = text.HowItWorks, Url = HowItWorksUrl },
            },
            Assets = new DiscordAssets
            {
                LargeImage = largeImage,
                LargeText = ClipNullable(largeText),
                SmallImage = ResolveSmallImage(status, player, snapshot.PlayerExecutable, snapshot.ProgramIconUrl),
                SmallText = ClipNullable(smallText),
            },
        };
    }

    /// <summary>
    /// Discord renders its own clock from an absolute start/end pair; there is no
    /// percentage field. Both values are Unix epoch milliseconds:
    ///   start = now - position
    ///   end   = now + (duration - position)
    /// While paused the engine re-anchors this pair every PauseRefreshSeconds so the
    /// client-side bar stays visually pinned at the paused position.
    /// </summary>
    private DiscordTimestamps? BuildTimestamps(MediaSnapshot snapshot, PlaybackState status)
    {
        if (!_options.ShowProgress || status is not (PlaybackState.Playing or PlaybackState.Paused))
        {
            return null;
        }

        var now = snapshot.CapturedAt.ToUnixTimeMilliseconds();
        var position = (long)snapshot.Position.TotalMilliseconds;
        var duration = (long)snapshot.Duration.TotalMilliseconds;

        // Evitar timestamps negativos.
        if (position < 0) position = 0;
        if (duration < 0) duration = 0;
        if (now < 0) now = 0;

        if (status == PlaybackState.Paused && duration > 0)
        {
            if (!_options.PauseResetProgress)
            {
                // Barra congelada en la posición de pausa (no en 0:00): el par se ancla al
                // instante actual y el motor lo reancla cada PauseRefreshSeconds para que
                // Discord no siga avanzando el reloj.
                return new DiscordTimestamps
                {
                    Start = now - position,
                    End = now + (duration - position),
                };
            }

            // Freeze the progress bar at 0:00 while paused (Discord cannot freeze at
            // an arbitrary position, so 0:00 is the closest we can get).
            // The engine re-anchors this pair on every PauseRefreshSeconds tick.
            var frozenStart = now + duration;
            return new DiscordTimestamps { Start = frozenStart, End = frozenStart + duration };
        }

        var start = now - position;
        var timestamps = new DiscordTimestamps { Start = start };

        if (duration > 0)
        {
            timestamps.End = now + (duration - position);
        }

        return timestamps;
    }

    /// <summary>
    /// Small badge priority: pause icon while paused, then the UniversalPresence app icon,
    /// then the legacy per-player badge, then the program icon of the player.
    /// </summary>
    private string? ResolveSmallImage(PlaybackState status, string player, string? executablePath, string? programIconUrl)
    {
        // 1️⃣  Icono de pausa (si corresponde)
        if (status == PlaybackState.Paused
            && _options.ShowPausedIcon
            && !string.IsNullOrWhiteSpace(_options.PauseAssetKey))
        {
            return _options.PauseAssetKey;
        }

        // 2️⃣  Icono del programa que está reproduciendo (si está subido a un host público).
        //     En algunas versiones de Discord IPC se renderiza; en otras se muestra placeholder.
        if (string.IsNullOrWhiteSpace(_options.AppIconAssetKey) && !string.IsNullOrWhiteSpace(programIconUrl))
        {
            return programIconUrl;
        }

        // 3️⃣  Fallback: icono de la propia aplicación o del badge del jugador
        if (!string.IsNullOrWhiteSpace(_options.AppIconAssetKey))
        {
            return _options.AppIconAssetKey;
        }

        return NullIfEmpty(_options.PlayerAssetKey);
    }

    private string? ResolveSmallText(PlaybackState status, string player, PresenceText text)
    {
        if (status == PlaybackState.Paused
            && _options.ShowPausedIcon
            && !string.IsNullOrWhiteSpace(_options.PauseAssetKey))
        {
            return text.Paused;
        }

        if (!string.IsNullOrWhiteSpace(_options.AppIconAssetKey))
        {
            return "UniversalPresence";
        }

        return ClipNullable(player);
    }

    private string? ResolveLargeImage(MediaSnapshot snapshot)
    {
        var artwork = _options.Artwork;

        if (artwork.IsDisabled)
        {
            return null;
        }

        // 1. The real SMTC cover, served from the user's public HTTPS host.
        if (artwork.UsesExternalUrl && snapshot.Artwork is not null)
        {
            return $"{artwork.ExternalUrlBase}/{snapshot.Artwork.FileName}";
        }

        // 2. A public cover: the uploaded SMTC artwork or a catalogue match. Discord can only
        //    fetch public URLs, so both end up as https links resolved by the engine.
        if (artwork.ResolvesArtwork && !string.IsNullOrWhiteSpace(snapshot.PublicArtworkUrl))
        {
            return snapshot.PublicArtworkUrl;
        }

        // 3. Uploaded asset key; empty lets Discord fall back to the application icon.
        return NullIfEmpty(artwork.FallbackAssetKey);
    }

    private string ApplyTemplate(string template, MediaSnapshot snapshot, PlaybackState status, PresenceText text)
    {
        var result = template;
        result = result.Replace("{title}", snapshot.Title ?? "", StringComparison.Ordinal);
        result = result.Replace("{artist}", snapshot.Artist ?? "", StringComparison.Ordinal);
        result = result.Replace("{album}", snapshot.Album ?? "", StringComparison.Ordinal);
        result = result.Replace("{player}", snapshot.PlayerName ?? "", StringComparison.Ordinal);
        result = result.Replace("{status}", status.ToString(), StringComparison.Ordinal);
        return result;
    }

    private static int MapType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "playing" => 0,
        "watching" => 3,
        "competing" => 5,
        _ => 2,
    };

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ClipNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : Clip(value);

    private static string Clip(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxFieldLength ? trimmed : trimmed[..(MaxFieldLength - 1)] + "…";
    }
}
