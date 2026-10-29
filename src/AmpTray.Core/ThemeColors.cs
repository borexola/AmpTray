using System;
using System.Collections.Generic;
using System.Linq;

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

/// <summary>The built-in themes and how a setting picks one. The WPF side lives in <c>Theme</c>.</summary>
public static class ThemePresets
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

    public static IEnumerable<string> Names => Presets.Keys.Append(Custom);
}
