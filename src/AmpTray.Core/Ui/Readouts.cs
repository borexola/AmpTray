using System;
using System.Collections.Generic;
using System.Linq;
using AmpTray.Device;
using AmpTray.Gpu;

namespace AmpTray.Ui;

/// <summary>
/// Every piece of text the widget and tray show, built from the telemetry types. Duplicated from the
/// WPF widget (WidgetWindow, WidgetWindow.Gpu, PowerGraph, App) so each front end wording-matches.
/// Strings go through <see cref="Loc"/> exactly where the WPF code does.
/// </summary>
public static class Readouts
{
    /// <summary>Placeholder for a reading that is not available.</summary>
    public const string NotAvailable = "n/a";

    /// <summary>Row labels of the wires table, in column order (translate with <see cref="Loc.T"/>).</summary>
    public static readonly string[] PinHeaderKeys = { "AMPS", "VOLTS", "WATTS", "PEAK" };

    /// <summary>Labels of the four temperature cells.</summary>
    public static readonly string[] TempLabels = { "IN", "OUT", "TS1", "TS2" };

    /// <summary>Labels of the three GPU bars, in row order (translate with <see cref="Loc.T"/>).</summary>
    public static readonly string[] GpuBarKeys = { "LOAD", "POWER", "VRAM" };

    /// <summary>Labels of the GPU detail cells, in grid order (translate with <see cref="Loc.T"/>).</summary>
    public static readonly string[] GpuDetailKeys =
        { "Core clock", "Memory clock", "Fan", "Memory temp", "Encode / decode", "PCIe link", "Power limit", "Driver" };

    // ===== Units =====

    /// <summary>Energy in Wh below 1 kWh ("12.3 Wh"), otherwise in kWh ("1.23 kWh").</summary>
    public static string FormatEnergy(double wh) => wh >= 1000 ? $"{wh / 1000:0.00} kWh" : $"{wh:0.0} Wh";

    /// <summary>A duration as "30 s", "5 min" or "1.5 h".</summary>
    public static string FormatSpan(double seconds) =>
        seconds >= 3600 ? Loc.F("{0} h", (seconds / 3600).ToString("0.#"))
        : seconds >= 60 ? Loc.F("{0} min", (seconds / 60).ToString("0"))
        : Loc.F("{0} s", seconds.ToString("0"));

    // ===== Header =====

    /// <summary>Status line while no device is connected: the reason, or the search notice.</summary>
    public static string DisconnectedStatus(string? reason) => reason ?? Loc.T("Searching for device…");

    /// <summary>"COM3 · 12:34:56", with the cable rating appended in the expanded view.</summary>
    public static string StatusLine(SensorSnapshot s, string port, bool expanded) =>
        expanded && s.CableRatingW > 0
            ? $"{port} · {s.Timestamp:HH:mm:ss} · " + Loc.F("Cable {0} W", s.CableRatingW)
            : $"{port} · {s.Timestamp:HH:mm:ss}";

    public static string TotalPower(SensorSnapshot s) => $"{s.TotalPowerW:0.0} W";

    public static string TotalCurrent(SensorSnapshot s) => $"{s.TotalCurrentA:0.00} A · {s.PinVoltage.Average():0.00} V";

    // ===== Wires =====

    /// <summary>Caption under the wire bars: what a full bar means.</summary>
    public static string WireScale(SensorSnapshot s, double limitA) =>
        s.CableRatingW > 0
            ? Loc.F("0 to {0} A per wire ({1} W cable)", limitA.ToString("0.0#"), s.CableRatingW)
            : Loc.F("0 to {0} A per wire (pin rating)", limitA.ToString("0.0"));

    public static string WireAmps(double amps) => $"{amps:0.00} A";
    public static string WireVolts(double volts) => $"{volts:0.00} V";
    public static string WireWatts(double volts, double amps) => $"{volts * amps:0.0} W";
    public static string WirePeak(double maxAmps) => $"{maxAmps:0.00} A";

    /// <summary>Tooltip of one wire row: its deviation from the mean, or null when the draw is too small to compare.</summary>
    public static string? WireTooltip(int index, double amps, double meanAmps) =>
        meanAmps > 0.05 ? Loc.F("Wire {0}: {1}% vs average", index + 1, ((amps - meanAmps) / meanAmps * 100).ToString("+0;-0;0")) : null;

