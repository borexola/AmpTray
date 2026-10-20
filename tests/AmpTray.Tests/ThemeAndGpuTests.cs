using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AmpTray.Gpu;
using Xunit;

namespace AmpTray.Tests;

public class ThemeTests
{
    [Fact]
    public void Colours_parse_with_a_fallback()
    {
        Assert.Equal(Colors.Red, Theme.Parse("#FF0000", Colors.Black));
        Assert.Equal(Colors.Lime, Theme.Parse("nonsense", Colors.Lime));
        Assert.Equal(Colors.Lime, Theme.Parse(null, Colors.Lime));
        Assert.True(Theme.TryParse(" #010203 ", out var c));
        Assert.Equal("#010203", Theme.ToHex(c));
        Assert.False(Theme.TryParse("", out _));
        Assert.False(Theme.TryParse("#12", out _));
        Assert.Equal(0x80, Theme.WithAlpha(Colors.White, 0x80).A);
    }

    [Fact]
    public void Presets_know_whether_they_are_dark()
    {
        Assert.True(Theme.IsDark(Theme.Presets["Graphite"]));
        Assert.True(Theme.IsDark(Theme.Presets["High Contrast"]));
        Assert.False(Theme.IsDark(Theme.Presets["Light"]));
    }

    [Fact]
    public void Resolve_returns_copies_and_maps_the_renamed_preset()
    {
        var s = new Settings { ThemeName = "Ember" };
        var resolved = Theme.Resolve(s);
        resolved.Background = "#123456";
        Assert.NotEqual("#123456", Theme.Presets["Ember"].Background);

        var old = new Settings { ThemeName = "Grizzly Orange" };
        Assert.Equal(Theme.Presets["Ember"].Accent, Theme.Resolve(old).Accent);
        Assert.Equal("Ember", old.ThemeName);

        var custom = new Settings { ThemeName = Theme.Custom };
        custom.CustomColors.Accent = "#ABCDEF";
        var mine = Theme.Resolve(custom);
        Assert.Equal("#ABCDEF", mine.Accent);
        Assert.NotSame(custom.CustomColors, mine);

        var unknown = new Settings { ThemeName = "Does not exist" };
        Assert.Equal(unknown.CustomColors.Background, Theme.Resolve(unknown).Background);
    }

    [Fact]
    public void Apply_fills_the_resource_brushes()
    {
        Sta.Run(() =>
        {
            Theme.SetPanelAlpha(1);
            Theme.Apply(Theme.Presets["Graphite"]);
            var res = Application.Current.Resources;
            Assert.Equal(Theme.Parse(Theme.Presets["Graphite"].Background, Colors.Black), ((SolidColorBrush)res["PanelSolidBrush"]).Color);
            Assert.Equal(Theme.Parse(Theme.Presets["Graphite"].Text, Colors.White), ((SolidColorBrush)res["TextPrimary"]).Color);
            Assert.Equal(Theme.Parse(Theme.Presets["Graphite"].Accent, Colors.White), ((SolidColorBrush)res["OkBrush"]).Color);
            Assert.Equal(255, ((SolidColorBrush)res["PanelFadeBrush"]).Color.A);
            Assert.True(Theme.IsDarkApplied());

            Theme.Apply(Theme.Presets["Light"]);
            Assert.False(Theme.IsDarkApplied());
        });
    }

    [Fact]
    public void Panel_alpha_fades_only_the_background_and_lifts_muted_text()
    {
        Sta.Run(() =>
        {
            Theme.Apply(Theme.Presets["Graphite"]);
            Theme.SetPanelAlpha(1);
            var res = Application.Current.Resources;
            byte mutedSolid = ((SolidColorBrush)res["TextMuted"]).Color.R;

            Theme.SetPanelAlpha(0.5);
            Assert.Equal(0.5, Theme.PanelAlpha);
            Assert.Equal(128, ((SolidColorBrush)res["PanelFadeBrush"]).Color.A);
            Assert.Equal(255, ((SolidColorBrush)res["TextPrimary"]).Color.A);
            Assert.True(((SolidColorBrush)res["TextMuted"]).Color.R > mutedSolid);

            Theme.SetPanelAlpha(0);
            Assert.Equal(0, ((SolidColorBrush)res["PanelFadeBrush"]).Color.A);
            Assert.Equal(((SolidColorBrush)res["PanelSolidBrush"]).Color.R, ((SolidColorBrush)res["PanelFadeBrush"]).Color.R);

            Theme.SetPanelAlpha(1);
            Assert.Equal(mutedSolid, ((SolidColorBrush)res["TextMuted"]).Color.R);
        });
    }
}

public class GpuSnapshotTests
{
    [Theory]
    [InlineData(79, "OkBrush")]
    [InlineData(80, "WarnBrush")]
    [InlineData(89, "WarnBrush")]
    [InlineData(90, "CritBrush")]
    public void Temperature_level_follows_the_drivers_slowdown_point(double temp, string level)
    {
        Assert.Equal(level, new GpuSnapshot { TempC = temp, SlowdownC = 90 }.TempLevel);
    }

    [Fact]
    public void Idle_and_application_clocks_are_not_throttling()
    {
        Assert.False(new GpuSnapshot { Throttle = ThrottleReason.GpuIdle | ThrottleReason.ApplicationClocks | ThrottleReason.DisplayClocks }.IsThrottling);
        Assert.True(new GpuSnapshot { Throttle = ThrottleReason.GpuIdle | ThrottleReason.PowerCap }.IsThrottling);
    }

    [Fact]
    public void Throttle_labels_are_ordered_and_thermal_ones_are_recognised()
    {
        var g = new GpuSnapshot { Throttle = ThrottleReason.ThermalSlowdown | ThrottleReason.PowerCap | ThrottleReason.GpuIdle };
        Assert.Equal(new[] { "Power cap", "Thermal" }, g.ThrottleLabels());
        Assert.True(GpuSnapshot.IsThermalReason("Thermal"));
        Assert.True(GpuSnapshot.IsThermalReason("HW thermal"));
        Assert.False(GpuSnapshot.IsThermalReason("Power cap"));
        Assert.Empty(new GpuSnapshot().ThrottleLabels());
    }

    [Fact]
    public void Percentages_are_safe_when_limits_are_unknown()
    {
        Assert.Equal(0, new GpuSnapshot { MemUsedBytes = 10 }.MemUsedPct);
        Assert.Equal(0, new GpuSnapshot { PowerW = 100 }.PowerPct);
        Assert.Equal(25, new GpuSnapshot { MemUsedBytes = 1, MemTotalBytes = 4 }.MemUsedPct);
        Assert.Equal(50, new GpuSnapshot { PowerW = 225, PowerLimitW = 450 }.PowerPct);
    }

    [Fact]
    public void Bytes_format_in_gigabytes_above_one()
    {
        using var _ = new CultureScope();
        Assert.Equal("2.0 GB", GpuSnapshot.FormatBytes(2UL << 30));
        Assert.Equal("1.5 GB", GpuSnapshot.FormatBytes(3UL << 29));
        Assert.Equal("512 MB", GpuSnapshot.FormatBytes(512UL << 20));
    }
}

public class PowerGraphTests
{
    [Fact]
    public void Spans_are_shown_in_the_largest_sensible_unit()
    {
        using var _ = new CultureScope();
        Assert.Equal("45 s", PowerGraph.FormatSpan(45));
        Assert.Equal("5 min", PowerGraph.FormatSpan(300));
        Assert.Equal("1 h", PowerGraph.FormatSpan(3600));
        Assert.Equal("1.5 h", PowerGraph.FormatSpan(5400));
    }
}
