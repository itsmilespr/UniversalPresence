using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>Cola de subidas fallidas, para reintentarlas cuando vuelva la red.</summary>
    private readonly string _queuePath =
        Path.Combine(AppOptions.LocalDataDirectory, "upload-queue.json");
    private readonly object _queueLock = new();

    private const int MaxQueueEntries = 40;
    private const int MaxRetriesPerEntry = 12;

    // La URL de cada carátula se guarda en disco: la segunda vez que suena una canción ya
    // no hay que volver a subirla, ni siquiera tras reiniciar la aplicación. El TTL es menor
    // que la vida real del archivo en Litterbox (72 h) y la antigüedad se conserva para
    // poder revalidar o renovar la URL antes de que se rompa.
    private readonly PersistentCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _revalidated = new(StringComparer.Ordinal);

    // Subidas de la pista que se está mostrando ahora mismo. La cola de reintentos se
    // aparta mientras haya alguna: la carátula visible nunca espera detrás de una antigua.
    private int _foregroundUploads;

    public ArtworkUploadService(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
        // Un fallo se recuerda solo 60 s: si el host tuvo un corte momentáneo, la portada
        // se reintenta dentro de la misma canción en lugar de quedarse sin imagen 10 min.
        _cache = new PersistentCache(
            Path.Combine(AppOptions.LocalDataDirectory, "covers-cache.json"),
            log,
            RemoteAssetPolicy.EntryTtl,
            failureTtl: TimeSpan.FromSeconds(60));
    }

    /// <summary>URL pública ya subida, con su antigüedad y lo que conviene hacer con ella.</summary>
    public sealed record CachedCover(string? Url, TimeSpan Age, bool NeedsRevalidation, bool NeedsRefresh);

    /// <summary>
    /// Devuelve la entrada cacheada de una carátula, aunque esté a punto de caducar. Null solo
    /// cuando no hay nada guardado para esa imagen.
    /// </summary>
    public CachedCover? GetCachedCover(ArtworkInfo artwork)
    {
        var key = KeyFor(artwork);

        if (!_cache.TryGet(key, out var url, out var age))
        {
            return null;
        }

        // Fallo reciente guardado con TTL corto: no se reintenta en cada vuelta del bucle.
        if (string.IsNullOrWhiteSpace(url))
        {
            return new CachedCover(null, age, NeedsRevalidation: false, NeedsRefresh: false);
        }

        // Más vieja que la vida real del archivo: ya no se puede publicar.
        if (age >= RemoteAssetPolicy.HostLifetime)
        {
            _log.Debug($"La URL de la carátula tiene {age.TotalHours:0} h y ya caducó; se sube otra.");
            return new CachedCover(null, age, NeedsRevalidation: false, NeedsRefresh: true);
        }

        // Queda poco margen: se sigue mostrando la actual mientras se sube una nueva.
        if (age >= RemoteAssetPolicy.MaxReuseAge)
        {
            return new CachedCover(url, age, NeedsRevalidation: false, NeedsRefresh: true);
        }

        var needsRevalidation = age >= RemoteAssetPolicy.RevalidateAfter
            && !_revalidated.ContainsKey(url);

        return new CachedCover(url, age, needsRevalidation, NeedsRefresh: false);
    }

    /// <summary>Descarta la URL cacheada de una carátula (el host ya la borró).</summary>
    public void Invalidate(ArtworkInfo artwork) => _cache.Remove(KeyFor(artwork));

    /// <summary>
    /// Olvida los fallos cacheados: al cambiar el host, el token o la conexión hay que
    /// reintentar la subida al momento, no esperar al TTL negativo.
    /// </summary>
    public void InvalidateFailures() => _cache.ClearFailures();

    /// <summary>Marca una URL como comprobada en esta ejecución (no se repite el HEAD).</summary>
    public void MarkRevalidated(string url) => _revalidated[url] = 0;

    public bool IsRevalidated(string url) => _revalidated.ContainsKey(url);

    private static string KeyFor(ArtworkInfo artwork) =>
        string.IsNullOrWhiteSpace(artwork.Hash) ? artwork.FileName : artwork.Hash;

    /// <summary>
    /// Public https URL of the uploaded cover, or null when the upload is off/failed.
    /// <paramref name="force"/> sube otra aunque haya una URL en caché (renovación).
    /// </summary>
    public async Task<string?> UploadAsync(
        ArtworkInfo artwork,
        CancellationToken cancellationToken,
        bool force = false,
        bool background = false)
    {
        if (!_options.Artwork.UsesUpload)
        {
            return null;
        }

        var key = KeyFor(artwork);
        var previous = _cache.Get(key);

        if (!force && !string.IsNullOrWhiteSpace(previous))
        {
            return previous;
        }

        // Un fallo reciente (entrada con valor nulo) se respeta. Sin esto, una portada
        // propia o una entrada de la cola lanzaba una subida completa en cada vuelta del
        // bucle mientras el host estuviera caído (timeouts de hasta 30 s cada vez).
        if (!force && _cache.TryGet(key, out var failed, out _) && string.IsNullOrWhiteSpace(failed))
        {
            return null;
        }

        if (!background)
        {
            Interlocked.Increment(ref _foregroundUploads);
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!background)
            {
                Interlocked.Decrement(ref _foregroundUploads);
            }

            throw;
        }

        try
        {
            if (!force)
            {
                var cached = _cache.Get(key);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    return cached;
                }
            }

            var payload = await PreparePayloadAsync(artwork, cancellationToken).ConfigureAwait(false);
            var url = await UploadToConfiguredHostAsync(payload, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(url))
            {
                // No se borra una URL que todavía puede servir: si la renovación falla, la
                // portada anterior sigue viva hasta su caducidad real.
                if (string.IsNullOrWhiteSpace(previous))
                {
                    _cache.Set(key, null);
                }

                EnqueueForRetry(artwork);
                return null;
            }

            Dequeue(key);
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

            if (string.IsNullOrWhiteSpace(previous))
            {
                _cache.Set(key, null);
            }

            // Sin conexión también se apunta: es justo el caso que la cola debe recuperar.
            EnqueueForRetry(artwork);
            return null;
        }
        finally
        {
            _gate.Release();

            if (!background)
            {
                Interlocked.Decrement(ref _foregroundUploads);
            }
        }
    }

    /// <summary>
    /// Comprueba si una URL sigue sirviendo la imagen. true = vive, false = el host la borró,
    /// null = no se pudo saber (red, 5xx, 403…).
    /// </summary>
    public async Task<bool?> IsUrlAliveAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsPublicHttps(url))
        {
            return false;
        }

        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await Http
                .SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (headResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return false;
            }

            if (headResponse.IsSuccessStatusCode)
            {
                return true;
            }

            if (headResponse.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
            {
                return null;
            }

            // Algunos hosts no aceptan HEAD: se pide solo el primer byte.
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var getResponse = await Http
                .SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (getResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return false;
            }

            return getResponse.IsSuccessStatusCode ? true : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo comprobar la carátula: {ex.Message}");
            return null;
        }
    }

    /// <summary>Imagen ya preparada para subir (posiblemente redimensionada y recomprimida).</summary>
    private sealed record Payload(byte[] Bytes, string ContentType, string FileName);

    /// <summary>
    /// Lee la carátula y, si la opción está activa, la reduce antes de subirla. Discord no
    /// dibuja la imagen a más de ~300 px, así que reenviar un PNG de 3000 px solo hace la
    /// subida más lenta y aumenta el riesgo de que el host la rechace.
    /// </summary>
    private async Task<Payload> PreparePayloadAsync(ArtworkInfo artwork, CancellationToken cancellationToken)
    {
        var bytes = await ReadArtworkAsync(artwork, cancellationToken).ConfigureAwait(false);

        if (!_options.Artwork.OptimizeUploads)
        {
            return new Payload(bytes, artwork.ContentType, artwork.FileName);
        }

        var prepared = ImageOptimizer.Prepare(bytes, artwork.ContentType, artwork.FileName);

        if (prepared.Changed)
        {
            _log.Debug(
                $"Carátula optimizada antes de subir: {bytes.Length / 1024} KB → {prepared.Bytes.Length / 1024} KB.");
        }

        return new Payload(prepared.Bytes, prepared.ContentType, prepared.FileName);
    }

    /// <summary>
    /// Sube al host configurado y, si falla y la opción está activa, al host alternativo
    /// gratuito (Litterbox). Así un host caído o un límite temporal no deja la presencia sin
    /// portada.
    /// </summary>
    private async Task<string?> UploadToConfiguredHostAsync(Payload payload, CancellationToken cancellationToken)
    {
        var mode = _options.Artwork.UploadMode.Trim().ToLowerInvariant();

        string? url = null;
        try
        {
            url = await TryUploadAsync(mode, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Sin red, DNS caído o timeout: no es una respuesta del host, pero el host de
            // respaldo (si es otro servicio) todavía puede servir la carátula.
            _log.Debug($"El host principal de carátulas no respondió: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (!_options.Artwork.AllowFallbackUploadHost
            || string.Equals(mode, "catbox", StringComparison.Ordinal))
        {
            return null;
        }

        _log.Info("El host de portadas no respondió; se intenta el host alternativo (Litterbox).");

        try
        {
            return await UploadToCatboxAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"El host alternativo tampoco respondió: {ex.Message}");
            return null;
        }
    }

    private Task<string?> TryUploadAsync(string mode, Payload payload, CancellationToken cancellationToken) =>
        mode switch
        {
            "imgur" => UploadToImgurAsync(payload, cancellationToken),
            "catbox" => UploadToCatboxAsync(payload, cancellationToken),
            "selfhosted" => UploadToSelfHostedAsync(payload, cancellationToken),
            _ => Task.FromResult<string?>(null),
        };

    private async Task<string?> UploadToImgurAsync(Payload payload, CancellationToken cancellationToken)
    {
        var clientId = _options.Artwork.UploadApiKey.Trim();

        if (clientId.Length == 0)
        {
            _log.Warn(
                "Artwork.UploadMode es «Imgur» pero Artwork.UploadApiKey está vacío " +
                "(Client-ID de Imgur). Se usa la búsqueda de portada como respaldo.");
            return null;
        }

        if (payload.Bytes.Length > ImgurMaxBytes)
        {
            _log.Warn(
                $"La carátula pesa {payload.Bytes.Length / 1024} KB y el límite anónimo de Imgur es " +
                "10 MB. Se usa la búsqueda de portada como respaldo.");
            return null;
        }

        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(payload.Bytes);
        image.Headers.ContentType = new MediaTypeHeaderValue(payload.ContentType);
        form.Add(image, "image", payload.FileName);

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

    private async Task<string?> UploadToCatboxAsync(Payload payload, CancellationToken cancellationToken)
    {
        // Catbox (catbox.moe) devolvía archivos vacíos de forma intermitente, así que se
        // usa Litterbox (litterbox.catbox.moe), la variante con caducidad de la misma
        // infraestructura, que sirve el contenido de forma fiable. Caduca a las 72 h; la
        // caché de subida se invalida y se vuelve a subir cuando el proxy de Discord deja
        // de servir la imagen.
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("fileupload"), "reqtype");
        form.Add(new StringContent("72h"), "time");

        var file = new ByteArrayContent(payload.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(payload.ContentType);
        form.Add(file, "fileToUpload", payload.FileName);

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

    private async Task<string?> UploadToSelfHostedAsync(Payload payload, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.Artwork.UploadUrl)
            ? "https://universal-presence-covers-s0q6cn.freebuff.site"
            : _options.Artwork.UploadUrl.Trim().TrimEnd('/');

        var token = _options.Artwork.UploadApiKey.Trim();
        if (token.Length == 0)
        {
            _log.Warn(
                "Artwork.UploadMode es «SelfHosted» pero Artwork.UploadApiKey está vacío " +
                "(token de subida del host propio). Se usa la búsqueda de portada como respaldo.");
            return null;
        }

        // Raw image body: simpler and immune to multipart parser quirks at the host.
        var content = new ByteArrayContent(payload.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(payload.ContentType);

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/upload")
        {
            Content = content,
        };
        request.Headers.Add("X-Upload-Token", token);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            _log.Warn(
                $"El host propio respondió {(int)response.StatusCode} al subir la carátula" +
                (error.Length > 0 && error.Length <= 160 ? $" («{error}»)" : "") + ".");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.TryGetProperty("url", out var urlElement)
            && urlElement.GetString() is { Length: > 0 } url
            && IsPublicHttps(url))
        {
            _log.Info($"Carátula subida al host propio: {url}");
            return url;
        }

        _log.Warn("El host propio no devolvió un enlace utilizable.");
        return null;
    }

    /// <summary>
    /// Reintenta las subidas que fallaron (sin red, host caído, límite temporal). Se llama al
    /// arrancar y cada vez que empieza una canción nueva; sube como mucho cinco por pasada
    /// para no competir con la portada que se está mostrando ahora mismo.
    /// </summary>
    public async Task<int> DrainQueueAsync(CancellationToken cancellationToken)
    {
        if (!_options.Artwork.UsesUpload)
        {
            return 0;
        }

        List<QueueEntry> snapshot;
        lock (_queueLock)
        {
            snapshot = LoadQueueUnlocked();
        }

        if (snapshot.Count == 0)
        {
            return 0;
        }

        var uploaded = 0;
        var doneKeys = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new List<QueueEntry>();

        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(entry.FilePath))
            {
                // El archivo temporal ya se limpió: la canción volverá a generar su carátula.
                doneKeys.Add(entry.Key);
                continue;
            }

            if (Volatile.Read(ref _foregroundUploads) > 0)
            {
                // La portada de la pista actual manda: se corta la pasada y el resto queda
                // en la cola para el siguiente intento.
                _log.Debug("Drenaje de portadas pausado: hay una subida de la pista actual en curso.");
                break;
            }

            if (uploaded >= 5)
            {
                remaining.Add(entry);
                continue;
            }

            entry.Attempts++;
            var artwork = new ArtworkInfo(entry.FilePath, entry.FileName, entry.ContentType, entry.Hash, entry.ByteCount);

            try
            {
                var url = await UploadAsync(artwork, cancellationToken, background: true).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    uploaded++;
                    doneKeys.Add(entry.Key);
                    _log.Info($"Carátula pendiente subida al recuperar la conexión: {url}");
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Debug($"Reintento de subida fallido: {ex.Message}");
            }

            if (entry.Attempts < MaxRetriesPerEntry)
            {
                remaining.Add(entry);
            }
        }

        lock (_queueLock)
        {
            // Se conserva lo que se haya encolado durante la pasada, pero las entradas
            // reintentadas se sustituyen por su versión actualizada: si se dejara la que
            // hay en disco, el contador de intentos nunca subiría y una portada que falla
            // siempre se reintentaría cada diez minutos para siempre.
            var attempted = new Dictionary<string, QueueEntry>(StringComparer.Ordinal);
            foreach (var entry in remaining)
            {
                attempted[entry.Key] = entry;
            }

            var merged = new List<QueueEntry>();

            foreach (var item in LoadQueueUnlocked())
            {
                if (doneKeys.Contains(item.Key))
                {
                    continue;
                }

                merged.Add(attempted.TryGetValue(item.Key, out var fresh) ? fresh : item);
            }

            foreach (var entry in remaining)
            {
                if (merged.All(item => !string.Equals(item.Key, entry.Key, StringComparison.Ordinal)))
                {
                    merged.Add(entry);
                }
            }

            if (merged.Count > MaxQueueEntries)
            {
                merged = merged
                    .OrderByDescending(item => item.AddedAt)
                    .Take(MaxQueueEntries)
                    .ToList();
            }

            SaveQueueUnlocked(merged);
        }

        return uploaded;
    }

    /// <summary>Número de subidas pendientes (para diagnóstico y autotest).</summary>
    public int PendingRetryCount
    {
        get
        {
            lock (_queueLock)
            {
                return LoadQueueUnlocked().Count;
            }
        }
    }

    /// <summary>Guarda una carátula para reintentar más tarde. Se ignora si ya está en cola.</summary>
    private void EnqueueForRetry(ArtworkInfo artwork)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(artwork.FilePath) || !File.Exists(artwork.FilePath))
            {
                return;
            }

            lock (_queueLock)
            {
                var entries = LoadQueueUnlocked();

                if (entries.Any(item => string.Equals(item.Hash, artwork.Hash, StringComparison.Ordinal)))
                {
                    return;
                }

                entries.Add(new QueueEntry
                {
                    Hash = artwork.Hash,
                    FilePath = artwork.FilePath,
                    FileName = artwork.FileName,
                    ContentType = artwork.ContentType,
                    ByteCount = artwork.ByteCount,
                    AddedAt = DateTimeOffset.Now,
                });

                if (entries.Count > MaxQueueEntries)
                {
                    entries = entries
                        .OrderByDescending(item => item.AddedAt)
                        .Take(MaxQueueEntries)
                        .ToList();
                }

                SaveQueueUnlocked(entries);
            }

            _log.Debug("Subida pendiente guardada para reintentar cuando vuelva la conexión.");
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo guardar la cola de subidas: {ex.Message}");
        }
    }

    private void Dequeue(string key)
    {
        try
        {
            lock (_queueLock)
            {
                var entries = LoadQueueUnlocked();
                if (entries.RemoveAll(item => string.Equals(item.Hash, key, StringComparison.Ordinal)) > 0)
                {
                    SaveQueueUnlocked(entries);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo actualizar la cola de subidas: {ex.Message}");
        }
    }

    private List<QueueEntry> LoadQueueUnlocked()
    {
        try
        {
            if (!File.Exists(_queuePath))
            {
                return new List<QueueEntry>();
            }

            var json = File.ReadAllText(_queuePath);
            return JsonSerializer.Deserialize<List<QueueEntry>>(json) ?? new List<QueueEntry>();
        }
        catch (Exception ex)
        {
            _log.Debug($"Cola de subidas ilegible, se descarta: {ex.Message}");
            return new List<QueueEntry>();
        }
    }

    private void SaveQueueUnlocked(List<QueueEntry> entries)
    {
        Directory.CreateDirectory(AppOptions.LocalDataDirectory);

        if (entries.Count == 0)
        {
            if (File.Exists(_queuePath))
            {
                File.Delete(_queuePath);
            }

            return;
        }

        AtomicFile.WriteAllText(_queuePath, JsonSerializer.Serialize(entries, QueueJson));
    }

    private static readonly JsonSerializerOptions QueueJson = new() { WriteIndented = true };

    private sealed class QueueEntry
    {
        public string Hash { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "image/jpeg";
        public int ByteCount { get; set; }
        public DateTimeOffset AddedAt { get; set; }
        public int Attempts { get; set; }

        /// <summary>Identidad estable de la entrada; la caché usa el hash y el nombre como respaldo.</summary>
        [JsonIgnore]
        public string Key => !string.IsNullOrWhiteSpace(Hash)
            ? Hash
            : !string.IsNullOrWhiteSpace(FileName) ? FileName : FilePath;
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