    /// <summary>"spread 0.12 A", with the worst deviation added in the expanded view when there is a draw.</summary>
    public static string SpreadLine(SensorSnapshot s, bool expanded)
    {
        double mean = s.PinCurrent.Average();
        double spread = s.PinCurrent.Max() - s.PinCurrent.Min();
        return expanded && mean > 0.05
            ? Loc.F("spread {0} A · max deviation {1}%", spread.ToString("0.00"), (s.PinCurrent.Max(a => Math.Abs(a - mean)) / mean * 100).ToString("0"))
            : Loc.F("spread {0} A", spread.ToString("0.00"));
    }

    // ===== Temperatures =====

    public static string TempText(double c) => $"{c:0.0}°";

    /// <summary>Session maximum under a temperature cell, or "" while there is none.</summary>
    public static string TempMax(double maxC) => double.IsNaN(maxC) ? "" : Loc.F("max {0}°", maxC.ToString("0.0"));

    /// <summary>Shown in place of the maximum when the sensor is not plugged in.</summary>
    public static string TempNotConnected() => Loc.T("not connected");

    // ===== Graph and session =====

    /// <summary>Label of the graph's cable rating marker.</summary>
    public static string CableRatingTag(int cableRatingW) => Loc.F("{0} W cable", cableRatingW);

    /// <summary>Left time-axis label, e.g. "−5 min".</summary>
    public static string WindowStartLabel(double windowSeconds) => $"−{FormatSpan(windowSeconds)}";

    /// <summary>Statistics of the samples in the graph window, or "" when there are none.</summary>
    public static string GraphStats(IReadOnlyCollection<(DateTime T, double W)> window, double windowSeconds, bool expanded)
    {
        if (window.Count == 0) return "";
        string span = FormatSpan(windowSeconds);
        return expanded
            ? Loc.F("last {0} · avg {1} W · max {2} W", span, window.Average(p => p.W).ToString("0"), window.Max(p => p.W).ToString("0"))
            : Loc.F("last {0} · max {1} W", span, window.Max(p => p.W).ToString("0"));
    }

    /// <summary>"Since 12:34 · peak 480 W · avg 310 W · 12.3 Wh".</summary>
    public static string SessionLine(SessionStats stats) =>
        Loc.F("Since {0} · peak {1} W · avg {2} W · {3}", stats.Since.ToString("HH:mm"), stats.PeakPowerW.ToString("0"), stats.AvgPowerW.ToString("0"), FormatEnergy(stats.EnergyWh));

    /// <summary>"Since 12:34 · peak 71° · peak 320 W · 12.3 Wh".</summary>
    public static string GpuSessionLine(GpuStats stats) =>
        Loc.F("Since {0} · peak {1}° · peak {2} W · {3}", stats.Since.ToString("HH:mm"), stats.PeakTempC.ToString("0"), stats.PeakPowerW.ToString("0"), FormatEnergy(stats.EnergyWh));

    // ===== Faults =====

    /// <summary>Comma-separated names of the faults set in <paramref name="bits"/>.</summary>
    public static string FaultNames(ushort bits) => string.Join(", ", SensorSnapshot.Decode(bits).Select(SensorSnapshot.Describe));

    /// <summary>One line of the recent-events list: "12:34:56   Over-current raised".</summary>
    public static (string Time, string What) FaultEventLine(FaultEvent ev) =>
        ($"{ev.Time:HH:mm:ss}   ", $"{SensorSnapshot.Describe(ev.Fault)} {(ev.Active ? Loc.T("raised") : Loc.T("cleared"))}");

    // ===== GPU =====

