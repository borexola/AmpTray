using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AmpTray.Service;
using Xunit;

namespace AmpTray.Tests;

public class PresentTallyTests
{
    private const string Header = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,TimeInSeconds";
    private const long Tick = 1000; // fake ticks per second

    [Fact]
    public void Header_columns_are_found_by_name()
    {
        var c = PresentTally.ParseHeader(Header);
        Assert.NotNull(c);
        Assert.Equal(0, c!.App);
        Assert.Equal(1, c.Pid);
        Assert.Equal(2, c.Chain);
        Assert.Equal(12, c.Time);
    }

    [Fact]
    public void Older_layout_falls_back_to_cpu_start_time_and_missing_columns_fail()
    {
        var c = PresentTally.ParseHeader("Application,ProcessID,SwapChainAddress,CPUStartTime");
        Assert.Equal(3, c!.Time);
        Assert.Null(PresentTally.ParseHeader("Application,SwapChainAddress,TimeInSeconds"));
        Assert.Null(PresentTally.ParseHeader("ProcessID,SwapChainAddress,Runtime"));
    }

    private static PresentTally.Columns Cols => PresentTally.ParseHeader(Header)!;

    private static string Row(string app, int pid, string chain, double t) =>
        $"{app},{pid},{chain},DXGI,1,0,0,Hardware: Legacy Flip,{t:0.000000},8.33,2,1,{t:0.000000}";

    [Fact]
    public void Frames_per_second_are_presents_in_the_last_second_on_the_busiest_chain()
    {
        var tally = new PresentTally();
        for (int i = 0; i < 120; i++) Assert.True(tally.AddRow(Row("game.exe", 42, "0x00000202F1B3C9A0", 10 + i / 120.0), Cols, Tick));
        for (int i = 0; i < 30; i++) tally.AddRow(Row("game.exe", 42, "0x00000202F1B3D000", 10 + i / 30.0), Cols, Tick);

        var e = Assert.Single(tally.Entries(Tick, Tick));
        Assert.Equal(42, e.Pid);
        Assert.Equal("game", e.Name);
        Assert.Equal(120, e.Fps);
        Assert.Equal(150, tally.Rows);
    }

    [Fact]
    public void Only_the_last_second_counts()
    {
        var tally = new PresentTally();
        for (int i = 0; i < 60; i++) tally.AddRow(Row("game.exe", 1, "0x1", i / 60.0), Cols, Tick);       // second 0: 60 fps
        for (int i = 0; i < 30; i++) tally.AddRow(Row("game.exe", 1, "0x1", 2 + i / 30.0), Cols, Tick);   // second 2: 30 fps
        Assert.Equal(30, Assert.Single(tally.Entries(Tick, Tick)).Fps);
    }

    [Fact]
    public void Silent_processes_drop_out_and_system_processes_are_ignored()
    {
        var tally = new PresentTally();
        for (int i = 0; i < 10; i++) tally.AddRow(Row("game.exe", 1, "0x1", i / 10.0), Cols, nowTicks: Tick);
        for (int i = 0; i < 10; i++) tally.AddRow(Row("dwm.exe", 2, "0x2", i / 10.0), Cols, nowTicks: Tick);
        for (int i = 0; i < 10; i++) tally.AddRow(Row("AmpTray.exe", 3, "0x3", i / 10.0), Cols, nowTicks: Tick);

        Assert.Equal(new[] { 1 }, tally.Entries(Tick, Tick).Select(e => e.Pid));
        Assert.Single(tally.Entries(Tick + 1500, Tick));   // 1.5 s quiet: still reported
        Assert.Empty(tally.Entries(Tick + 2500, Tick));    // 2.5 s quiet: the game stopped presenting
        Assert.Empty(tally.Entries(Tick + 6000, Tick));    // forgotten
    }

    [Fact]
    public void A_single_present_is_not_a_frame_rate()
    {
        var tally = new PresentTally();
        tally.AddRow(Row("game.exe", 1, "0x1", 1.0), Cols, Tick);
        Assert.Empty(tally.Entries(Tick, Tick));
    }

    [Fact]
    public void Unreadable_rows_are_skipped()
    {
        var tally = new PresentTally();
        Assert.False(tally.AddRow("game.exe,abc,0x1,DXGI", Cols, Tick));
        Assert.False(tally.AddRow("too,short", Cols, Tick));
        Assert.False(tally.AddRow(Row("game.exe", 1, "0x1", 1.0).Replace("1.000000", "nope"), Cols, Tick));
        Assert.Equal(0, tally.Rows);
        Assert.True(tally.AddRow(Row("game.exe", 1, "12345", 1.0), Cols, Tick)); // decimal chain id is fine too
    }

    [Fact]
    public void Rows_without_an_application_column_still_count()
    {
        var c = PresentTally.ParseHeader("ProcessID,SwapChainAddress,TimeInSeconds")!;
        var tally = new PresentTally();
        tally.AddRow("7,0x1,1.0", c, Tick);
        tally.AddRow("7,0x1,1.5", c, Tick);
        var e = Assert.Single(tally.Entries(Tick, Tick));
        Assert.Equal("", e.Name);
        Assert.Equal(2, e.Fps);
    }
}

public class FpsSnapshotTests : IDisposable
{
    private readonly TempDir _dir = new();

    public FpsSnapshotTests()
    {
        ServiceSettings.FolderOverride = _dir.Path;
    }

    public void Dispose()
    {
        ServiceSettings.FolderOverride = null;
        _dir.Dispose();
    }

    [Fact]
    public void Save_and_read_round_trip()
    {
        new FpsSnapshot { Timestamp = DateTime.Now, Processes = new List<FpsEntry> { new(5, "game", 144) } }.TrySave();
        var read = FpsSnapshot.TryRead();
        Assert.NotNull(read);
        var e = Assert.Single(read!.Processes);
        Assert.Equal((5, "game", 144.0), (e.Pid, e.Name, e.Fps));
        Assert.False(File.Exists(FpsSnapshot.FilePath + ".tmp"));
    }

    [Fact]
    public void Stale_or_missing_snapshots_read_as_nothing()
    {
        Assert.Null(FpsSnapshot.TryRead());
        new FpsSnapshot { Timestamp = DateTime.Now.AddSeconds(-10) }.TrySave();
        Assert.Null(FpsSnapshot.TryRead());
        Assert.True(new FpsSnapshot { Timestamp = DateTime.Now.AddSeconds(-4) }.IsStale);
        Assert.False(new FpsSnapshot { Timestamp = DateTime.Now }.IsStale);
    }

    [Fact]
    public void A_broken_file_reads_as_nothing()
    {
        File.WriteAllText(FpsSnapshot.FilePath, "{");
        Assert.Null(FpsSnapshot.TryRead());
    }
}
