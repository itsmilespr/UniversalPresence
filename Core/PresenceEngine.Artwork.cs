using System.Collections.Concurrent;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using UniversalPresence.Media;

namespace UniversalPresence.Core;

/// <summary>
/// Portadas e iconos: subida al host público, caché y revalidación, búsqueda en catálogo,
/// portada propia del usuario y cola de reintentos.
/// </summary>
public sealed partial class PresenceEngine : IAsyncDisposable, IDisposable
{
    private DateTimeOffset _nextQueueKick = DateTimeOffset.MinValue;
    // Portadas propias subidas en esta ejecución (clave = ruta del archivo).
    private readonly Dictionary<string, string> _overrideUrls = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _overrideGate = new();

    private volatile string? _lastLookupKey;

    private volatile string? _lastLookupUrl;
    // Resoluciones de portada en segundo plano: el bucle nunca espera a la red.
    // La clave queda reservada mientras hay una subida, búsqueda o conversión en curso.
    private readonly ConcurrentDictionary<string, byte> _resolvingCovers = new(StringComparer.Ordinal);

    private int _queueDrainRunning;

    private DateTimeOffset _nextQueueDrain = DateTimeOffset.MinValue;

    /// <summary>
    /// Reintenta en segundo plano las subidas pendientes. Nunca bloquea el bucle: si el
    /// host sigue caído, la portada actual no se toca y se vuelve a intentar más tarde.
    /// </summary>
    private void StartQueueDrain()
    {
        if (Interlocked.Exchange(ref _queueDrainRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var uploaded = await _artworkUpload.DrainQueueAsync(_cts.Token).ConfigureAwait(false);

                if (uploaded > 0)
                {
                    _log.Info($"Se recuperaron {uploaded} portada(s) que no se pudieron subir antes.");
                    Interlocked.Exchange(ref _forceRepublish, 1);
                    _signal.Set();
                }
            }
            catch (OperationCanceledException)
            {
                // cerrando
            }
            catch (Exception ex)
            {
                _log.Debug($"Reintento de subidas pendientes falló: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _queueDrainRunning, 0);
            }
        });
    }

    /// <summary>
    /// Pista en la que el usuario pidió «buscar otra portada»: hasta que cambie la canción
    /// (o la búsqueda no encuentre nada) se prefiere el catálogo a la carátula del reproductor,
    /// que es justo la que se quiere sustituir.
    /// </summary>
    private volatile string? _preferCatalogKey;

    private static string ArtworkKey(MediaSnapshot snapshot) =>
        $"{snapshot.Title}|{snapshot.Artist}|{snapshot.Album}";

