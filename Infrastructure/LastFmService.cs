using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalPresence.Configuration;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// Scrobbling a Last.fm.
///
/// Usa la API Web 2.0 firmada (md5 de los parámetros ordenados + shared secret). Solo se
/// activa cuando hay API key, shared secret, usuario y session key; si falta algo, todas
/// las llamadas son no-ops y la app funciona igual.
/// </summary>
public sealed class LastFmService
{
    private const string ApiRoot = "https://ws.audioscrobbler.com/2.0/";
    private const string AuthRoot = "https://www.last.fm/api/auth/";

    /// <summary>Last.fm ignora las pistas de 30 s o menos.</summary>
    private static readonly TimeSpan MinimumTrackDuration = TimeSpan.FromSeconds(30);

    /// <summary>Nunca hace falta escuchar más de 4 minutos para que un scrobble cuente.</summary>
    private static readonly TimeSpan MaximumRequiredPlay = TimeSpan.FromMinutes(4);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AppOptions _options;
    private readonly Log _log;

    public LastFmService(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
    }

    public bool HasKeys =>
        !string.IsNullOrWhiteSpace(_options.LastFmApiKey)
        && !string.IsNullOrWhiteSpace(_options.LastFmSecret);

    public bool IsEnabled => HasKeys
        && !string.IsNullOrWhiteSpace(_options.LastFmUser)
        && !string.IsNullOrWhiteSpace(_options.LastFmSessionKey);

    /// <summary>Página que el usuario abre para autorizar la aplicación.</summary>
    public static string AuthUrl(string apiKey, string token) =>
        $"{AuthRoot}?api_key={Uri.EscapeDataString(apiKey)}&token={Uri.EscapeDataString(token)}";

    /// <summary>Paso 1 del «desktop authentication»: un token temporal.</summary>
    public async Task<string?> RequestTokenAsync(CancellationToken cancellationToken)
    {
        if (!HasKeys)
        {
            return null;
        }

        var body = await CallAsync(
            new Dictionary<string, string> { ["method"] = "auth.getToken" },
            needsSession: false,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("token", out var token)
                && token.GetString() is { Length: > 0 } value
                ? value
                : null;
        }
        catch (JsonException)
        {
            _log.Warn("Last.fm devolvió una respuesta inesperada al pedir el token.");
            return null;
        }
    }

    /// <summary>Paso 2: cambia el token autorizado por la session key definitiva.</summary>
    public async Task<(string User, string SessionKey)?> CompleteSessionAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var body = await CallAsync(
            new Dictionary<string, string> { ["method"] = "auth.getSession", ["token"] = token },
            needsSession: false,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("session", out var session))
            {
                if (document.RootElement.TryGetProperty("message", out var message))
                {
                    _log.Warn($"Last.fm: {message.GetString()}");
                }

                return null;
            }

            var user = session.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
            var key = session.TryGetProperty("key", out var sessionKey) ? sessionKey.GetString() ?? "" : "";

            return key.Length > 0 ? (user, key) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Publica en el perfil de Last.fm qué se está escuchando ahora mismo.</summary>
    public async Task<bool> UpdateNowPlayingAsync(
        string title,
        string artist,
        string album,
        long durationSeconds,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return false;
        }

        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.updateNowPlaying",
            ["track"] = title,
            ["artist"] = artist,
        };

        if (!string.IsNullOrWhiteSpace(album))
        {
            parameters["album"] = album;
        }

        if (durationSeconds > 0)
        {
            parameters["duration"] = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var body = await CallAsync(parameters, needsSession: true, cancellationToken).ConfigureAwait(false);
        return body is not null && !body.Contains("\"error\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Regla oficial de scrobble: la pista debe durar más de 30 s y haberse escuchado al
    /// menos la mitad de su duración o 4 minutos, lo que llegue antes. Un scrobble que no
    /// la cumple no debe enviarse: Last.fm lo descarta igualmente, y enviarlo solo gasta
    /// cuota y ensucia el log.
    /// </summary>
    internal static bool MeetsScrobbleRule(TimeSpan duration, TimeSpan listened)
    {
        if (listened <= TimeSpan.Zero)
        {
            return false;
        }

        if (duration > TimeSpan.Zero && duration <= MinimumTrackDuration)
        {
            return false;
        }

        // Sin duración (algunos reproductores no la publican) no se puede calcular la
        // mitad, así que se exige el tope de 4 minutos.
        var required = duration > TimeSpan.Zero
            ? TimeSpan.FromTicks(duration.Ticks / 2)
            : MaximumRequiredPlay;

        return listened >= (required < MaximumRequiredPlay ? required : MaximumRequiredPlay);
    }

    /// <summary>
    /// Envía un scrobble. <paramref name="startedAt"/> es cuándo empezó a sonar la pista,
    /// <paramref name="duration"/> su duración y <paramref name="listened"/> el tiempo que
    /// realmente se ha reproducido; si no cumple la regla de Last.fm, no se envía nada.
    /// </summary>
    public async Task<bool> ScrobbleAsync(
        string title,
        string artist,
        string album,
        DateTimeOffset startedAt,
        TimeSpan duration,
        TimeSpan listened,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return false;
        }

        if (!MeetsScrobbleRule(duration, listened))
        {
            _log.Debug(
                $"Last.fm: no se scrobbela «{artist} - {title}» " +
                $"({listened.TotalSeconds:0} s de {(long)duration.TotalSeconds} s).");
            return false;
        }

        var durationSeconds = (long)duration.TotalSeconds;

        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.scrobble",
            ["track"] = title,
            ["artist"] = artist,
            ["timestamp"] = startedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(album))
        {
            parameters["album"] = album;
        }

        if (durationSeconds > 0)
        {
            parameters["duration"] = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var body = await CallAsync(parameters, needsSession: true, cancellationToken).ConfigureAwait(false);
        return body is not null && !body.Contains("\"error\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Firma y envía una llamada. La firma es md5 de los pares clave+valor ordenados
    /// alfabéticamente, seguidos del shared secret; nunca incluye api_sig ni format.
    /// </summary>
    private async Task<string?> CallAsync(
        Dictionary<string, string> parameters,
        bool needsSession,
        CancellationToken cancellationToken)
    {
        if (!HasKeys)
        {
            return null;
        }

        try
        {
            var payload = new Dictionary<string, string>(parameters, StringComparer.Ordinal)
            {
                ["api_key"] = _options.LastFmApiKey,
            };

            if (needsSession)
            {
                payload["sk"] = _options.LastFmSessionKey;
            }

            var signature = BuildSignature(payload, _options.LastFmSecret);
            payload["api_sig"] = signature;
            payload["format"] = "json";

            var form = string.Join("&", payload
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

            using var content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var response = await Http
                .PostAsync(ApiRoot, content, cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Debug($"Last.fm respondió {(int)response.StatusCode}: {Trim(body)}");
                return null;
            }

            if (body.Contains("\"error\"", StringComparison.Ordinal))
            {
                _log.Debug($"Last.fm devolvió un error: {Trim(body)}");
                return null;
            }

            return body;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Debug($"Last.fm error: {ex.Message}");
            return null;
        }
    }

    private static string BuildSignature(Dictionary<string, string> parameters, string secret)
    {
        var builder = new StringBuilder();
        foreach (var pair in parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.Equals(pair.Key, "format", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "callback", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "api_sig", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(pair.Key).Append(pair.Value);
        }

        builder.Append(secret);
        return Md5(builder.ToString());
    }

    private static string Trim(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";

    private static string Md5(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
