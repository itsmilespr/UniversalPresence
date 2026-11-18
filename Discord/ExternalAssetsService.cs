using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Discord;

/// <summary>
/// Convierte URLs HTTPS externas (Catbox, Deezer, Apple, etc.) en URLs «mp:» de Discord
/// usando la API de external-assets. Sin esa conversión Discord no descarga imágenes
/// externas y la actividad sale con el placeholder del signo de interrogación.
///
/// El endpoint solo acepta un token OAuth2 de usuario (nunca uno de bot); el token lo
/// gestiona <see cref="DiscordTokenService"/>, que además lo renueva solo.
/// </summary>
public sealed class ExternalAssetsService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly AppOptions _options;
    private readonly DiscordTokenService _tokens;
    private readonly Log _log;

    // La conversión a «mp:» se guarda en disco: una carátula ya convertida aparece al
    // instante la siguiente vez, sin ninguna petición a Discord.
    private readonly PersistentCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExternalAssetsService(AppOptions options, DiscordTokenService tokens, Log log)
    {
        _options = options;
        _tokens = tokens;
        _log = log;
        _cache = new PersistentCache(Path.Combine(AppOptions.LocalDataDirectory, "mp-cache.json"), log);
    }

    /// <summary>False cuando con el token actual Discord no puede pintar imágenes propias.</summary>
    public bool CanProxyImages => _tokens.CanProxyImages;

    /// <summary>Motivo por el que no hay proxy de imágenes, o null.</summary>
    public string? ImageProxyHint => _tokens.Hint;

    /// <summary>URL «mp:» ya convertida, leída de la caché sin tocar la red.</summary>
    public string? GetCached(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        // Un «mp:» guardado vale tal cual; si estaba apuntado como fallo, es null.
        return imageUrl.StartsWith("mp:", StringComparison.Ordinal) ? imageUrl : _cache.Get(imageUrl);
    }

    /// <summary>
    /// Olvida los fallos cacheados: tras conectar la cuenta las carátulas deben intentarse
    /// otra vez al momento, no cinco minutos después.
    /// </summary>
    public void InvalidateFailures() => _cache.ClearFailures();

    /// <summary>
    /// Espera a que el proxy de Discord (media.discordapp.net) tenga la imagen cacheada
    /// antes de publicar la actividad. Sin esta espera Discord intenta descargar el asset
    /// antes de que esté disponible y la carátula no se muestra («Invalid resource»).
    /// </summary>
    public async Task<bool> WaitForProxyAsync(string mpUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mpUrl) || !mpUrl.StartsWith("mp:", StringComparison.Ordinal))
        {
            return true;
        }

        var proxyUrl = "https://media.discordapp.net/" + mpUrl.Substring(3);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await Http.SendAsync(
                        new HttpRequestMessage(HttpMethod.Head, proxyUrl),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // ignore and retry
            }

            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Olvida la conversión «mp:» cacheada para una URL externa: si el proxy de Discord no
    /// la sirve (imagen vacía/caducada), se descarta para que se reintente la conversión
    /// la próxima vez.
    /// </summary>
    public void Invalidate(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return;
        }

        _cache.Set(imageUrl, null);
    }

    /// <summary>
    /// Devuelve la URL «mp:» de la imagen, o null si no se pudo convertir.
    /// </summary>
    public async Task<string?> ConvertAsync(
        string imageUrl,
        CancellationToken cancellationToken,
        bool ignoreCache = false)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        if (imageUrl.StartsWith("mp:", StringComparison.Ordinal))
        {
            return imageUrl;
        }

        var clientId = _options.DiscordClientId.Trim();
        if (clientId.Length == 0)
        {
            return null;
        }

        if (!_tokens.CanProxyImages)
        {
            return null;
        }

        if (!ignoreCache)
        {
            var cached = _cache.Get(imageUrl);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ignoreCache)
            {
                var cachedAgain = _cache.Get(imageUrl);
                if (!string.IsNullOrWhiteSpace(cachedAgain))
                {
                    return cachedAgain;
                }
            }

            var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            for (var attempt = 0; attempt < 2 && !string.IsNullOrWhiteSpace(token); attempt++)
            {
                var (mpUrl, unauthorized) = await ConvertCoreAsync(imageUrl, clientId, token!, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(mpUrl))
                {
                    _cache.Set(imageUrl, mpUrl);
                    return mpUrl;
                }

                if (!unauthorized)
                {
                    break;
                }

                // El token caducó (o lo rechazaron): se renueva y se reintenta una vez.
                _tokens.Invalidate();
                token = await _tokens.GetAccessTokenAsync(cancellationToken, forceRefresh: true)
                    .ConfigureAwait(false);
            }

            _cache.Set(imageUrl, null);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string? MpUrl, bool Unauthorized)> ConvertCoreAsync(
        string imageUrl,
        string clientId,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { urls = new[] { imageUrl } });
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://discord.com/api/v10/applications/{clientId}/external-assets")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Debug($"External-assets respondió {(int)response.StatusCode} para {imageUrl}.");
                return (null, response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return (null, false);
            }

            var first = document.RootElement[0];
            if (first.TryGetProperty("external_asset_path", out var pathProperty)
                && pathProperty.GetString() is { Length: > 0 } path)
            {
                var mpUrl = $"mp:{path}";
                _log.Info($"Imagen convertida a mp: {imageUrl} → {mpUrl}");
                return (mpUrl, false);
            }

            return (null, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"Error convirtiendo imagen externa: {ex.Message}");
            return (null, false);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalPresence/1.0");
        return client;
    }
}
