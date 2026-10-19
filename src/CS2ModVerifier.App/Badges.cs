using System.Drawing.Drawing2D;
using CS2ModVerifier.Core;

namespace CS2ModVerifier.App;

/// <summary>Severity colours and badges. Every badge also carries a letter or word, so colour is never the only cue.</summary>
internal static class Badges
{
    public static readonly Color High = Color.FromArgb(0xC6, 0x28, 0x28);
    public static readonly Color Medium = Color.FromArgb(0xE6, 0x51, 0x00);
    public static readonly Color Low = Color.FromArgb(0xB2, 0x8B, 0x00);
    public static readonly Color Info = Color.FromArgb(0x15, 0x65, 0xC0);
    public static readonly Color Good = Color.FromArgb(0x2E, 0x7D, 0x32);
    public static readonly Color Ignored = Color.FromArgb(0x75, 0x75, 0x75);

    public static Color For(Severity severity) => severity switch
    {
        Severity.High => High,
        Severity.Medium => Medium,
        Severity.Low => Low,
        _ => Info,
    };

    public static string Letter(Severity severity) => severity switch
    {
        Severity.High => "H",
        Severity.Medium => "M",
        Severity.Low => "L",
        _ => "i",
    };

    /// <summary>A round badge with a letter: H, M, L, i, or a check mark for "no open findings".</summary>
    public static void DrawDot(Graphics g, Rectangle bounds, Color color, string letter, Font font)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(color))
        {
            g.FillEllipse(brush, bounds);
        }

        TextRenderer.DrawText(g, letter, font, bounds, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        g.SmoothingMode = SmoothingMode.Default;
    }

    /// <summary>A rounded label such as "HIGH" or "IGNORED". Returns its width.</summary>
    public static int DrawPill(Graphics g, Point location, int height, Color color, string text, Font font)
    {
        var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
        var bounds = new Rectangle(location.X, location.Y, size.Width + 12, height);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Rounded(bounds, height / 2))
        using (var brush = new SolidBrush(color))
        {
            g.FillPath(brush, path);
        }

        g.SmoothingMode = SmoothingMode.Default;
        TextRenderer.DrawText(g, text, font, bounds, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return bounds.Width;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
