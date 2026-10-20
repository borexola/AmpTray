using System;
using System.Linq;
using AmpTray.Gpu;
using Xunit;

namespace AmpTray.Tests;

public class ShutdownMonitorTests
{
    private static readonly DateTime T0 = new(2026, 3, 4, 10, 0, 0);

    private static ShutdownSettings TempOnly(double minutes = 0) => new()
    {
        Enabled = true,
        TempRule = true, TempSensor = 0, TempAboveC = 80, TempMinutes = minutes,
        WireRule = false, FaultRule = false, PowerRule = false, GpuTempRule = false,
    };

    [Fact]
    public void Nothing_is_pending_while_disarmed()
    {
        var m = new ShutdownMonitor();
        var cfg = TempOnly();
        cfg.Enabled = false;
        var r = m.Evaluate(Snap.Make(tempIn: 95), null, T0, cfg, true, false);
        Assert.Empty(r.Pending);
        Assert.Null(r.Triggered);
    }

    [Fact]
    public void A_held_condition_counts_down_and_fires_after_two_fresh_readings()
    {
        var m = new ShutdownMonitor();
        var cfg = TempOnly(minutes: 0); // below the grace floor, so the grace applies

        var first = m.Evaluate(Snap.Make(tempIn: 95), null, T0, cfg, true, false);
        var p = Assert.Single(first.Pending);
        Assert.Equal(ShutdownRule.Temperature, p.Rule);
        Assert.Equal(ShutdownMonitor.MinGrace, p.Remaining);
        Assert.Contains("95.0", p.Condition);
        Assert.Null(first.Triggered);

        var half = m.Evaluate(Snap.Make(tempIn: 95), null, T0.AddSeconds(5), cfg, true, false);
        Assert.Equal(TimeSpan.FromSeconds(5), half.Pending[0].Remaining);
        Assert.Null(half.Triggered);

        var done = m.Evaluate(Snap.Make(tempIn: 95), null, T0 + ShutdownMonitor.MinGrace, cfg, true, false);
        Assert.NotNull(done.Triggered);
        Assert.Equal(TimeSpan.Zero, done.Triggered!.Remaining);
    }

    [Fact]
    public void A_single_reading_never_fires_even_when_time_is_up()
    {
        var m = new ShutdownMonitor();
        var cfg = TempOnly();
        m.Evaluate(Snap.Make(tempIn: 95), null, T0, cfg, true, false);
        // Stale re-evaluations (GPU ticks) do not add confirmations.
        var r = m.Evaluate(Snap.Make(tempIn: 95), null, T0.AddSeconds(10), cfg, deviceFresh: false, gpuFresh: true);
        Assert.Single(r.Pending);
        Assert.Null(r.Triggered);
    }

    [Fact]
    public void Minutes_setting_stretches_the_countdown()
    {
        var m = new ShutdownMonitor();
        var cfg = TempOnly(minutes: 2);
        var r = m.Evaluate(Snap.Make(tempIn: 95), null, T0, cfg, true, false);
        Assert.Equal(TimeSpan.FromMinutes(2), r.Pending[0].Remaining);
    }

    [Fact]
    public void A_brief_dip_keeps_timing_but_a_real_clear_resets()
    {
        var m = new ShutdownMonitor { MaxGap = TimeSpan.FromMinutes(5) };
        var cfg = TempOnly();
        m.Evaluate(Snap.Make(tempIn: 95), null, T0, cfg, true, false);

        var dip = m.Evaluate(Snap.Make(tempIn: 70), null, T0.AddSeconds(4), cfg, true, false);
        var p = Assert.Single(dip.Pending);
        Assert.Contains("95.0", p.Condition); // last condition is still shown
        Assert.Equal(TimeSpan.FromSeconds(6), p.Remaining);

        var cleared = m.Evaluate(Snap.Make(tempIn: 70), null, T0.AddSeconds(4) + ShutdownMonitor.ClearGrace + TimeSpan.FromSeconds(1), cfg, true, false);
        Assert.Empty(cleared.Pending);

        // Back above the threshold: the countdown starts over.
        var again = m.Evaluate(Snap.Make(tempIn: 95), null, T0.AddSeconds(30), cfg, true, false);
        Assert.Equal(ShutdownMonitor.MinGrace, again.Pending[0].Remaining);
    }

