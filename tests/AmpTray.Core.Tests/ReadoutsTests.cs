using System;
using System.Collections.Generic;
using AmpTray.Device;
using AmpTray.Gpu;
using AmpTray.Ui;
using Xunit;

namespace AmpTray.Tests;

public class ReadoutsTests
{
    private static readonly DateTime T0 = new(2026, 5, 6, 12, 34, 56);

    [Theory]
    [InlineData(0, "0.0 Wh")]
    [InlineData(12.34, "12.3 Wh")]
    [InlineData(999.96, "1000.0 Wh")]
    [InlineData(1000, "1.00 kWh")]
    [InlineData(1234.5, "1.23 kWh")]
    public void Energy_switches_to_kWh_at_1000(double wh, string expected)
    {
        using var _ = new CultureScope();
        Assert.Equal(expected, Readouts.FormatEnergy(wh));
    }

    [Theory]
    [InlineData(30, "30 s")]
    [InlineData(59.6, "60 s")]
    [InlineData(60, "1 min")]
    [InlineData(300, "5 min")]
    [InlineData(3600, "1 h")]
    [InlineData(5400, "1.5 h")]
    public void Span_picks_the_unit(double seconds, string expected)
    {
        using var _ = new CultureScope();
        Assert.Equal(expected, Readouts.FormatSpan(seconds));
        Assert.Equal("−" + expected, Readouts.WindowStartLabel(seconds));
    }

    [Fact]
    public void Shutdown_banner_counts_down_and_mentions_further_rules()
    {
        using var _ = new CultureScope();
        var next = new PendingRule(ShutdownRule.Temperature, "IN above 70 °C", TimeSpan.FromSeconds(95));
        Assert.Equal("PC shuts down in 1:35: IN above 70 °C", Readouts.ShutdownBanner(next, 0));
        Assert.Equal("PC shuts down in 1:35: IN above 70 °C (+2 more)", Readouts.ShutdownBanner(next, 2));
        Assert.Equal("PC shuts down in 0:05: IN above 70 °C", Readouts.ShutdownBanner(next with { Remaining = TimeSpan.FromSeconds(5) }, 0));
        Assert.Equal("PC shuts down now: IN above 70 °C", Readouts.ShutdownBanner(next with { Remaining = TimeSpan.Zero }, 0));
    }

    [Fact]
    public void Status_and_totals()
    {
        using var _ = new CultureScope();
        var s = Snap.Make(volts: 12.0, amps: 5, at: T0, rating: 600);
        Assert.Equal("COM3 · 12:34:56", Readouts.StatusLine(s, "COM3", expanded: false));
        Assert.Equal("COM3 · 12:34:56 · Cable 600 W", Readouts.StatusLine(s, "COM3", expanded: true));
        Assert.Equal("COM3 · 12:34:56", Readouts.StatusLine(Snap.Make(rating: 0, at: T0), "COM3", expanded: true));
        Assert.Equal("360.0 W", Readouts.TotalPower(s));
        Assert.Equal("30.00 A · 12.00 V", Readouts.TotalCurrent(s));
        Assert.Equal("Searching for device…", Readouts.DisconnectedStatus(null));
        Assert.Equal("Port busy", Readouts.DisconnectedStatus("Port busy"));
    }

    [Fact]
    public void Wire_scale_spread_and_tooltip()
    {
        using var _ = new CultureScope();
        var s = Snap.Make(amps: 5, rating: 600);
        Assert.Equal("0 to 8.33 A per wire (600 W cable)", Readouts.WireScale(s, Levels.WireLimitA(s)));
        var none = Snap.Make(rating: 0);
        Assert.Equal("0 to 9.2 A per wire (pin rating)", Readouts.WireScale(none, Levels.WireLimitA(none)));

        Assert.Equal("spread 0.00 A", Readouts.SpreadLine(s, expanded: false));
        var uneven = s.WithCurrents(6, 4);          // mean 5, max deviation 1 A = 20 %
        Assert.Equal("spread 2.00 A", Readouts.SpreadLine(uneven, expanded: false));
        Assert.Equal("spread 2.00 A · max deviation 20%", Readouts.SpreadLine(uneven, expanded: true));
        Assert.Equal("spread 0.00 A", Readouts.SpreadLine(Snap.Make(amps: 0), expanded: true));

        Assert.Equal("Wire 1: +20% vs average", Readouts.WireTooltip(0, 6, 5));
        Assert.Equal("Wire 2: -20% vs average", Readouts.WireTooltip(1, 4, 5));
        Assert.Equal("Wire 3: 0% vs average", Readouts.WireTooltip(2, 5, 5));
        Assert.Null(Readouts.WireTooltip(0, 0, 0.01));

        Assert.Equal("5.00 A", Readouts.WireAmps(5));
        Assert.Equal("12.05 V", Readouts.WireVolts(12.05));
        Assert.Equal("60.3 W", Readouts.WireWatts(12.05, 5));
        Assert.Equal("36.5°", Readouts.TempText(36.49));
        Assert.Equal("max 40.0°", Readouts.TempMax(40));
        Assert.Equal("", Readouts.TempMax(double.NaN));
    }

