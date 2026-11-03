using System.Drawing.Drawing2D;

namespace CS2ModVerifier.App;

/// <summary>The tray icon: the shield, and the shield with a dot while there are unread notifications.</summary>
internal static class TrayIcons
{
    public static readonly Icon Normal = SystemIcons.Shield;

    public static readonly Icon Unread = WithDot(SystemIcons.Shield);

    private static Icon WithDot(Icon icon)
    {
        const int Size = 32;
        using var bitmap = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawIcon(icon, new Rectangle(0, 0, Size, Size));

            // A large dot in the corner: tray icons are shown at about 16 pixels.
            using var white = new SolidBrush(Color.White);
            using var dot = new SolidBrush(Badges.High);
            g.FillEllipse(white, 13, 13, 19, 19);
            g.FillEllipse(dot, 15, 15, 15, 15);
        }

        // The icon lives as long as the app, so its handle does not need to be released.
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
