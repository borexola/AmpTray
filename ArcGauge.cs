using System;
using System.Windows;
using System.Windows.Media;

namespace AmpTray;

/// <summary>Small 270° arc gauge: a muted track with a coloured fill proportional to <see cref="Value"/>.</summary>
public sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushKeyProperty = DependencyProperty.Register(
        nameof(BrushKey), typeof(string), typeof(ArcGauge), new FrameworkPropertyMetadata("OkBrush", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1 fraction of the arc to fill.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Application resource key of the fill brush.</summary>
    public string BrushKey
    {
        get => (string)GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 6) return;
        double thickness = Math.Max(2, size / 7);
        double r = size / 2 - thickness / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        var track = new Pen((Brush)Application.Current.Resources["TrackBrush"], thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var fill = new Pen((Brush)Application.Current.Resources[BrushKey], thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

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
            ctx.BeginFigure(At(startDeg), false, false);
            ctx.ArcTo(At(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