    [Fact]
    public void Graph_stats_and_session_line()
    {
        using var _ = new CultureScope();
        Assert.Equal("", Readouts.GraphStats(new List<(DateTime, double)>(), 300, true));
        var window = new List<(DateTime T, double W)> { (T0, 100), (T0.AddSeconds(1), 300) };
        Assert.Equal("last 5 min · max 300 W", Readouts.GraphStats(window, 300, expanded: false));
        Assert.Equal("last 5 min · avg 200 W · max 300 W", Readouts.GraphStats(window, 300, expanded: true));

        var stats = new SessionStats();
        stats.Add(Snap.Make(volts: 10, amps: 2, at: T0));
        stats.Add(Snap.Make(volts: 10, amps: 4, at: T0.AddSeconds(1)));
        string line = Readouts.SessionLine(stats);
        Assert.StartsWith("Since ", line);
        Assert.EndsWith(" · peak 240 W · avg 180 W · 0.1 Wh", line);
    }

    [Fact]
    public void Tray_tooltip_is_cut_to_127_characters()
    {
        using var _ = new CultureScope();
        Assert.Equal("WireView: searching for device…", Readouts.TrayTooltip(null, null));
        var s = Snap.Make(volts: 12, amps: 5, tempIn: 41.25, rating: 600);
        Assert.Equal("WireView: 360 W · peak wire 5.00 A · 41.3 °C\nNo faults", Readouts.TrayTooltip(s, null));
        var g = new GpuSnapshot { TempC = 71, LoadPct = 98.6, PowerW = 320.4 };
        Assert.Equal("WireView: 360 W · peak wire 5.00 A · 41.3 °C\nNo faults\nGPU 71 °C · 99 % · 320 W", Readouts.TrayTooltip(s, g));

        var faulty = Snap.Make(volts: 12, amps: 5, faults: 0x3F, rating: 600);
        string text = Readouts.TrayTooltip(faulty, g);
        Assert.Equal(127, text.Length);
        Assert.Contains("FAULT: Chip over-temp, Sensor over-temp", text);

        var logged = new SensorSnapshot { PinVoltage = new double[6], PinCurrent = new double[6], TempInC = -60, TempOutC = -60, TempTs1C = -60, TempTs2C = -60, FaultLog = 1 };
        Assert.Equal("WireView: 0 W · peak wire 0.00 A\nFaults logged earlier", Readouts.TrayTooltip(logged, null));
    }

    [Fact]
    public void Power_path_sentence()
    {
        using var _ = new CultureScope();
        Assert.Equal("Board 300 W = 12V-2x6 250 W + PCIe slot ~50 W", Readouts.PowerPath(300, 250, cableAbove: false));
        Assert.Equal("Board 300 W = 12V-2x6 310 W + PCIe slot ~0 W", Readouts.PowerPath(300, 310, cableAbove: false));
        Assert.Equal("Board 400 W = 12V-2x6 300 W + PCIe slot ~100 W  ⚠ slot above its 75 W spec", Readouts.PowerPath(400, 300, cableAbove: false));
        Assert.Equal("12V-2x6 reads 310 W, 10 W above the 300 W board power the driver reports", Readouts.PowerPath(300, 310, cableAbove: true));

        var sm = new PowerPathSmoother();
        sm.Add(T0, 300, 250);
        Assert.Equal("Board 300 W = 12V-2x6 250 W + PCIe slot ~50 W", Readouts.PowerPath(sm));
    }

