using System;
using System.Collections.Generic;
using System.Globalization;

namespace AmpTray.Ui;

/// <summary>An opaque colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// Turns the six user-facing colours of a <see cref="ThemeColors"/> into the full set of brushes the
/// widget uses, with the same derivations as the WPF <c>Theme.Apply</c>. Colours are ARGB packed
/// as 0xAARRGGBB.
/// </summary>
public static class ThemePalette
{
    /// <summary>Every brush key <see cref="Derive"/> produces, in the order the WPF resources list them.</summary>
    public static readonly string[] Keys =
    {
        "PanelBrush", "PanelSolidBrush", "PanelFadeBrush", "PanelBorderBrush", "DividerBrush", "TrackBrush", "HoverBrush",
        "TextPrimary", "TextSecondary", "TextMuted", "IdleBrush", "OkBrush", "WarnBrush", "CritBrush", "GraphBrush",
    };

    // Fallbacks when a colour string does not parse: the named WPF colours Theme.Apply used.
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb LimeGreen = new(50, 205, 50);
    public static readonly Rgb Orange = new(255, 165, 0);
    public static readonly Rgb Red = new(255, 0, 0);
    public static readonly Rgb DeepSkyBlue = new(0, 191, 255);

    /// <summary>
    /// The brushes for a theme. <paramref name="panelAlpha"/> (0..1) is the opacity of the panel
    /// background only: as it thins out, the desktop takes its place behind the text, so the secondary
    /// and muted shades (which are blends towards the background) move towards the text colour to keep
    /// their contrast.
    /// </summary>
    public static IReadOnlyDictionary<string, uint> Derive(ThemeColors t, double panelAlpha)
    {
        var bg = Parse(t.Background, Black);
        var fg = Parse(t.Text, White);
        double thin = 1 - Math.Clamp(panelAlpha / 0.85, 0, 1);

        return new Dictionary<string, uint>
        {
            ["PanelBrush"] = Argb(bg, 0xFC),
            ["PanelSolidBrush"] = Argb(bg, 0xFF),
            ["PanelFadeBrush"] = Argb(bg, (byte)Math.Round(Math.Clamp(panelAlpha, 0, 1) * 255)),
            ["PanelBorderBrush"] = Argb(fg, 0x26),
            ["DividerBrush"] = Argb(fg, 0x14),
            ["TrackBrush"] = Argb(fg, (byte)(0x1F + 0x30 * thin)),
            ["HoverBrush"] = Argb(fg, 0x14),
            ["TextPrimary"] = Argb(fg, 0xFF),
            ["TextSecondary"] = Argb(Lerp(bg, fg, 0.62 + 0.33 * thin), 0xFF),
            ["TextMuted"] = Argb(Lerp(bg, fg, 0.38 + 0.47 * thin), 0xFF),
            ["IdleBrush"] = Argb(Lerp(bg, fg, 0.38), 0xFF),
            ["OkBrush"] = Argb(Parse(t.Accent, LimeGreen), 0xFF),
            ["WarnBrush"] = Argb(Parse(t.Warning, Orange), 0xFF),
            ["CritBrush"] = Argb(Parse(t.Critical, Red), 0xFF),
            ["GraphBrush"] = Argb(Parse(t.Graph, DeepSkyBlue), 0xFF),
        };
    }

    /// <summary>True when the theme's background is dark (text should be light).</summary>
    public static bool IsDark(ThemeColors t) => IsDark(Parse(t.Background, Black));

    /// <summary>True for a colour whose luminance is below mid-grey.</summary>
    public static bool IsDark(Rgb c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 128;

    /// <summary>Parses "#RGB", "#ARGB", "#RRGGBB" or "#AARRGGBB" (alpha is ignored); the fallback when it does not parse.</summary>
    public static Rgb Parse(string? hex, Rgb fallback) => TryParse(hex, out var c) ? c : fallback;

    /// <summary>Parses "#RGB", "#ARGB", "#RRGGBB" or "#AARRGGBB" (alpha is ignored).</summary>
    public static bool TryParse(string? hex, out Rgb c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.Trim();
        if (!s.StartsWith('#')) return false;
        s = s[1..];
        if (s.Length == 3 || s.Length == 4)
        {
            // Short form: each digit doubles. A leading alpha digit is dropped.
            var chars = new char[6];
            for (int i = 0; i < 3; i++) chars[2 * i] = chars[2 * i + 1] = s[s.Length - 3 + i];
            s = new string(chars);
        }
        else if (s.Length == 8)
        {
            s = s[2..];
        }
        else if (s.Length != 6)
        {
            return false;
        }
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        c = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string ToHex(Rgb c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Packs a colour and alpha as 0xAARRGGBB.</summary>
    public static uint Argb(Rgb c, byte a) => (uint)a << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;

    /// <summary>The colour part of a packed ARGB value.</summary>
    public static Rgb FromArgb(uint argb) => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>The alpha part of a packed ARGB value.</summary>
    public static byte AlphaOf(uint argb) => (byte)(argb >> 24);

    /// <summary>Blends from <paramref name="a"/> (t = 0) to <paramref name="b"/> (t = 1), truncating each channel like the WPF code.</summary>
    public static Rgb Lerp(Rgb a, Rgb b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
