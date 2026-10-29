using System;
using AmpTray.Device;

namespace AmpTray.Ui;

/// <summary>
/// Status thresholds and the brush keys that colour them. Duplicated from the WPF widget
/// (WidgetWindow / WidgetWindow.Gpu) so every front end paints the same readings the same way.
/// </summary>
public static class Levels
{
    // 12V-2x6 terminals are rated 9.2 A each; warn a bit before that.
    public const double PinWarnA = 8.0;
    public const double PinCritA = 9.2;
    public const double TempWarnC = 65;
    public const double TempCritC = 80;

    /// <summary>Resource keys of the status brushes (see <see cref="ThemePalette"/>).</summary>
    public const string Ok = "OkBrush";
    public const string Warn = "WarnBrush";
    public const string Crit = "CritBrush";
    public const string Idle = "IdleBrush";

    /// <summary>Foreground key for a value with the given level: plain text while OK, the level's colour otherwise.</summary>
    public static string TextKey(string level) => level == Ok ? "TextPrimary" : level;

    /// <summary>
    /// Current one wire may carry: the cable rating spread over six 12 V wires
    /// (600 W → 8.33 A, 450 W → 6.25 A, 300 W → 4.17 A, 150 W → 2.08 A), never above the 9.2 A pin rating.
    /// Falls back to the pin rating when the device reports no cable rating.
    /// </summary>
    public static double WireLimitA(SensorSnapshot s) =>
        s.CableRatingW > 0 ? Math.Min(PinCritA, s.CableRatingW / 12.0 / SensorSnapshot.PinCount) : PinCritA;

    /// <summary>Amber from the wire's cable allowance (or 8 A, whichever is lower), red at the 9.2 A pin rating.</summary>
    public static string WireLevel(double amps, double limitA) =>
        amps >= PinCritA ? Crit : amps >= Math.Min(PinWarnA, limitA) ? Warn : Ok;

    /// <summary>Red from 80 °C, amber from 65 °C, null (plain text) below.</summary>
    public static string? TempLevel(double c) => c >= TempCritC ? Crit : c >= TempWarnC ? Warn : null;

    /// <summary>Worst of wires, temperatures and faults; drives the status dot and the tray icon tint.</summary>
    public static string OverallLevel(SensorSnapshot s)
    {
        string wire = WireLevel(s.MaxPinCurrentA, WireLimitA(s));
        if (s.HasActiveFault || wire == Crit || s.MaxTempC >= TempCritC) return Crit;
        if (s.HasLoggedFault || wire == Warn || s.MaxTempC >= TempWarnC) return Warn;
        return Ok;
    }

    /// <summary>GPU board power as a share of its limit: red at 100 %, amber from 90 %.</summary>
    public static string GpuPowerLevel(double pct) => pct >= 100 ? Crit : pct >= 90 ? Warn : Ok;

    /// <summary>VRAM in use: red from 97 %, amber from 90 %.</summary>
    public static string GpuVramLevel(double pct) => pct >= 97 ? Crit : pct >= 90 ? Warn : Ok;

    /// <summary>Memory temperature against the driver's slowdown point: red at it, amber within 10 °C of it.</summary>
    public static string GpuMemTempLevel(double memC, double slowdownC) =>
        memC >= slowdownC ? Crit : memC >= slowdownC - 10 ? Warn : Ok;
}
