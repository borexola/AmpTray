using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
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

    // X position of the mouse while it is over the plot; the readout follows it.
    private double? _hoverX;

    public PowerGraph()
    {
        MouseMove += (_, e) => { _hoverX = e.GetPosition(this).X; InvalidateVisual(); };
        MouseLeave += (_, _) => { _hoverX = null; InvalidateVisual(); };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        // An invisible fill makes the whole plot hit-testable, so hovering works over empty areas too.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

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

        if (_hoverX is double hx && hx >= px && hx <= w)
            DrawReadout(dc, hx, now, pts, gpuPts, X, Y, px, pw, ph, line, lineColor, dpi);
    }

    /// <summary>
    /// Cursor line at the hovered time with the cable and GPU board values nearest to it, and the
    /// clock time of that sample. The box flips to the left of the cursor near the right edge.
    /// </summary>
    private void DrawReadout(DrawingContext dc, double hx, DateTime now, List<(DateTime T, double W)> pts, List<(DateTime T, double W)> gpuPts,
        Func<DateTime, double> X, Func<double, double> Y, double px, double pw, double ph, Brush line, Color lineColor, double dpi)
    {
        DateTime t = now.AddSeconds(-(1 - (hx - px) / pw) * WindowSeconds);
        var cable = Nearest(pts, t);
        var gpu = Nearest(gpuPts, t);
        if (cable == null && gpu == null) return;

        var at = cable?.T ?? gpu!.Value.T;
        double cx = Math.Round(X(at)) + 0.5;
        var cursorPen = new Pen(new SolidColorBrush(Theme.WithAlpha(((SolidColorBrush)Res("TextPrimary")).Color, 0x70)), 1);
        dc.DrawLine(cursorPen, new Point(cx, 0), new Point(cx, ph));

        var text = Res("TextPrimary");
        var muted = Res("TextMuted");
        var lines = new List<FormattedText>();
        if (cable is { } c)
        {
            lines.Add(MakeText($"{Loc.T("cable")}  {c.W:0} W", text, 10.5, dpi));
            dc.DrawEllipse(line, null, new Point(X(c.T), Y(c.W)), 3, 3);
        }
        if (gpu is { } g)
        {
            lines.Add(MakeText($"{Loc.T("GPU board")}  {g.W:0} W", text, 10.5, dpi));
            dc.DrawEllipse(new SolidColorBrush(Theme.WithAlpha(lineColor, 0xA0)), null, new Point(X(g.T), Y(g.W)), 2.4, 2.4);
        }
        lines.Add(MakeText(at.ToString("T", CultureInfo.CurrentCulture), muted, 9.5, dpi));

        const double pad = 6;
        double bw = lines.Max(l => l.Width) + pad * 2;
        double bh = lines.Sum(l => l.Height) + pad * 2 - 2;
        double bx = cx + 8;
        if (bx + bw > px + pw) bx = cx - 8 - bw;
        double by = Math.Clamp(Y(cable?.W ?? gpu!.Value.W) - bh / 2, 0, Math.Max(0, ph - bh));

        var boxFill = new SolidColorBrush(Theme.WithAlpha(((SolidColorBrush)Res("PanelSolidBrush")).Color, 0xE6));
        dc.DrawRoundedRectangle(boxFill, new Pen(Res("PanelBorderBrush"), 1), new Rect(bx, by, bw, bh), 4, 4);
        double ty = by + pad - 1;
        foreach (var l in lines)
        {
            dc.DrawText(l, new Point(bx + pad, ty));
            ty += l.Height;
        }
    }

    /// <summary>The sample closest to <paramref name="t"/>, or null when the nearest one is across a gap.</summary>
    private (DateTime T, double W)? Nearest(List<(DateTime T, double W)> pts, DateTime t)
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
        return (best.T - t).Duration().TotalSeconds <= GapSeconds ? best : null;
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
