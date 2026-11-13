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

    // Faults the device powers the PC off for, as read from a real device: sensor over-temp,
    // over-current, wire over-current and over-power (hard), none soft.
    private const ushort FactoryPowerOff = 0x001E;
    private const ushort Imbalance = 0x0020, OverCurrent = 0x0004;

    [Fact]
    public void A_fault_without_a_device_power_action_is_cleared_in_both_registers()
    {
        // Keep-masks (a set bit keeps that fault): CMD_CLEAR_FAULTS, status mask, log mask.
        var frame = WireViewReader.BuildClearFrame(FactoryPowerOff, status: Imbalance, log: Imbalance, out ushort blockedBy);
        Assert.Equal(new byte[] { 0x0E, 0x5E, 0xFF, 0x40, 0xFF }, frame);
        Assert.Equal(0, blockedBy);
    }

    [Fact]
    public void Nothing_is_sent_while_a_fault_the_device_powers_off_for_is_active()
    {
        // Not even for the other faults: the device may be counting down to its own shutdown.
        ushort both = Imbalance | OverCurrent;
        Assert.Null(WireViewReader.BuildClearFrame(FactoryPowerOff, status: both, log: both, out ushort blockedBy));
        Assert.Equal(OverCurrent, blockedBy);
        Assert.Null(WireViewReader.BuildClearFrame(FactoryPowerOff, status: OverCurrent, log: 0, out blockedBy));
        Assert.Equal(OverCurrent, blockedBy);
    }

    [Fact]
    public void Nothing_is_sent_when_there_is_no_fault()
    {
        Assert.Null(WireViewReader.BuildClearFrame(FactoryPowerOff, status: 0, log: 0, out ushort blockedBy));
        Assert.Equal(0, blockedBy);
    }

    [Fact]
    public void A_logged_power_off_fault_that_is_no_longer_active_is_history_and_is_cleared()
    {
        var frame = WireViewReader.BuildClearFrame(FactoryPowerOff, status: 0, log: OverCurrent, out ushort blockedBy);
        Assert.NotNull(frame);
        Assert.Equal(0, blockedBy);
        ushort keepLog = (ushort)(frame![3] | frame[4] << 8);
        Assert.Equal(0, OverCurrent & keepLog);
    }

    [Fact]
    public void A_sent_frame_still_keeps_every_power_off_fault_and_no_mask_byte_is_a_command()
    {
        // Known opcodes of the device protocol; a mask byte must never be one of them.
        byte[] opcodes = { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0xF0, 0xF1, 0xF2 };
        for (ushort powerOff = 0; powerOff <= 0x3F; powerOff++)
        for (ushort status = 0; status <= 0x3F; status++)
        {
            var frame = WireViewReader.BuildClearFrame(powerOff, status, log: 0x3F, out ushort blockedBy);
            Assert.Equal(status & powerOff, blockedBy);
            Assert.Equal(blockedBy != 0, frame == null); // the log is never empty here, so only a block stops it
            if (frame == null) continue;
            Assert.Equal(0x0E, frame[0]);
            ushort keepStatus = (ushort)(frame[1] | frame[2] << 8), keepLog = (ushort)(frame[3] | frame[4] << 8);
            // One raised between our reading and the clear survives: the mask keeps them all.
            Assert.Equal(powerOff, keepStatus & powerOff);
            Assert.Equal(0, status & keepStatus & 0x3F); // every active fault in the reading is cleared
            Assert.Equal(0, keepLog & 0x3F);
            for (int i = 1; i < frame.Length; i++) Assert.DoesNotContain(frame[i], opcodes);
        }
    }

    [Fact]
    public void Power_off_faults_are_read_from_the_three_known_config_layouts_only()
    {
        static byte[] Config(byte version, int length, ushort soft, ushort hard)
        {
            var c = new byte[length];
            c[2] = version;
            c[50] = (byte)soft; c[51] = (byte)(soft >> 8);
            c[52] = (byte)hard; c[53] = (byte)(hard >> 8);
            return c;
        }
        Assert.Equal((ushort)0x001E, WireViewReader.ParsePowerOffFaults(Config(2, 96, 0x0000, 0x001E)));
        Assert.Equal((ushort)0x0021, WireViewReader.ParsePowerOffFaults(Config(1, 74, 0x0020, 0x0001)));
        Assert.Equal((ushort)0x0000, WireViewReader.ParsePowerOffFaults(Config(0, 72, 0, 0)));

        // Anything that is not exactly a known layout is refused, so nothing is cleared on a guess.
        Assert.Null(WireViewReader.ParsePowerOffFaults(Config(2, 95, 0, 0x001E)));   // short read
        Assert.Null(WireViewReader.ParsePowerOffFaults(Config(2, 74, 0, 0x001E)));   // length of another version
        Assert.Null(WireViewReader.ParsePowerOffFaults(Config(3, 96, 0, 0x001E)));   // unknown version
        Assert.Null(WireViewReader.ParsePowerOffFaults(Config(2, 96, 0, 0x0040)));   // undefined fault bit
        Assert.Null(WireViewReader.ParsePowerOffFaults(System.Array.Empty<byte>()));
    }

    [Fact]
    public void The_only_screen_command_is_a_redraw_of_the_current_screen()
    {
        // CMD_SCREEN_CHANGE + SCREEN_GOTO_SAME: no screen switch, no pause of the display.
        Assert.Equal(new byte[] { 0x0C, 0xEF }, WireViewReader.CmdRedrawScreen);
    }

    [Fact]
    public void Clearing_faults_without_a_device_sends_nothing()
    {
        using var reader = new WireViewReader();
        Assert.Equal(new ClearFaultsResult(ClearFaultsOutcome.NotSent, 0), reader.ClearFaults());
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
