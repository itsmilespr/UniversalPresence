using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Media;

/// <summary>
/// Completa el artista que entrega SMTC con la lista de artistas acreditados en la pista.
///
/// Windows expone un solo <c>Artist</c> (normalmente el principal) y un <c>AlbumArtist</c>;
/// Spotify, en particular, no incluye a los invitados. Deezer publica la lista completa en
/// <c>contributors</c> del track, así que se usa como fuente cuando la pista coincide por
/// título y artista. Es una mejora oportunista: si no hay coincidencia, se mantiene el
/// artista original y no se cambia nada.
/// </summary>
public sealed class TrackArtistLookupService
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(400);
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Cuánto se deja de consultar Deezer tras un error de cuota.</summary>
    private static readonly TimeSpan QuotaPause = TimeSpan.FromMinutes(5);

    // Deezer contesta 200 con {"error":{"code":4}} al superar su cuota. Es un fallo
    // temporal, no «la pista no tiene artistas»: se cachea como fallo y se deja pasar
    // unos minutos antes de volver a preguntar.
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    private readonly Log? _log;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public TrackArtistLookupService(Log log, HttpClient? http = null)
    {
        _log = log;
        _http = http ?? Http;
    }

    public async Task<string?> ResolveAsync(string title, string artist, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var key = TextMatch.TrackKey(artist, title);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            if (DateTimeOffset.UtcNow < _pausedUntil)
            {
                return null;
            }

            var wait = _lastRequestAt + MinimumInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            var result = await SearchAsync(title, artist, cancellationToken).ConfigureAwait(false);
            _lastRequestAt = DateTimeOffset.UtcNow;

            if (_cache.Count >= 256)
            {
                _cache.Clear();
            }

            _cache[key] = result;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un fallo de red o un 5xx es temporal: no se cachea como «esta pista no tiene
            // artistas», o el enriquecimiento quedaría desactivado hasta reiniciar.
            _log?.Debug($"Búsqueda de artistas falló: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> SearchAsync(string title, string artist, CancellationToken cancellationToken)
    {
        var term = Uri.EscapeDataString($"{artist} {title}".Trim());
        var uri = $"https://api.deezer.com/search?q={term}&limit=10";

        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Se lanza para que ResolveAsync distinga un fallo temporal de un «sin
            // resultados» y no envenene la caché con un negativo permanente.
            throw new HttpRequestException(
                $"Deezer respondió {(int)response.StatusCode} al buscar artistas.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        ThrowIfQuotaExceeded(document.RootElement);

        if (!document.RootElement.TryGetProperty("data", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in results.EnumerateArray())
        {
            var trackName = item.TryGetProperty("title", out var titleElement)
                ? titleElement.GetString()
                : null;

            string? artistName = null;
            if (item.TryGetProperty("artist", out var artistElement)
                && artistElement.ValueKind == JsonValueKind.Object
                && artistElement.TryGetProperty("name", out var nameElement))
            {
                artistName = nameElement.GetString();
            }

            if (!TextMatch.Similar(trackName, title))
            {
                continue;
            }

            // Spotify puede reportar el artista del álbum («Various Artists») en una
            // recopilación. En ese caso se acepta el resultado por título y se usan los
            // contribuidores reales de la pista; si el artista no es genérico, tiene que
            // coincidir para no atribuir la canción a otra versión.
            if (!IsGenericArtist(artist) && !TextMatch.Similar(artistName, artist))
            {
                continue;
            }

            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
            {
                continue;
            }

            var display = await ReadContributorsAsync(id, artist, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(display))
            {
                _log?.Debug($"Artistas de la pista (Deezer) para «{title}»: {display}");
                return display;
            }
        }

        return null;
    }

    private async Task<string?> ReadContributorsAsync(
        long trackId,
        string reportedArtist,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync($"https://api.deezer.com/track/{trackId}", cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Deezer respondió {(int)response.StatusCode} al leer la pista {trackId}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        ThrowIfQuotaExceeded(document.RootElement);

        if (!document.RootElement.TryGetProperty("contributors", out var contributors)
            || contributors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var contributor in contributors.EnumerateArray())
        {
            var name = contributor.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var role = contributor.TryGetProperty("role", out var roleElement)
                ? roleElement.GetString()
                : null;

            // La lista incluye productores y remezcladores; para la línea de artista solo
            // cuentan el intérprete principal y los invitados.
            if (!string.IsNullOrWhiteSpace(role)
                && !string.Equals(role, "Main", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(role, "Featured", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            names.Add(name.Trim());
        }

        return JoinArtists(names, reportedArtist);
    }

    /// <summary>
    /// Une y depura la lista de artistas. Devuelve null cuando no hay mejora real: menos
    /// de dos nombres, todos son el mismo, o el artista que reporta Windows no aparece
    /// (señal de que el catálogo devolvió otra versión de la canción).
    /// </summary>
    internal static string? JoinArtists(IEnumerable<string> names, string reportedArtist)
    {
        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var normalized = TextMatch.Normalize(name);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            unique.Add(name.Trim());
        }

        var genericReported = IsGenericArtist(reportedArtist);

        if (unique.Count == 0 || (unique.Count < 2 && !genericReported))
        {
            return null;
        }

        // Con un artista genérico («Various Artists») no hay nada que validar contra el
        // reportado; se sustituye por el intérprete real de la pista.
        if (!genericReported && !unique.Any(name => TextMatch.Similar(name, reportedArtist)))
        {
            return null;
        }

        var joined = string.Join(", ", unique);
        return string.Equals(joined, reportedArtist.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : joined;
    }

    /// <summary>
    /// Artistas de álbum que no identifican al intérprete real (recopilaciones).
    /// </summary>
    private static readonly string[] GenericArtists =
    {
        "various artists",
        "various artist",
        "various",
        "varios artistas",
        "artistas varios",
        "unknown artist",
        "artista desconocido",
        "va",
        "v a",
    };

    internal static bool IsGenericArtist(string? value)
    {
        var normalized = TextMatch.Normalize(value);
        return normalized.Length > 0
            && GenericArtists.Contains(normalized, StringComparer.Ordinal);
    }

    /// <summary>
    /// Si Deezer superó la cuota, aparca el catálogo unos minutos y lanza, para que
    /// <see cref="ResolveAsync"/> no lo confunda con «esta pista no tiene artistas» ni lo
    /// guarde en la caché.
    /// </summary>
    private void ThrowIfQuotaExceeded(JsonElement root)
    {
        if (!DeezerQuota.IsExceeded(root))
        {
            return;
        }

        _pausedUntil = DateTimeOffset.UtcNow + QuotaPause;
        _log?.Warn($"Deezer alcanzó su cuota de peticiones; se deja de consultar {QuotaPause.TotalMinutes:0} minutos.");

        throw new HttpRequestException("Deezer alcanzó su cuota de peticiones al buscar artistas.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalPresence/1.0");
        return client;
    }
}
