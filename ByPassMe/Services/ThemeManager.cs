using System.Windows;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace ByPassMe.Services;

/// <summary>Тема и палитры — зеркало Android Theme.kt / FloatingToolbar.</summary>
public sealed class ThemeManager
{
    public static ThemeManager Instance { get; } = new();

    public string ThemeMode { get; private set; } = "system";
    public string Palette { get; private set; } = "indigo";

    public event Action? Changed;

    private ThemeManager()
    {
        var store = SettingsStore.Instance;
        ThemeMode = store.ThemeMode;
        Palette = store.ThemePalette;
    }

    public bool IsDark =>
        ThemeMode switch
        {
            "dark" => true,
            "light" => false,
            _ => IsSystemDark()
        };

    public void SetThemeMode(string mode)
    {
        ThemeMode = mode;
        SettingsStore.Instance.SaveTheme(ThemeMode, false, Palette);
        Apply();
        Changed?.Invoke();
    }

    public void SetPalette(string palette)
    {
        Palette = palette;
        SettingsStore.Instance.SaveTheme(ThemeMode, false, Palette);
        Apply();
        Changed?.Invoke();
    }

    public void Apply()
    {
        var scheme = GetScheme(Palette, IsDark);
        var res = Application.Current.Resources;

        SetBrush(res, "PrimaryBrush", scheme.Primary);
        SetBrush(res, "PrimaryDarkBrush", scheme.PrimaryDark);
        SetBrush(res, "OnPrimaryBrush", scheme.OnPrimary);
        SetBrush(res, "PrimaryContainerBrush", scheme.PrimaryContainer);
        SetBrush(res, "OnPrimaryContainerBrush", scheme.OnPrimaryContainer);
        SetBrush(res, "BackgroundBrush", scheme.Background);
        SetBrush(res, "SurfaceBrush", scheme.Surface);
        SetBrush(res, "SurfaceVariantBrush", scheme.SurfaceVariant);
        SetBrush(res, "CardBrush", scheme.Card);
        SetBrush(res, "CardBorderBrush", scheme.CardBorder);
        SetBrush(res, "TextPrimaryBrush", scheme.OnSurface);
        SetBrush(res, "TextSecondaryBrush", scheme.OnSurfaceVariant);
        SetBrush(res, "ErrorBrush", scheme.Error);
        SetBrush(res, "NavShellBrush", scheme.NavShell);
        SetBrush(res, "NavIndicatorBrush", scheme.NavIndicator);
        SetBrush(res, "NavBorderBrush", scheme.NavBorder);
        SetBrush(res, "OrbOuterBrush", scheme.OrbOuter);
        SetBrush(res, "OrbMiddleBrush", scheme.OrbMiddle);
        SetBrush(res, "OrbOffBrush", scheme.OrbOff);
        SetBrush(res, "InputBorderBrush", scheme.Outline);
        SetBrush(res, "InputBgBrush", scheme.Surface);
        SetBrush(res, "BackdropTopGlowBrush", scheme.BackdropTop);
        SetBrush(res, "BackdropLeftGlowBrush", scheme.BackdropLeft);
        SetBrush(res, "BackdropBottomGlowBrush", scheme.BackdropBottom);
        SetBrush(res, "FloatingTabBrush", scheme.PrimaryContainer);
    }

