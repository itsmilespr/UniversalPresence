using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace UniversalPresence.Media;

/// <summary>
/// Reduce las carátulas antes de subirlas.
///
/// El SMTC entrega miniaturas de hasta 4–5 MB (algunos reproductores entregan el PNG del
/// álbum a resolución completa). Discord nunca dibuja la imagen a más de ~300 px, así que
/// subir un archivo de 3000 px solo cuesta tiempo, ancho de banda y a veces el rechazo del
/// host. Aquí se limita el lado a <see cref="MaxSide"/> y se recomprime en JPEG/PNG según
/// cuál pese menos, conservando el original cuando ya era pequeño.
/// </summary>
public static class ImageOptimizer
{
    public const int MaxSide = 1000;

    /// <summary>Por encima de este tamaño se recomprime aunque las dimensiones ya encajen.</summary>
    public const long MaxBytes = 400 * 1024;

    /// <summary>Resultado listo para subir; si no convenía tocarla, devuelve la original.</summary>
    public sealed record Prepared(byte[] Bytes, string ContentType, string FileName, bool Changed);

    public static Prepared Prepare(byte[] input, string contentType, string fileName)
    {
        if (input.Length == 0)
        {
            return new Prepared(input, contentType, fileName, false);
        }

        try
        {
            using var inputStream = new MemoryStream(input, writable: false);
            using var source = Image.FromStream(inputStream);

            var alreadySmall = source.Width <= MaxSide
                && source.Height <= MaxSide
                && input.Length <= MaxBytes;

            if (alreadySmall)
            {
                return new Prepared(input, contentType, fileName, false);
            }

            var scale = Math.Min(
                1.0,
                (double)MaxSide / Math.Max(source.Width, source.Height));

            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Fondo blanco: si el PNG original tenía transparencia, el JPEG no la
                // convierte en negro.
                graphics.Clear(Color.White);
                graphics.DrawImage(source, 0, 0, width, height);
            }

            var jpeg = Encode(target, ImageFormat.Jpeg);
            var png = Encode(target, ImageFormat.Png);

            // El PNG solo gana si de verdad pesa menos (portadas planas, capturas).
            var usePng = png.Length < jpeg.Length;
            var bytes = usePng ? png : jpeg;

            if (bytes.Length >= input.Length && scale >= 1.0)
            {
                return new Prepared(input, contentType, fileName, false);
            }

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "cover";
            }

            return new Prepared(
                bytes,
                usePng ? "image/png" : "image/jpeg",
                baseName + (usePng ? ".png" : ".jpg"),
                true);
        }
        catch
        {
            // Un formato raro (webp sin códec, imagen dañada) se sube tal cual: mejor una
            // portada grande que ninguna.
            return new Prepared(input, contentType, fileName, false);
        }
    }

    private static byte[] Encode(Image image, ImageFormat format)
    {
        using var buffer = new MemoryStream();

        if (ReferenceEquals(format, ImageFormat.Jpeg))
        {
            var encoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

            if (encoder is not null)
            {
                using var parameters = new EncoderParameters(1);
                using var quality = new EncoderParameter(Encoder.Quality, 88L);
                parameters.Param[0] = quality;
                image.Save(buffer, encoder, parameters);
                return buffer.ToArray();
            }
        }

        image.Save(buffer, format);
        return buffer.ToArray();
    }
}
