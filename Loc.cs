using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace AmpTray;

/// <summary>
/// UI strings. English text is the key; other languages come from the embedded
/// Resources/Strings/{code}.json files (English → translation). Missing entries fall back to English.
/// </summary>
public static class Loc
{
    public const string Auto = "auto";

    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"),
        ("fr", "Français"),
        ("da", "Dansk"),
        ("pl", "Polski"),
    };

    private static Dictionary<string, string> _map = new();
    private static bool _loaded;

    public static string Current { get; private set; } = "en";

    /// <summary>Raised after the language changes; windows re-apply their texts.</summary>
    public static event Action? Changed;

    public static string Resolve(string? setting)
    {
        if (setting != null && setting != Auto && Languages.Any(l => l.Code == setting)) return setting;
        string system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == system) ? system : "en";
    }

    public static void Apply(string? setting)
    {
        string lang = Resolve(setting);
        if (_loaded && lang == Current) return;
        Current = lang;
        _map = Load(lang);
        _loaded = true;
        Changed?.Invoke();
    }

    private static Dictionary<string, string> Load(string lang)
    {
        if (lang == "en") return new();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"AmpTray.Resources.Strings.{lang}.json");
            if (stream == null) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Translates an English string.</summary>
    public static string T(string english) =>
        _map.TryGetValue(english, out var v) && v.Length > 0 ? v : english;

    /// <summary>Translates an English format string and formats it with the current culture.</summary>
    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);
}
