using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HoyoTray;

/// <summary>Colors that follow the Windows system (taskbar) light/dark setting.</summary>
class Theme
{
    public Color Back, Border, Text, Dim, Hover, Track, Separator;
    public bool Dark;
    public static readonly Color Resin = Color.FromArgb(90, 169, 255);
    public static readonly Color Battery = Color.FromArgb(245, 184, 61);
    public static readonly Color Trailblaze = Color.FromArgb(196, 146, 255);
    public static readonly Color Good = Color.FromArgb(76, 195, 138);
    public static readonly Color Warn = Color.FromArgb(255, 159, 67);

    public static Theme Current()
    {
        bool light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { }

        return light
            ? new Theme
            {
                Back = Color.FromArgb(249, 249, 249), Border = Color.FromArgb(222, 222, 222),
                Text = Color.FromArgb(26, 26, 26), Dim = Color.FromArgb(110, 110, 110),
                Hover = Color.FromArgb(234, 234, 234), Track = Color.FromArgb(225, 225, 225),
                Separator = Color.FromArgb(229, 229, 229),
            }
            : new Theme
            {
                Dark = true,
                Back = Color.FromArgb(40, 40, 40), Border = Color.FromArgb(60, 60, 60),
                Text = Color.FromArgb(245, 245, 245), Dim = Color.FromArgb(160, 160, 160),
                Hover = Color.FromArgb(58, 58, 58), Track = Color.FromArgb(68, 68, 68),
                Separator = Color.FromArgb(58, 58, 58),
            };
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---- Windows 11 rounded popup corners ----
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int)); } catch { }
    }

    public static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        try { DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int)); } catch { }
    }

    // ---- Tray icon ----
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Selectable tray icons: key (embedded &lt;key&gt;.png) and display name.</summary>
    public static readonly (string Key, string Name)[] IconChoices =
        [("resin", "Original Resin"), ("trailblaze", "Trailblaze Power"), ("battery", "Battery Charge")];

    public static Bitmap LoadArt(string key)
    {
        if (!IconChoices.Any(c => c.Key == key)) key = "resin";
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream($"{key}.png")!;
        return new Bitmap(stream);
    }

    /// <summary>The chosen icon art, plus a greyed-out copy for error states.</summary>
    public static (Icon ok, Icon bad) TrayIcons(string key)
    {
        var size = SystemInformation.SmallIconSize;
        using var art = LoadArt(key);
        using var ok = Resize(art, size);
        using var bad = Grayscale(ok);
        return (ToIcon(ok), ToIcon(bad));
    }

    static Bitmap Resize(Image src, Size size)
    {
        var bmp = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(Point.Empty, size));
        return bmp;
    }

    static Bitmap Grayscale(Image src)
    {
        var bmp = new Bitmap(src.Width, src.Height);
        using var g = Graphics.FromImage(bmp);
        var m = new ColorMatrix([
            [.3f, .3f, .3f, 0, 0],
            [.59f, .59f, .59f, 0, 0],
            [.11f, .11f, .11f, 0, 0],
            [0, 0, 0, .8f, 0],
            [0, 0, 0, 0, 1],
        ]);
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(m);
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return bmp;
    }

    static Icon ToIcon(Bitmap bmp)
    {
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }
}
