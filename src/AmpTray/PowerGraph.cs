using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace AmpTray;

/// <summary>Realtime line graph of total power over a sliding time window.</summary>
public sealed class PowerGraph : FrameworkElement
{
    private static readonly double[] NiceSteps = { 25, 50, 100, 150, 200, 300, 400, 500, 600, 800, 1000 };

    public PowerHistory? History { get; set; }

    /// <summary>Optional second series: GPU board power as the driver reports it (dashed line).</summary>
    public PowerHistory? GpuHistory { get; set; }
    public double WindowSeconds { get; set; } = 300;

    /// <summary>Cable rating reported by the device (W). Used as the graph's full scale; 0 = unknown.</summary>
    public int CableRatingW { get; set; }

    /// <summary>Points further apart than this are treated as a disconnect and not joined.</summary>
    public double GapSeconds { get; set; } = 5;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;

        var line = Res("GraphBrush");
        var grid = Res("DividerBrush");
        var muted = Res("TextMuted");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        DateTime now = DateTime.Now;
        DateTime from = now.AddSeconds(-WindowSeconds);
        var pts = History?.Since(from).ToList() ?? new();
        var gpuPts = GpuHistory?.Since(from).ToList() ?? new();

        // Full scale is the cable rating; only if the draw exceeds it does the scale grow past it.
        double peak = Math.Max(pts.Count == 0 ? 0 : pts.Max(p => p.W), gpuPts.Count == 0 ? 0 : gpuPts.Max(p => p.W));
        double NiceAbove(double v) => NiceSteps.FirstOrDefault(s => v <= s * 0.95, Math.Ceiling(v / 100) * 100 + 100);
        double top = CableRatingW > 0 && peak <= CableRatingW ? CableRatingW : NiceAbove(peak);

        // Plot area leaves room for the y-axis label column on the left.
        const double labelW = 34;
        double px = labelW, pw = w - labelW, ph = h - 14;

        var gridPen = new Pen(grid, 1);
        for (int i = 0; i <= 2; i++)
        {
            double y = Math.Round(ph * i / 2) + 0.5;
            dc.DrawLine(gridPen, new Point(px, y), new Point(w, y));
            double value = top * (1 - i / 2.0);
            DrawText(dc, $"{value:0}", muted, 9.5, new Point(0, y - 6), dpi);
        }
        if (CableRatingW > 0)
        {
            // Rating marker: label at the top gridline normally, dashed red line once exceeded.
            double ry = Math.Round(ph * (1 - CableRatingW / top)) + 0.5;
            if (top > CableRatingW)
                dc.DrawLine(new Pen(Res("CritBrush"), 1) { DashStyle = DashStyles.Dash }, new Point(px, ry), new Point(w, ry));
            var tag = MakeText(Loc.F("{0} W cable", CableRatingW), muted, 9.5, dpi);
            dc.DrawText(tag, new Point(w - tag.Width, ry + 2));
        }
        DrawText(dc, $"−{FormatSpan(WindowSeconds)}", muted, 9.5, new Point(px, ph + 1), dpi);
        var nowText = MakeText(Loc.T("now"), muted, 9.5, dpi);
        dc.DrawText(nowText, new Point(w - nowText.Width, ph + 1));

        if (pts.Count < 2 && gpuPts.Count < 2) return;

        double X(DateTime t) => px + pw * (1 - (now - t).TotalSeconds / WindowSeconds);
        double Y(double v) => ph * (1 - Math.Clamp(v / top, 0, 1));

        var lineColor = ((SolidColorBrush)line).Color;
        dc.PushClip(new RectangleGeometry(new Rect(px, 0, pw, ph + 1)));

