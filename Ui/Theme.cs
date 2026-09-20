using Microsoft.Win32;

namespace OShareSender.Ui;

/// <summary>One Material-3 style color scheme (teal seed #009688), light or dark.</summary>
public sealed class ThemeScheme
{
    public Color Primary;
    public Color OnPrimary;
    public Color PrimaryContainer;
    public Color OnPrimaryContainer;
    public Color SecondaryContainer;
    public Color OnSecondaryContainer;
    public Color Surface;               // scaffold background
    public Color CardColor;             // cards / nav rail (elevated surface)
    public Color SurfaceContainerHigh;  // dialogs, dropdowns
    public Color SurfaceVariant;
    public Color OnSurface;
    public Color OnSurfaceVariant;
    public Color Outline;
    public Color OutlineVariant;
    public Color Error;
    public Color OnError;
    public Color ErrorContainer;
    public Color OnErrorContainer;
    public Color Warning;               // LocalSend uses Colors.orange for cancelled/rejected
    public Color Track;                 // progress bar track
    public Color Hover;                 // generic hover overlay
    public Color Pressed;               // generic pressed overlay
}

/// <summary>Global app theme: M3 teal palette, fonts, radii; light/dark with a toggle that persists.</summary>
public static class Theme
{
    public const int RadiusSmall = 5;    // badges, progress bars
    public const int RadiusMedium = 10;  // cards, thumbnails
    public const int RadiusDialog = 16;

    private static readonly ThemeScheme Light = new()
    {
        Primary = FromHex("#006A60"),
        OnPrimary = FromHex("#FFFFFF"),
        PrimaryContainer = FromHex("#74F8E8"),
        OnPrimaryContainer = FromHex("#00201B"),
        SecondaryContainer = FromHex("#CCE8E2"),
        OnSecondaryContainer = FromHex("#05201C"),
        Surface = FromHex("#F4FBF8"),
        CardColor = FromHex("#FFFFFF"),
        SurfaceContainerHigh = FromHex("#E9EFEC"),
        SurfaceVariant = FromHex("#DAE5E1"),
        OnSurface = FromHex("#161D1B"),
        OnSurfaceVariant = FromHex("#3F4947"),
        Outline = FromHex("#6F7976"),
        OutlineVariant = FromHex("#BEC9C6"),
        Error = FromHex("#BA1A1A"),
        OnError = FromHex("#FFFFFF"),
        ErrorContainer = FromHex("#FFDAD6"),
        OnErrorContainer = FromHex("#410002"),
        Warning = FromHex("#B26A00"),
        Track = FromHex("#DEE7E4"),
        Hover = Color.FromArgb(0x14, 0, 0, 0),
        Pressed = Color.FromArgb(0x24, 0, 0, 0),
    };

    private static readonly ThemeScheme Dark = new()
    {
        Primary = FromHex("#52DBCC"),
        OnPrimary = FromHex("#003731"),
        PrimaryContainer = FromHex("#005048"),
        OnPrimaryContainer = FromHex("#74F8E8"),
        SecondaryContainer = FromHex("#334B47"),
        OnSecondaryContainer = FromHex("#CCE8E2"),
        Surface = FromHex("#0E1513"),
        CardColor = FromHex("#191F1E"),
        SurfaceContainerHigh = FromHex("#222826"),
        SurfaceVariant = FromHex("#3F4947"),
        OnSurface = FromHex("#DDE4E1"),
        OnSurfaceVariant = FromHex("#BEC9C5"),
        Outline = FromHex("#89938F"),
        OutlineVariant = FromHex("#3F4947"),
        Error = FromHex("#FFB4AB"),
        OnError = FromHex("#690005"),
        ErrorContainer = FromHex("#93000A"),
        OnErrorContainer = FromHex("#FFDAD6"),
        Warning = FromHex("#FFB74D"),
        Track = FromHex("#28302D"),
        Hover = Color.FromArgb(0x14, 255, 255, 255),
        Pressed = Color.FromArgb(0x24, 255, 255, 255),
    };

    public static bool IsDark { get; private set; }

    /// <summary>Fired after the mode flips; controls re-read <see cref="Scheme"/> and repaint.</summary>
    public static event Action? Changed;

    public static ThemeScheme Scheme => IsDark ? Dark : Light;

    private static readonly string UiFamily = ResolveUiFamily();

    private static string ResolveUiFamily()
    {
        // LocalSend uses "Segoe UI Variable Display" on Windows
        try
        {
            using var probe = new FontFamily("Segoe UI Variable Display");
            return probe.Name;
        }
        catch
        {
            return "Segoe UI";
        }
    }

    private static readonly Dictionary<string, Font> FontCache = new();

    /// <summary>Regular-weight text font at the given point size (cached).</summary>
    public static Font Font(float size) => GetFont(UiFamily, size, FontStyle.Regular);

    /// <summary>Medium/semibold text font — M3 "label large" / titles (cached).</summary>
    public static Font SemiFont(float size) =>
        GetFont(FontFamilyExists("Segoe UI Semibold") ? "Segoe UI Semibold" : UiFamily, size, FontStyle.Regular);

    public static Font HeaderFont => SemiFont(19f);

    /// <summary>Segoe MDL2 Assets glyph font (present on Win10/11).</summary>
    public static Font IconFont(float size) => GetFont("Segoe MDL2 Assets", size, FontStyle.Regular);

    private static bool FontFamilyExists(string name)
    {
        try { using var f = new FontFamily(name); return true; }
        catch { return false; }
    }

    private static Font GetFont(string family, float size, FontStyle style)
    {
        var key = $"{family}|{size}|{style}";
        if (!FontCache.TryGetValue(key, out var font))
        {
            try { font = new Font(family, size, style); }
            catch { font = new Font("Segoe UI", size, style); }
            FontCache[key] = font;
        }
        return font;
    }

    public static Color FromHex(string hex)
    {
        var r = Convert.ToByte(hex[1..3], 16);
        var g = Convert.ToByte(hex[3..5], 16);
        var b = Convert.ToByte(hex[5..7], 16);
        return Color.FromArgb(255, r, g, b);
    }

    /// <summary>Blend fg toward bg by t (0..1) — used to fade text for stale/dim states
    /// because GDI TextRenderer does not honor alpha.</summary>
    public static Color Blend(Color fg, Color bg, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(255,
            (int)(fg.R + (bg.R - fg.R) * t),
            (int)(fg.G + (bg.G - fg.G) * t),
            (int)(fg.B + (bg.B - fg.B) * t));
    }

    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(alpha, c);

    private const string RegistryKey = @"Software\OShareSender";
    private const string RegistryValue = "DarkMode";

    /// <summary>Loads the persisted mode; falls back to the Windows app theme.</summary>
    public static void Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
            if (key?.GetValue(RegistryValue) is int saved)
            {
                IsDark = saved != 0;
                return;
            }
        }
        catch { }
        IsDark = SystemIsDark();
    }

    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Toggle()
    {
        IsDark = !IsDark;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
            key.SetValue(RegistryValue, IsDark ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
        Changed?.Invoke();
    }
}
