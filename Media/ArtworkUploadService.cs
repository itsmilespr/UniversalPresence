using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Media;

/// <summary>
/// Uploads the locally extracted SMTC cover to a public image host so Discord can fetch it.
///
/// Discord resolves activity images server-side and only accepts a public https URL: a local
/// file, a data URI or a loopback address never render. This service is the alternative to
/// <c>Artwork.ExternalUrlBase</c> (your own host) and to the iTunes catalogue lookup.
///
/// Supported hosts, chosen with <see cref="ArtworkOptions.UploadMode"/>:
///   Imgur  — anonymous upload, needs a Client-ID in <see cref="ArtworkOptions.UploadApiKey"/>.
///   Catbox — anonymous upload, no key.
///
/// Results are cached per artwork hash, including failures (short TTL), so a cover is uploaded
/// once per run. Any failure returns null and the caller falls back to the lookup or the
/// fallback asset.
/// </summary>
public sealed class ArtworkUploadService
{
    private const long ImgurMaxBytes = 10 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private readonly AppOptions _options;
    private readonly Log _log;

    // La URL de cada carátula se guarda en disco: la segunda vez que suena una canción ya
    // no hay que volver a subirla, ni siquiera tras reiniciar la aplicación. El TTL es menor
    // que la vida real del archivo en Litterbox (72 h) y la antigüedad se conserva para
    // poder revalidar o renovar la URL antes de que se rompa.
    private readonly PersistentCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _revalidated = new(StringComparer.Ordinal);

    public ArtworkUploadService(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
        _cache = new PersistentCache(
            Path.Combine(AppOptions.LocalDataDirectory, "covers-cache.json"),
            log,
            RemoteAssetPolicy.EntryTtl);
    }

    /// <summary>URL pública ya subida, con su antigüedad y lo que conviene hacer con ella.</summary>
    public sealed record CachedCover(string? Url, TimeSpan Age, bool NeedsRevalidation, bool NeedsRefresh);

    /// <summary>
    /// Devuelve la entrada cacheada de una carátula, aunque esté a punto de caducar. Null solo
    /// cuando no hay nada guardado para esa imagen.
    /// </summary>
    public CachedCover? GetCachedCover(ArtworkInfo artwork)
    {
        var key = KeyFor(artwork);

        if (!_cache.TryGet(key, out var url, out var age))
        {
            return null;
        }

        // Fallo reciente guardado con TTL corto: no se reintenta en cada vuelta del bucle.
        if (string.IsNullOrWhiteSpace(url))
        {
            return new CachedCover(null, age, NeedsRevalidation: false, NeedsRefresh: false);
        }

        // Más vieja que la vida real del archivo: ya no se puede publicar.
        if (age >= RemoteAssetPolicy.HostLifetime)
        {
            _log.Debug($"La URL de la carátula tiene {age.TotalHours:0} h y ya caducó; se sube otra.");
            return new CachedCover(null, age, NeedsRevalidation: false, NeedsRefresh: true);
        }

        // Queda poco margen: se sigue mostrando la actual mientras se sube una nueva.
        if (age >= RemoteAssetPolicy.MaxReuseAge)
        {
            return new CachedCover(url, age, NeedsRevalidation: false, NeedsRefresh: true);
        }

        var needsRevalidation = age >= RemoteAssetPolicy.RevalidateAfter
            && !_revalidated.ContainsKey(url);

        return new CachedCover(url, age, needsRevalidation, NeedsRefresh: false);
    }

    /// <summary>Descarta la URL cacheada de una carátula (el host ya la borró).</summary>
    public void Invalidate(ArtworkInfo artwork) => _cache.Remove(KeyFor(artwork));

    /// <summary>Marca una URL como comprobada en esta ejecución (no se repite el HEAD).</summary>
    public void MarkRevalidated(string url) => _revalidated[url] = 0;

    public bool IsRevalidated(string url) => _revalidated.ContainsKey(url);

    private static string KeyFor(ArtworkInfo artwork) =>
        string.IsNullOrWhiteSpace(artwork.Hash) ? artwork.FileName : artwork.Hash;

    /// <summary>
    /// Public https URL of the uploaded cover, or null when the upload is off/failed.
    /// <paramref name="force"/> sube otra aunque haya una URL en caché (renovación).
    /// </summary>
    public async Task<string?> UploadAsync(
        ArtworkInfo artwork,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (!_options.Artwork.UsesUpload)
        {
            return null;
        }

        var key = KeyFor(artwork);
        var previous = _cache.Get(key);

        if (!force && !string.IsNullOrWhiteSpace(previous))
        {
            return previous;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force)
            {
                var cached = _cache.Get(key);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    return cached;
                }
            }

