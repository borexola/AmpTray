using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AmpTray.Logging;
using Xunit;

namespace AmpTray.Tests;

public class EventLogTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _path;

    public EventLogTests() => _path = _dir.File("test.log");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Events_round_trip_through_json_lines()
    {
        var log = new EventLog(_path);
        log.Write(LogLevel.Info, "device", "connected", "Device connected on COM3", ("port", "COM3"), ("skipped", null));
        log.Write(LogLevel.Error, "crash", "unhandled", "Boom", new InvalidOperationException("bad state"), ("stage", "run"));

        var lines = File.ReadAllLines(_path);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("{", l));

        var events = EventLog.Read(_path);
        Assert.Equal(2, events.Count);
        Assert.Equal((LogLevel.Info, "device", "connected", "Device connected on COM3"), (events[0].Level, events[0].Source, events[0].Event, events[0].Message));
        Assert.Equal("COM3", events[0].Data!["port"]);
        Assert.False(events[0].Data!.ContainsKey("skipped"));
        Assert.Equal(LogLevel.Error, events[1].Level);
        Assert.Equal("System.InvalidOperationException", events[1].Data!["exception"]);
        Assert.Contains("bad state", events[1].Data!["detail"]);
        Assert.Equal("run", events[1].Data!["stage"]);
        Assert.True((DateTimeOffset.Now - events[1].Time).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Events_without_data_omit_the_field()
    {
        var e = new LogEvent(DateTimeOffset.Now, LogLevel.Info, "app", "started", "AmpTray started", null);
        Assert.DoesNotContain("\"data\"", e.ToJson());
        Assert.Equal(e.Message, LogEvent.Parse(e.ToJson())!.Message);
    }

    [Fact]
    public void Lines_that_are_not_events_are_skipped()
    {
        File.WriteAllText(_path, "2026-01-01 10:00:00  old plain text line\n{\"ts\":\"nonsense\"}\n{ broken\n" +
            new LogEvent(DateTimeOffset.Now, LogLevel.Warning, "service", "log", "kept", null).ToJson() + "\n");
        var events = EventLog.Read(_path);
        Assert.Equal("kept", Assert.Single(events).Message);
    }

    [Fact]
    public void The_file_never_grows_past_the_cap_and_keeps_the_newest_events()
    {
        var log = new EventLog(_path);
        string filler = new('x', 900);
        int n = 0;
        while (n < 6000)
        {
            log.Write(LogLevel.Info, "test", "fill", filler, ("n", n));
            n++;
            Assert.True(new FileInfo(_path).Length <= EventLog.MaxBytes, $"grew past the cap after {n} events");
        }
        var events = EventLog.Read(_path);
        Assert.True(events.Count > 500);
        Assert.Equal((n - 1).ToString(), events[^1].Data!["n"]);
        // Oldest survivors form one unbroken run up to the newest.
        var numbers = events.Select(e => int.Parse(e.Data!["n"])).ToList();
        Assert.Equal(Enumerable.Range(numbers[0], numbers.Count), numbers);
        Assert.True(new FileInfo(_path).Length <= EventLog.MaxBytes);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Trimming_never_leaves_half_a_line()
    {
        var log = new EventLog(_path);
        string filler = new('y', 5000);
        for (int i = 0; i < 600; i++) log.Write(LogLevel.Info, "test", "fill", filler, ("n", i));
        var first = File.ReadLines(_path).First();
        Assert.NotNull(LogEvent.Parse(first));
    }

    [Fact]
    public void Clear_empties_the_log_and_writes_resume()
    {
        var log = new EventLog(_path);
        log.Write(LogLevel.Info, "a", "b", "one");
        log.Clear();
        Assert.False(File.Exists(_path));
        log.Write(LogLevel.Info, "a", "b", "two");
        Assert.Equal("two", Assert.Single(EventLog.Read(_path)).Message);
    }

    [Fact]
    public void Written_event_fires_per_line()
    {
        var log = new EventLog(_path);
        int fired = 0;
        log.Written += () => fired++;
        log.Write(LogLevel.Info, "a", "b", "c");
        log.Write(LogLevel.Info, "a", "b", "d");
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Reader_returns_only_new_lines_and_restarts_after_a_trim()
    {
        var log = new EventLog(_path);
        var reader = new EventLogReader(_path);
        Assert.False(reader.Poll(out var none));
        Assert.Empty(none);

        log.Write(LogLevel.Info, "a", "b", "first");
        Assert.False(reader.Poll(out var first));
        Assert.Equal("first", Assert.Single(first).Message);

        log.Write(LogLevel.Info, "a", "b", "second");
        Assert.False(reader.Poll(out var second));
        Assert.Equal("second", Assert.Single(second).Message);
        Assert.False(reader.Poll(out var again));
        Assert.Empty(again);

        log.Clear();
        log.Write(LogLevel.Info, "a", "b", "fresh");
        Assert.True(reader.Poll(out var fresh));
        Assert.Equal("fresh", Assert.Single(fresh).Message);
    }

    [Fact]
    public void Reader_holds_back_a_line_that_is_still_being_written()
    {
        var reader = new EventLogReader(_path);
        var e = new LogEvent(DateTimeOffset.Now, LogLevel.Info, "a", "b", "whole", null).ToJson();
        File.WriteAllText(_path, e + "\n" + e[..10], new UTF8Encoding(false));
        reader.Poll(out var events);
        Assert.Single(events);
        File.AppendAllText(_path, e[10..] + "\n");
        reader.Poll(out var rest);
        Assert.Equal("whole", Assert.Single(rest).Message);
    }

    [Fact]
    public void Long_exception_details_are_truncated()
    {
        var log = new EventLog(_path);
        log.Write(LogLevel.Error, "a", "b", "c", new Exception(new string('z', 10_000)));
        Assert.True(EventLog.Read(_path)[0].Data!["detail"].Length < 4100);
    }
}

public class LogFilterTests
{
    private static LogEvent Ev(LogLevel level, string source, string message, params (string, string)[] data) =>
        new(DateTimeOffset.Now, level, source, "evt", message, data.Length == 0 ? null : data.ToDictionary(d => d.Item1, d => d.Item2));

    [Fact]
    public void Level_is_a_minimum()
    {
        Assert.True(LogFilter.Matches(Ev(LogLevel.Error, "app", "x"), LogLevel.Warning, null, null));
        Assert.True(LogFilter.Matches(Ev(LogLevel.Warning, "app", "x"), LogLevel.Warning, null, null));
        Assert.False(LogFilter.Matches(Ev(LogLevel.Info, "app", "x"), LogLevel.Warning, null, null));
    }

    [Fact]
    public void Source_is_exact_and_case_insensitive()
    {
        Assert.True(LogFilter.Matches(Ev(LogLevel.Info, "Device", "x"), LogLevel.Debug, "device", null));
        Assert.False(LogFilter.Matches(Ev(LogLevel.Info, "devices", "x"), LogLevel.Debug, "device", null));
        Assert.True(LogFilter.Matches(Ev(LogLevel.Info, "gpu", "x"), LogLevel.Debug, "", null));
    }

    [Fact]
    public void Text_searches_message_event_source_and_data()
    {
        var e = Ev(LogLevel.Info, "device", "Device connected", ("port", "COM3"));
        Assert.True(LogFilter.Matches(e, LogLevel.Debug, null, "CONNECTED"));
        Assert.True(LogFilter.Matches(e, LogLevel.Debug, null, "com3"));
        Assert.True(LogFilter.Matches(e, LogLevel.Debug, null, "evt"));
        Assert.True(LogFilter.Matches(e, LogLevel.Debug, null, "  dev "));
        Assert.False(LogFilter.Matches(e, LogLevel.Debug, null, "gpu"));
        Assert.True(LogFilter.Matches(e, LogLevel.Debug, null, "   "));
    }
}

public class CrashRecoveryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _originalHistory = CrashRecovery.HistoryPath;

    public CrashRecoveryTests() => CrashRecovery.HistoryPath = _dir.File("crashes.json");

    public void Dispose()
    {
        CrashRecovery.HistoryPath = _originalHistory;
        _dir.Dispose();
    }

    [Fact]
    public void Restarts_until_the_third_crash_in_the_window()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0);
        Assert.True(CrashRecovery.ShouldRestart(Array.Empty<DateTime>(), now));
        Assert.True(CrashRecovery.ShouldRestart(new[] { now.AddMinutes(-1), now.AddMinutes(-2) }, now));
        Assert.False(CrashRecovery.ShouldRestart(new[] { now.AddMinutes(-1), now.AddMinutes(-2), now.AddMinutes(-3) }, now));
        // Old crashes do not count against a fresh run.
        Assert.True(CrashRecovery.ShouldRestart(new[] { now.AddMinutes(-1), now.AddMinutes(-20), now.AddMinutes(-30) }, now));
        // Clock set back: a crash "in the future" is ignored rather than counted forever.
        Assert.True(CrashRecovery.ShouldRestart(new[] { now.AddMinutes(5), now.AddMinutes(6), now.AddMinutes(7) }, now));
    }

    [Fact]
    public void History_keeps_only_the_window_and_survives_a_broken_file()
    {
        var now = new DateTime(2026, 7, 1, 12, 0, 0);
        Assert.Single(CrashRecovery.Record(now.AddMinutes(-30)));
        Assert.Single(CrashRecovery.Record(now)); // the old one has aged out
        Assert.Equal(2, CrashRecovery.Record(now.AddMinutes(1)).Count);

        File.WriteAllText(CrashRecovery.HistoryPath, "not json");
        Assert.Single(CrashRecovery.Record(now.AddMinutes(2)));
    }
}
