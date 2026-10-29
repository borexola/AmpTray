using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace AmpTray;

/// <summary>Applies a theme to the WPF resources; the colours and presets themselves live in <see cref="ThemePresets"/>.</summary>
public static class Theme
{
    public const string Custom = ThemePresets.Custom;

    public static IReadOnlyDictionary<string, ThemeColors> Presets => ThemePresets.Presets;

    public static ThemeColors Resolve(Settings s) => ThemePresets.Resolve(s);

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

    public static IEnumerable<string> Names => ThemePresets.Names;
}
