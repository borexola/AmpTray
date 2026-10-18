using System;
using System.Collections.Generic;
using System.Linq;

namespace AmpTray.Device;

/// <summary>Fault bit indices as defined by the WireView Pro II firmware (bit = 1 &lt;&lt; index).</summary>
public enum Fault
{
    ChipOverTemp = 0,
    SensorOverTemp = 1,
    OverCurrent = 2,
    WireOverCurrent = 3,
    OverPower = 4,
    CurrentImbalance = 5,
}

public sealed class SensorSnapshot
{
    public const int PinCount = 6;

    /// <summary>Temperature sensors report this (or lower) when nothing is plugged in.</summary>
    public const double NoSensorC = -50;

    public DateTime Timestamp { get; init; } = DateTime.Now;
    public double[] PinVoltage { get; init; } = new double[PinCount];
    public double[] PinCurrent { get; init; } = new double[PinCount];
    public double TempInC { get; init; }
    public double TempOutC { get; init; }
    public double TempTs1C { get; init; }
    public double TempTs2C { get; init; }
    public int CableRatingW { get; init; }
    public ushort FaultStatus { get; init; }
    public ushort FaultLog { get; init; }

    public double TotalCurrentA => PinCurrent.Sum();
    public double TotalPowerW => PinVoltage.Zip(PinCurrent, (v, i) => v * i).Sum();
    public double MaxPinCurrentA => PinCurrent.Max();
    public double MaxTempC => new[] { TempInC, TempOutC, TempTs1C, TempTs2C }.Where(IsTempPresent).DefaultIfEmpty(double.NaN).Max();

    public bool HasActiveFault => FaultStatus != 0;
    public bool HasLoggedFault => FaultLog != 0;

    public static bool IsTempPresent(double c) => c > NoSensorC;

    public static IEnumerable<Fault> Decode(ushort bits) =>
        Enum.GetValues<Fault>().Where(f => (bits & (1 << (int)f)) != 0);

    public static string Describe(Fault f) => Loc.T(f switch
    {
        Fault.ChipOverTemp => "Chip over-temp",
        Fault.SensorOverTemp => "Sensor over-temp",
        Fault.OverCurrent => "Over-current",
        Fault.WireOverCurrent => "Wire over-current",
        Fault.OverPower => "Over-power",
        Fault.CurrentImbalance => "Current imbalance",
        _ => f.ToString(),
    });
}
