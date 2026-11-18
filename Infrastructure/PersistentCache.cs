using System.Text.Json;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// Caché en memoria respaldada por un archivo JSON.
///
/// Se usa para lo que no cambia entre canciones ni entre ejecuciones: la URL pública de
/// una carátula ya subida, su conversión a «mp:» y el resultado de las búsquedas en el
/// catálogo. Sin esto, cada reinicio volvía a subir y a convertir la misma portada, que es
/// lo que hacía que tardara en aparecer.
///
/// Discord sirve los «mp:» mientras la aplicación no cambie, así que una entrada válida se
/// reutiliza durante días.
/// </summary>
public sealed class PersistentCache
{
    private const string CacheVersion = "2";
    private const int MaxEntries = 3000;

    private static readonly TimeSpan SuccessTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(10);

    private readonly string _path;
    private readonly Log _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public PersistentCache(string path, Log log)
    {
        _path = path;
        _log = log;
        Load();
    }

    /// <summary>Valor todavía válido, o null si no está, caducó o es un fallo cacheado.</summary>
    public string? Get(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _entries.Remove(key);
                return null;
            }

            return entry.Value;
        }
    }

    /// <summary>Guarda un acierto (<paramref name="value"/> no nulo) o un fallo (TTL corto).</summary>
    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            _entries[key] = new Entry
            {
                Value = value,
                ExpiresAt = DateTimeOffset.UtcNow + (value is null ? FailureTtl : SuccessTtl),
            };

            if (_entries.Count > MaxEntries)
            {
                Prune();
            }

            Persist();
        }
    }

    /// <summary>Borra los fallos cacheados: tras conectar la cuenta hay que reintentar ya.</summary>
    public void ClearFailures()
    {
        lock (_gate)
        {
            foreach (var pair in _entries
                         .Where(pair => pair.Value.Value is null || pair.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _entries.Remove(pair);
            }

            Persist();
        }
    }

    private void Prune()
    {
        foreach (var key in _entries
                     .Where(pair => pair.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(key);
        }

        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in _entries
                     .OrderBy(pair => pair.Value.ExpiresAt)
                     .Take(_entries.Count - MaxEntries)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(key);
        }
    }

    private void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new CacheFile
            {
                Version = CacheVersion,
                Entries = _entries.ToDictionary(pair => pair.Key, pair => pair.Value),
            };

            File.WriteAllText(_path, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo guardar la caché '{Path.GetFileName(_path)}': {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path));
            if (payload is null || !string.Equals(payload.Version, CacheVersion, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var pair in payload.Entries ?? new Dictionary<string, Entry>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    _entries[pair.Key] = pair.Value;
                }
            }

            _log.Debug($"Caché '{Path.GetFileName(_path)}' cargada: {_entries.Count} entrada(s).");
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo leer la caché '{Path.GetFileName(_path)}': {ex.Message}");
        }
    }

    private sealed class CacheFile
    {
        public string? Version { get; set; }

        public Dictionary<string, Entry>? Entries { get; set; }
    }

    public sealed class Entry
    {
        public string? Value { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }
    }
}
