using System;

namespace AmpTray.Ui;

/// <summary>
/// Smoothing behind the power-path sentence. The driver's board power and the WireView's cable
/// power both jitter from sample to sample, so the sentence uses a few seconds of smoothing, is
/// rewritten at most every two seconds, and only changes its wording once the difference has
/// clearly moved to the other side of zero. Duplicated from the WPF widget (WidgetWindow.Gpu).
/// </summary>
public sealed class PowerPathSmoother
{
    /// <summary>Time constant of the exponential moving average.</summary>
    public const double TauSeconds = 5.0;

    /// <summary>The sentence is rewritten at most this often.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(2);

    /// <summary>The wording flips to "cable above board" once the slot share drops below this.</summary>
    public const double FlipBelowW = -5;

    /// <summary>...and back once the slot share rises above this.</summary>
    public const double FlipAboveW = 2;

    private double _boardW = double.NaN, _cableW = double.NaN;
    private DateTime _written = DateTime.MinValue, _sampled = DateTime.MinValue;

    /// <summary>Smoothed board power (NaN before the first sample).</summary>
    public double BoardW => _boardW;

    /// <summary>Smoothed cable power (NaN before the first sample).</summary>
    public double CableW => _cableW;

    /// <summary>Board minus cable: the estimated PCIe slot share.</summary>
    public double SlotW => _boardW - _cableW;

    /// <summary>True while the sentence says the cable reads more than the board power.</summary>
    public bool CableAbove { get; private set; }

    /// <summary>True once <see cref="Add"/> has asked for the sentence to be written.</summary>
    public bool HasText => _written != DateTime.MinValue;

    /// <summary>Forgets the smoothed values (the WireView went away); the wording state is kept.</summary>
    public void Reset()
    {
        _boardW = _cableW = double.NaN;
        _written = _sampled = DateTime.MinValue;
    }

    /// <summary>The sentence was cleared elsewhere, so the next sample writes it regardless of the refresh interval.</summary>
    public void Invalidate() => _written = DateTime.MinValue;

    /// <summary>
    /// Feeds one pair of readings. Returns true when the sentence should be (re)written now, in which
    /// case <see cref="BoardW"/>, <see cref="CableW"/> and <see cref="CableAbove"/> hold its inputs.
    /// </summary>
    public bool Add(DateTime now, double boardW, double cableW)
    {
        // Exponential moving average with a time constant of about five seconds at any poll rate.
        double dt = Math.Clamp((now - _sampled).TotalSeconds, 0.05, 60);
        double k = 1 - Math.Exp(-dt / TauSeconds);
        _sampled = now;
        _boardW = double.IsNaN(_boardW) ? boardW : _boardW + k * (boardW - _boardW);
        _cableW = double.IsNaN(_cableW) ? cableW : _cableW + k * (cableW - _cableW);
        if (now - _written < Refresh && HasText) return false;
        _written = now;

        double slot = SlotW;
        if (CableAbove ? slot > FlipAboveW : slot < FlipBelowW) CableAbove = !CableAbove;
        return true;
    }
}
