using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace AmpTray;

/// <summary>The six user-facing colours of a theme. Everything else is derived from them.</summary>
public sealed class ThemeColors
{
    public string Background { get; set; } = "#14161B";
    public string Text { get; set; } = "#F2F4F7";
    public string Accent { get; set; } = "#3DD6A0";
    public string Warning { get; set; } = "#F5B83D";
    public string Critical { get; set; } = "#F0524F";
    public string Graph { get; set; } = "#4CC2FF";

    public ThemeColors Clone() => (ThemeColors)MemberwiseClone();

    /// <summary>Names and accessors used by the settings editor.</summary>
    public static readonly (string Label, Func<ThemeColors, string> Get, Action<ThemeColors, string> Set)[] Fields =
    {
        ("Background", t => t.Background, (t, v) => t.Background = v),
        ("Text", t => t.Text, (t, v) => t.Text = v),
        ("Accent / OK", t => t.Accent, (t, v) => t.Accent = v),
        ("Warning", t => t.Warning, (t, v) => t.Warning = v),
        ("Critical / fault", t => t.Critical, (t, v) => t.Critical = v),
        ("Power graph", t => t.Graph, (t, v) => t.Graph = v),
    };
}

public static class Theme
{
    public const string Custom = "Custom";

    public static readonly IReadOnlyDictionary<string, ThemeColors> Presets = new Dictionary<string, ThemeColors>
    {
        ["Graphite"] = new(),
        ["Ember"] = new() { Background = "#0E0E0F", Text = "#F5F5F5", Accent = "#FF7A1A", Warning = "#FFC93D", Critical = "#FF3B3B", Graph = "#FF7A1A" },
        ["Midnight"] = new() { Background = "#0F1729", Text = "#E6ECF5", Accent = "#60A5FA", Warning = "#FBBF24", Critical = "#F87171", Graph = "#A78BFA" },
        ["Forest"] = new() { Background = "#101813", Text = "#E8F0EA", Accent = "#7BD88F", Warning = "#E6C35C", Critical = "#EF6461", Graph = "#7BD88F" },
        ["Light"] = new() { Background = "#F7F8FA", Text = "#1B1F24", Accent = "#12A378", Warning = "#C98204", Critical = "#D93A36", Graph = "#2563EB" },
        ["High Contrast"] = new() { Background = "#000000", Text = "#FFFFFF", Accent = "#00FF66", Warning = "#FFD400", Critical = "#FF2020", Graph = "#00E5FF" },
    };

    public static ThemeColors Resolve(Settings s)
    {
        if (s.ThemeName == "Grizzly Orange") s.ThemeName = "Ember"; // renamed preset in early builds
        return s.ThemeName == Custom || !Presets.TryGetValue(s.ThemeName, out var preset)
            ? s.CustomColors.Clone()
            : preset.Clone();
    }

    /// <summary>
    /// Alpha of the widget's panel background (0..1). Only the background fades with the opacity
    /// setting; text, bars and banners stay fully drawn so they remain readable.
    /// </summary>
    public static double PanelAlpha { get; private set; } = 1;

    private static ThemeColors? _applied;

    /// <summary>Pushes a theme into the application resources; DynamicResource users update live.</summary>
    public static void Apply(ThemeColors t)
    {
        _applied = t;
        var bg = Parse(t.Background, Colors.Black);
        var fg = Parse(t.Text, Colors.White);
        var res = Application.Current.Resources;

        // As the panel background thins out, the desktop takes its place behind the text, so the
        // secondary and muted shades (which are blends towards the background) move towards the
        // text colour to keep their contrast; the halo drawn by the widget does the rest.
        double thin = 1 - Math.Clamp(PanelAlpha / 0.85, 0, 1);

        Set(res, "PanelBrush", WithAlpha(bg, 0xFC));
        Set(res, "PanelSolidBrush", bg);
        Set(res, "PanelFadeBrush", WithAlpha(bg, (byte)Math.Round(Math.Clamp(PanelAlpha, 0, 1) * 255)));
        Set(res, "PanelBorderBrush", WithAlpha(fg, 0x26));
        Set(res, "DividerBrush", WithAlpha(fg, 0x14));
        Set(res, "TrackBrush", WithAlpha(fg, (byte)(0x1F + 0x30 * thin)));
        Set(res, "HoverBrush", WithAlpha(fg, 0x14));
        Set(res, "TextPrimary", fg);
        Set(res, "TextSecondary", Lerp(bg, fg, 0.62 + 0.33 * thin));
        Set(res, "TextMuted", Lerp(bg, fg, 0.38 + 0.47 * thin));
        Set(res, "IdleBrush", Lerp(bg, fg, 0.38));
        Set(res, "OkBrush", Parse(t.Accent, Colors.LimeGreen));
        Set(res, "WarnBrush", Parse(t.Warning, Colors.Orange));
        Set(res, "CritBrush", Parse(t.Critical, Colors.Red));
        Set(res, "GraphBrush", Parse(t.Graph, Colors.DeepSkyBlue));
    }

    /// <summary>Re-tints the panel background with a new alpha; the current theme colour is kept.</summary>
    public static void SetPanelAlpha(double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        if (alpha == PanelAlpha && _applied != null) return;
        PanelAlpha = alpha;
        Apply(_applied ?? new ThemeColors());
    }

    public static bool IsDark(ThemeColors t)
    {
        var c = Parse(t.Background, Colors.Black);
        return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 128;
    }

    /// <summary>True when the theme currently applied to the application has a dark background.</summary>
    public static bool IsDarkApplied()
    {
        var c = Application.Current.Resources["PanelSolidBrush"] is SolidColorBrush b ? b.Color : Colors.Black;
        return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 128;
    }

    public static Color Parse(string? hex, Color fallback)
    {
        try { return hex == null ? fallback : (Color)ColorConverter.ConvertFromString(hex.Trim()); }
        catch { return fallback; }
    }

    public static bool TryParse(string? hex, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try
        {
            if (ColorConverter.ConvertFromString(hex.Trim()) is Color parsed)
            {
                c = parsed;
                return true;
            }
        }
        catch (FormatException) { }
        catch (NotSupportedException) { }
        return false;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static void Set(ResourceDictionary res, string key, Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        res[key] = brush;
    }

    public static IEnumerable<string> Names => Presets.Keys.Append(Custom);
}