    [Fact]
    public void A_gap_in_readings_voids_the_timer()
    {
        var m = new ShutdownMonitor { MaxGap = TimeSpan.FromSeconds(10) };
        var cfg = TempOnly();
        m.Evaluate(Snap.Make(tempIn: 95), null, T0, cfg, true, false);
        // Sleep, unplug or a clock step: the next reading arrives half a minute later.
        var r = m.Evaluate(Snap.Make(tempIn: 95), null, T0.AddSeconds(30), cfg, true, false);
        Assert.Null(r.Triggered);
        Assert.Equal(ShutdownMonitor.MinGrace, r.Pending[0].Remaining);
    }

    [Fact]
    public void Suppressed_rules_stay_quiet_until_their_condition_clears()
    {
        var m = new ShutdownMonitor { MaxGap = TimeSpan.FromMinutes(5) };
        var cfg = TempOnly();
        m.Evaluate(Snap.Make(tempIn: 95), null, T0, cfg, true, false);
        m.Suppress(new[] { ShutdownRule.Temperature });

        var quiet = m.Evaluate(Snap.Make(tempIn: 95), null, T0.AddSeconds(20), cfg, true, false);
        Assert.Empty(quiet.Pending);
        Assert.Null(quiet.Triggered);

        // Clears for good, then comes back: the rule is live again.
        m.Evaluate(Snap.Make(tempIn: 60), null, T0.AddSeconds(25), cfg, true, false);
        m.Evaluate(Snap.Make(tempIn: 60), null, T0.AddSeconds(40), cfg, true, false);
        var live = m.Evaluate(Snap.Make(tempIn: 95), null, T0.AddSeconds(45), cfg, true, false);
        Assert.Single(live.Pending);
    }

    [Fact]
    public void Reset_forgets_every_timer()
    {
        var m = new ShutdownMonitor();
        var cfg = TempOnly();
        m.Evaluate(Snap.Make(tempIn: 95), null, T0, cfg, true, false);
        m.Reset();
        var r = m.Evaluate(Snap.Make(tempIn: 95), null, T0.AddSeconds(10), cfg, true, false);
        Assert.Null(r.Triggered);
        Assert.Equal(ShutdownMonitor.MinGrace, r.Pending[0].Remaining);
    }

    [Fact]
    public void Temperature_rule_can_watch_one_sensor()
    {
        var m = new ShutdownMonitor();
        var cfg = TempOnly();
        cfg.TempSensor = 2; // OUT only
        var ignored = m.Evaluate(Snap.Make(tempIn: 95, tempOut: 40), null, T0, cfg, true, false);
        Assert.Empty(ignored.Pending);
        var hit = m.Evaluate(Snap.Make(tempIn: 40, tempOut: 95), null, T0.AddSeconds(1), cfg, true, false);
        Assert.StartsWith("OUT", Assert.Single(hit.Pending).Condition);
    }

    [Fact]
    public void Absent_sensors_never_trip_the_temperature_rule()
    {
        var m = new ShutdownMonitor();
        var cfg = TempOnly();
        cfg.TempAboveC = -60;
        var r = m.Evaluate(Snap.Make(tempIn: -50, tempOut: -50, ts1: -50, ts2: -50), null, T0, cfg, true, false);
        Assert.Empty(r.Pending);
    }

    [Fact]
    public void Wire_rule_needs_the_configured_number_of_hot_wires()
    {
        var m = new ShutdownMonitor();
        var cfg = new ShutdownSettings { Enabled = true, TempRule = false, WireRule = true, WireCount = 2, WireAboveA = 9.0, GpuTempRule = false };
        var one = m.Evaluate(Snap.Make(amps: 5).WithCurrents(9.5), null, T0, cfg, true, false);
        Assert.Empty(one.Pending);
        var two = m.Evaluate(Snap.Make(amps: 5).WithCurrents(9.5, 5, 9.1), null, T0.AddSeconds(1), cfg, true, false);
        var p = Assert.Single(two.Pending);
        Assert.Equal(ShutdownRule.Wires, p.Rule);
        Assert.Contains("#1, #3", p.Condition);
    }

    [Fact]
    public void Fault_rule_respects_the_mask()
    {
        var m = new ShutdownMonitor();
        var cfg = new ShutdownSettings { Enabled = true, TempRule = false, WireRule = false, GpuTempRule = false, FaultRule = true, FaultMask = 0x04 };
        Assert.Empty(m.Evaluate(Snap.Make(faults: 0x01), null, T0, cfg, true, false).Pending);
        var hit = m.Evaluate(Snap.Make(faults: 0x05), null, T0.AddSeconds(1), cfg, true, false);
        var p = Assert.Single(hit.Pending);
        Assert.Equal(ShutdownRule.Fault, p.Rule);
        Assert.Contains("Over-current", p.Condition);
        Assert.DoesNotContain("Chip", p.Condition);
    }