    /// <summary>
    /// Resuelve la portada sin bloquear el bucle.
    ///
    /// Antes se esperaba aquí a Catbox + external-assets (segundos en cada cambio de
    /// pista) y por eso el título, la barra y la portada aparecían tarde. Ahora una URL
    /// ya resuelta se reutiliza al instante y, si hay que subir o buscar la carátula, se
    /// lanza en segundo plano y la actividad se republica en cuanto está lista.
    /// </summary>
    private MediaSnapshot? ApplyArtwork(MediaSnapshot? snapshot, MediaSnapshot? previous)
    {
        // Some players drop the SMTC thumbnail while paused. Carry the previous
        // snapshot's artwork over for the same track so the cover never disappears.
        snapshot = CarryOverArtwork(snapshot, previous);

        if (snapshot is null)
        {
            _lastLookupKey = null;
            _lastLookupUrl = null;
            return null;
        }

        // Same track: la URL anterior es solo un respaldo (pausa / relectura). Si la
        // caché ya tiene una URL nueva por una renovación, esa manda.
        var carriedUrl = SameMediaTrack(previous, snapshot) ? previous?.PublicArtworkUrl : null;

        var artwork = _options.Artwork;

        if (artwork.IsDisabled)
        {
            return snapshot;
        }

        // Tras «buscar otra portada» se intenta primero el catálogo, aunque el reproductor
        // traiga una carátula: es precisamente la que el usuario quiere cambiar.
        var preferCatalog = artwork.UsesLookup
            && _preferCatalogKey is not null
            && string.Equals(_preferCatalogKey, ArtworkKey(snapshot), StringComparison.Ordinal);

        if (!preferCatalog)
        {
            _preferCatalogKey = null;
        }

        if (artwork.UsesUpload && snapshot.Artwork is not null && !preferCatalog)
        {
            var cachedCover = _artworkUpload.GetCachedCover(snapshot.Artwork);

            if (cachedCover is not null)
            {
                // La URL se revalida a las 24 h y se renueva a las 60 h: Litterbox borra el
                // archivo a las 72 h y antes se guardaba la URL durante días.
                if (cachedCover.NeedsRevalidation)
                {
                    StartCoverRevalidation(snapshot.Artwork, cachedCover.Url!);
                }

                if (cachedCover.NeedsRefresh)
                {
                    StartCoverUpload(snapshot.Artwork, force: true);
                }

                if (!string.IsNullOrWhiteSpace(cachedCover.Url))
                {
                    // Discord hace proxy automáticamente de las URLs HTTPS públicas en el
                    // RPC de escritorio, así que se envía la URL cruda (más fiable que el
                    // mp:, que en este cliente no se resolvía).
                    return snapshot with { PublicArtworkUrl = cachedCover.Url };
                }
            }
            else
            {
                StartCoverUpload(snapshot.Artwork);
            }

            // Sin URL nueva (subida en curso o fallo reciente): se conserva la anterior de
            // la misma pista mientras siga siendo válida.
            if (!string.IsNullOrWhiteSpace(carriedUrl))
            {
                return snapshot with { PublicArtworkUrl = carriedUrl };
            }

            // Si el modo no permite buscar en catálogo, la pista se queda con la carátula
            // del reproductor o con la anterior de la misma pista.
            if (!artwork.UsesLookup)
            {
                return snapshot;
            }
        }

        if (!string.IsNullOrWhiteSpace(carriedUrl))
        {
            snapshot = snapshot with { PublicArtworkUrl = carriedUrl };
        }

        if (!artwork.UsesLookup)
        {
            _lastLookupKey = null;
            _lastLookupUrl = null;
            return snapshot;
        }

        var key = $"{snapshot.Title}|{snapshot.Artist}|{snapshot.Album}";

        if (string.Equals(key, _lastLookupKey, StringComparison.Ordinal))
        {
            return _lastLookupUrl is null ? snapshot : snapshot with { PublicArtworkUrl = _lastLookupUrl };
        }

        _lastLookupKey = key;
        _lastLookupUrl = null;
        StartLookup(snapshot, key);
        return snapshot;
    }

    /// <summary>
    /// Icono del reproductor cacheado por AUMID. Antes se extraía, se subía y se convertía
    /// en cada vuelta del bucle (una vez por segundo) aunque nada hubiera cambiado; ahora
    /// se hace una sola vez por reproductor y el cambio de reproductor lo muestra al
    /// instante.
    /// </summary>
    private MediaSnapshot? ApplyProgramIcon(MediaSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var url = _programIcons.GetOrRequest(
            snapshot.AppUserModelId,
            snapshot.PlayerExecutable,
            OnProgramIconReady);

        return string.IsNullOrWhiteSpace(url) ? snapshot : snapshot with { ProgramIconUrl = url };
    }

    /// <summary>Se llama desde un hilo de fondo cuando el icono ya tiene URL pública.</summary>
    private void OnProgramIconReady()
    {
        // El icono ya tiene URL pública (cruda): se republica al instante para que el
        // badge aparezca sin esperar a ningún proxy.
        Interlocked.Exchange(ref _forceRepublish, 1);
        _signal.Set();
    }

