using System;
using AmpTray.Ui;
using Xunit;

namespace AmpTray.Tests;

public class CountdownClockTests
{
    private static readonly DateTime T0 = new(2026, 5, 6, 12, 0, 0);

    [Fact]
    public void Counts_down_by_real_time()
    {
        var c = new CountdownClock(10, T0);
        Assert.Equal(10, c.DisplaySeconds);
        Assert.Equal("10", c.DisplayText);
        Assert.False(c.Tick(T0));                      // first tick right after start changes nothing
        Assert.Equal(10, c.Remaining);
        Assert.False(c.Tick(T0.AddMilliseconds(200)));
        Assert.Equal(9.8, c.Remaining, 9);
        Assert.Equal(10, c.DisplaySeconds);            // rounded up
        Assert.False(c.Tick(T0.AddMilliseconds(1000)));
        Assert.Equal(9, c.Remaining, 9);
        Assert.Equal(9, c.DisplaySeconds);
    }

    [Fact]
    public void A_tick_subtracts_at_most_one_second()
    {
        var c = new CountdownClock(10, T0);
        Assert.False(c.Tick(T0.AddHours(1)));           // the PC was asleep
        Assert.Equal(9, c.Remaining, 9);
        Assert.False(c.Elapsed);
    }

    [Fact]
    public void Clock_moved_backwards_is_ignored()
    {
        var c = new CountdownClock(10, T0);
        Assert.False(c.Tick(T0.AddSeconds(-30)));
        Assert.Equal(10, c.Remaining);
        Assert.False(c.Tick(T0.AddSeconds(-29.5)));     // counted from the new reference
        Assert.Equal(9.5, c.Remaining, 9);
    }

    [Fact]
    public void Elapses_at_zero_and_never_shows_a_negative_number()
    {
        var c = new CountdownClock(1, T0);
        Assert.False(c.Tick(T0.AddSeconds(0.9)));
        Assert.Equal("1", c.DisplayText);
        Assert.True(c.Tick(T0.AddSeconds(1.9)));
        Assert.True(c.Elapsed);
        Assert.Equal("0", c.DisplayText);
        Assert.Equal(0, c.DisplaySeconds);
        Assert.True(c.Tick(T0.AddSeconds(10)));
        Assert.Equal("0", c.DisplayText);
    }
}
