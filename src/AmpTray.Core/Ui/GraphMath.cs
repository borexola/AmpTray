using System;
using System.Collections.Generic;
using System.Linq;

namespace AmpTray.Ui;

/// <summary>
/// Scale, axis mapping and sample lookup of the power graph, without any drawing. Duplicated from
/// the WPF <c>PowerGraph</c> so other front ends plot the same picture.
/// </summary>
public static class GraphMath
{
    /// <summary>Full-scale candidates for the y-axis when the draw exceeds the cable rating (or there is none).</summary>
    public static readonly double[] NiceSteps = { 25, 50, 100, 150, 200, 300, 400, 500, 600, 800, 1000 };

    /// <summary>Width of the y-axis label column on the left of the plot.</summary>
    public const double LabelWidth = 34;

    /// <summary>Height reserved under the plot for the time axis labels.</summary>
    public const double AxisHeight = 14;

    /// <summary>Default window of the graph, in seconds.</summary>
    public const double DefaultWindowSeconds = 300;

    /// <summary>Samples further apart than this are treated as a disconnect and not joined (at least 5 s, or four poll periods).</summary>
    public static double GapSeconds(int pollMs) => Math.Max(5, pollMs * 4 / 1000.0);

    /// <summary>The first nice step that leaves 5 % headroom above <paramref name="v"/>, else the next hundred plus one.</summary>
    public static double NiceAbove(double v) => NiceSteps.FirstOrDefault(s => v <= s * 0.95, Math.Ceiling(v / 100) * 100 + 100);

    /// <summary>Full scale is the cable rating; only if the draw exceeds it does the scale grow past it.</summary>
    public static double TopScale(double peak, int cableRatingW) =>
        cableRatingW > 0 && peak <= cableRatingW ? cableRatingW : NiceAbove(peak);

    /// <summary>Highest value of either series (0 when both are empty).</summary>
    public static double Peak(IReadOnlyList<(DateTime T, double W)> pts, IReadOnlyList<(DateTime T, double W)> gpuPts) =>
        Math.Max(pts.Count == 0 ? 0 : pts.Max(p => p.W), gpuPts.Count == 0 ? 0 : gpuPts.Max(p => p.W));

    /// <summary>X of a sample time: the plot spans <paramref name="plotLeft"/> to <paramref name="plotLeft"/> + <paramref name="plotWidth"/>, now at the right edge.</summary>
    public static double X(DateTime t, DateTime now, double windowSeconds, double plotLeft, double plotWidth) =>
        plotLeft + plotWidth * (1 - (now - t).TotalSeconds / windowSeconds);

    /// <summary>Y of a value, clamped to the plot: 0 at the top, <paramref name="plotHeight"/> at zero watts.</summary>
    public static double Y(double v, double top, double plotHeight) => plotHeight * (1 - Math.Clamp(v / top, 0, 1));

    /// <summary>The sample time under a cursor X (inverse of <see cref="X"/>).</summary>
    public static DateTime TimeAt(double x, DateTime now, double windowSeconds, double plotLeft, double plotWidth) =>
        now.AddSeconds(-(1 - (x - plotLeft) / plotWidth) * windowSeconds);

    /// <summary>Gridline y for the i-th of three lines (top, middle, bottom), snapped to the pixel grid.</summary>
    public static double GridY(int i, double plotHeight) => Math.Round(plotHeight * i / 2) + 0.5;

    /// <summary>Value written at the i-th gridline.</summary>
    public static double GridValue(int i, double top) => top * (1 - i / 2.0);

    /// <summary>Y of the cable rating marker, snapped to the pixel grid.</summary>
    public static double RatingY(int cableRatingW, double top, double plotHeight) =>
        Math.Round(plotHeight * (1 - cableRatingW / top)) + 0.5;

    /// <summary>The sample closest to <paramref name="t"/>, or null when the nearest one is across a gap.</summary>
    public static (DateTime T, double W)? Nearest(IReadOnlyList<(DateTime T, double W)> pts, DateTime t, double gapSeconds)
    {
        if (pts.Count == 0) return null;
        int lo = 0, hi = pts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (pts[mid].T < t) lo = mid + 1; else hi = mid;
        }
        var best = pts[lo];
        if (lo > 0 && (t - pts[lo - 1].T).Duration() < (best.T - t).Duration()) best = pts[lo - 1];
        return (best.T - t).Duration().TotalSeconds <= gapSeconds ? best : null;
    }

    /// <summary>
    /// Runs of samples to draw as one line, broken at gaps (disconnects). Runs of a single sample
    /// are dropped, as a line needs two points.
    /// </summary>
    public static List<(DateTime T, double W)[]> Segments(IReadOnlyList<(DateTime T, double W)> pts, double gapSeconds)
    {
        var segments = new List<(DateTime T, double W)[]>();
        int start = 0;
        for (int i = 1; i <= pts.Count; i++)
        {
            bool gap = i == pts.Count || (pts[i].T - pts[i - 1].T).TotalSeconds > gapSeconds;
            if (!gap) continue;
            if (i - start >= 2)
                segments.Add(pts.Skip(start).Take(i - start).ToArray());
            start = i;
        }
        return segments;
    }
}
