using System;
using System.IO;
using System.Linq;
using AmpTray.Logging;
using AmpTray.Service;
using Xunit;

namespace AmpTray.Tests;

public class ServiceLogTests : IDisposable
{
    private readonly TempDir _dir = new();

    public ServiceLogTests() => ServiceSettings.FolderOverride = _dir.File("svc");

    public void Dispose()
    {
        ServiceSettings.FolderOverride = null;
        _dir.Dispose();
    }

    [Fact]
    public void Service_lines_are_structured_events_with_a_level_guessed_from_the_text()
    {
        // The service's own EventLog is created once per process; point it at this folder before the first write.
        ServiceSettings.Log("Device connected on COM3");
        ServiceSettings.Log("PresentMon failed to start: not found");
        ServiceSettings.Log(LogLevel.Error, "stopped", "Monitor stopped", ("reason", "test"));

        var events = EventLog.Read(ServiceSettings.LogFile).Where(e => e.Source == "service").TakeLast(3).ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal(LogLevel.Info, events[0].Level);
        Assert.Equal("Device connected on COM3", events[0].Message);
        Assert.Equal(LogLevel.Warning, events[1].Level);
        Assert.Equal((LogLevel.Error, "stopped", "test"), (events[2].Level, events[2].Event, events[2].Data!["reason"]));
        Assert.All(File.ReadAllLines(ServiceSettings.LogFile), l => Assert.NotNull(LogEvent.Parse(l)));
    }
}
