using System;
using System.Linq;
using AmpTray.Device;
using Xunit;

namespace AmpTray.Tests;

public class WireViewFrameTests
{
    [Fact]
    public void Parse_reads_every_field_of_the_sensor_struct()
    {
        var frame = new Frame
        {
            TempsC = { [0] = 25.0, [1] = 30.5, [2] = -50.0, [3] = 40.0 },
            VoltsV = { [2] = 11.95 },
            AmpsA = { [2] = 7.25 },
            Rating = 0,
            FaultStatus = 0x04,
            FaultLog = 0x05,
        };

        var s = WireViewReader.Parse(frame.Build());

        Assert.Equal(25.0, s.TempInC, 3);
        Assert.Equal(30.5, s.TempOutC, 3);
        Assert.Equal(-50.0, s.TempTs1C, 3);
        Assert.Equal(40.0, s.TempTs2C, 3);
        Assert.Equal(12.05, s.PinVoltage[0], 3);
        Assert.Equal(11.95, s.PinVoltage[2], 3);
        Assert.Equal(5.0, s.PinCurrent[0], 3);
        Assert.Equal(7.25, s.PinCurrent[2], 3);
        Assert.Equal(600, s.CableRatingW);
        Assert.Equal(0x04, s.FaultStatus);
        Assert.Equal(0x05, s.FaultLog);
        Assert.Equal(5 * 5.0 + 7.25, s.TotalCurrentA, 3);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(1, 450)]
    [InlineData(2, 300)]
    [InlineData(3, 150)]
    [InlineData(9, 0)]
    public void Parse_maps_the_cable_rating_code(byte code, int watts)
    {
        var frame = new Frame { Rating = code };
        Assert.Equal(watts, WireViewReader.Parse(frame.Build()).CableRatingW);
    }

    [Fact]
    public void Plausible_accepts_a_consistent_frame_and_an_idle_one()
    {
        Assert.True(WireViewReader.IsPlausible(new Frame().Build()));
        var idle = new Frame { AmpsA = { [0] = 0, [1] = 0, [2] = 0, [3] = 0, [4] = 0, [5] = 0 } };
        Assert.True(WireViewReader.IsPlausible(idle.Build()));
    }

    [Fact]
    public void Plausible_rejects_the_wrong_length()
    {
        Assert.False(WireViewReader.IsPlausible(new byte[99]));
        Assert.False(WireViewReader.IsPlausible(new Frame().Build().Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void Plausible_rejects_impossible_temperatures()
    {
        Assert.False(WireViewReader.IsPlausible(new Frame { TempsC = { [1] = 250.0 } }.Build()));
        Assert.False(WireViewReader.IsPlausible(new Frame { TempsC = { [3] = -120.0 } }.Build()));
    }

    [Fact]
    public void Plausible_rejects_power_that_disagrees_with_volts_times_amps()
    {
        var frame = new Frame();
        frame.WattsOverride[1] = 12.05 * 5.0 * 1.5;
        Assert.False(WireViewReader.IsPlausible(frame.Build()));
    }

    [Fact]
    public void Plausible_rejects_totals_that_disagree_with_the_wires()
    {
        Assert.False(WireViewReader.IsPlausible(new Frame { TotalMaOverride = 30_000 + 5_000 }.Build()));
        Assert.False(WireViewReader.IsPlausible(new Frame { TotalMwOverride = 10_000 }.Build()));
    }

    [Fact]
    public void Plausible_rejects_unknown_rating_codes_and_fault_bits()
    {
        Assert.False(WireViewReader.IsPlausible(new Frame { Rating = 4 }.Build()));
        Assert.False(WireViewReader.IsPlausible(new Frame { FaultStatus = 0x40 }.Build()));
        Assert.False(WireViewReader.IsPlausible(new Frame { FaultLog = 0x80 }.Build()));
        Assert.True(WireViewReader.IsPlausible(new Frame { FaultStatus = 0x3F, FaultLog = 0x3F }.Build()));
    }

    [Fact]
    public void A_misaligned_frame_is_rejected()
    {
        // One byte shifted, as happens when a partial read leaves data in flight.
        var good = new Frame { AmpsA = { [0] = 9.1, [1] = 8.7, [2] = 9.0, [3] = 8.9, [4] = 9.2, [5] = 8.8 } }.Build();
        var shifted = new byte[good.Length];
        Array.Copy(good, 1, shifted, 0, good.Length - 1);
        shifted[^1] = good[0];
        Assert.False(WireViewReader.IsPlausible(shifted));
    }
}

public class SensorSnapshotTests
{
    [Fact]
    public void Totals_and_maxima_follow_the_wires()
    {
        var s = Snap.Make(volts: 12.0, amps: 2.0).WithCurrents(1.0, 2.0, 3.0, 4.0, 5.0, 6.0);
        Assert.Equal(21.0, s.TotalCurrentA, 6);
        Assert.Equal(12.0 * 21.0, s.TotalPowerW, 6);
        Assert.Equal(6.0, s.MaxPinCurrentA, 6);
    }

    [Fact]
    public void Max_temperature_ignores_absent_sensors()
    {
        var s = Snap.Make(tempIn: 41, tempOut: 39, ts1: SensorSnapshot.NoSensorC, ts2: -60);
        Assert.Equal(41, s.MaxTempC);
        Assert.False(SensorSnapshot.IsTempPresent(SensorSnapshot.NoSensorC));
        Assert.True(SensorSnapshot.IsTempPresent(-49.9));

        var none = Snap.Make(tempIn: -50, tempOut: -50, ts1: -50, ts2: -50);
        Assert.True(double.IsNaN(none.MaxTempC));
    }

    [Fact]
    public void Fault_bits_decode_in_order_and_describe_in_english()
    {
        var faults = SensorSnapshot.Decode(0b100101).ToList();
        Assert.Equal(new[] { Fault.ChipOverTemp, Fault.OverCurrent, Fault.CurrentImbalance }, faults);
        Assert.Equal("Over-current", SensorSnapshot.Describe(Fault.OverCurrent));
        Assert.Empty(SensorSnapshot.Decode(0));
    }

    [Fact]
    public void Active_and_logged_faults_are_separate()
    {
        var s = new SensorSnapshot { FaultStatus = 0, FaultLog = 0x02 };
        Assert.False(s.HasActiveFault);
        Assert.True(s.HasLoggedFault);
    }
}
