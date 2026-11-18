using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalPresence.Configuration;
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
///
/// El host (Litterbox) borra el archivo a las 72 h, así que la URL se revalida a las 24 h
/// y se renueva a las 60 h: nunca se publica una URL ya caducada.
/// </summary>
public sealed class ProgramIconAssetService
{
    private const string CacheVersion = "6";
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);

    private readonly AppOptions _options;
    private readonly ArtworkUploadService _upload;
    private readonly Log _log;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _revalidating = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fileGate = new();

    public ProgramIconAssetService(
        AppOptions options,
        ArtworkUploadService upload,
        Log log)
    {
        _options = options;
        _upload = upload;
        _log = log;
        _cachePath = Path.Combine(AppOptions.LocalDataDirectory, "icons-cache.json");
        Load();
    }

    private readonly record struct Entry(string? Url, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

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

        var now = DateTimeOffset.UtcNow;

        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
        {
            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                // Fallo reciente: se respeta el TTL negativo en vez de reintentar cada vuelta.
                return null;
            }

            var age = now - entry.CreatedAt;

            if (age < RemoteAssetPolicy.HostLifetime)
            {
                var retryBlocked = _retryAfter.TryGetValue(key, out var retryAt) && retryAt > now;

                if (!retryBlocked)
                {
                    if (age >= RemoteAssetPolicy.MaxReuseAge)
                    {
                        // Sigue siendo válida unas horas, pero se prepara otra sin dejar de
                        // mostrarla: si la subida falla, la vieja sigue en pie.
                        StartResolve(key, appUserModelId, executablePath, resolved);
                    }
                    else if (age >= RemoteAssetPolicy.RevalidateAfter && !_upload.IsRevalidated(entry.Url!))
                    {
                        StartRevalidation(key, entry.Url!, appUserModelId, executablePath, resolved);
                    }
                }

                return entry.Url;
            }

            // Más vieja que la vida real del archivo: ya no sirve.
            _entries.TryRemove(key, out _);
        }

        StartResolve(key, appUserModelId, executablePath, resolved);
        return null;
    }

    private void StartResolve(string key, string? appUserModelId, string? executablePath, Action? resolved)
    {
        if (!_pending.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(() => ResolveAsync(key, appUserModelId, executablePath, resolved));
    }

    private void StartRevalidation(
        string key,
        string url,
        string? appUserModelId,
        string? executablePath,
        Action? resolved)
    {
        if (!_revalidating.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var alive = await _upload.IsUrlAliveAsync(url, CancellationToken.None).ConfigureAwait(false);
                _upload.MarkRevalidated(url);

                if (alive != false)
                {
                    // Sigue viva (o no se pudo comprobar): no se toca.
                    return;
                }

                _log.Info($"El icono del reproductor «{key}» ya no está en el host; se renueva.");
                _entries.TryRemove(key, out _);
                StartResolve(key, appUserModelId, executablePath, resolved);
            }
            catch (Exception ex)
            {
                _log.Debug($"No se pudo revalidar el icono de «{key}»: {ex.Message}");
            }
            finally
            {
                _revalidating.TryRemove(key, out _);
            }
        });
    }

    private async Task ResolveAsync(string key, string? appUserModelId, string? executablePath, Action? resolved)
    {
        _entries.TryGetValue(key, out var previous);

        try
        {
            // La extracción bloquea (enumera procesos y usa GDI): fuera del hilo del bucle.
            var iconPath = await Task.Run(
                    () => PlayerIdentityResolver.GetProgramIconPath(appUserModelId, executablePath))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(iconPath))
            {
                Fail(key, previous, "no se encontró el icono del ejecutable");
                return;
            }

            var file = new FileInfo(iconPath);
            if (!file.Exists || file.Length == 0)
            {
                Fail(key, previous, "el archivo del icono está vacío");
                return;
            }

            var artwork = new ArtworkInfo(
                file.FullName,
                file.Name,
                "image/png",
                StableHash($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}"),
                (int)file.Length);

            // force: el caché de subidas puede tener la URL vieja (ya caducada en el host).
            var url = await _upload
                .UploadAsync(artwork, CancellationToken.None, force: true)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(url))
            {
                Fail(key, previous, "el host no devolvió una URL");
                return;
            }

            // Discord hace proxy automáticamente de las URLs HTTPS públicas en el RPC de
            // escritorio, así que se envía la URL cruda (Litterbox). La conversión a mp:
            // no renderizaba en el cliente y el icono no aparecía, así que se omite.
            _retryAfter.TryRemove(key, out _);
            Remember(key, url, DateTimeOffset.UtcNow + RemoteAssetPolicy.EntryTtl);
            _log.Info($"Icono del reproductor listo: {iconPath} → {url}");
            resolved?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo preparar el icono del reproductor: {ex.Message}");
            Fail(key, previous, ex.Message);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Un fallo no debe tirar una URL que todavía puede servir: si la anterior sigue viva
    /// se conserva y solo se pospone el reintento.
    /// </summary>
    private void Fail(string key, Entry previous, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        _retryAfter[key] = now + NegativeTtl;

        if (previous.Url is { Length: > 0 } && previous.CreatedAt + RemoteAssetPolicy.HostLifetime > now)
        {
            _log.Debug($"Se mantiene el icono anterior de «{key}»: {reason}.");
            _entries[key] = previous;
            return;
        }

        _log.Debug($"Sin icono para «{key}»: {reason}.");
        Remember(key, null, now + NegativeTtl);
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
                _retryAfter.TryRemove(pair.Key, out _);
            }
        }
    }

    private void Remember(string key, string? url, DateTimeOffset expiresAt)
    {
        _entries[key] = new Entry(url, DateTimeOffset.UtcNow, expiresAt);

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

            var now = DateTimeOffset.UtcNow;
            var entries = new Dictionary<string, SavedEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _entries)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value.Url) && pair.Value.ExpiresAt > now)
                {
                    entries[pair.Key] = new SavedEntry
                    {
                        Url = pair.Value.Url,
                        CreatedAt = pair.Value.CreatedAt,
                    };
                }
            }

            var payload = new CacheFile
            {
                Version = CacheVersion,
                ClientId = _options.DiscordClientId,
                SavedAt = now,
                Entries = entries,
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
            if (payload is null || !string.Equals(payload.Version, CacheVersion, StringComparison.Ordinal))
            {
                return;
            }

            // Las URLs pertenecen a la aplicación de Discord con la que se generaron.
            if (!string.Equals(payload.ClientId ?? string.Empty, _options.DiscordClientId, StringComparison.Ordinal))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var pair in payload.Entries ?? new Dictionary<string, SavedEntry>())
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value.Url))
                {
                    continue;
                }

                var expiresAt = pair.Value.CreatedAt + RemoteAssetPolicy.EntryTtl;
                if (expiresAt <= now)
                {
                    continue;
                }

                _entries[pair.Key] = new Entry(pair.Value.Url, pair.Value.CreatedAt, expiresAt);
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

        public Dictionary<string, SavedEntry>? Entries { get; set; }
    }

    private sealed class SavedEntry
    {
        public string? Url { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
