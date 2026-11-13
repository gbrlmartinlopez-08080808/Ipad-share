using System.Drawing.Drawing2D;
using System.Drawing.Text;
using IpadShare.App.App;
using static IpadShare.App.Native.NativeMethods;

namespace IpadShare.App.UI;

/// <summary>Windows 11-style palette that follows the system light/dark setting and accent colour.</summary>
internal static class Theme
{
    public static bool Dark { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentText => Color.White;
    public static Color Background { get; private set; }
    public static Color Card { get; private set; }
    public static Color CardBorder { get; private set; }
    public static Color Text { get; private set; }
    public static Color SubText { get; private set; }
    public static Color Control { get; private set; }
    public static Color ControlHover { get; private set; }
    public static Color Track { get; private set; }
    public static Color Good => Color.FromArgb(52, 199, 89);
    public static Color Warn => Color.FromArgb(255, 159, 10);
    public static Color Bad => Color.FromArgb(255, 69, 58);

    public static readonly string FontFamily = PickFont();

    static Theme() => Refresh();

    public static void Refresh()
    {
        Dark = SystemInfo.IsDarkMode();
        Accent = SystemInfo.AccentColor();
        if (Dark)
        {
            Background = Color.FromArgb(32, 32, 32);
            Card = Color.FromArgb(43, 43, 43);
            CardBorder = Color.FromArgb(58, 58, 58);
            Text = Color.FromArgb(255, 255, 255);
            SubText = Color.FromArgb(170, 170, 170);
            Control = Color.FromArgb(58, 58, 58);
            ControlHover = Color.FromArgb(70, 70, 70);
            Track = Color.FromArgb(90, 90, 90);
        }
        else
        {
            Background = Color.FromArgb(243, 243, 243);
            Card = Color.FromArgb(255, 255, 255);
            CardBorder = Color.FromArgb(229, 229, 229);
            Text = Color.FromArgb(27, 27, 27);
            SubText = Color.FromArgb(96, 96, 96);
            Control = Color.FromArgb(246, 246, 246);
            ControlHover = Color.FromArgb(236, 236, 236);
            Track = Color.FromArgb(200, 200, 200);
        }
    }

    private static string PickFont()
    {
        using var fonts = new InstalledFontCollection();
        foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI" })
            if (fonts.Families.Any(f => f.Name == name)) return name;
        return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
    }

    public static Font Body(float size = 10f, FontStyle style = FontStyle.Regular) => new(FontFamily, size, style);
    public static Font Title(float size = 20f) => new(FontFamily == "Segoe UI Variable Text" ? "Segoe UI Variable Display" : FontFamily, size, FontStyle.Bold);
    public static Font Semibold(float size = 10.5f) =>
        new(FontFamily == "Segoe UI Variable Text" ? "Segoe UI Variable Text Semibold" : FontFamily, size,
            FontFamily == "Segoe UI Variable Text" ? FontStyle.Regular : FontStyle.Bold);

    /// <summary>Dark title bar + rounded corners for a top-level window.</summary>
    public static void ApplyToWindow(Form f)
    {
        f.BackColor = Background;
        f.ForeColor = Text;
        f.Font = Body();
        try
        {
            int dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(f.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { }
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
