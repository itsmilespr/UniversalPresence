using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalPresence.Configuration;
using UniversalPresence.Discord;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Media;

/// <summary>
/// Icono del reproductor (AUMID → asset público) resuelto una sola vez.
///
/// Extraer el icono enumera procesos y ventanas de Windows, y subirlo a un host público
/// tarda cientos de milisegundos. Hacerlo en cada vuelta del bucle (cada segundo) era una
/// de las razones por las que el cambio de reproductor tardaba en verse y por las que el
/// bucle se quedaba bloqueado. Ahora se extrae y se sube una sola vez, en segundo plano,
/// y a partir de ahí se reutiliza al instante; también entre reinicios, desde
/// %LOCALAPPDATA%\UniversalPresence\icons-cache.json.
/// </summary>
public sealed class ProgramIconAssetService
{
    private const string CacheVersion = "5";
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(7);

    private readonly AppOptions _options;
    private readonly ArtworkUploadService _upload;
    private readonly ExternalAssetsService _externalAssets;
    private readonly Log _log;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileGate = new();

    public ProgramIconAssetService(
        AppOptions options,
        ArtworkUploadService upload,
        ExternalAssetsService externalAssets,
        Log log)
    {
        _options = options;
        _upload = upload;
        _externalAssets = externalAssets;
        _log = log;
        _cachePath = Path.Combine(AppOptions.LocalDataDirectory, "icons-cache.json");
        Load();
    }

    private readonly record struct Entry(string? Url, DateTimeOffset ExpiresAt);

    /// <summary>
    /// URL pública del icono si ya se conoce (respuesta inmediata). Si no, arranca la
    /// extracción y la subida en segundo plano y devuelve null; cuando termina se llama a
    /// <paramref name="resolved"/> para que el motor republique la actividad con el icono.
    /// </summary>
    public string? GetOrRequest(string? appUserModelId, string? executablePath, Action? resolved)
    {
        var key = BuildKey(appUserModelId, executablePath);
        if (key is null)
        {
            return null;
        }

        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return entry.Url;
        }

        if (!_pending.TryAdd(key, 0))
        {
            // Ya hay una resolución en curso para este reproductor.
            return null;
        }

        _ = Task.Run(() => ResolveAsync(key, appUserModelId, executablePath, resolved));
        return null;
    }

    private async Task ResolveAsync(string key, string? appUserModelId, string? executablePath, Action? resolved)
    {
        try
        {
            // La extracción bloquea (enumera procesos y usa GDI): fuera del hilo del bucle.
            var iconPath = await Task.Run(
                    () => PlayerIdentityResolver.GetProgramIconPath(appUserModelId, executablePath))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(iconPath))
            {
                Remember(key, null, DateTimeOffset.UtcNow + NegativeTtl);
                return;
            }

            var file = new FileInfo(iconPath);
            if (!file.Exists || file.Length == 0)
            {
                Remember(key, null, DateTimeOffset.UtcNow + NegativeTtl);
                return;
            }

            var artwork = new ArtworkInfo(
                file.FullName,
                file.Name,
                "image/png",
                StableHash($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}"),
                (int)file.Length);

            var url = await _upload.UploadAsync(artwork, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                Remember(key, null, DateTimeOffset.UtcNow + NegativeTtl);
                return;
            }

            // Discord hace proxy automáticamente de las URLs HTTPS públicas en el RPC de
            // escritorio, así que se envía la URL cruda (Litterbox). La conversión a mp:
            // no renderizaba en el cliente y el icono no aparecía, así que se omite.
            Remember(key, url, DateTimeOffset.UtcNow + PositiveTtl);
            _log.Info($"Icono del reproductor listo: {iconPath} → {url}");
            resolved?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo preparar el icono del reproductor: {ex.Message}");
            Remember(key, null, DateTimeOffset.UtcNow + NegativeTtl);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Descarta los fallos cacheados (por ejemplo, un icono que no se pudo convertir porque
    /// aún no había token) para que se reintenten en la siguiente vuelta del motor.
    /// </summary>
    public void InvalidateFailures()
    {
        foreach (var pair in _entries)
        {
            if (string.IsNullOrWhiteSpace(pair.Value.Url) || pair.Value.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task<string?> ConvertWithRetryAsync(string url)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var mpUrl = await _externalAssets
                .ConvertAsync(url, CancellationToken.None, ignoreCache: attempt > 0)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(mpUrl))
            {
                return mpUrl;
            }

            if (attempt < 2)
            {
                await Task.Delay(350 * (attempt + 1)).ConfigureAwait(false);
            }
        }

        return null;
    }

    private void Remember(string key, string? url, DateTimeOffset expiresAt)
    {
        _entries[key] = new Entry(url, expiresAt);

        if (url is not null)
        {
            lock (_fileGate)
            {
                Save();
            }
        }
    }

    private static string? BuildKey(string? appUserModelId, string? executablePath)
    {
        var aumid = (appUserModelId ?? string.Empty).Trim();
        var executable = (executablePath ?? string.Empty).Trim();

        if (aumid.Length == 0 && executable.Length == 0)
        {
            return null;
        }

        return (aumid.Length > 0 ? aumid : executable).ToLowerInvariant();
    }

    private static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _entries)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value.Url) && pair.Value.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    urls[pair.Key] = pair.Value.Url!;
                }
            }

            var payload = new CacheFile
            {
                Version = CacheVersion,
                ClientId = _options.DiscordClientId,
                SavedAt = DateTimeOffset.UtcNow,
                Urls = urls,
            };

            File.WriteAllText(_cachePath, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo guardar la caché de iconos: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath));
            if (payload is null
                || !string.Equals(payload.Version, CacheVersion, StringComparison.Ordinal)
                || DateTimeOffset.UtcNow - payload.SavedAt > PositiveTtl)
            {
                return;
            }

            // Las URLs mp: pertenecen a la aplicación de Discord con la que se generaron.
            if (!string.Equals(payload.ClientId ?? string.Empty, _options.DiscordClientId, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var pair in payload.Urls ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    _entries[pair.Key] = new Entry(pair.Value, DateTimeOffset.UtcNow + PositiveTtl);
                }
            }

            _log.Debug($"Caché de iconos cargada: {_entries.Count} reproductor(es).");
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo leer la caché de iconos: {ex.Message}");
        }
    }

    private sealed class CacheFile
    {
        public string? Version { get; set; }

        public string? ClientId { get; set; }

        public DateTimeOffset SavedAt { get; set; }

        public Dictionary<string, string>? Urls { get; set; }
    }
}
