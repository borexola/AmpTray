using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using AmpTray.Device;
using Xunit;

// Loc and Settings hold static state, so the suites run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AmpTray.Tests;

/// <summary>Builds the 100-byte sensor struct the WireView Pro II answers with, field by field.</summary>
internal sealed class Frame
{
    public double[] TempsC = { 25.0, 30.0, 35.0, 40.0 };
    public double[] VoltsV = { 12.05, 12.05, 12.05, 12.05, 12.05, 12.05 };
    public double[] AmpsA = { 5.0, 5.0, 5.0, 5.0, 5.0, 5.0 };
    public double?[] WattsOverride = new double?[6];   // null = consistent with V and I
    public uint? TotalMwOverride;
    public uint? TotalMaOverride;
    public byte Rating;                                // 0 = 600 W, 1 = 450, 2 = 300, 3 = 150
    public ushort FaultStatus;
    public ushort FaultLog;

    public byte[] Build()
    {
        var b = new byte[WireViewReader.SensorStructSize];
        var s = b.AsSpan();
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(i * 2), (short)Math.Round(TempsC[i] * 10));

        long sumMa = 0, sumMw = 0;
        for (int i = 0; i < 6; i++)
        {
            int o = 12 + i * 12;
            short mv = (short)Math.Round(VoltsV[i] * 1000);
            uint ma = (uint)Math.Round(AmpsA[i] * 1000);
            uint mw = WattsOverride[i] is double w ? (uint)Math.Round(w * 1000) : (uint)Math.Round((double)mv * ma / 1000.0);
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(o), mv);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 4), ma);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(o + 8), mw);
            sumMa += ma;
            sumMw += mw;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(84), TotalMwOverride ?? (uint)sumMw);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(88), TotalMaOverride ?? (uint)sumMa);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(92), (ushort)Math.Round(VoltsV[0] * 1000));
        b[94] = Rating;
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(96), FaultStatus);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(98), FaultLog);
        return b;
    }
}

internal static class Snap
{
    /// <summary>A sensor snapshot with every wire at the same voltage and current.</summary>
    public static SensorSnapshot Make(double volts = 12.0, double amps = 1.0, double tempIn = 25, double tempOut = 25,
        double ts1 = 25, double ts2 = 25, ushort faults = 0, DateTime? at = null, int rating = 600)
    {
        var v = new double[SensorSnapshot.PinCount];
        var a = new double[SensorSnapshot.PinCount];
        Array.Fill(v, volts);
        Array.Fill(a, amps);
        return new SensorSnapshot
        {
            Timestamp = at ?? new DateTime(2026, 1, 2, 3, 4, 5),
            PinVoltage = v,
            PinCurrent = a,
            TempInC = tempIn,
            TempOutC = tempOut,
            TempTs1C = ts1,
            TempTs2C = ts2,
            FaultStatus = faults,
            CableRatingW = rating,
        };
    }

    public static SensorSnapshot WithCurrents(this SensorSnapshot s, params double[] amps)
    {
        var a = (double[])s.PinCurrent.Clone();
        for (int i = 0; i < amps.Length; i++) a[i] = amps[i];
        return new SensorSnapshot
        {
            Timestamp = s.Timestamp, PinVoltage = s.PinVoltage, PinCurrent = a,
            TempInC = s.TempInC, TempOutC = s.TempOutC, TempTs1C = s.TempTs1C, TempTs2C = s.TempTs2C,
            FaultStatus = s.FaultStatus, FaultLog = s.FaultLog, CableRatingW = s.CableRatingW,
        };
    }
}

/// <summary>A scratch directory that disappears with the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AmpTrayTests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

/// <summary>Switches the UI language for the scope of a test and back to English afterwards.</summary>
internal sealed class LangScope : IDisposable
{
    public LangScope(string code) => Loc.Apply(code);
    public void Dispose() => Loc.Apply("en");
}

/// <summary>Formats numbers the same way on every machine while the scope lasts.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _ui = CultureInfo.CurrentUICulture;

    public CultureScope(CultureInfo? c = null)
    {
        CultureInfo.CurrentCulture = c ?? CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = c ?? CultureInfo.InvariantCulture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _ui;
    }
}