    [Fact]
    public void Gpu_texts()
    {
        using var _ = new CultureScope();
        var g = new GpuSnapshot
        {
            Name = "NVIDIA GeForce RTX 5090", DriverVersion = "580.1", TempC = 71, MemTempC = 80, SlowdownC = 90, ShutdownC = 100,
            LoadPct = 98.6, PowerW = 450, PowerLimitW = 500, PowerLimitMaxW = 600, MemUsedBytes = 3UL << 30, MemTotalBytes = 32UL << 30,
            CoreMhz = 2700, CoreMaxMhz = 2800, MemMhz = 1500, FanPct = 55, FanCount = 2, PState = 0, PcieGen = 5, PcieWidth = 16,
            EncoderPct = 10, Throttle = ThrottleReason.PowerCap,
        };
        Assert.Equal("GeForce RTX 5090", Readouts.GpuName(g));
        Assert.Equal("71°", Readouts.GpuTempText(g.TempC));
        Assert.Equal("NVIDIA GeForce RTX 5090\nGPU 71 °C. The driver throttles at 90 °C and shuts down at 100 °C\nMemory 80 °C", Readouts.GpuStripTooltip(g));

        Assert.Equal(("P0 · 2700 MHz · Power cap", "WarnBrush"), Readouts.GpuClock(g));
        Assert.Equal(("2700 MHz", "TextMuted"), Readouts.GpuClock(new GpuSnapshot { CoreMhz = 2700 }));
        Assert.Equal(("P2 · 1500 MHz · Thermal", "CritBrush"), Readouts.GpuClock(new GpuSnapshot { CoreMhz = 1500, PState = 2, Throttle = ThrottleReason.ThermalSlowdown }));

        var bars = Readouts.GpuBars(g);
        Assert.Equal(3, bars.Length);
        Assert.Equal((0.986, "99 %", "OkBrush"), bars[0]);
        Assert.Equal((0.9, "450 / 500 W", "WarnBrush"), bars[1]);
        Assert.Equal("3.0 GB / 32.0 GB", bars[2].Text);
        Assert.Equal("OkBrush", bars[2].Key);
        Assert.Equal(3.0 / 32, bars[2].Fraction, 9);
        Assert.Equal("320 W", Readouts.GpuBars(new GpuSnapshot { PowerW = 320 })[1].Text);

        var chips = Readouts.GpuThrottleChips(g);
        Assert.Equal(new[] { ("Power cap", "WarnBrush", false) }, chips);
        Assert.Equal(new[] { ("✓  Full clocks", "OkBrush", false) }, Readouts.GpuThrottleChips(new GpuSnapshot()));
        Assert.Equal(("Thermal", "CritBrush", true), Readouts.GpuThrottleChips(new GpuSnapshot { Throttle = ThrottleReason.ThermalSlowdown })[0]);

        var d = Readouts.GpuDetails(g);
        Assert.Equal(Readouts.GpuDetailKeys.Length, d.Count);
        Assert.Equal("2700 / 2800 MHz", d["Core clock"]);
        Assert.Equal("1500 MHz", d["Memory clock"]);
        Assert.Equal("55 % · 2 fans", d["Fan"]);
        Assert.Equal("80 °C", d["Memory temp"]);
        Assert.Equal("10 % / 0 %", d["Encode / decode"]);
        Assert.Equal("Gen 5 ×16", d["PCIe link"]);
        Assert.Equal("500 W (max 600)", d["Power limit"]);
        Assert.Equal("580.1", d["Driver"]);

        var bare = Readouts.GpuDetails(new GpuSnapshot { CoreMhz = 2700, PowerLimitW = 500, PowerLimitMaxW = 500.4 });
        Assert.Equal("2700 MHz", bare["Core clock"]);
        Assert.Equal("n/a", bare["Fan"]);
        Assert.Equal("not reported", bare["Memory temp"]);
        Assert.Equal("n/a", bare["Encode / decode"]);
        Assert.Equal("n/a", bare["PCIe link"]);
        Assert.Equal("500 W", bare["Power limit"]);
        Assert.Equal("n/a", bare["Driver"]);
    }

    [Fact]
    public void Fps_and_banners()
    {
        using var _ = new CultureScope();
        Assert.Equal(("144", "TextPrimary", "Frames per second presented by game.exe"), Readouts.Fps(143.6, "game.exe", true));
        Assert.Equal("Frames per second presented by ?", Readouts.Fps(60, null, true).Tooltip);
        var off = Readouts.Fps(null, null, serviceRunning: false);
        Assert.Equal(("n/a", "TextMuted"), (off.Text, off.Key));
        Assert.Contains("needs the background service", off.Tooltip);
        Assert.Contains("not presenting frames", Readouts.Fps(null, null, serviceRunning: true).Tooltip);

        Assert.Equal("AmpTray v1.2 is available", Readouts.UpdateBanner(new UpdateInfo(new Version(1, 2), "v1.2", "", "v1.2")));
        Assert.Equal("AmpTray v1.2 is available: Spring", Readouts.UpdateBanner(new UpdateInfo(new Version(1, 2), "v1.2", "", "Spring")));
        Assert.Equal("⚠  Over-current", Readouts.AlertBanner("Over-current"));
        Assert.Null(Readouts.ArmedTooltip(null));
        Assert.Equal("Auto shutdown armed:\nrules", Readouts.ArmedTooltip("rules"));
        Assert.Equal("Over-current, Over-power", Readouts.FaultNames(0x14));
        var ev = Readouts.FaultEventLine(new FaultEvent(T0, Fault.OverPower, true));
        Assert.Equal(("12:34:56   ", "Over-power raised"), ev);
    }

    [Fact]
    public void Strings_follow_the_language()
    {
        using var _ = new CultureScope();
        using var fr = new LangScope("fr");
        string english = Loc.T("No faults");
        // Whatever the French file says for the line, the tooltip carries it rather than the English key.
        Assert.Contains(Loc.T("No faults"), Readouts.TrayTooltip(Snap.Make(), null));
        Assert.Equal(Loc.F("{0} min", "5"), Readouts.FormatSpan(300));
        Assert.NotNull(english);
    }
}