    private static void SetBrush(ResourceDictionary res, string key, MediaColor color)
    {
        if (res.Contains(key))
            res[key] = new SolidColorBrush(color);
        else
            res.Add(key, new SolidColorBrush(color));
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ColorScheme GetScheme(string palette, bool dark) => (palette, dark) switch
    {
        ("espresso", false) => new(
            Primary: C(0x6D, 0x4C, 0x41), PrimaryDark: C(0x5D, 0x40, 0x37), OnPrimary: C(0xFF, 0xFF, 0xFF),
            PrimaryContainer: C(0xD7, 0xCC, 0xC8), OnPrimaryContainer: C(0x3E, 0x27, 0x23),
            Background: C(0xF2, 0xF0, 0xEC), Surface: C(0xFA, 0xF8, 0xF4), SurfaceVariant: C(0xEF, 0xEB, 0xE9),
            OnSurface: C(0x1C, 0x1B, 0x1A), OnSurfaceVariant: C(0x5D, 0x40, 0x37),
            Error: C(0xBA, 0x1A, 0x1A), Outline: C(0xBC, 0xAA, 0xA4)),
        ("espresso", true) => new(
            Primary: C(0xD7, 0xCC, 0xC8), PrimaryDark: C(0xBC, 0xAA, 0xA4), OnPrimary: C(0x3E, 0x27, 0x23),
            PrimaryContainer: C(0x5D, 0x40, 0x37), OnPrimaryContainer: C(0xEF, 0xEB, 0xE9),
            Background: C(0x1A, 0x16, 0x14), Surface: C(0x21, 0x1D, 0x1B), SurfaceVariant: C(0x2C, 0x26, 0x24),
            OnSurface: C(0xED, 0xE0, 0xD4), OnSurfaceVariant: C(0xD7, 0xCC, 0xC8),
            Error: C(0xFF, 0xB4, 0xAB), Outline: C(0x8D, 0x6E, 0x63)),
        ("forest", false) => new(
            Primary: C(0x5F, 0x5D, 0x68), PrimaryDark: C(0x4F, 0x4D, 0x58), OnPrimary: C(0xFF, 0xFF, 0xFF),
            PrimaryContainer: C(0xE5, 0xE0, 0xF0), OnPrimaryContainer: C(0x1C, 0x1A, 0x23),
            Background: C(0xFC, 0xF8, 0xFF), Surface: C(0xF7, 0xF2, 0xFA), SurfaceVariant: C(0xE6, 0xE0, 0xE9),
            OnSurface: C(0x1D, 0x1B, 0x20), OnSurfaceVariant: C(0x48, 0x45, 0x4E),
            Error: C(0xBA, 0x1A, 0x1A), Outline: C(0xCA, 0xC4, 0xD0)),
        ("forest", true) => new(
            Primary: C(0xC8, 0xC4, 0xD3), PrimaryDark: C(0xA8, 0xA4, 0xB3), OnPrimary: C(0x31, 0x2F, 0x38),
            PrimaryContainer: C(0x47, 0x45, 0x50), OnPrimaryContainer: C(0xE5, 0xE0, 0xF0),
            Background: C(0x14, 0x13, 0x18), Surface: C(0x1D, 0x1B, 0x20), SurfaceVariant: C(0x48, 0x45, 0x4E),
            OnSurface: C(0xCA, 0xC4, 0xD0), OnSurfaceVariant: C(0xCA, 0xC4, 0xD0),
            Error: C(0xFF, 0xB4, 0xAB), Outline: C(0x93, 0x8F, 0x99)),
        ("indigo", true) => new(
            Primary: C(0xC4, 0xC0, 0xFF), PrimaryDark: C(0xA4, 0xA0, 0xDF), OnPrimary: C(0x2D, 0x2A, 0x5B),
            PrimaryContainer: C(0x43, 0x40, 0x73), OnPrimaryContainer: C(0xE2, 0xDF, 0xFF),
            Background: C(0x13, 0x13, 0x16), Surface: C(0x1B, 0x1B, 0x1F), SurfaceVariant: C(0x47, 0x46, 0x4F),
            OnSurface: C(0xC8, 0xC5, 0xD0), OnSurfaceVariant: C(0xC8, 0xC5, 0xD0),
            Error: C(0xFF, 0xB4, 0xAB), Outline: C(0x91, 0x8F, 0x9A)),
        _ => new( // indigo light (default)
            Primary: C(0x5B, 0x58, 0x8D), PrimaryDark: C(0x4B, 0x48, 0x7D), OnPrimary: C(0xFF, 0xFF, 0xFF),
            PrimaryContainer: C(0xE2, 0xDF, 0xFF), OnPrimaryContainer: C(0x1A, 0x17, 0x44),
            Background: C(0xFB, 0xF8, 0xFF), Surface: C(0xF6, 0xF3, 0xFA), SurfaceVariant: C(0xE4, 0xE1, 0xEC),
            OnSurface: C(0x1B, 0x1B, 0x1F), OnSurfaceVariant: C(0x47, 0x46, 0x4F),
            Error: C(0xBA, 0x1A, 0x1A), Outline: C(0xC8, 0xC5, 0xD0))
    };

    private static MediaColor C(byte r, byte g, byte b) => MediaColor.FromRgb(r, g, b);

    private readonly record struct ColorScheme(
        MediaColor Primary, MediaColor PrimaryDark, MediaColor OnPrimary,
        MediaColor PrimaryContainer, MediaColor OnPrimaryContainer,
        MediaColor Background, MediaColor Surface, MediaColor SurfaceVariant,
        MediaColor OnSurface, MediaColor OnSurfaceVariant,
        MediaColor Error, MediaColor Outline)
    {
        public MediaColor Card => Lerp(Surface, SurfaceVariant, IsDark ? 0.10 : 0.28);
        public MediaColor CardBorder => WithAlpha(Outline, (byte)(IsDark ? 0x42 : 0x3D));
        public MediaColor NavShell => WithAlpha(Surface, (byte)(IsDark ? 0xC7 : 0xF2));
        public MediaColor NavIndicator => WithAlpha(PrimaryContainer, (byte)(IsDark ? 0xD6 : 0xF7));
        public MediaColor NavBorder => WithAlpha(Outline, (byte)(IsDark ? 0x6B : 0x29));
        public MediaColor OrbOuter => WithAlpha(Primary, 0x26);
        public MediaColor OrbMiddle => WithAlpha(Primary, 0x66);
        public MediaColor OrbOff => IsDark ? C(0x47, 0x46, 0x4F) : C(0xE4, 0xE1, 0xEC);
        public MediaColor BackdropTop => WithAlpha(Primary, (byte)(IsDark ? 0x0E : 0x17));
        public MediaColor BackdropLeft => WithAlpha(Primary, (byte)(IsDark ? 0x0B : 0x3D));
        public MediaColor BackdropBottom => WithAlpha(Primary, (byte)(IsDark ? 0x0A : 0x38));

        private bool IsDark => Background.R < 0x40;

        private static MediaColor Lerp(MediaColor a, MediaColor b, double t) =>
            MediaColor.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));

        private static MediaColor WithAlpha(MediaColor c, byte alpha) =>
            MediaColor.FromArgb(alpha, c.R, c.G, c.B);
    }
}