    /// <summary>
    /// Aplica una portada propia configurada para esta aplicación (Configuración → Avanzado).
    /// Acepta una URL https (se usa tal cual) o un archivo local, que se publica con el
    /// mismo mecanismo que las carátulas de SMTC y se cachea por ruta.
    /// </summary>
    private MediaSnapshot? ApplyCoverOverride(MediaSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        // Prioridad: canción concreta > aplicación > búsqueda en catálogo. La de canción
        // permite corregir una portada equivocada sin cambiar la de todo el reproductor.
        var trackSource = _options.ResolveTrackCoverOverride(snapshot.Artist, snapshot.Title);
        var source = !string.IsNullOrWhiteSpace(trackSource)
            ? trackSource
            : _options.ResolveCoverOverride(snapshot.AppUserModelId, snapshot.PlayerName);

        if (!string.IsNullOrWhiteSpace(trackSource))
        {
            _log.Debug($"Portada propia por canción para «{snapshot.Title}»: {trackSource}");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return snapshot;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            return snapshot with { PublicArtworkUrl = source, ArtworkDecision = "override" };
        }

        try
        {
            if (!File.Exists(source))
            {
                return snapshot;
            }

            var key = "override|" + source;

            lock (_overrideGate)
            {
                if (_overrideUrls.TryGetValue(key, out var known))
                {
                    return snapshot with { PublicArtworkUrl = known, ArtworkDecision = "override" };
                }
            }

            // Host propio con carpeta pública: basta copiar el archivo a la caché.
            if (!_options.Artwork.UsesUpload && _options.Artwork.UsesExternalUrl)
            {
                var directory = _options.Artwork.ResolvedCacheDirectory;
                Directory.CreateDirectory(directory);
                var name = "override-" + ShortHash(source) + Path.GetExtension(source).ToLowerInvariant();
                var target = Path.Combine(directory, name);
                if (!File.Exists(target) || File.GetLastWriteTimeUtc(target) < File.GetLastWriteTimeUtc(source))
                {
                    File.Copy(source, target, overwrite: true);
                }

                var hostedUrl = $"{_options.Artwork.ExternalUrlBase}/{name}";
                lock (_overrideGate)
                {
                    _overrideUrls[key] = hostedUrl;
                }

                return snapshot with { PublicArtworkUrl = hostedUrl, ArtworkDecision = "override" };
            }

            if (!_options.Artwork.UsesUpload)
            {
                return snapshot;
            }

            if (_resolvingCovers.TryAdd(key, 0))
            {
                var info = new ArtworkInfo(
                    source,
                    Path.GetFileName(source),
                    ContentTypeFor(source),
                    "override-" + ShortHash(source),
                    (int)new FileInfo(source).Length);

                _ = Task.Run(() => ResolveOverrideCoverAsync(key, info));
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo aplicar la portada propia: {ex.Message}");
        }

        return snapshot;
    }

    private async Task ResolveOverrideCoverAsync(string key, ArtworkInfo info)
    {
        try
        {
            var url = await _artworkUpload.UploadAsync(info, _cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            lock (_overrideGate)
            {
                _overrideUrls[key] = url;
            }

            _log.Info($"Portada propia publicada: {info.FileName} → {url}");
            Interlocked.Exchange(ref _forceRepublish, 1);
            _signal.Set();
        }
        catch (OperationCanceledException)
        {
            // cerrando
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo subir la portada propia: {ex.Message}");
        }
        finally
        {
            _resolvingCovers.TryRemove(key, out _);
        }
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };

    private static string ShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private void MaybeKickUploadQueue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextQueueKick)
        {
            return;
        }

        _nextQueueKick = now.AddSeconds(30);
        StartQueueDrain();
    }

    private static MediaSnapshot? CarryOverArtwork(MediaSnapshot? snapshot, MediaSnapshot? previous)
    {
        if (snapshot is { Artwork: null }
            && previous?.Artwork is not null
            && SameMediaTrack(previous, snapshot))
        {
            return snapshot with { Artwork = previous.Artwork };
        }

        return snapshot;
    }

    private static string CoverKey(ArtworkInfo artwork) =>
        string.IsNullOrWhiteSpace(artwork.Hash) ? artwork.FileName : artwork.Hash;

    /// <summary>Sube la carátula al host público en segundo plano y republica al terminar.</summary>
    private void StartCoverUpload(ArtworkInfo artwork, bool force = false)
    {
        var coverKey = CoverKey(artwork);

        if (!_resolvingCovers.TryAdd(coverKey, 0))
        {
            return;
        }

        _ = Task.Run(() => ResolveCoverAsync(coverKey, artwork, force));
    }

    private async Task ResolveCoverAsync(string coverKey, ArtworkInfo artwork, bool force)
    {
        try
        {
            var url = await _artworkUpload.UploadAsync(artwork, _cts.Token, force).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            _log.Info($"Portada lista: {artwork.FileName} → {url}");

            // La siguiente vuelta del bucle la toma de la caché y la publica al momento.
            Interlocked.Exchange(ref _forceRepublish, 1);
            _signal.Set();
        }
        catch (OperationCanceledException)
        {
            // cerrando
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo preparar la portada: {ex.Message}");
        }
        finally
        {
            _resolvingCovers.TryRemove(coverKey, out _);
        }
    }

    /// <summary>
    /// Comprueba en segundo plano que una URL de portada sigue viva. Si el host ya la borró,
    /// se sube otra y se republica; si solo hay dudas (red, 5xx), no se toca.
    /// </summary>
    private void StartCoverRevalidation(ArtworkInfo artwork, string url)
    {
        var coverKey = CoverKey(artwork);

        if (!_resolvingCovers.TryAdd(coverKey, 0))
        {
            return;
        }

        _ = Task.Run(() => RevalidateCoverAsync(coverKey, artwork, url));
    }

