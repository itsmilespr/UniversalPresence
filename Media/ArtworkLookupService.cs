using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Media;

/// <summary>
/// Resolves a publicly reachable cover URL from the track metadata.
///
/// Why this exists: Discord resolves activity images from its own side and refuses
/// loopback/local images and base64 payloads, so an SMTC thumbnail cannot be sent
/// directly. When the user has no public HTTPS host for the extracted cover
/// (Artwork.ExternalUrlBase), this uses public catalogues to obtain an https artwork URL
/// that Discord can fetch.
///
/// La cadena de respaldo es escalonada a propósito:
///   1. iTunes y Deezer en paralelo. Apple es rápido con lanzamientos comerciales y Deezer
///      tiene mucho más catálogo urbano latino (singles que Apple publica tarde o nunca).
///   2. MusicBrainz + Cover Art Archive, cuando ninguno de los dos anteriores acierta.
///      Cubre sellos independientes, ediciones físicas y discografías antiguas.
///   3. Last.fm, solo si el usuario configuró su API key. Es el último recurso porque sus
///      portadas son de menor resolución y dependen de una cuenta.
///
/// It sends only title/artist/album, caches results for hours, and is disabled with
/// "Artwork": { "Mode": "None" } or "DiscordAsset".
/// </summary>
public sealed class ArtworkLookupService
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(1500);

    private static readonly HttpClient Http = CreateClient();

    /// <summary>MusicBrainz y Cover Art Archive piden un User-Agent identificable.</summary>
    private static readonly HttpClient CatalogHttp = CreateCatalogClient();

    private const string LastFmPlaceholder = "2a96cbd8b46e442fc41c2b86b821562f";

    private readonly AppOptions _options;
    private readonly Log _log;

    // La portada encontrada se guarda en disco: la misma canción ya no vuelve a consultar
    // el catálogo, ni siquiera en la siguiente ejecución.
    private readonly PersistentCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    /// <summary>Serializa y espacia las peticiones a MusicBrainz / Cover Art Archive.</summary>
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static DateTimeOffset _lastCatalogRequest = DateTimeOffset.MinValue;

    public ArtworkLookupService(AppOptions options, Log log, string cacheFilePath)
    {
        _options = options;
        _log = log;
        _cache = new PersistentCache(cacheFilePath, log);
    }

    /// <summary>
    /// Olvida la portada cacheada de una canción. Se usa cuando el usuario pide «buscar
    /// portada de nuevo»: sin esto, la respuesta anterior (incluido un fallo de 10 minutos)
    /// seguiría sirviéndose desde disco.
    /// </summary>
    public void Forget(string title, string artist, string album)
    {
        _cache.Remove($"{title}\u001f{artist}\u001f{album}".ToLowerInvariant());
    }

    public async Task<string?> ResolveAsync(
        string title,
        string artist,
        string album,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var key = $"{title}\u001f{artist}\u001f{album}".ToLowerInvariant();

        var cached = _cache.Get(key);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cachedAgain = _cache.Get(key);
            if (!string.IsNullOrWhiteSpace(cachedAgain))
            {
                return cachedAgain;
            }

            var wait = _lastRequestAt + MinimumInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            var url = await SearchAsync(title, artist, album, cancellationToken).ConfigureAwait(false);
            _lastRequestAt = DateTimeOffset.UtcNow;

            _cache.Set(key, url);
            return url;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Búsqueda de portada falló: {ex.Message}");
            _cache.Set(key, null);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> SearchAsync(
        string title,
        string artist,
        string album,
        CancellationToken cancellationToken)
    {
        // Se consultan los dos catálogos a la vez: Apple suele responder muy rápido y
        // con portadas de 1000 px, mientras que Deezer tiene mucho catálogo urbano
        // latino que Apple publica tarde o no publica (p. ej. «Na' Nuevo», «MACDONALDS»).
        var itunes = SearchItunesAsync(title, artist, album, cancellationToken);
        var deezer = SearchDeezerAsync(title, artist, cancellationToken);

        var found = await Task.WhenAll(itunes, deezer).ConfigureAwait(false);
        var direct = found.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        // Segunda capa: catálogos de metadatos. Solo se consultan cuando las dos tiendas
        // no tienen nada, así que no añaden latencia al caso habitual.
        var archive = await SearchMusicBrainzAsync(title, artist, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(archive))
        {
            return archive;
        }

        return await SearchLastFmAsync(title, artist, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// MusicBrainz (metadatos) + Cover Art Archive (imágenes). Se acepta el primer
    /// release-group con imagen frontal, con un máximo de tres candidatos.
    /// </summary>
    private async Task<string?> SearchMusicBrainzAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        try
        {
            await ThrottleCatalogAsync(cancellationToken).ConfigureAwait(false);

            var query = Uri.EscapeDataString($"recording:\"{title}\" AND artist:\"{artist}\"");
            var uri = $"https://musicbrainz.org/ws/2/recording?query={query}&fmt=json&limit=5";

            using var response = await CatalogHttp.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Debug($"MusicBrainz respondió {(int)response.StatusCode}.");
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("recordings", out var recordings)
                || recordings.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var groups = new List<string>();
            foreach (var recording in recordings.EnumerateArray())
            {
                if (!recording.TryGetProperty("releases", out var releases)
                    || releases.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var release in releases.EnumerateArray())
                {
                    if (!release.TryGetProperty("release-group", out var group)
                        || group.ValueKind != JsonValueKind.Object
                        || !group.TryGetProperty("id", out var idElement)
                        || idElement.GetString() is not { Length: > 0 } id)
                    {
                        continue;
                    }

                    if (!groups.Contains(id, StringComparer.Ordinal))
                    {
                        groups.Add(id);
                    }

                    if (groups.Count >= 3)
                    {
                        break;
                    }
                }

                if (groups.Count >= 3)
                {
                    break;
                }
            }

            foreach (var groupId in groups)
            {
                var cover = await CoverArtArchiveAsync(groupId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(cover))
                {
                    _log.Debug($"Portada pública encontrada (Cover Art Archive): {cover}");
                    return cover;
                }
            }

            _log.Debug($"MusicBrainz no tiene portada para '{artist} - {title}'.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Búsqueda en MusicBrainz fallida: {ex.Message}");
            return null;
        }
    }

    /// <summary>Imagen frontal de un release-group en Cover Art Archive, o null.</summary>
    private async Task<string?> CoverArtArchiveAsync(string releaseGroupId, CancellationToken cancellationToken)
    {
        try
        {
            await ThrottleCatalogAsync(cancellationToken).ConfigureAwait(false);

            var uri = $"https://coverartarchive.org/release-group/{Uri.EscapeDataString(releaseGroupId)}";
            using var response = await CatalogHttp.GetAsync(uri, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("images", out var images)
                || images.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var image in images.EnumerateArray())
            {
                // Se prefiere la frontal; si el catálogo no la marca, vale la primera.
                var isFront = image.TryGetProperty("front", out var front)
                    && front.ValueKind == JsonValueKind.True;

                if (!isFront && images.GetArrayLength() > 1)
                {
                    continue;
                }

                // La miniatura de 500 px es la que se quiere: la imagen original de
                // archive.org puede pesar varios megas y Discord la descarga cada vez
                // que cambia la canción.
                var url = Thumbnail(image, "500") ?? Thumbnail(image, "large");

                if (url is null
                    && image.TryGetProperty("image", out var urlElement)
                    && urlElement.GetString() is { Length: > 0 } full
                    && full.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = full;
                }

                if (url is not null)
                {
                    return url;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Cover Art Archive falló: {ex.Message}");
            return null;
        }
    }

    /// <summary>Miniatura concreta del objeto <c>thumbnails</c>, si el catálogo la publica.</summary>
    private static string? Thumbnail(JsonElement image, string size)
    {
        if (image.TryGetProperty("thumbnails", out var thumbnails)
            && thumbnails.ValueKind == JsonValueKind.Object
            && thumbnails.TryGetProperty(size, out var element)
            && element.GetString() is { Length: > 0 } url
            && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return null;
    }

    /// <summary>
    /// MusicBrainz pide como mucho una petición por segundo y por IP; sin esta espera puede
    /// responder 503 durante un rato y dejar la búsqueda de portada sin catálogo.
    /// </summary>
    private static async Task ThrottleCatalogAsync(CancellationToken cancellationToken)
    {
        await CatalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var wait = _lastCatalogRequest + TimeSpan.FromMilliseconds(1100) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _lastCatalogRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            CatalogGate.Release();
        }
    }

    /// <summary>Último recurso: la portada del álbum según Last.fm (requiere API key).</summary>
    private async Task<string?> SearchLastFmAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        var apiKey = _options.LastFmApiKey?.Trim() ?? "";

        if (apiKey.Length == 0)
        {
            return null;
        }

        try
        {
            var uri = "https://ws.audioscrobbler.com/2.0/" +
                $"?method=track.getInfo&autocorrect=1&format=json&api_key={Uri.EscapeDataString(apiKey)}" +
                $"&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}";

            using var response = await CatalogHttp.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Debug($"Last.fm respondió {(int)response.StatusCode} al buscar portada.");
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("track", out var track)
                || !track.TryGetProperty("album", out var albumElement)
                || !albumElement.TryGetProperty("image", out var images)
                || images.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? best = null;
            foreach (var image in images.EnumerateArray())
            {
                if (!image.TryGetProperty("#text", out var text)
                    || text.GetString() is not { Length: > 0 } url
                    || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || url.Contains(LastFmPlaceholder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var size = image.TryGetProperty("size", out var sizeElement) ? sizeElement.GetString() : null;

                // El orden del array no está garantizado: se guarda la mejor vista.
                if (best is null || size is "mega" or "extralarge")
                {
                    best = url;
                }

                if (size == "mega")
                {
                    break;
                }
            }

            if (best is not null)
            {
                _log.Debug($"Portada pública encontrada (Last.fm): {best}");
            }

            return best;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Búsqueda en Last.fm fallida: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> SearchItunesAsync(
        string title,
        string artist,
        string album,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SearchItunesCoreAsync(title, artist, album, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Búsqueda en iTunes fallida: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> SearchDeezerAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        try
        {
            var term = Uri.EscapeDataString($"{artist} {title}".Trim());
            var uri = $"https://api.deezer.com/search?q={term}&limit=10";

            using var response = await Http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Debug($"Deezer respondió {(int)response.StatusCode}.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("data", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in results.EnumerateArray())
            {
                var trackName = item.TryGetProperty("title", out var track) ? track.GetString() : null;

                string? artistName = null;
                if (item.TryGetProperty("artist", out var artistElement)
                    && artistElement.ValueKind == JsonValueKind.Object
                    && artistElement.TryGetProperty("name", out var nameElement))
                {
                    artistName = nameElement.GetString();
                }

                if (!TextMatch.Similar(trackName, title) || !TextMatch.Similar(artistName, artist))
                {
                    continue;
                }

                string? cover = null;
                if (item.TryGetProperty("album", out var albumElement)
                    && albumElement.ValueKind == JsonValueKind.Object)
                {
                    if (albumElement.TryGetProperty("cover_xl", out var xl) && xl.GetString() is { Length: > 0 } xlUrl)
                    {
                        cover = xlUrl;
                    }
                    else if (albumElement.TryGetProperty("cover_big", out var big))
                    {
                        cover = big.GetString();
                    }
                }

                if (string.IsNullOrWhiteSpace(cover))
                {
                    continue;
                }

                _log.Debug($"Portada pública encontrada (Deezer): {cover}");
                return cover;
            }

            _log.Debug($"Deezer no tiene portada para '{artist} - {title}'.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Búsqueda en Deezer fallida: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> SearchItunesCoreAsync(
        string title,
        string artist,
        string album,
        CancellationToken cancellationToken)
    {
        // Se busca por artista + título (sin el álbum): el álbum que reporta el reproductor
        // suele ser el del álbum completo, mientras que el single en el catálogo tiene otro
        // título de álbum, y añadirlo hacía que iTunes no devolviera resultados.
        var term = Uri.EscapeDataString($"{artist} {title}".Trim());
        var uri = $"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=5";

        using var response = await Http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log.Debug($"iTunes Search respondió {(int)response.StatusCode}.");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in results.EnumerateArray())
        {
            var trackName = item.TryGetProperty("trackName", out var track) ? track.GetString() : null;
            var artistName = item.TryGetProperty("artistName", out var name) ? name.GetString() : null;

            if (!TextMatch.Similar(trackName, title) || !TextMatch.Similar(artistName, artist))
            {
                continue;
            }

            if (!item.TryGetProperty("artworkUrl100", out var artwork)
                || artwork.GetString() is not { Length: > 0 } artworkUrl)
            {
                continue;
            }

            var highResolution = artworkUrl.Replace("100x100bb", "600x600bb", StringComparison.Ordinal);
            _log.Debug($"Portada pública encontrada: {highResolution}");
            return highResolution;
        }

        _log.Debug($"Sin portada pública para '{artist} - {title}'.");
        return null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalPresence/1.0");
        return client;
    }

    private static HttpClient CreateCatalogClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // MusicBrainz exige un User-Agent con forma de contacto y limita a 1 req/s; el
        // intervalo mínimo de la clase ya respeta ese límite.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "UniversalPresence/1.0 (https://github.com/universal-presence)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
