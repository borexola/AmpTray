using System;
using AmpTray.Ui;
using Xunit;

namespace AmpTray.Tests;

public class PowerPathSmootherTests
{
    private static readonly DateTime T0 = new(2026, 5, 6, 12, 0, 0);

    [Fact]
    public void First_sample_is_taken_as_is_and_written_at_once()
    {
        var s = new PowerPathSmoother();
        Assert.False(s.HasText);
        Assert.True(s.Add(T0, 300, 250));
        Assert.Equal(300, s.BoardW);
        Assert.Equal(250, s.CableW);
        Assert.Equal(50, s.SlotW);
        Assert.False(s.CableAbove);
        Assert.True(s.HasText);
    }

    [Fact]
    public void Rewrites_at_most_every_two_seconds()
    {
        var s = new PowerPathSmoother();
        Assert.True(s.Add(T0, 300, 250));
        Assert.False(s.Add(T0.AddSeconds(1), 300, 250));
        Assert.False(s.Add(T0.AddSeconds(1.9), 300, 250));
        Assert.True(s.Add(T0.AddSeconds(2), 300, 250));
        Assert.False(s.Add(T0.AddSeconds(3), 300, 250));
        s.Invalidate();
        Assert.True(s.Add(T0.AddSeconds(3.5), 300, 250));
    }

    [Fact]
    public void Smooths_with_a_five_second_time_constant()
    {
        var s = new PowerPathSmoother();
        s.Add(T0, 0, 0);
        s.Add(T0.AddSeconds(5), 100, 50);
        double k = 1 - Math.Exp(-1); // one time constant
        Assert.Equal(100 * k, s.BoardW, 9);
        Assert.Equal(50 * k, s.CableW, 9);
        // The step is clamped to 60 s, so a long pause does not snap fully to the new value.
        s.Add(T0.AddSeconds(5 + 3600), 100, 50);
        Assert.True(s.BoardW < 100);
        Assert.Equal(100 * k + (100 - 100 * k) * (1 - Math.Exp(-12)), s.BoardW, 9);
    }

    [Fact]
    public void Wording_flips_with_hysteresis()
    {
        var s = new PowerPathSmoother();
        var t = T0;
        bool Feed(double board, double cable) { t = t.AddSeconds(2); return s.Add(t, board, cable); }

        Assert.True(s.Add(t, 300, 300));
        Assert.False(s.CableAbove);

        // Slot share drifting down to -4 W: not yet below -5, so the wording stays.
        s.Reset();
        Assert.True(Feed(300, 304));
        Assert.False(s.CableAbove);

        // Below -5 W flips to "cable above".
        s.Reset();
        Assert.True(Feed(300, 306));
        Assert.True(s.CableAbove);

        // Back at +1 W is inside the dead band, still "cable above".
        s.Reset();
        Assert.True(Feed(301, 300));
        Assert.True(s.CableAbove);

        // Above +2 W flips back.
        s.Reset();
        Assert.True(Feed(303, 300));
        Assert.False(s.CableAbove);
    }

    [Fact]
    public void Reset_forgets_the_averages_but_not_the_wording()
    {
        var s = new PowerPathSmoother();
        s.Add(T0, 300, 310);
        Assert.True(s.CableAbove);
        s.Reset();
        Assert.True(double.IsNaN(s.BoardW));
        Assert.True(double.IsNaN(s.CableW));
        Assert.False(s.HasText);
        Assert.True(s.CableAbove);
        Assert.True(s.Add(T0.AddSeconds(0.1), 100, 20));
        Assert.Equal(100, s.BoardW);
        Assert.False(s.CableAbove);
    }
}
