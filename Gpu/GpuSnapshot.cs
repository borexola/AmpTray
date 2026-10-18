using System;
using System.Collections.Generic;

namespace AmpTray.Gpu;

/// <summary>Why the GPU is not running at full clocks (NVML clocks event reason bits).</summary>
[Flags]
public enum ThrottleReason : ulong
{
    None = 0,
    GpuIdle = 0x1,
    ApplicationClocks = 0x2,
    PowerCap = 0x4,
    HwSlowdown = 0x8,
    SyncBoost = 0x10,
    ThermalSlowdown = 0x20,
    HwThermalSlowdown = 0x40,
    HwPowerBrake = 0x80,
    DisplayClocks = 0x100,
}

public sealed class GpuSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Name { get; init; } = "";
    public string DriverVersion { get; init; } = "";

    public double TempC { get; init; }
    public double? MemTempC { get; init; }
    public double SlowdownC { get; init; } = 90;   // driver's own throttle point
    public double ShutdownC { get; init; } = 100;

    public double LoadPct { get; init; }
    public double MemControllerPct { get; init; }
    public double? EncoderPct { get; init; }
    public double? DecoderPct { get; init; }

    public ulong MemUsedBytes { get; init; }
    public ulong MemTotalBytes { get; init; }

    public double PowerW { get; init; }
    public double PowerLimitW { get; init; }
    public double PowerLimitMaxW { get; init; }
    public double? EnergyJ { get; init; }          // cumulative since driver load

    public double CoreMhz { get; init; }
    public double CoreMaxMhz { get; init; }
    public double MemMhz { get; init; }
    public double MemMaxMhz { get; init; }
    public double? FanPct { get; init; }
    public int FanCount { get; init; }
    public int PState { get; init; } = -1;
    public ThrottleReason Throttle { get; init; }

    public int PcieGen { get; init; }
    public int PcieWidth { get; init; }

    public double MemUsedPct => MemTotalBytes == 0 ? 0 : 100.0 * MemUsedBytes / MemTotalBytes;
    public double PowerPct => PowerLimitW <= 0 ? 0 : 100.0 * PowerW / PowerLimitW;

    /// <summary>Amber from 10 °C below the driver's slowdown point, red at the slowdown point.</summary>
    public string TempLevel => TempC >= SlowdownC ? "CritBrush" : TempC >= SlowdownC - 10 ? "WarnBrush" : "OkBrush";

    public bool IsThrottling => (Throttle & ~(ThrottleReason.GpuIdle | ThrottleReason.ApplicationClocks | ThrottleReason.DisplayClocks)) != 0;

    public IEnumerable<string> ThrottleLabels()
    {
        if (Throttle.HasFlag(ThrottleReason.PowerCap)) yield return Loc.T("Power cap");
        if (Throttle.HasFlag(ThrottleReason.ThermalSlowdown)) yield return Loc.T("Thermal");
        if (Throttle.HasFlag(ThrottleReason.HwThermalSlowdown)) yield return Loc.T("HW thermal");
        if (Throttle.HasFlag(ThrottleReason.HwPowerBrake)) yield return Loc.T("Power brake");
        if (Throttle.HasFlag(ThrottleReason.HwSlowdown)) yield return Loc.T("HW slowdown");
        if (Throttle.HasFlag(ThrottleReason.SyncBoost)) yield return Loc.T("Sync boost");
    }

    /// <summary>True for the reasons that mean the GPU is too hot (shown in red).</summary>
    public static bool IsThermalReason(string label) =>
        label == Loc.T("Thermal") || label == Loc.T("HW thermal");

    public static string FormatBytes(ulong b) => b >= 1UL << 30 ? $"{b / (double)(1UL << 30):0.0} GB" : $"{b / (double)(1UL << 20):0} MB";
}