            var url = _options.Artwork.UploadMode.Trim().ToLowerInvariant() switch
            {
                "imgur" => await UploadToImgurAsync(artwork, cancellationToken).ConfigureAwait(false),
                "catbox" => await UploadToCatboxAsync(artwork, cancellationToken).ConfigureAwait(false),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(url))
            {
                // No se borra una URL que todavía puede servir: si la renovación falla, la
                // portada anterior sigue viva hasta su caducidad real.
                if (string.IsNullOrWhiteSpace(previous))
                {
                    _cache.Set(key, null);
                }

                return null;
            }

            _cache.Set(key, url);
            return url;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"La subida de la carátula falló: {ex.Message}");

            if (string.IsNullOrWhiteSpace(previous))
            {
                _cache.Set(key, null);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Comprueba si una URL sigue sirviendo la imagen. true = vive, false = el host la borró,
    /// null = no se pudo saber (red, 5xx, 403…).
    /// </summary>
    public async Task<bool?> IsUrlAliveAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsPublicHttps(url))
        {
            return false;
        }

        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await Http
                .SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (headResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return false;
            }

            if (headResponse.IsSuccessStatusCode)
            {
                return true;
            }

            if (headResponse.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
            {
                return null;
            }

            // Algunos hosts no aceptan HEAD: se pide solo el primer byte.
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var getResponse = await Http
                .SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (getResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return false;
            }

            return getResponse.IsSuccessStatusCode ? true : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo comprobar la carátula: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> UploadToImgurAsync(ArtworkInfo artwork, CancellationToken cancellationToken)
    {
        var clientId = _options.Artwork.UploadApiKey.Trim();

        if (clientId.Length == 0)
        {
            _log.Warn(
                "Artwork.UploadMode es «Imgur» pero Artwork.UploadApiKey está vacío " +
                "(Client-ID de Imgur). Se usa la búsqueda de portada como respaldo.");
            return null;
        }

        if (artwork.ByteCount > ImgurMaxBytes)
        {
            _log.Warn(
                $"La carátula pesa {artwork.ByteCount / 1024} KB y el límite anónimo de Imgur es " +
                "10 MB. Se usa la búsqueda de portada como respaldo.");
            return null;
        }

        var bytes = await ReadArtworkAsync(artwork, cancellationToken).ConfigureAwait(false);

        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(bytes);
        image.Headers.ContentType = new MediaTypeHeaderValue(artwork.ContentType);
        form.Add(image, "image", artwork.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.imgur.com/3/image")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", clientId);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.Warn($"Imgur respondió {(int)response.StatusCode} al subir la carátula.");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (document.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("link", out var link)
            && link.GetString() is { Length: > 0 } url
            && IsPublicHttps(url))
        {
            _log.Info($"Carátula subida a Imgur: {url}");
            return url;
        }

        _log.Warn("Imgur no devolvió un enlace utilizable.");
        return null;
    }

    private async Task<string?> UploadToCatboxAsync(ArtworkInfo artwork, CancellationToken cancellationToken)
    {
        // Catbox (catbox.moe) devolvía archivos vacíos de forma intermitente, así que se
        // usa Litterbox (litterbox.catbox.moe), la variante con caducidad de la misma
        // infraestructura, que sirve el contenido de forma fiable. Caduca a las 72 h; la
        // caché de subida se invalida y se vuelve a subir cuando el proxy de Discord deja
        // de servir la imagen.
        var bytes = await ReadArtworkAsync(artwork, cancellationToken).ConfigureAwait(false);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("fileupload"), "reqtype");
        form.Add(new StringContent("72h"), "time");

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(artwork.ContentType);
        form.Add(file, "fileToUpload", artwork.FileName);

        using var response = await Http
            .PostAsync("https://litterbox.catbox.moe/resources/internals/api.php", form, cancellationToken)
            .ConfigureAwait(false);

        var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();

        if (!response.IsSuccessStatusCode || !IsPublicHttps(body))
        {
            _log.Warn(
                $"Litterbox respondió {(int)response.StatusCode}" +
                (body.Length > 0 && body.Length <= 120 ? $" («{body}»)" : "") +
                " al subir la carátula.");
            return null;
        }

        _log.Info($"Carátula subida a Litterbox: {body}");
        return body;
    }

    private static async Task<byte[]> ReadArtworkAsync(ArtworkInfo artwork, CancellationToken cancellationToken)
    {
        if (!File.Exists(artwork.FilePath))
        {
            throw new FileNotFoundException("La carátula cacheada ya no existe.", artwork.FilePath);
        }

        return await File.ReadAllBytesAsync(artwork.FilePath, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPublicHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !uri.IsLoopback
        && value!.Length <= 256;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalPresence/1.0");
        return client;
    }
}
