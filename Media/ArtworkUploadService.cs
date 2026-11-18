using System.Collections.Concurrent;
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
    // no hay que volver a subirla, ni siquiera tras reiniciar la aplicación.
    private readonly PersistentCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArtworkUploadService(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
        _cache = new PersistentCache(Path.Combine(AppOptions.LocalDataDirectory, "covers-cache.json"), log);
    }

    /// <summary>Returns the cached public URL if already uploaded, otherwise null (without starting an upload).</summary>
    public string? GetCachedUrl(ArtworkInfo artwork)
    {
        var key = string.IsNullOrWhiteSpace(artwork.Hash) ? artwork.FileName : artwork.Hash;
        return _cache.Get(key);
    }

    /// <summary>Public https URL of the uploaded cover, or null when the upload is off/failed.</summary>
    public async Task<string?> UploadAsync(ArtworkInfo artwork, CancellationToken cancellationToken)
    {
        if (!_options.Artwork.UsesUpload)
        {
            return null;
        }

        var key = string.IsNullOrWhiteSpace(artwork.Hash) ? artwork.FileName : artwork.Hash;

        var cached = _cache.Get(key);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cache.Get(key);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            var url = _options.Artwork.UploadMode.Trim().ToLowerInvariant() switch
            {
                "imgur" => await UploadToImgurAsync(artwork, cancellationToken).ConfigureAwait(false),
                "catbox" => await UploadToCatboxAsync(artwork, cancellationToken).ConfigureAwait(false),
                _ => null,
            };

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
            _cache.Set(key, null);
            return null;
        }
        finally
        {
            _gate.Release();
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
