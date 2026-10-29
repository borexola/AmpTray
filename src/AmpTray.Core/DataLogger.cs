using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AmpTray.Device;
using AmpTray.Gpu;

namespace AmpTray;

/// <summary>
/// Appends samples to a daily CSV file (wireview_yyyy-MM-dd.csv) in the chosen folder.
/// If an existing file was written by an older version with different columns, a numbered
/// sibling (wireview_yyyy-MM-dd_2.csv) is used so every file has one consistent header.
/// </summary>
public sealed class DataLogger : IDisposable
{
    private StreamWriter? _writer;
    private string? _basePath;
    private DateTime _lastWrite = DateTime.MinValue;

    public string? CurrentFile { get; private set; }
    public string? LastError { get; private set; }

    public void Write(SensorSnapshot s, GpuSnapshot? gpu, string folder, int intervalSeconds)
    {
        if (intervalSeconds > 0 && (s.Timestamp - _lastWrite).TotalSeconds < intervalSeconds - 0.05) return;

        try
        {
            string basePath = Path.Combine(folder, $"wireview_{s.Timestamp:yyyy-MM-dd}.csv");
            if (basePath != _basePath || _writer == null) Open(basePath);
            _writer!.WriteLine(FormatRow(s, gpu));
            _lastWrite = s.Timestamp;
            LastError = null;
        }
        catch (Exception ex)
        {
            // Typically the file is locked by another program; drop the writer and retry on the next sample.
            LastError = ex.Message;
            Close();
        }
    }

    private void Open(string basePath)
    {
        Close();
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);

        string path = basePath;
        bool isNew;
        for (int n = 2; ; n++)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) { isNew = true; break; }
            if (FirstLine(path) == Header) { isNew = false; break; }
            path = Path.Combine(Path.GetDirectoryName(basePath)!, $"{Path.GetFileNameWithoutExtension(basePath)}_{n}.csv");
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _basePath = basePath;
        CurrentFile = path;
        if (isNew) _writer.WriteLine(Header);
    }

    private static string FirstLine(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadLine() ?? "";
    }

    public void Close()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _basePath = null;
        CurrentFile = null;
    }

    public void Dispose() => Close();

    private static readonly string Header = string.Join(",",
        new[] { "timestamp", "total_w", "total_a" }
            .Concat(Enumerable.Range(1, 6).Select(i => $"wire{i}_v"))
            .Concat(Enumerable.Range(1, 6).Select(i => $"wire{i}_a"))
            .Concat(Enumerable.Range(1, 6).Select(i => $"wire{i}_w"))
            .Concat(new[] { "temp_in_c", "temp_out_c", "temp_ts1_c", "temp_ts2_c", "fault_status", "fault_log", "active_faults" })
            .Concat(new[] { "gpu_temp_c", "gpu_mem_temp_c", "gpu_load_pct", "gpu_power_w", "gpu_power_limit_w", "gpu_mem_used_mb", "gpu_core_mhz", "gpu_fan_pct" }));

    private static string FormatRow(SensorSnapshot s, GpuSnapshot? g)
    {
        var inv = CultureInfo.InvariantCulture;
        string T(double c) => SensorSnapshot.IsTempPresent(c) ? c.ToString("0.0", inv) : "";
        string N(double? v, string f) => v is double d ? d.ToString(f, inv) : "";

        var cells = new[]
            {
                s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", inv),
                s.TotalPowerW.ToString("0.00", inv),
                s.TotalCurrentA.ToString("0.000", inv),
            }
            .Concat(s.PinVoltage.Select(v => v.ToString("0.000", inv)))
            .Concat(s.PinCurrent.Select(a => a.ToString("0.000", inv)))
            .Concat(s.PinVoltage.Zip(s.PinCurrent, (v, a) => (v * a).ToString("0.00", inv)))
            .Concat(new[]
            {
                T(s.TempInC), T(s.TempOutC), T(s.TempTs1C), T(s.TempTs2C),
                $"0x{s.FaultStatus:X4}", $"0x{s.FaultLog:X4}",
                "\"" + string.Join("; ", SensorSnapshot.Decode(s.FaultStatus).Select(SensorSnapshot.Describe)) + "\"",
            })
            .Concat(g == null
                ? Enumerable.Repeat("", 8)
                : new[]
                {
                    N(g.TempC, "0"), N(g.MemTempC, "0"), N(g.LoadPct, "0"), N(g.PowerW, "0.0"), N(g.PowerLimitW, "0"),
                    N(g.MemUsedBytes / 1048576.0, "0"), N(g.CoreMhz, "0"), N(g.FanPct, "0"),
                });
        return string.Join(",", cells);
    }
}
