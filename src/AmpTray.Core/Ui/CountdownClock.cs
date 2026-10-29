using System;

namespace AmpTray.Ui;

/// <summary>
/// The shutdown countdown's clock. It counts down by the time that actually passed between ticks,
/// capped at one second per tick, so a PC that was asleep (or had its clock moved) does not find the
/// countdown already expired. Duplicated from the WPF ShutdownCountdownWindow.
/// </summary>
public sealed class CountdownClock
{
    /// <summary>How often the WPF window ticks; a front end may tick faster or slower.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>The most one tick may subtract, in seconds.</summary>
    public const double MaxStepSeconds = 1.0;

    private DateTime _lastTick;

    public CountdownClock(double seconds, DateTime now)
    {
        Remaining = seconds;
        _lastTick = now;
    }

    /// <summary>Seconds left; may go slightly below zero on the final tick.</summary>
    public double Remaining { get; private set; }

    /// <summary>True once the countdown has run out.</summary>
    public bool Elapsed => Remaining <= 0;

    /// <summary>Whole seconds to show, rounded up and never negative.</summary>
    public int DisplaySeconds => (int)Math.Max(0, Math.Ceiling(Remaining));

    /// <summary>The number as the window shows it.</summary>
    public string DisplayText => Math.Max(0, Math.Ceiling(Remaining)).ToString("0");

    /// <summary>Advances the clock to <paramref name="now"/>. Returns true when the countdown has run out.</summary>
    public bool Tick(DateTime now)
    {
        double elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (elapsed > 0) Remaining -= Math.Min(MaxStepSeconds, elapsed);
        return Remaining <= 0;
    }
}