    private async Task RevalidateCoverAsync(string coverKey, ArtworkInfo artwork, string url)
    {
        try
        {
            var alive = await _artworkUpload.IsUrlAliveAsync(url, _cts.Token).ConfigureAwait(false);
            _artworkUpload.MarkRevalidated(url);

            if (alive != false)
            {
                return;
            }

            _log.Info("La URL pública de la portada ya no existe; se sube otra.");
            _artworkUpload.Invalidate(artwork);

            var fresh = await _artworkUpload
                .UploadAsync(artwork, _cts.Token, force: true)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(fresh))
            {
                return;
            }

            _log.Info($"Portada renovada: {artwork.FileName} → {fresh}");
            Interlocked.Exchange(ref _forceRepublish, 1);
            _signal.Set();
        }
        catch (OperationCanceledException)
        {
            // cerrando
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo revalidar la portada: {ex.Message}");
        }
        finally
        {
            _resolvingCovers.TryRemove(coverKey, out _);
        }
    }

    /// <summary>Busca una portada pública (iTunes + Deezer) en segundo plano y republica al terminar.</summary>
    private void StartLookup(MediaSnapshot snapshot, string key)
    {
        if (!_resolvingCovers.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(() => LookupCoverAsync(snapshot, key));
    }

    private async Task LookupCoverAsync(MediaSnapshot snapshot, string key)
    {
        try
        {
            var rawUrl = await _artworkLookup
                .ResolveAsync(snapshot.Title, snapshot.Artist, snapshot.Album, _cts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                // La búsqueda manual no encontró nada mejor: se vuelve a la carátula del
                // reproductor en lugar de dejar la pista sin portada.
                ClearCatalogPreference(key);
                return;
            }

            // Si mientras tanto empezó otra canción, el resultado ya no sirve.
            if (!string.Equals(_lastLookupKey, key, StringComparison.Ordinal))
            {
                return;
            }

            // Discord hace proxy automáticamente de las URLs HTTPS públicas (Apple/Deezer)
            // en el RPC de escritorio, así que se envía la URL cruda: la conversión a mp:
            // no renderizaba en el cliente y la portada no aparecía.
            _lastLookupUrl = rawUrl;

            Interlocked.Exchange(ref _forceRepublish, 1);
            _signal.Set();
        }
        catch (OperationCanceledException)
        {
            // Al cerrar no hay nada que reintentar; un timeout de red sí debe devolver la
            // pista a su carátula normal.
            if (!_cts.IsCancellationRequested)
            {
                ClearCatalogPreference(key);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Búsqueda de portada fallida: {ex.Message}");
            ClearCatalogPreference(key);
        }
        finally
        {
            _resolvingCovers.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// La búsqueda manual no dio resultado: la pista vuelve a usar la carátula que entrega
    /// el reproductor en lugar de quedarse sin imagen hasta que cambie la canción.
    /// </summary>
    private void ClearCatalogPreference(string key)
    {
        if (_preferCatalogKey is null
            || !string.Equals(_preferCatalogKey, key, StringComparison.Ordinal)
            || !string.Equals(_lastLookupKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _preferCatalogKey = null;
        Interlocked.Exchange(ref _forceRepublish, 1);
        _signal.Set();
    }

    /// <summary>
    /// Olvida la portada cacheada de la pista actual y la vuelve a buscar (menú de bandeja).
    /// Devuelve false cuando no hay nada sonando.
    /// </summary>
    public bool RefreshCurrentCover()
    {
        if (_lastSnapshot is not { IsActive: true } snapshot || string.IsNullOrWhiteSpace(snapshot.Title))
        {
            _log.Debug("No hay una pista activa de la que buscar la portada de nuevo.");
            return false;
        }

        _artworkLookup.Forget(snapshot.Title, snapshot.Artist, snapshot.Album);
        _preferCatalogKey = ArtworkKey(snapshot);
        _lastLookupKey = null;
        _lastLookupUrl = null;
        _desiredKey = null;
        Interlocked.Exchange(ref _forceRepublish, 1);
        _signal.Set();
        _log.Info($"Buscando de nuevo la portada de «{snapshot.Title}».");
        Updated?.Invoke();
        return true;
    }

    /// <summary>
    /// Las referencias WinRT del bucle (sesiones, metadatos, miniaturas) solo se liberan en
    /// un GC de gen2, y con un heap gestionado de ~2 MB puede no llegar nunca: la memoria
    /// privada subía sin parar (480 MB tras unos minutos). Se comprueba cada 5 s y, si
    /// supera 80 MB, se fuerza una recolección completa en segundo plano. Así el pico se
    /// queda cerca de 90 MB en lugar de crecer hasta los 200 MB.
    /// </summary>
}
