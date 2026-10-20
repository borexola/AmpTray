using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AmpTray.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("v1.2.3-beta.1", "1.2.3.0")]
    [InlineData("v2", "2.0.0.0")]
    [InlineData(" V0.0.14 ", "0.0.14.0")]
    [InlineData("1.2.3+build7", "1.2.3.0")]
    public void Release_tags_parse_to_versions(string tag, string expected)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var v));
        Assert.Equal(Version.Parse(expected), v);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("v1.x")]
    public void Nonsense_tags_are_rejected(string tag)
    {
        Assert.False(UpdateChecker.TryParseVersion(tag, out _));
    }

    [Fact]
    public void Display_drops_a_zero_revision()
    {
        Assert.Equal("0.0.14", UpdateChecker.Display(new Version(0, 0, 14, 0)));
        Assert.Equal("1.2.3.4", UpdateChecker.Display(new Version(1, 2, 3, 4)));
    }

    [Fact]
    public void Current_is_the_assembly_version_normalised()
    {
        var current = UpdateChecker.Current;
        Assert.True(current.Build >= 0 && current.Revision >= 0);
        Assert.True(UpdateChecker.TryParseVersion("v" + UpdateChecker.Display(current), out var same));
        Assert.Equal(current, same);
    }

    [Fact]
    public void Newer_is_decided_by_version_order_not_text()
    {
        Assert.True(UpdateChecker.TryParseVersion("v0.0.10", out var ten));
        Assert.True(UpdateChecker.TryParseVersion("v0.0.9", out var nine));
        Assert.True(ten > nine);
    }
}

public class LocTests
{
    [Fact]
    public void Resolve_accepts_known_codes_and_falls_back_otherwise()
    {
        Assert.Equal("fr", Loc.Resolve("fr"));
        Assert.Equal("pl", Loc.Resolve("pl"));
        Assert.Contains(Loc.Resolve("xx"), Loc.Languages.Select(l => l.Code));
        Assert.Contains(Loc.Resolve(null), Loc.Languages.Select(l => l.Code));
        Assert.Contains(Loc.Resolve(Loc.Auto), Loc.Languages.Select(l => l.Code));
    }

    [Fact]
    public void Switching_language_translates_and_raises_changed()
    {
        int changes = 0;
        Action handler = () => changes++;
        Loc.Changed += handler;
        try
        {
            using (new LangScope("fr"))
            {
                Assert.Equal("fr", Loc.Current);
                Assert.Equal("maintenant", Loc.T("now"));
                Assert.Equal("câble", Loc.T("cable"));
                Assert.Equal("no such string", Loc.T("no such string"));
            }
            Assert.Equal("en", Loc.Current);
            Assert.Equal("now", Loc.T("now"));
        }
        finally
        {
            Loc.Changed -= handler;
        }
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Format_uses_the_current_culture()
    {
        using (new CultureScope(CultureInfo.GetCultureInfo("fr-FR")))
            Assert.Equal("0,5 s", Loc.F("{0} s", 0.5));
        using (new CultureScope())
            Assert.Equal("0.5 s", Loc.F("{0} s", 0.5));
    }

    private static Dictionary<string, string> Strings(string lang)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"AmpTray.Resources.Strings.{lang}.json");
        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)!;
    }

    public static IEnumerable<object[]> Codes() => Loc.Languages.Where(l => l.Code != "en").Select(l => new object[] { l.Code });

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_translation_file_is_complete_and_keeps_placeholders(string lang)
    {
        var reference = Strings("fr");
        var strings = Strings(lang);
        Assert.Equal(reference.Keys.OrderBy(k => k), strings.Keys.OrderBy(k => k));
        foreach (var (key, value) in strings)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"{lang}: empty translation for '{key}'");
            foreach (Match m in Regex.Matches(key, @"\{\d\}"))
                Assert.Contains(m.Value, value);
        }
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Translations_follow_the_no_dash_rule(string lang)
    {
        foreach (var (key, value) in Strings(lang))
        {
            Assert.DoesNotContain("--", key);
            Assert.DoesNotContain("--", value);
            Assert.DoesNotContain("—", value); // em dash
            Assert.DoesNotContain("–", value); // en dash
        }
    }

    [Fact]
    public void Every_string_used_in_code_has_a_translation()
    {
        string? root = FindSourceRoot();
        if (root == null) return; // packaged test run without sources: nothing to compare against
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Loc\.(?:T|F)\(\s*""((?:[^""\\]|\\.)*)"""))
                used.Add(Regex.Unescape(m.Groups[1].Value));
        }
        Assert.NotEmpty(used);
        var fr = Strings("fr");
        var missing = used.Where(k => !fr.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Untranslated: " + string.Join(" | ", missing));
    }

    private static string? FindSourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "AmpTray");
            if (File.Exists(Path.Combine(candidate, "AmpTray.csproj"))) return candidate;
        }
        return null;
    }
}
