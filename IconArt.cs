using System.Drawing.Drawing2D;

namespace BtQuickConnect;

/// <summary>Огонёк с руной Bluetooth: круг цвета состояния и белая руна внутри.</summary>
internal static class IconArt
{
    public static readonly Color Red = Color.FromArgb(226, 60, 58);
    public static readonly Color Orange = Color.FromArgb(242, 150, 20);
    public static readonly Color Green = Color.FromArgb(46, 180, 74);
    public static readonly Color Dim = Color.FromArgb(125, 88, 40);

    /// <summary>Рисует с 4-кратным запасом и уменьшает — иначе руна в 16 px рассыпается.</summary>
    public static Bitmap Render(int size, Color color)
    {
        const int ss = 4;
        int big = size * ss;
        using var hi = new Bitmap(big, big);
        using (var g = Graphics.FromImage(hi))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float pad = big * 0.03f, d = big - 2 * pad;
            using (var fill = new SolidBrush(color))
                g.FillEllipse(fill, pad, pad, d, d);
            float ring = Math.Max(ss, big * 0.045f);
            using (var pen = new Pen(Darker(color, 0.62f), ring))
                g.DrawEllipse(pen, pad + ring / 2, pad + ring / 2, d - ring, d - ring);

            // руна в квадрате 24×24: M7 7 L17 17 L12 22 V2 L17 7 L7 17
            float u = big / 24f * 0.82f, ox = (big - 24 * u) / 2, oy = (big - 24 * u) / 2;
            PointF P(float x, float y) => new(ox + x * u, oy + y * u);
            using var rune = new Pen(Color.White, Math.Max(ss * 1.4f, big * (size <= 20 ? 0.11f : 0.085f)))
            {
                LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round,
            };
            g.DrawLines(rune, new[] { P(7, 7), P(17, 17), P(12, 22), P(12, 2), P(17, 7), P(7, 17) });
        }

        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(hi, 0, 0, size, size);
        }
        return bmp;
    }

    public static Icon MakeIcon(int size, Color color)
    {
        using var bmp = Render(size, color);
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
        return icon;
    }

    private static Color Darker(Color c, float k) =>
        Color.FromArgb(c.A, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
}
