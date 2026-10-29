using System;
using System.Collections.Generic;
using System.Linq;
using AmpTray.Ui;
using Xunit;

namespace AmpTray.Tests;

public class GraphMathTests
{
    private static readonly DateTime T0 = new(2026, 5, 6, 12, 0, 0);

    private static List<(DateTime T, double W)> Series(params (double Sec, double W)[] pts) =>
        pts.Select(p => (T0.AddSeconds(p.Sec), p.W)).ToList();

    [Theory]
    [InlineData(0, 25)]
    [InlineData(23.75, 25)]
    [InlineData(23.76, 50)]
    [InlineData(570, 600)]
    [InlineData(571, 800)]
    [InlineData(950, 1000)]
    [InlineData(951, 1100)]   // past the table: next hundred plus one
    [InlineData(1000, 1100)]
    [InlineData(1001, 1200)]
    public void Nice_scale_leaves_five_percent_headroom(double peak, double expected)
    {
        Assert.Equal(expected, GraphMath.NiceAbove(peak));
    }

    [Fact]
    public void Full_scale_is_the_cable_rating_until_the_draw_exceeds_it()
    {
        Assert.Equal(600, GraphMath.TopScale(10, 600));
        Assert.Equal(600, GraphMath.TopScale(600, 600));
        Assert.Equal(800, GraphMath.TopScale(601, 600));
        Assert.Equal(800, GraphMath.TopScale(700, 0));
        Assert.Equal(1000, GraphMath.TopScale(800, 0));
        Assert.Equal(25, GraphMath.TopScale(0, 0));
    }

    [Fact]
    public void Gap_is_at_least_five_seconds_or_four_poll_periods()
    {
        Assert.Equal(5, GraphMath.GapSeconds(500));
        Assert.Equal(8, GraphMath.GapSeconds(2000));
    }

    [Fact]
    public void X_and_time_mapping_are_inverses()
    {
        var now = T0.AddSeconds(300);
        Assert.Equal(34 + 266, GraphMath.X(now, now, 300, 34, 266), 9);
        Assert.Equal(34, GraphMath.X(T0, now, 300, 34, 266), 9);
        Assert.Equal(34 + 133, GraphMath.X(T0.AddSeconds(150), now, 300, 34, 266), 9);
        var t = GraphMath.TimeAt(34 + 133, now, 300, 34, 266);
        Assert.Equal(T0.AddSeconds(150), t);
        Assert.Equal(0, GraphMath.Y(600, 600, 100));
        Assert.Equal(100, GraphMath.Y(0, 600, 100));
        Assert.Equal(0, GraphMath.Y(900, 600, 100)); // clamped
    }

    [Fact]
    public void Nearest_picks_the_closest_sample_on_either_side()
    {
        var pts = Series((0, 10), (1, 11), (2, 12), (3, 13));
        Assert.Equal((T0.AddSeconds(1), 11.0), GraphMath.Nearest(pts, T0.AddSeconds(1.4), 5));
        Assert.Equal((T0.AddSeconds(2), 12.0), GraphMath.Nearest(pts, T0.AddSeconds(1.6), 5));
        Assert.Equal((T0, 10.0), GraphMath.Nearest(pts, T0.AddSeconds(-1), 5));
        Assert.Equal((T0.AddSeconds(3), 13.0), GraphMath.Nearest(pts, T0.AddSeconds(4), 5));
        Assert.Null(GraphMath.Nearest(new List<(DateTime, double)>(), T0, 5));
    }

    [Fact]
    public void Nearest_returns_null_across_a_gap()
    {
        var pts = Series((0, 10), (1, 11), (30, 12), (31, 13));
        Assert.Null(GraphMath.Nearest(pts, T0.AddSeconds(15), 5));
        Assert.Equal((T0.AddSeconds(1), 11.0), GraphMath.Nearest(pts, T0.AddSeconds(5), 5));
        Assert.Equal((T0.AddSeconds(30), 12.0), GraphMath.Nearest(pts, T0.AddSeconds(26), 5));
        Assert.Null(GraphMath.Nearest(pts, T0.AddSeconds(40), 5));
    }

    [Fact]
    public void Segments_split_at_gaps_and_drop_lone_samples()
    {
        var pts = Series((0, 1), (1, 2), (2, 3), (20, 4), (40, 5), (41, 6));
        var segs = GraphMath.Segments(pts, 5);
        Assert.Equal(2, segs.Count);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, segs[0].Select(p => p.W));
        Assert.Equal(new[] { 5.0, 6.0 }, segs[1].Select(p => p.W));
        Assert.Empty(GraphMath.Segments(Series((0, 1)), 5));
        Assert.Single(GraphMath.Segments(Series((0, 1), (5, 2)), 5)); // exactly the gap is still joined
    }
}
