using AmpTray.Ui;
using Xunit;

namespace AmpTray.Tests;

public class LevelsTests
{
    [Theory]
    [InlineData(600, 8.333)]
    [InlineData(450, 6.25)]
    [InlineData(300, 4.1667)]
    [InlineData(150, 2.0833)]
    public void Wire_limit_is_the_cable_rating_spread_over_six_wires(int rating, double expectedA)
    {
        Assert.Equal(expectedA, Levels.WireLimitA(Snap.Make(rating: rating)), 3);
    }

    [Fact]
    public void Wire_limit_falls_back_to_the_pin_rating()
    {
        Assert.Equal(Levels.PinCritA, Levels.WireLimitA(Snap.Make(rating: 0)));
        // A rating above what six pins may carry is capped at the pin rating.
        Assert.Equal(Levels.PinCritA, Levels.WireLimitA(Snap.Make(rating: 1200)));
    }

    [Fact]
    public void Wire_level_thresholds()
    {
        double limit = Levels.WireLimitA(Snap.Make(rating: 600));
        Assert.Equal("CritBrush", Levels.WireLevel(9.2, limit));
        Assert.Equal("WarnBrush", Levels.WireLevel(8.0, limit));
        Assert.Equal("OkBrush", Levels.WireLevel(7.9, limit));
        // With a 300 W cable the amber point is the lower cable allowance (4.17 A), not 8 A.
        double small = Levels.WireLimitA(Snap.Make(rating: 300));
        Assert.Equal("WarnBrush", Levels.WireLevel(4.2, small));
        Assert.Equal("OkBrush", Levels.WireLevel(4.1, small));
    }

    [Fact]
    public void Level_keys_match_the_brush_resource_names()
    {
        Assert.Equal("OkBrush", Levels.Ok);
        Assert.Equal("WarnBrush", Levels.Warn);
        Assert.Equal("CritBrush", Levels.Crit);
        Assert.Equal("IdleBrush", Levels.Idle);
        Assert.Equal("TextPrimary", Levels.TextKey(Levels.Ok));
        Assert.Equal("WarnBrush", Levels.TextKey(Levels.Warn));
    }

    [Theory]
    [InlineData(64.9, null)]
    [InlineData(65, "WarnBrush")]
    [InlineData(79.9, "WarnBrush")]
    [InlineData(80, "CritBrush")]
    public void Temperature_level_thresholds(double c, string? expected)
    {
        Assert.Equal(expected, Levels.TempLevel(c));
    }

    [Fact]
    public void Overall_level_is_the_worst_of_wires_temperatures_and_faults()
    {
        Assert.Equal("OkBrush", Levels.OverallLevel(Snap.Make(amps: 5)));
        Assert.Equal("WarnBrush", Levels.OverallLevel(Snap.Make(amps: 5).WithCurrents(8.0)));
        Assert.Equal("CritBrush", Levels.OverallLevel(Snap.Make(amps: 5).WithCurrents(9.2)));
        Assert.Equal("WarnBrush", Levels.OverallLevel(Snap.Make(amps: 5, ts1: 70)));
        Assert.Equal("CritBrush", Levels.OverallLevel(Snap.Make(amps: 5, tempOut: 80)));
        Assert.Equal("CritBrush", Levels.OverallLevel(Snap.Make(amps: 5, faults: 0x04)));
        var logged = new Device.SensorSnapshot { PinVoltage = new double[6], PinCurrent = new double[6], FaultLog = 0x01 };
        Assert.Equal("WarnBrush", Levels.OverallLevel(logged));
    }

    [Fact]
    public void Gpu_levels()
    {
        Assert.Equal("OkBrush", Levels.GpuPowerLevel(89.9));
        Assert.Equal("WarnBrush", Levels.GpuPowerLevel(90));
        Assert.Equal("CritBrush", Levels.GpuPowerLevel(100));
        Assert.Equal("OkBrush", Levels.GpuVramLevel(89.9));
        Assert.Equal("WarnBrush", Levels.GpuVramLevel(96.9));
        Assert.Equal("CritBrush", Levels.GpuVramLevel(97));
        Assert.Equal("OkBrush", Levels.GpuMemTempLevel(79, 90));
        Assert.Equal("WarnBrush", Levels.GpuMemTempLevel(80, 90));
        Assert.Equal("CritBrush", Levels.GpuMemTempLevel(90, 90));
    }
}
