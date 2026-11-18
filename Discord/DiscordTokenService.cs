using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Discord;

/// <summary>
/// Mantiene el token OAuth2 que usa <c>external-assets</c>: lo renueva solo cuando está a
/// punto de caducar (o cuando Discord lo rechaza) y guarda los tokens nuevos en
/// config.json, para no tener que volver a autorizar cada semana.
/// </summary>
public sealed class DiscordTokenService
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);

    private readonly AppOptions _options;
    private readonly Log _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _warnedRefreshFailure;
    private DateTimeOffset _lastRefreshAttempt = DateTimeOffset.MinValue;

    public DiscordTokenService(AppOptions options, Log log)
    {
        _options = options;
        _log = log;

        if (options.DiscordAccessToken.Length > 0
            && LooksLikeBotToken(options.DiscordAccessToken)
            && !options.Artwork.IsDisabled)
        {
            _log.Warn(
                "DiscordAccessToken is a BOT token and Discord does not allow converting images with " +
                "bots (403 «Bots cannot use this endpoint»). Use the tray icon → " +
                "«Connect with Discord… to connect the account so covers show up.");
        }
    }

    /// <summary>
    /// True cuando hay forma de obtener un token de usuario: uno OAuth2 ya configurado o un
    /// refresh token con el que pedir uno nuevo.
    /// </summary>
    public bool CanProxyImages
    {
        get
        {
            var access = _options.DiscordAccessToken.Trim();

            if (access.Length > 0 && !LooksLikeBotToken(access))
            {
                return true;
            }

            return _options.DiscordRefreshToken.Trim().Length > 0;
        }
    }

    /// <summary>Motivo por el que las imágenes propias no se pueden mostrar, o null.</summary>
    public string? Hint
    {
        get
        {
            if (CanProxyImages)
            {
                return null;
            }

            return _options.DiscordAccessToken.Trim().Length == 0
                ? "no account connected: Discord does not show own covers"
                : "bot token: Discord does not allow proxying images";
        }
    }

    /// <summary>
    /// Token listo para usar, renovándolo antes si está caducado o a punto de caducar.
    /// Null cuando no hay ninguna forma de obtener uno.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var access = _options.DiscordAccessToken.Trim();

        if (access.Length > 0 && LooksLikeBotToken(access))
        {
            return null;
        }

        var needsRefresh = forceRefresh || ExpiresSoon();

        if (needsRefresh && _options.DiscordRefreshToken.Trim().Length > 0)
        {
            if (await RefreshAsync(cancellationToken).ConfigureAwait(false))
            {
                access = _options.DiscordAccessToken.Trim();
            }
        }

        if (access.Length > 0 && LooksLikeBotToken(access))
        {
            return null;
        }

        return access.Length == 0 ? null : access;
    }

    /// <summary>Renueva el access token y persiste el par nuevo en config.json.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        var refreshToken = _options.DiscordRefreshToken.Trim();
        var clientId = _options.DiscordClientId.Trim();

        if (refreshToken.Length == 0 || clientId.Length == 0)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Si otra conversión acaba de renovarlo, no se repite la llamada.
            if (DateTimeOffset.UtcNow - _lastRefreshAttempt < TimeSpan.FromSeconds(5) && !ExpiresSoon())
            {
                return _options.DiscordAccessToken.Length > 0;
            }

            _lastRefreshAttempt = DateTimeOffset.UtcNow;

            var tokens = await DiscordOAuth
                .RefreshAsync(clientId, _options.DiscordClientSecret, refreshToken, cancellationToken)
                .ConfigureAwait(false);

            if (tokens is null)
            {
                return false;
            }

            Apply(tokens);
            _log.Info("OAuth2 token renewed automatically (expires in " +
                       $"{TimeSpan.FromSeconds(tokens.ExpiresInSeconds).TotalDays:0.#} days).");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_warnedRefreshFailure)
            {
                _warnedRefreshFailure = true;
                _log.Warn(
                    $"No se pudo renovar el token de Discord: {ex.Message} " +
                    "Si se repite, vuelve a conectar la cuenta desde la bandeja o activa " +
                    "«Public Client» en el portal de desarrolladores (OAuth2).");
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Guarda el resultado de una autorización (lo usa la ventana de conexión).</summary>
    public void Apply(DiscordTokens tokens)
    {
        _options.DiscordAccessToken = tokens.AccessToken;

        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            _options.DiscordRefreshToken = tokens.RefreshToken!;
        }

        _options.DiscordTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresInSeconds);
        _warnedRefreshFailure = false;

        try
        {
            _options.Save();
        }
        catch (Exception ex)
        {
            _log.Warn($"No se pudieron guardar los tokens de Discord: {ex.Message}");
        }
    }

    /// <summary>Se olvida de un token rechazado para que la próxima llamada lo renueve.</summary>
    public void Invalidate()
    {
        _options.DiscordTokenExpiresAt = DateTimeOffset.MinValue;
    }

    private bool ExpiresSoon()
    {
        var expiresAt = _options.DiscordTokenExpiresAt;

        if (expiresAt is null or { Year: < 2000 })
        {
            return false;
        }

        return expiresAt.Value - DateTimeOffset.UtcNow <= RefreshMargin;
    }

    /// <summary>Los tokens de bot tienen formato «id.timestamp.firma»; los OAuth2, no.</summary>
    public static bool LooksLikeBotToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();

        if (trimmed.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.Count(character => character == '.') == 2;
    }
}
