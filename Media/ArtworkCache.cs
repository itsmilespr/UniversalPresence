using System.Security.Cryptography;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using Windows.Storage.Streams;

namespace UniversalPresence.Media;

/// <summary>
/// Reads the SMTC thumbnail into memory and caches it on disk.
///
/// Discord cannot fetch loopback/local images: external asset URLs must be publicly
/// reachable over HTTPS. So the extracted file is either served from a user-provided
/// public base URL (Artwork.Mode = ExternalUrl) or the activity falls back to the
/// Discord application icon. The bytes are cached regardless so --test can prove that
/// the artwork was really read from the player.
/// </summary>
public sealed class ArtworkCache
{
    private const long MaxArtworkBytes = 8 * 1024 * 1024;

    private readonly ArtworkOptions _options;
    private readonly Log _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, ArtworkInfo?> _memo = new(StringComparer.Ordinal);

    public ArtworkCache(AppOptions options, Log log)
    {
        _options = options.Artwork;
        _log = log;
        Directory = _options.ResolvedCacheDirectory;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch (Exception ex)
        {
            _log.Warn($"No se pudo crear la carpeta de carátulas '{Directory}': {ex.Message}");
        }
    }

    public string Directory { get; }

    public async Task<ArtworkInfo?> GetAsync(IRandomAccessStreamReference? reference, string mediaKey, CancellationToken cancellationToken)
    {
        if (reference is null || _options.IsDisabled)
        {
            return null;
        }

        lock (_gate)
        {
            if (_memo.TryGetValue(mediaKey, out var cached))
            {
                return cached;
            }
        }

        ArtworkInfo? info = null;

        try
        {
            info = await ExtractAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A closing session can invalidate the stream at any time.
            _log.Debug($"No se pudo leer la carátula: {ex.Message}");
        }

        lock (_gate)
        {
            if (_memo.Count > 128)
            {
                _memo.Clear();
            }

            _memo[mediaKey] = info;
        }

        return info;
    }

    private async Task<ArtworkInfo?> ExtractAsync(IRandomAccessStreamReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = await reference.OpenReadAsync();
        if (stream is null || stream.Size == 0 || stream.Size > MaxArtworkBytes)
        {
            return null;
        }

        stream.Seek(0);
        var size = (uint)stream.Size;
        byte[] bytes;

        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            var loaded = await reader.LoadAsync(size);
            bytes = new byte[loaded];
            reader.ReadBytes(bytes);
        }

        if (bytes.Length == 0)
        {
            return null;
        }

        // Some integrations report a list such as "image/jpeg,image/jpe,image/jpg".
        var contentType = (stream.ContentType ?? "")
            .Split(',', ';')[0]
            .Trim();

        if (contentType.Length == 0)
        {
            contentType = "image/jpeg";
        }

        // Discord requires large images to be at least 512x512 and prefers 1024x1024.
        // Resize to 1024 to ensure it shows properly and reduce upload time.
        bytes = ResizeImage(bytes, contentType, 1024);

        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        var fileName = hash + ExtensionFor(contentType);
        var filePath = Path.Combine(Directory, fileName);

        try
        {
            if (!File.Exists(filePath))
            {
                await File.WriteAllBytesAsync(filePath, bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudo guardar la carátula '{filePath}': {ex.Message}");
        }

        return new ArtworkInfo(filePath, fileName, contentType, hash, bytes.Length);
    }

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".jpg",
    };

    /// <summary>
    /// Resizes an image to a maximum dimension while preserving aspect ratio.
    /// Returns the original bytes if resizing fails or the image is already small enough.
    /// </summary>
    private static byte[] ResizeImage(byte[] bytes, string contentType, int maxDimension)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var original = System.Drawing.Image.FromStream(ms);

            // Discord requiere imágenes de al menos 512x512 para large_image.
            // Si es más pequeña, la ampliamos; si es mayor que maxDimension, la reducimos.
            const int minDimension = 512;
            if (original.Width <= maxDimension && original.Height <= maxDimension
                && original.Width >= minDimension && original.Height >= minDimension)
            {
                return bytes; // Ya está en el rango adecuado.
            }

            int targetMax = maxDimension;
            if (original.Width < minDimension || original.Height < minDimension)
            {
                targetMax = minDimension;
            }

            var ratio = Math.Min(
                (double)targetMax / original.Width,
                (double)targetMax / original.Height);

            var newWidth = (int)(original.Width * ratio);
            var newHeight = (int)(original.Height * ratio);

            using var resized = new System.Drawing.Bitmap(newWidth, newHeight);
            using (var g = System.Drawing.Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(original, 0, 0, newWidth, newHeight);
            }

            using var outMs = new MemoryStream();
            var format = contentType.ToLowerInvariant() switch
            {
                "image/png" => System.Drawing.Imaging.ImageFormat.Png,
                "image/gif" => System.Drawing.Imaging.ImageFormat.Gif,
                "image/bmp" => System.Drawing.Imaging.ImageFormat.Bmp,
                _ => System.Drawing.Imaging.ImageFormat.Jpeg,
            };
            resized.Save(outMs, format);
            return outMs.ToArray();
        }
        catch
        {
            return bytes; // If anything fails, use original.
        }
    }
}