    [Fact]
    public void Power_rule_uses_total_watts()
    {
        var m = new ShutdownMonitor();
        var cfg = new ShutdownSettings { Enabled = true, TempRule = false, WireRule = false, GpuTempRule = false, PowerRule = true, PowerAboveW = 600 };
        Assert.Empty(m.Evaluate(Snap.Make(volts: 12, amps: 8), null, T0, cfg, true, false).Pending);   // 576 W
        var hit = m.Evaluate(Snap.Make(volts: 12, amps: 9), null, T0.AddSeconds(1), cfg, true, false); // 648 W
        Assert.Equal(ShutdownRule.Power, Assert.Single(hit.Pending).Rule);
    }

    [Fact]
    public void Gpu_rule_runs_on_gpu_readings_without_the_device()
    {
        var m = new ShutdownMonitor();
        var cfg = new ShutdownSettings { Enabled = true, TempRule = false, WireRule = false, GpuTempRule = true, GpuTempAboveC = 95, GpuTempMinutes = 0 };
        var hot = new GpuSnapshot { TempC = 97 };
        m.Evaluate(null, hot, T0, cfg, deviceFresh: false, gpuFresh: true);
        var r = m.Evaluate(null, hot, T0.AddSeconds(10), cfg, deviceFresh: false, gpuFresh: true);
        Assert.NotNull(r.Triggered);
        Assert.Equal(ShutdownRule.GpuTemperature, r.Triggered!.Rule);
        Assert.StartsWith("GPU 97", r.Triggered.Condition);
    }

    [Fact]
    public void Losing_the_device_drops_device_rules_after_the_clear_grace()
    {
        var m = new ShutdownMonitor { MaxGap = TimeSpan.FromMinutes(5) };
        var cfg = TempOnly();
        m.Evaluate(Snap.Make(tempIn: 95), null, T0, cfg, true, false);
        Assert.Single(m.Evaluate(null, null, T0.AddSeconds(3), cfg, false, false).Pending);
        Assert.Empty(m.Evaluate(null, null, T0.AddSeconds(15), cfg, false, false).Pending);
    }

    [Fact]
    public void Descriptions_name_the_thresholds()
    {
        using var _ = new CultureScope();
        var c = new ShutdownSettings { TempSensor = 3, TempAboveC = 75.5, TempMinutes = 2, WireCount = 2, WireAboveA = 9.2, WireMinutes = 0,
            FaultMinutes = 1, PowerAboveW = 550, PowerMinutes = 1.5, GpuTempAboveC = 90, GpuTempMinutes = 0.25 };
        Assert.Equal("TS1 ≥ 75.5°C for 2 min", ShutdownMonitor.Describe(ShutdownRule.Temperature, c));
        Assert.Equal("2+ wire(s) ≥ 9.2 A for 10 s", ShutdownMonitor.Describe(ShutdownRule.Wires, c));
        Assert.Equal("device fault for 1 min", ShutdownMonitor.Describe(ShutdownRule.Fault, c));
        Assert.Equal("power ≥ 550 W for 1.5 min", ShutdownMonitor.Describe(ShutdownRule.Power, c));
        Assert.Equal("GPU ≥ 90°C for 15 s", ShutdownMonitor.Describe(ShutdownRule.GpuTemperature, c));
    }

    [Fact]
    public void Minutes_never_go_below_the_grace_floor()
    {
        using var _ = new CultureScope();
        Assert.Equal("10 s", ShutdownMonitor.Minutes(0));
        Assert.Equal("10 s", ShutdownMonitor.Minutes(0.1));
        Assert.Equal("30 s", ShutdownMonitor.Minutes(0.5));
        Assert.Equal("1 min", ShutdownMonitor.Minutes(1));
        Assert.Equal("2.5 min", ShutdownMonitor.Minutes(2.5));
    }

    [Fact]
    public void Active_rule_descriptions_follow_the_toggles()
    {
        var c = new ShutdownSettings { TempRule = true, WireRule = false, FaultRule = true, PowerRule = false, GpuTempRule = true };
        Assert.Equal(3, ShutdownMonitor.ActiveRuleDescriptions(c).Count());
    }

    [Fact]
    public void Sensor_names_are_clamped()
    {
        Assert.Equal("Any sensor", ShutdownSettings.SensorName(0));
        Assert.Equal("TS2", ShutdownSettings.SensorName(4));
        Assert.Equal("TS2", ShutdownSettings.SensorName(99));
        Assert.Equal("Any sensor", ShutdownSettings.SensorName(-3));
    }
}
