using System;
using System.Linq;
using AmpTray.Device;
using AmpTray.Gpu;
using Xunit;

namespace AmpTray.Tests;

public class PowerHistoryTests
{
    private static readonly DateTime T0 = new(2026, 5, 6, 12, 0, 0);

    [Fact]
    public void Since_returns_points_from_the_window_only()
    {
        var h = new PowerHistory();
        for (int i = 0; i < 10; i++) h.Add(T0.AddSeconds(i), 100 + i);
        var recent = h.Since(T0.AddSeconds(7)).ToList();
        Assert.Equal(3, recent.Count);
        Assert.Equal(107, recent[0].W);
        Assert.Equal(109, recent[^1].W);
    }

    [Fact]
    public void Points_older_than_an_hour_are_dropped()
    {
        var h = new PowerHistory();
        h.Add(T0, 1);
        h.Add(T0.AddMinutes(30), 2);
        h.Add(T0.AddMinutes(61), 3);
        Assert.Equal(new[] { 2.0, 3.0 }, h.Points.Select(p => p.W));
        Assert.Equal(TimeSpan.FromHours(1), PowerHistory.MaxAge);
    }
}

public class SessionStatsTests
{
    private static readonly DateTime T0 = new(2026, 5, 6, 12, 0, 0);

    [Fact]
    public void Average_peak_and_energy_accumulate()
    {
        var st = new SessionStats();
        st.Add(Snap.Make(volts: 10, amps: 2, at: T0));            // 120 W
        st.Add(Snap.Make(volts: 10, amps: 4, at: T0.AddSeconds(1))); // 240 W
        Assert.Equal(2, st.Samples);
        Assert.Equal(180, st.AvgPowerW, 6);
        Assert.Equal(240, st.PeakPowerW, 6);
        Assert.Equal(240.0 / 3600, st.EnergyWh, 9); // 240 W for 1 s
    }

    [Fact]
    public void Energy_ignores_gaps_from_disconnects()
    {
        var st = new SessionStats();
        st.Add(Snap.Make(volts: 10, amps: 2, at: T0));
        st.Add(Snap.Make(volts: 10, amps: 2, at: T0.AddSeconds(30)));
        Assert.Equal(0, st.EnergyWh);
    }

    [Fact]
    public void Per_wire_minima_maxima_and_temperature_maxima_track()
    {
        var st = new SessionStats();
        st.Add(Snap.Make(amps: 1, tempIn: 30, ts1: -50).WithCurrents(0.5, 3.0));
        st.Add(Snap.Make(amps: 2, tempIn: 35, ts1: -50).WithCurrents(1.5, 1.0));
        Assert.Equal(1.5, st.PinMaxA[0]);
        Assert.Equal(0.5, st.PinMinA[0]);
        Assert.Equal(3.0, st.PinMaxA[1]);
        Assert.Equal(1.0, st.PinMinA[1]);
        Assert.Equal(35, st.TempMaxC[0]);
        Assert.True(double.IsNaN(st.TempMaxC[2])); // absent sensor stays unknown
    }

    [Fact]
    public void Fault_transitions_become_events_and_the_list_is_capped()
    {
        var st = new SessionStats();
        st.Add(Snap.Make(faults: 0, at: T0));
        st.Add(Snap.Make(faults: 0x04, at: T0.AddSeconds(1)));
        st.Add(Snap.Make(faults: 0x04, at: T0.AddSeconds(2))); // unchanged: no event
        st.Add(Snap.Make(faults: 0, at: T0.AddSeconds(3)));
        Assert.Equal(2, st.FaultEvents.Count);
        Assert.Equal((Fault.OverCurrent, true), (st.FaultEvents[0].Fault, st.FaultEvents[0].Active));
        Assert.Equal((Fault.OverCurrent, false), (st.FaultEvents[1].Fault, st.FaultEvents[1].Active));

        for (int i = 0; i < 80; i++) st.Add(Snap.Make(faults: (ushort)(i % 2 == 0 ? 1 : 0), at: T0.AddSeconds(10 + i)));
        Assert.Equal(50, st.FaultEvents.Count);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var st = new SessionStats();
        st.Add(Snap.Make(faults: 1).WithCurrents(7));
        st.Reset();
        Assert.Equal(0, st.Samples);
        Assert.Equal(0, st.PeakPowerW);
        Assert.Equal(0, st.PinMaxA[0]);
        Assert.True(double.IsNaN(st.PinMinA[0]));
        Assert.Empty(st.FaultEvents);
    }
}

public class GpuStatsTests
{
    private static readonly DateTime T0 = new(2026, 5, 6, 12, 0, 0);

    [Fact]
    public void Prefers_the_drivers_energy_counter()
    {
        var st = new GpuStats();
        st.Add(new GpuSnapshot { Timestamp = T0, PowerW = 100, TempC = 60, EnergyJ = 1000 });
        st.Add(new GpuSnapshot { Timestamp = T0.AddSeconds(1), PowerW = 300, TempC = 70, EnergyJ = 4600 });
        Assert.Equal(1.0, st.EnergyWh, 9);
        Assert.Equal(70, st.PeakTempC);
        Assert.Equal(300, st.PeakPowerW);
    }

    [Fact]
    public void Integrates_power_when_there_is_no_counter_and_skips_gaps()
    {
        var st = new GpuStats();
        st.Add(new GpuSnapshot { Timestamp = T0, PowerW = 100 });
        st.Add(new GpuSnapshot { Timestamp = T0.AddSeconds(2), PowerW = 100 });   // +200 J
        st.Add(new GpuSnapshot { Timestamp = T0.AddSeconds(20), PowerW = 100 });  // gap: ignored
        Assert.Equal(200.0 / 3600, st.EnergyWh, 9);
        st.Reset();
        Assert.Equal(0, st.EnergyWh);
        Assert.Equal(0, st.PeakTempC);
    }
}
