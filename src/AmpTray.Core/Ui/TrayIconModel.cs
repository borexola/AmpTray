using System;
using System.Linq;
using AmpTray.Device;

namespace AmpTray.Ui;

/// <summary>
/// What the tray icon draws: a tiny live bar chart of the six wires, tinted by overall status, on a
/// rounded panel in theme colours. Duplicated from the WPF App.RenderTrayIcon; the drawing itself is
/// left to each front end.
/// </summary>
public static class TrayIconModel
{
    public const int Size = 32;
    public const int CornerRadius = 7;
    public const int BarWidth = 3, Gap = 1, Left = 4, Bottom = 27;

    /// <summary>Height of a bar when there is no device.</summary>
    public const int IdleHeight = 3;

    /// <summary>Brush key the bars are drawn with: idle without a device, critical while alerting, else the overall status.</summary>
    public static string ColorKey(SensorSnapshot? s, bool alerting) =>
        s == null ? Levels.Idle : alerting ? Levels.Crit : Levels.OverallLevel(s);

    /// <summary>Brush key of the panel behind the bars.</summary>
    public const string BackgroundKey = "PanelSolidBrush";

    /// <summary>
    /// Bar heights in pixels. Bars use the same scale as the widget: full height = one wire's share of
    /// the cable rating (<paramref name="limitA"/>). 2 px steps, so small current ripples don't redraw the icon.
    /// </summary>
    public static int[] BarHeights(SensorSnapshot? s, double limitA) =>
        Enumerable.Range(0, SensorSnapshot.PinCount)
            .Select(i => s == null ? IdleHeight : IdleHeight + 2 * (int)Math.Round(9.5 * Math.Clamp(s.PinCurrent[i] / limitA, 0, 1)))
            .ToArray();

    /// <summary>X of the i-th bar's left edge.</summary>
    public static int BarX(int i) => Left + i * (BarWidth + Gap);

    /// <summary>Identifies one rendering; equal keys mean the icon need not be redrawn.</summary>
    public static string Key(uint colorArgb, uint backgroundArgb, int[] heights) =>
        $"{colorArgb}:{backgroundArgb}:{string.Join(",", heights)}";
}
