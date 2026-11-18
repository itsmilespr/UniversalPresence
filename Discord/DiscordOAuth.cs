using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniversalPresence.Discord;

/// <summary>Tokens devueltos por el endpoint /oauth2/token.</summary>
public sealed record DiscordTokens(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

/// <summary>
/// OAuth2 de Discord con PKCE, sin client_secret.
///
/// Sirve para una sola cosa: conseguir un token de USUARIO que pueda llamar a
/// <c>external-assets</c> y convertir las carátulas en URLs «mp:». Un token de bot no
/// vale (Discord responde 403 «Bots cannot use this endpoint»).
///
/// PKCE evita guardar el client_secret en el equipo: el navegador devuelve un código
/// atado a un verifier que solo conoce esta aplicación, y ese código se canjea por el
/// par access/refresh token.
/// </summary>
public static class DiscordOAuth
{
    public const string AuthorizeEndpoint = "https://discord.com/oauth2/authorize";
    public const string TokenEndpoint = "https://discord.com/api/v10/oauth2/token";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Valor aleatorio que viaja en la URL y se comprueba al volver.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    /// <summary>Secreto local del PKCE; nunca sale del equipo.</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string BuildAuthorizeUrl(string clientId, string redirectUri, string state, string challenge) =>
        $"{AuthorizeEndpoint}" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        "&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        "&scope=identify" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&code_challenge={Uri.EscapeDataString(challenge)}" +
        "&code_challenge_method=S256" +
        "&prompt=consent";

    /// <summary>Canjea el código de la URL de vuelta por un par de tokens.</summary>
    public static Task<DiscordTokens?> ExchangeCodeAsync(
        string clientId,
        string? clientSecret,
        string redirectUri,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        };

        AddSecret(form, clientSecret);
        return PostAsync(form, "code exchange", cancellationToken);
    }

    /// <summary>Renueva el access token sin volver a pedir permiso al usuario.</summary>
    public static Task<DiscordTokens?> RefreshAsync(
        string clientId,
        string? clientSecret,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        };

        AddSecret(form, clientSecret);
        return PostAsync(form, "token refresh", cancellationToken);
    }

    private static void AddSecret(Dictionary<string, string> form, string? clientSecret)
    {
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            form["client_secret"] = clientSecret.Trim();
        }
    }

    private static async Task<DiscordTokens?> PostAsync(
        Dictionary<string, string> form,
        string what,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("UniversalPresence/1.0");

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new DiscordOAuthException(
                $"Discord rejected the {what} ({(int)response.StatusCode}): {Summarize(body)}");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var accessToken = root.TryGetProperty("access_token", out var access) ? access.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new DiscordOAuthException($"Discord did not return access_token ({Summarize(body)}).");
            }

            var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? seconds
                : 604_800;

            return new DiscordTokens(accessToken!, refreshToken, expiresIn);
        }
        catch (JsonException)
        {
            throw new DiscordOAuthException($"Respuesta inesperada de Discord ({Summarize(body)}).");
        }
    }

    private static string Summarize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "sin cuerpo";
        }

        var trimmed = body.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalPresence/1.0");
        return client;
    }
}

/// <summary>Fallo de OAuth con un mensaje ya listo para enseñar al usuario.</summary>
public sealed class DiscordOAuthException : Exception
{
    public DiscordOAuthException(string message) : base(message)
    {
    }
}
