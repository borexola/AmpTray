using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AmpTray.Avalonia;

/// <summary>
/// Applies a theme to the application resources; every DynamicResource consumer updates live.
/// The colours and presets live in <see cref="ThemePresets"/> (Core); the derivation of the
/// secondary shades matches the WPF Theme.Apply so both widgets look the same.
/// </summary>
public static class AppTheme
{
    public const string Custom = ThemePresets.Custom;

    public static IReadOnlyDictionary<string, ThemeColors> Presets => ThemePresets.Presets;
    public static IEnumerable<string> Names => ThemePresets.Names;
    public static ThemeColors Resolve(Settings s) => ThemePresets.Resolve(s);

    /// <summary>
    /// Alpha of the widget's panel background (0..1). Only the background fades with the opacity
    /// setting; text, bars and banners stay fully drawn so they remain readable.
    /// </summary>
    public static double PanelAlpha { get; private set; } = 1;

    private static ThemeColors? _applied;

    public static void Apply(ThemeColors t)
    {
        _applied = t;
        var res = Application.Current!.Resources;
        foreach (var (key, argb) in Derive(t, PanelAlpha))
            res[key] = new ImmutableSolidColorBrush(Color.FromUInt32(argb));
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
    public static bool IsDarkApplied() => IsDark(_applied ?? new ThemeColors());

    /// <summary>The brush currently bound to a theme key, for code that draws (graph, chips, tray icon).</summary>
    public static Color ColorOf(string key, Color fallback)
    {
        if (Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) && v is ISolidColorBrush b)
            return b.Color;
        return fallback;
    }

    public static IBrush BrushOf(string key) => new ImmutableSolidColorBrush(ColorOf(key, Colors.Magenta));

    public static Color Parse(string? hex, Color fallback)
    {
        if (hex != null && Color.TryParse(hex.Trim(), out var c)) return c;
        return fallback;
    }

    public static bool TryParse(string? hex, out Color c)
    {
        c = default;
        return !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out c);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // The same derivation as WPF Theme.Apply, as ARGB values. (AmpTray.Ui.ThemePalette will take this over.)
    private static IEnumerable<(string Key, uint Argb)> Derive(ThemeColors t, double panelAlpha)
    {
        var bg = Parse(t.Background, Colors.Black);
        var fg = Parse(t.Text, Colors.White);
        double thin = 1 - Math.Clamp(panelAlpha / 0.85, 0, 1);

        yield return ("PanelBrush", WithAlpha(bg, 0xFC).ToUInt32());
        yield return ("PanelSolidBrush", bg.ToUInt32());
        yield return ("PanelFadeBrush", WithAlpha(bg, (byte)Math.Round(Math.Clamp(panelAlpha, 0, 1) * 255)).ToUInt32());
        yield return ("PanelBorderBrush", WithAlpha(fg, 0x26).ToUInt32());
        yield return ("DividerBrush", WithAlpha(fg, 0x14).ToUInt32());
        yield return ("TrackBrush", WithAlpha(fg, (byte)(0x1F + 0x30 * thin)).ToUInt32());
        yield return ("HoverBrush", WithAlpha(fg, 0x14).ToUInt32());
        yield return ("TextPrimary", fg.ToUInt32());
        yield return ("TextSecondary", Lerp(bg, fg, 0.62 + 0.33 * thin).ToUInt32());
        yield return ("TextMuted", Lerp(bg, fg, 0.38 + 0.47 * thin).ToUInt32());
        yield return ("IdleBrush", Lerp(bg, fg, 0.38).ToUInt32());
        yield return ("OkBrush", Parse(t.Accent, Colors.LimeGreen).ToUInt32());
        yield return ("WarnBrush", Parse(t.Warning, Colors.Orange).ToUInt32());
        yield return ("CritBrush", Parse(t.Critical, Colors.Red).ToUInt32());
        yield return ("GraphBrush", Parse(t.Graph, Colors.DeepSkyBlue).ToUInt32());
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
