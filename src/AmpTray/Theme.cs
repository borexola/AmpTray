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

    /// <summary>Pushes a theme into the application resources; DynamicResource users update live.</summary>
    public static void Apply(ThemeColors t)
    {
        var bg = Parse(t.Background, Colors.Black);
        var fg = Parse(t.Text, Colors.White);
        var res = Application.Current.Resources;

        Set(res, "PanelBrush", WithAlpha(bg, 0xFC));
        Set(res, "PanelSolidBrush", bg);
        Set(res, "PanelBorderBrush", WithAlpha(fg, 0x26));
        Set(res, "DividerBrush", WithAlpha(fg, 0x14));
        Set(res, "TrackBrush", WithAlpha(fg, 0x1F));
        Set(res, "HoverBrush", WithAlpha(fg, 0x14));
        Set(res, "TextPrimary", fg);
        Set(res, "TextSecondary", Lerp(bg, fg, 0.62));
        Set(res, "TextMuted", Lerp(bg, fg, 0.38));
        Set(res, "IdleBrush", Lerp(bg, fg, 0.38));
        Set(res, "OkBrush", Parse(t.Accent, Colors.LimeGreen));
        Set(res, "WarnBrush", Parse(t.Warning, Colors.Orange));
        Set(res, "CritBrush", Parse(t.Critical, Colors.Red));
        Set(res, "GraphBrush", Parse(t.Graph, Colors.DeepSkyBlue));
    }

    public static bool IsDark(ThemeColors t)
    {
        var c = Parse(t.Background, Colors.Black);
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
