using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace UniversalPresence.Tray;

public enum TrayVisualState
{
    Idle,
    Playing,
    Paused,
    Error,
}

/// <summary>
/// Tray icons are drawn at runtime (no binary assets, no designer): a rounded square
/// with either an equalizer or a pause glyph, encoded as a multi-size PNG icon.
/// </summary>
internal static class TrayIcons
{
    private static readonly int[] Sizes = { 16, 20, 24, 32 };
    private static readonly Dictionary<TrayVisualState, Icon> Cache = new();

    public static Icon Get(TrayVisualState state)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(state, out var icon))
            {
                icon = Create(state);
                Cache[state] = icon;
            }

            return icon;
        }
    }

    private static Icon Create(TrayVisualState state)
    {
        var background = state switch
        {
            TrayVisualState.Playing => Color.FromArgb(88, 101, 242),
            TrayVisualState.Paused => Color.FromArgb(110, 116, 128),
            TrayVisualState.Error => Color.FromArgb(190, 62, 62),
            _ => Color.FromArgb(68, 71, 78),
        };

        var frames = Sizes.Select(size => (size, Png(size, background, state))).ToArray();
        var ico = BuildIco(frames);
        var size16 = SystemInformation.SmallIconSize;

        return new Icon(new MemoryStream(ico), new Size(size16.Width, size16.Height));
    }

    private static byte[] Png(int size, Color background, TrayVisualState state)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var brush = new SolidBrush(background);
            using var backgroundPath = RoundedRect(new RectangleF(0.5f, 0.5f, size - 1f, size - 1f), size * 0.24f);
            graphics.FillPath(brush, backgroundPath);

            using var glyph = new SolidBrush(Color.White);

            if (state == TrayVisualState.Paused)
            {
                var barWidth = size * 0.13f;
                var barHeight = size * 0.38f;
                var top = (size - barHeight) / 2f;
                graphics.FillRectangle(glyph, size * 0.33f, top, barWidth, barHeight);
                graphics.FillRectangle(glyph, size * 0.54f, top, barWidth, barHeight);
            }
            else
            {
                using var font = new Font("Segoe UI Symbol", size * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                graphics.DrawString("\u266A", font, glyph, new RectangleF(0, 0, size, size), sf);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static GraphicsPath RoundedRect(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2f;

        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static byte[] BuildIco((int Size, byte[] Png)[] frames)
    {
        using var stream = new MemoryStream();

        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)frames.Length);

            var offset = 6 + (16 * frames.Length);

            foreach (var (size, png) in frames)
            {
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(png.Length);
                writer.Write(offset);
                offset += png.Length;
            }

            foreach (var (_, png) in frames)
            {
                writer.Write(png);
            }
        }

        return stream.ToArray();
    }
}
