using System;
using System.IO;
using System.Linq;
using AmpTray.Gpu;
using Xunit;

namespace AmpTray.Tests;

public class DataLoggerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static readonly DateTime T0 = new(2026, 1, 2, 3, 4, 5, 678);

    public void Dispose() => _dir.Dispose();

    /// <summary>Reads while the logger still holds the file open for writing.</summary>
    private static string[] Lines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Fact]
    public void Creates_a_daily_file_with_a_header_and_one_row_per_sample()
    {
        using var log = new DataLogger();
        log.Write(Snap.Make(volts: 12, amps: 2, at: T0), null, _dir.Path, 0);
        log.Write(Snap.Make(volts: 12, amps: 3, at: T0.AddSeconds(1)), null, _dir.Path, 0);

        Assert.Equal(_dir.File("wireview_2026-01-02.csv"), log.CurrentFile);
        Assert.Null(log.LastError);
        var lines = Lines(log.CurrentFile!);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("timestamp,total_w,total_a,wire1_v", lines[0]);
        Assert.EndsWith("gpu_core_mhz,gpu_fan_pct", lines[0]);
        Assert.StartsWith("2026-01-02 03:04:05.678,144.00,12.000,12.000", lines[1]);
        Assert.Equal(lines[0].Split(',').Length, lines[1].Split(',').Length);
    }

    [Fact]
    public void Gpu_columns_are_filled_when_a_gpu_sample_is_present()
    {
        using var log = new DataLogger();
        var gpu = new GpuSnapshot { TempC = 61, MemTempC = null, LoadPct = 55, PowerW = 210.4, PowerLimitW = 450, MemUsedBytes = 3UL * 1024 * 1024 * 1024, CoreMhz = 2400, FanPct = 40 };
        log.Write(Snap.Make(at: T0), gpu, _dir.Path, 0);
        var row = Lines(log.CurrentFile!)[1].Split(',');
        Assert.Equal(new[] { "61", "", "55", "210.4", "450", "3072", "2400", "40" }, row[^8..]);

        log.Write(Snap.Make(at: T0.AddSeconds(1)), null, _dir.Path, 0);
        var noGpu = Lines(log.CurrentFile!)[2].Split(',');
        Assert.All(noGpu[^8..], cell => Assert.Equal("", cell));
    }

    [Fact]
    public void Faults_and_absent_temperatures_are_written_readably()
    {
        using var log = new DataLogger();
        log.Write(Snap.Make(ts2: -50, faults: 0x05, at: T0), null, _dir.Path, 0);
        var row = Lines(log.CurrentFile!)[1].Split(',');
        int tempIn = Array.IndexOf(Lines(log.CurrentFile!)[0].Split(','), "temp_in_c");
        Assert.Equal("25.0", row[tempIn]);
        Assert.Equal("", row[tempIn + 3]); // TS2 unplugged
        Assert.Equal("0x0005", row[tempIn + 4]);
        Assert.Equal("\"Chip over-temp; Over-current\"", row[tempIn + 6]);
    }

    [Fact]
    public void Interval_throttles_rows()
    {
        using var log = new DataLogger();
        log.Write(Snap.Make(at: T0), null, _dir.Path, 10);
        log.Write(Snap.Make(at: T0.AddSeconds(5)), null, _dir.Path, 10);   // too soon
        log.Write(Snap.Make(at: T0.AddSeconds(10)), null, _dir.Path, 10);  // due
        Assert.Equal(3, Lines(log.CurrentFile!).Length);
    }

    [Fact]
    public void A_file_with_a_foreign_header_is_left_alone_and_a_sibling_is_used()
    {
        File.WriteAllText(_dir.File("wireview_2026-01-02.csv"), "time,watts\n1,2\n");
        using var log = new DataLogger();
        log.Write(Snap.Make(at: T0), null, _dir.Path, 0);
        Assert.Equal(_dir.File("wireview_2026-01-02_2.csv"), log.CurrentFile);
        Assert.Equal(2, Lines(_dir.File("wireview_2026-01-02.csv")).Length);
    }

    [Fact]
    public void Reopening_the_same_day_appends_without_a_second_header()
    {
        using var log = new DataLogger();
        log.Write(Snap.Make(at: T0), null, _dir.Path, 0);
        log.Close();
        Assert.Null(log.CurrentFile);
        log.Write(Snap.Make(at: T0.AddSeconds(1)), null, _dir.Path, 0);
        var lines = Lines(log.CurrentFile!);
        Assert.Equal(3, lines.Length);
        Assert.Single(lines, l => l.StartsWith("timestamp"));
    }

    [Fact]
    public void A_new_day_starts_a_new_file()
    {
        using var log = new DataLogger();
        log.Write(Snap.Make(at: T0), null, _dir.Path, 0);
        log.Write(Snap.Make(at: T0.AddDays(1)), null, _dir.Path, 0);
        Assert.Equal(_dir.File("wireview_2026-01-03.csv"), log.CurrentFile);
        Assert.True(File.Exists(_dir.File("wireview_2026-01-02.csv")));
    }

    [Fact]
    public void An_unwritable_folder_is_reported_not_thrown()
    {
        using var log = new DataLogger();
        string blocked = _dir.File("blocked");
        File.WriteAllText(blocked, "a file where the folder should be");
        log.Write(Snap.Make(at: T0), null, blocked, 0);
        Assert.NotNull(log.LastError);
        Assert.Null(log.CurrentFile);
    }
}
