using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Media;

/// <summary>
/// Resolves a publicly reachable cover URL from the track metadata.
///
/// Why this exists: Discord resolves activity images from its own side and refuses
/// loopback/local images and base64 payloads, so an SMTC thumbnail cannot be sent
/// directly. When the user has no public HTTPS host for the extracted cover
/// (Artwork.ExternalUrlBase), this uses two public catalogues — Apple's iTunes Search and
/// Deezer — to obtain an https artwork URL that Discord can fetch. Both are queried in
/// parallel because Apple is fast for mainstream releases while Deezer has a much wider
/// Latin/urban catalogue (new singles often missing from Apple).
///
/// It sends only title/artist/album, caches results for hours, and is disabled with
/// "Artwork": { "Mode": "None" } or "DiscordAsset".
/// </summary>
public sealed class ArtworkLookupService
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(1500);

    private static readonly HttpClient Http = CreateClient();

    private readonly Log _log;

    // La portada encontrada se guarda en disco: la misma canción ya no vuelve a consultar
    // el catálogo, ni siquiera en la siguiente ejecución.
    private readonly PersistentCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public ArtworkLookupService(Log log, string cacheFilePath)
    {
        _log = log;
        _cache = new PersistentCache(cacheFilePath, log);
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
        return found.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
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

                if (!Similar(trackName, title) || !Similar(artistName, artist))
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
        var term = Uri.EscapeDataString($"{artist} {album} {title}".Trim());
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

            if (!Similar(trackName, title) || !Similar(artistName, artist))
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

    private static bool Similar(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);

        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.Ordinal)
            || a.Contains(b, StringComparison.Ordinal)
            || b.Contains(a, StringComparison.Ordinal))
        {
            return true;
        }

        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length > 2).ToHashSet();
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length > 2).ToHashSet();

        return tokensA.Intersect(tokensB).Count() >= 2;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalPresence/1.0");
        return client;
    }
}
