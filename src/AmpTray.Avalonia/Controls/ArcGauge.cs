using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AmpTray.Avalonia.Controls;

/// <summary>Small 270° arc gauge: a muted track with a coloured fill proportional to <see cref="Value"/>.</summary>
public sealed class ArcGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ArcGauge, double>(nameof(Value));

    public static readonly StyledProperty<string> BrushKeyProperty =
        AvaloniaProperty.Register<ArcGauge, string>(nameof(BrushKey), "OkBrush");

    static ArcGauge()
    {
        AffectsRender<ArcGauge>(ValueProperty, BrushKeyProperty);
    }

    /// <summary>0..1 fraction of the arc to fill.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Application resource key of the fill brush.</summary>
    public string BrushKey
    {
        get => GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        double size = Math.Min(w, h);
        if (size < 6) return;
        double thickness = Math.Max(2, size / 7);
        double r = size / 2 - thickness / 2;
        var c = new Point(w / 2, h / 2);

        var track = new Pen(AppTheme.BrushOf("TrackBrush"), thickness, lineCap: PenLineCap.Round);
        var fill = new Pen(AppTheme.BrushOf(BrushKey), thickness, lineCap: PenLineCap.Round);

        dc.DrawGeometry(null, track, Arc(c, r, 135, 270));
        double sweep = 270 * Math.Clamp(Value, 0, 1);
        if (sweep > 0.5) dc.DrawGeometry(null, fill, Arc(c, r, 135, sweep));
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        Point At(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(startDeg), false);
            ctx.ArcTo(At(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        return g;
    }
}