    /// <summary>The GPU name without the vendor prefix.</summary>
    public static string GpuName(GpuSnapshot g) => g.Name.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase);

    public static string GpuTempText(double c) => $"{c:0}°";

    /// <summary>Tooltip of the top strip: name, thresholds and memory temperature when reported.</summary>
    public static string GpuStripTooltip(GpuSnapshot g) =>
        g.Name + "\n"
        + Loc.F("GPU {0} °C. The driver throttles at {1} °C and shuts down at {2} °C", g.TempC.ToString("0"), g.SlowdownC.ToString("0"), g.ShutdownC.ToString("0"))
        + (g.MemTempC is double mt ? "\n" + Loc.F("Memory {0} °C", mt.ToString("0")) : "");

    /// <summary>Panel header: P-state, core clock and the throttle reasons, with the key that colours it.</summary>
    public static (string Text, string Key) GpuClock(GpuSnapshot g)
    {
        var reasons = g.ThrottleLabels().ToList();
        bool anyThermal = reasons.Any(GpuSnapshot.IsThermalReason);
        string text = (g.PState >= 0 ? $"P{g.PState} · " : "") + $"{g.CoreMhz:0} MHz"
                      + (reasons.Count > 0 ? " · " + string.Join(", ", reasons) : "");
        return (text, reasons.Count == 0 ? "TextMuted" : anyThermal ? Levels.Crit : Levels.Warn);
    }

    /// <summary>Throttle chips: one per reason (thermal ones filled red), or a single "Full clocks" chip.</summary>
    public static List<(string Text, string Key, bool Filled)> GpuThrottleChips(GpuSnapshot g)
    {
        var chips = new List<(string Text, string Key, bool Filled)>();
        foreach (var label in g.ThrottleLabels())
        {
            bool thermal = GpuSnapshot.IsThermalReason(label);
            chips.Add((label, thermal ? Levels.Crit : Levels.Warn, thermal));
        }
        if (chips.Count == 0) chips.Add(("✓  " + Loc.T("Full clocks"), Levels.Ok, false));
        return chips;
    }

    /// <summary>The LOAD, POWER and VRAM bars: fill (0..1, unclamped), value text and brush key.</summary>
    public static (double Fraction, string Text, string Key)[] GpuBars(GpuSnapshot g) => new[]
    {
        (g.LoadPct / 100, $"{g.LoadPct:0} %", Levels.Ok),
        (g.PowerPct / 100, g.PowerLimitW > 0 ? $"{g.PowerW:0} / {g.PowerLimitW:0} W" : $"{g.PowerW:0} W", Levels.GpuPowerLevel(g.PowerPct)),
        (g.MemUsedPct / 100, $"{GpuSnapshot.FormatBytes(g.MemUsedBytes)} / {GpuSnapshot.FormatBytes(g.MemTotalBytes)}", Levels.GpuVramLevel(g.MemUsedPct)),
    };

    /// <summary>The expanded detail cells, keyed by <see cref="GpuDetailKeys"/>.</summary>
    public static Dictionary<string, string> GpuDetails(GpuSnapshot g)
    {
        bool hasMem = g.MemTempC is double;
        return new Dictionary<string, string>
        {
            ["Core clock"] = g.CoreMaxMhz > 0 ? $"{g.CoreMhz:0} / {g.CoreMaxMhz:0} MHz" : $"{g.CoreMhz:0} MHz",
            ["Memory clock"] = $"{g.MemMhz:0} MHz",
            ["Fan"] = g.FanPct is double f ? $"{f:0} %" + (g.FanCount > 1 ? " · " + Loc.F("{0} fans", g.FanCount) : "") : NotAvailable,
            ["Memory temp"] = hasMem ? $"{g.MemTempC:0} °C" : Loc.T("not reported"),
            ["Encode / decode"] = g.EncoderPct is double e ? $"{e:0} % / {g.DecoderPct ?? 0:0} %" : NotAvailable,
            ["PCIe link"] = g.PcieGen > 0 ? $"Gen {g.PcieGen} ×{g.PcieWidth}" : NotAvailable,
            ["Power limit"] = g.PowerLimitW > 0
                ? $"{g.PowerLimitW:0} W" + (g.PowerLimitMaxW > g.PowerLimitW + 0.5 ? " " + Loc.F("(max {0})", g.PowerLimitMaxW.ToString("0")) : "")
                : NotAvailable,
            ["Driver"] = string.IsNullOrEmpty(g.DriverVersion) ? NotAvailable : g.DriverVersion,
        };
    }

    /// <summary>Shown in the power-path slot while no WireView is connected.</summary>
    public static string PowerPathDisconnected() =>
        Loc.T("Connect the WireView to see how board power splits between the 12V-2x6 cable and the PCIe slot.");

    /// <summary>
    /// How the driver's board power relates to the cable reading. <paramref name="cableAbove"/> is the
    /// hysteresis state kept by <see cref="PowerPathSmoother"/>; the inputs are its smoothed values.
    /// </summary>
    public static string PowerPath(double boardW, double cableW, bool cableAbove)
    {
        double slot = boardW - cableW;
        return !cableAbove
            ? Loc.F("Board {0} W = 12V-2x6 {1} W + PCIe slot ~{2} W", boardW.ToString("0"), cableW.ToString("0"), Math.Max(0, slot).ToString("0"))
              + (slot > 75 ? "  ⚠ " + Loc.T("slot above its 75 W spec") : "")
            : Loc.F("12V-2x6 reads {0} W, {1} W above the {2} W board power the driver reports", cableW.ToString("0"), (-slot).ToString("0"), boardW.ToString("0"));
    }

    /// <summary>The power-path sentence for the smoother's current state.</summary>
    public static string PowerPath(PowerPathSmoother smoother) => PowerPath(smoother.BoardW, smoother.CableW, smoother.CableAbove);

    /// <summary>Tooltip of the power-path sentence.</summary>
    public static string PowerPathTooltip() =>
        Loc.T("Driver power is a ~1 s average; the WireView reading is near-instant, so small gaps are normal.");

    // ===== FPS =====

    /// <summary>FPS counter text and the key that colours it, plus its tooltip.</summary>
    public static (string Text, string Key, string Tooltip) Fps(double? fps, string? processName, bool serviceRunning) =>
        fps is double f
            ? (f.ToString("0"), "TextPrimary", Loc.F("Frames per second presented by {0}", processName ?? "?"))
            : (NotAvailable, "TextMuted", serviceRunning
                ? Loc.T("The foreground app is not presenting frames; the counter shows the game while it is in front")
                : Loc.T("The FPS counter needs the background service (Settings → Window → Background monitoring)"));

    // ===== Banners =====

    /// <summary>"PC shuts down in 1:30: GPU above 90 °C (+2 more)".</summary>
    public static string ShutdownBanner(PendingRule next, int more)
    {
        var r = next.Remaining;
        string when = r <= TimeSpan.Zero ? Loc.T("now") : Loc.F("in {0}", $"{(int)r.TotalMinutes}:{r.Seconds:00}");
        return Loc.F("PC shuts down {0}: {1}", when, next.Condition) + (more > 0 ? " " + Loc.F("(+{0} more)", more) : "");
    }

    /// <summary>Tooltip of the ARMED badge, or null when the rules text is unknown.</summary>
    public static string? ArmedTooltip(string? rules) => rules == null ? null : Loc.T("Auto shutdown armed:") + "\n" + rules;

    /// <summary>"AmpTray v1.2.3 is available: Release name".</summary>
    public static string UpdateBanner(UpdateInfo info) =>
        Loc.F("AmpTray {0} is available", info.Tag) + (info.Name != info.Tag ? $": {info.Name}" : "");

    /// <summary>The alert banner text.</summary>
    public static string AlertBanner(string message) => "⚠  " + message;

    // ===== Tray =====

    /// <summary>Longest tooltip the Windows shell shows for a tray icon; longer texts are cut.</summary>
    public const int TrayTooltipMaxLength = 127;

    /// <summary>The tray icon's tooltip: WireView status, faults and the GPU line, cut to <see cref="TrayTooltipMaxLength"/>.</summary>
    public static string TrayTooltip(SensorSnapshot? s, GpuSnapshot? g)
    {
        string text;
        if (s == null)
        {
            text = "WireView: " + Loc.T("searching for device…");
        }
        else
        {
            string temp = double.IsNaN(s.MaxTempC) ? "" : $" · {s.MaxTempC:0.0} °C";
            string faults = s.HasActiveFault ? Loc.T("FAULT:") + " " + FaultNames(s.FaultStatus)
                : s.HasLoggedFault ? Loc.T("Faults logged earlier") : Loc.T("No faults");
            text = "WireView: " + Loc.F("{0} W · peak wire {1} A", s.TotalPowerW.ToString("0"), s.MaxPinCurrentA.ToString("0.00")) + temp + "\n" + faults;
        }
        if (g != null)
            text += $"\nGPU {g.TempC:0} °C · {g.LoadPct:0} % · {g.PowerW:0} W";
        if (text.Length > TrayTooltipMaxLength) text = text[..TrayTooltipMaxLength];
        return text;
    }
}