        // GPU board power first so the measured cable power draws on top of it.
        if (gpuPts.Count >= 2)
        {
            var gpuPen = new Pen(new SolidColorBrush(Theme.WithAlpha(lineColor, 0xA0)), 1.2)
            {
                DashStyle = new DashStyle(new double[] { 3, 2.5 }, 0),
                LineJoin = PenLineJoin.Round,
            };
            var (gpuLine, _) = Build(gpuPts, X, Y, ph);
            dc.DrawGeometry(null, gpuPen, gpuLine);
        }

        if (pts.Count >= 2)
        {
            var fill = new LinearGradientBrush(Theme.WithAlpha(lineColor, 0x55), Theme.WithAlpha(lineColor, 0x00), 90);
            var pen = new Pen(line, 1.6) { LineJoin = PenLineJoin.Round };
            var (lineGeo, areaGeo) = Build(pts, X, Y, ph);
            dc.DrawGeometry(fill, null, areaGeo);
            dc.DrawGeometry(null, pen, lineGeo);
            var lastPt = pts[^1];
            dc.DrawEllipse(line, null, new Point(X(lastPt.T), Y(lastPt.W)), 2.6, 2.6);
        }
        dc.Pop();

        // Legend, only when there are two series to tell apart.
        if (gpuPts.Count >= 2 && pts.Count >= 2)
        {
            double lx = px + 6, ly = 6;
            dc.DrawLine(new Pen(line, 1.6), new Point(lx, ly), new Point(lx + 12, ly));
            var cable = MakeText(Loc.T("cable"), muted, 9.5, dpi);
            dc.DrawText(cable, new Point(lx + 16, ly - cable.Height / 2));
            lx += 16 + cable.Width + 10;
            dc.DrawLine(new Pen(new SolidColorBrush(Theme.WithAlpha(lineColor, 0xA0)), 1.2) { DashStyle = new DashStyle(new double[] { 3, 2.5 }, 0) },
                new Point(lx, ly), new Point(lx + 12, ly));
            var gpu = MakeText(Loc.T("GPU board"), muted, 9.5, dpi);
            dc.DrawText(gpu, new Point(lx + 16, ly - gpu.Height / 2));
        }
    }

    /// <summary>Line and filled-area geometry for one series, breaking the line at gaps (disconnects).</summary>
    private (StreamGeometry Line, StreamGeometry Area) Build(List<(DateTime T, double W)> pts, Func<DateTime, double> X, Func<double, double> Y, double ph)
    {
        var lineGeo = new StreamGeometry();
        var areaGeo = new StreamGeometry();
        using (var lc = lineGeo.Open())
        using (var ac = areaGeo.Open())
        {
            int start = 0;
            for (int i = 1; i <= pts.Count; i++)
            {
                bool gap = i == pts.Count || (pts[i].T - pts[i - 1].T).TotalSeconds > GapSeconds;
                if (!gap) continue;
                if (i - start >= 2)
                {
                    var seg = pts.Skip(start).Take(i - start).Select(p => new Point(X(p.T), Y(p.W))).ToList();
                    lc.BeginFigure(seg[0], false, false);
                    lc.PolyLineTo(seg.Skip(1).ToList(), true, true);

                    ac.BeginFigure(new Point(seg[0].X, ph), true, true);
                    ac.PolyLineTo(seg.Append(new Point(seg[^1].X, ph)).ToList(), false, false);
                }
                start = i;
            }
        }
        lineGeo.Freeze();
        areaGeo.Freeze();
        return (lineGeo, areaGeo);
    }

    public static string FormatSpan(double seconds) =>
        seconds >= 3600 ? Loc.F("{0} h", (seconds / 3600).ToString("0.#"))
        : seconds >= 60 ? Loc.F("{0} min", (seconds / 60).ToString("0"))
        : Loc.F("{0} s", seconds.ToString("0"));

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static FormattedText MakeText(string s, Brush b, double size, double dpi) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, dpi);

    private static void DrawText(DrawingContext dc, string s, Brush b, double size, Point p, double dpi) =>
        dc.DrawText(MakeText(s, b, size, dpi), p);
}
