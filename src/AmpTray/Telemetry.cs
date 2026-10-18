using System;
using System.Collections.Generic;
using System.Linq;
using AmpTray.Device;

namespace AmpTray;

/// <summary>Rolling power history for the graph (kept for the longest selectable window).</summary>
public sealed class PowerHistory
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private readonly List<(DateTime T, double W)> _points = new();

    public IReadOnlyList<(DateTime T, double W)> Points => _points;

    public void Add(DateTime t, double w)
    {
        _points.Add((t, w));
        int drop = 0;
        while (drop < _points.Count && t - _points[drop].T > MaxAge) drop++;
        if (drop > 0) _points.RemoveRange(0, drop);
    }

    public IEnumerable<(DateTime T, double W)> Since(DateTime from) => _points.Where(p => p.T >= from);
}

public sealed record FaultEvent(DateTime Time, Fault Fault, bool Active);

/// <summary>GPU peaks and energy since the widget started (or since the last reset).</summary>
public sealed class GpuStats
{
    public DateTime Since { get; private set; } = DateTime.Now;
    public double PeakTempC { get; private set; }
    public double PeakPowerW { get; private set; }
    public double EnergyWh { get; private set; }

    private double? _energyBaseJ;
    private DateTime? _lastTime;
    private double _integratedJ;

    public void Add(Gpu.GpuSnapshot g)
    {
        PeakTempC = Math.Max(PeakTempC, g.TempC);
        PeakPowerW = Math.Max(PeakPowerW, g.PowerW);

        // Prefer the driver's own energy counter; otherwise integrate power like the WireView stats do.
        if (g.EnergyJ is double e)
        {
            _energyBaseJ ??= e;
            EnergyWh = Math.Max(0, e - _energyBaseJ.Value) / 3600.0;
        }
        else
        {
            if (_lastTime is DateTime last)
            {
                double dt = (g.Timestamp - last).TotalSeconds;
                if (dt > 0 && dt < 5) _integratedJ += g.PowerW * dt;
            }
            EnergyWh = _integratedJ / 3600.0;
        }
        _lastTime = g.Timestamp;
    }

    public void Reset()
    {
        Since = DateTime.Now;
        PeakTempC = 0;
        PeakPowerW = 0;
        EnergyWh = 0;
        _energyBaseJ = null;
        _lastTime = null;
        _integratedJ = 0;
    }
}

/// <summary>Min/max/energy statistics since the widget started (or since the last reset).</summary>
public sealed class SessionStats
{
    public DateTime Since { get; private set; } = DateTime.Now;
    public long Samples { get; private set; }
    public double PeakPowerW { get; private set; }
    public double EnergyWh { get; private set; }
    public double[] PinMaxA { get; } = new double[SensorSnapshot.PinCount];
    public double[] PinMinA { get; } = Enumerable.Repeat(double.NaN, SensorSnapshot.PinCount).ToArray();
    public double[] TempMaxC { get; } = Enumerable.Repeat(double.NaN, 4).ToArray();
    public List<FaultEvent> FaultEvents { get; } = new();

    private DateTime? _lastTime;
    private double _powerSum;
    private ushort _lastFaultStatus;

    public double AvgPowerW => Samples == 0 ? 0 : _powerSum / Samples;

    public void Add(SensorSnapshot s)
    {
        Samples++;
        double w = s.TotalPowerW;
        _powerSum += w;
        PeakPowerW = Math.Max(PeakPowerW, w);

        // Integrate energy; ignore gaps (disconnects) longer than a few seconds.
        if (_lastTime is DateTime last)
        {
            double dt = (s.Timestamp - last).TotalSeconds;
            if (dt > 0 && dt < 5) EnergyWh += w * dt / 3600.0;
        }
        _lastTime = s.Timestamp;

        for (int i = 0; i < SensorSnapshot.PinCount; i++)
        {
            PinMaxA[i] = Math.Max(PinMaxA[i], s.PinCurrent[i]);
            PinMinA[i] = double.IsNaN(PinMinA[i]) ? s.PinCurrent[i] : Math.Min(PinMinA[i], s.PinCurrent[i]);
        }

        double[] temps = { s.TempInC, s.TempOutC, s.TempTs1C, s.TempTs2C };
        for (int i = 0; i < temps.Length; i++)
            if (SensorSnapshot.IsTempPresent(temps[i]))
                TempMaxC[i] = double.IsNaN(TempMaxC[i]) ? temps[i] : Math.Max(TempMaxC[i], temps[i]);

        ushort changed = (ushort)(s.FaultStatus ^ _lastFaultStatus);
        foreach (var f in SensorSnapshot.Decode(changed))
            FaultEvents.Add(new FaultEvent(s.Timestamp, f, (s.FaultStatus & (1 << (int)f)) != 0));
        if (FaultEvents.Count > 50) FaultEvents.RemoveRange(0, FaultEvents.Count - 50);
        _lastFaultStatus = s.FaultStatus;
    }

    public void Reset()
    {
        Since = DateTime.Now;
        Samples = 0;
        PeakPowerW = 0;
        EnergyWh = 0;
        _powerSum = 0;
        _lastTime = null;
        Array.Clear(PinMaxA);
        Array.Fill(PinMinA, double.NaN);
        Array.Fill(TempMaxC, double.NaN);
        FaultEvents.Clear();
    }
}
