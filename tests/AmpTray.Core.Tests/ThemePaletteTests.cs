using AmpTray.Ui;
using Xunit;

namespace AmpTray.Tests;

public class ThemePaletteTests
{
    private static string Hex(uint argb) => $"#{argb:X8}";

    [Fact]
    public void Graphite_at_full_opacity_derives_the_widget_brushes()
    {
        var p = ThemePalette.Derive(ThemePresets.Presets["Graphite"], 1);
        Assert.Equal(ThemePalette.Keys.Length, p.Count);
        foreach (var k in ThemePalette.Keys) Assert.True(p.ContainsKey(k), k);

        Assert.Equal("#FC14161B", Hex(p["PanelBrush"]));
        Assert.Equal("#FF14161B", Hex(p["PanelSolidBrush"]));
        Assert.Equal("#FF14161B", Hex(p["PanelFadeBrush"]));
        Assert.Equal("#26F2F4F7", Hex(p["PanelBorderBrush"]));
        Assert.Equal("#14F2F4F7", Hex(p["DividerBrush"]));
        Assert.Equal("#1FF2F4F7", Hex(p["TrackBrush"]));
        Assert.Equal("#14F2F4F7", Hex(p["HoverBrush"]));
        Assert.Equal("#FFF2F4F7", Hex(p["TextPrimary"]));
        // Blends from the background towards the text: 62 % and 38 % of the way.
        Assert.Equal("#FF9D9FA3", Hex(p["TextSecondary"]));
        Assert.Equal("#FF686A6E", Hex(p["TextMuted"]));
        Assert.Equal("#FF686A6E", Hex(p["IdleBrush"]));
        Assert.Equal("#FF3DD6A0", Hex(p["OkBrush"]));
        Assert.Equal("#FFF5B83D", Hex(p["WarnBrush"]));
        Assert.Equal("#FFF0524F", Hex(p["CritBrush"]));
        Assert.Equal("#FF4CC2FF", Hex(p["GraphBrush"]));
    }

    [Fact]
    public void Thin_panel_shifts_the_muted_shades_towards_the_text()
    {
        var p = ThemePalette.Derive(ThemePresets.Presets["Graphite"], 0);
        Assert.Equal("#0014161B", Hex(p["PanelFadeBrush"]));
        Assert.Equal("#4FF2F4F7", Hex(p["TrackBrush"]));           // 0x1F + 0x30
        Assert.Equal("#FFE6E8EC", Hex(p["TextSecondary"]));        // 95 % of the way
        Assert.Equal("#FFD0D2D6", Hex(p["TextMuted"]));            // 85 %
        Assert.Equal("#FF686A6E", Hex(p["IdleBrush"]));            // unchanged
        Assert.Equal("#FC14161B", Hex(p["PanelBrush"]));           // unchanged

        // The shift saturates at 85 % panel opacity: above that nothing moves.
        var at85 = ThemePalette.Derive(ThemePresets.Presets["Graphite"], 0.85);
        Assert.Equal("#FF686A6E", Hex(at85["TextMuted"]));
        Assert.Equal("#D914161B", Hex(at85["PanelFadeBrush"]));    // round(0.85 * 255) = 217
        var half = ThemePalette.Derive(ThemePresets.Presets["Graphite"], 0.425);
        Assert.Equal("#37F2F4F7", Hex(half["TrackBrush"]));        // 0x1F + 0x30 * 0.5 = 0x37
    }

    [Fact]
    public void Dark_and_light_presets()
    {
        Assert.True(ThemePalette.IsDark(ThemePresets.Presets["Graphite"]));
        Assert.True(ThemePalette.IsDark(ThemePresets.Presets["High Contrast"]));
        Assert.False(ThemePalette.IsDark(ThemePresets.Presets["Light"]));
        Assert.True(ThemePalette.IsDark(new ThemeColors { Background = "garbage" })); // falls back to black
    }

    [Theory]
    [InlineData("#14161B", 0x14, 0x16, 0x1B)]
    [InlineData("  #14161b ", 0x14, 0x16, 0x1B)]
    [InlineData("#8014161B", 0x14, 0x16, 0x1B)]
    [InlineData("#ABC", 0xAA, 0xBB, 0xCC)]
    [InlineData("#FABC", 0xAA, 0xBB, 0xCC)]
    public void Hex_colours_parse(string hex, int r, int g, int b)
    {
        Assert.True(ThemePalette.TryParse(hex, out var c));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("14161B")]
    [InlineData("#14161")]
    [InlineData("#GG161B")]
    [InlineData("red")]
    public void Bad_colours_do_not_parse(string? hex)
    {
        Assert.False(ThemePalette.TryParse(hex, out _));
        Assert.Equal(ThemePalette.Red, ThemePalette.Parse(hex, ThemePalette.Red));
    }

    [Fact]
    public void Hex_argb_and_lerp_round_trip()
    {
        var c = new Rgb(0x14, 0x16, 0x1B);
        Assert.Equal("#14161B", ThemePalette.ToHex(c));
        Assert.Equal(0x8014161Bu, ThemePalette.Argb(c, 0x80));
        Assert.Equal(c, ThemePalette.FromArgb(0x8014161B));
        Assert.Equal(0x80, ThemePalette.AlphaOf(0x8014161B));
        Assert.Equal(new Rgb(0, 0, 0), ThemePalette.Lerp(new Rgb(0, 0, 0), new Rgb(255, 255, 255), 0));
        Assert.Equal(new Rgb(255, 255, 255), ThemePalette.Lerp(new Rgb(0, 0, 0), new Rgb(255, 255, 255), 1));
        Assert.Equal(new Rgb(127, 127, 127), ThemePalette.Lerp(new Rgb(0, 0, 0), new Rgb(255, 255, 255), 0.5)); // truncates
    }
}
