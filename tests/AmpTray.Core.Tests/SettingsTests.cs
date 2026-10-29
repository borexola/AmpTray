using System;
using System.IO;
using AmpTray.Service;
using Xunit;

namespace AmpTray.Tests;

public class SettingsNormalizeTests
{
    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var s = Settings.Normalize(new Settings { PollMs = 50, GraphWindowSeconds = 5, Opacity = 0.001, AlarmVolume = 0, GpuIndex = -2, LogIntervalSeconds = -9 });
        Assert.Equal(250, s.PollMs);
        Assert.Equal(30, s.GraphWindowSeconds);
        Assert.Equal(Settings.MinOpacity, s.Opacity);
        Assert.Equal(5, s.AlarmVolume);
        Assert.Equal(0, s.GpuIndex);
        Assert.Equal(0, s.LogIntervalSeconds);

        var big = Settings.Normalize(new Settings { PollMs = 99_999, GraphWindowSeconds = 99_999, Opacity = 7, AlarmVolume = 500 });
        Assert.Equal(5000, big.PollMs);
        Assert.Equal((int)PowerHistory.MaxAge.TotalSeconds, big.GraphWindowSeconds);
        Assert.Equal(1.0, big.Opacity);
        Assert.Equal(100, big.AlarmVolume);
    }

    [Fact]
    public void Non_finite_opacity_and_missing_objects_fall_back()
    {
        var s = Settings.Normalize(new Settings { Opacity = double.NaN, CustomColors = null!, Shutdown = null!, LogFolder = "  " });
        Assert.Equal(1.0, s.Opacity);
        Assert.NotNull(s.CustomColors);
        Assert.NotNull(s.Shutdown);
        Assert.Equal(Settings.DefaultLogFolder, s.LogFolder);
    }

    [Fact]
    public void Defaults_are_safe()
    {
        var s = new Settings();
        Assert.False(s.Shutdown.Enabled);
        Assert.False(s.ClickThrough);
        Assert.True(s.Topmost);
        Assert.Equal(1.0, s.Opacity);
        Assert.Equal(Loc.Auto, s.Language);
        Assert.False(s.LogEnabled);
    }
}

public class SettingsFileTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _originalPath = Settings.FilePath;
    private readonly string _originalLegacy = Settings.LegacyFilePath;

    public SettingsFileTests()
    {
        Settings.FilePath = _dir.File("settings.json");
        Settings.LegacyFilePath = Path.Combine(_dir.Path, "legacy", "settings.json");
    }

    public void Dispose()
    {
        Settings.FilePath = _originalPath;
        Settings.LegacyFilePath = _originalLegacy;
        _dir.Dispose();
    }

    [Fact]
    public void Save_then_load_round_trips_and_leaves_no_temp_file()
    {
        var s = new Settings { PollMs = 500, ThemeName = "Ember", Opacity = 0.4, Language = "pl" };
        s.Shutdown.Enabled = true;
        s.Shutdown.TempAboveC = 77;
        s.Save();

        Assert.True(File.Exists(Settings.FilePath));
        Assert.False(File.Exists(Settings.FilePath + ".tmp"));

        var loaded = Settings.Load();
        Assert.Equal(500, loaded.PollMs);
        Assert.Equal("Ember", loaded.ThemeName);
        Assert.Equal(0.4, loaded.Opacity);
        Assert.Equal("pl", loaded.Language);
        Assert.True(loaded.Shutdown.Enabled);
        Assert.Equal(77, loaded.Shutdown.TempAboveC);
    }

    [Fact]
    public void Missing_or_corrupt_file_gives_defaults()
    {
        Assert.Equal(1000, Settings.Load().PollMs);
        File.WriteAllText(Settings.FilePath, "{ not json");
        Assert.Equal(1000, Settings.Load().PollMs);
    }

    [Fact]
    public void A_hand_edited_file_is_clamped_on_load()
    {
        File.WriteAllText(Settings.FilePath, "{\"PollMs\": 10, \"Opacity\": 3, \"Shutdown\": null}");
        var s = Settings.Load();
        Assert.Equal(250, s.PollMs);
        Assert.Equal(1.0, s.Opacity);
        Assert.NotNull(s.Shutdown);
    }

    [Fact]
    public void Settings_from_the_old_product_name_are_carried_over_once()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Settings.LegacyFilePath)!);
        File.WriteAllText(Settings.LegacyFilePath, "{\"PollMs\": 2000}");
        Assert.Equal(2000, Settings.Load().PollMs);
        Assert.True(File.Exists(Settings.FilePath));
    }
}

public class ServiceSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public ServiceSettingsTests() => ServiceSettings.FolderOverride = _dir.File("service");

    public void Dispose()
    {
        ServiceSettings.FolderOverride = null;
        _dir.Dispose();
    }

    [Fact]
    public void FromUser_copies_what_the_service_needs()
    {
        var user = new Settings { PollMs = 500, GpuIndex = 1, LogEnabled = true, LogIntervalSeconds = 5, LogFolder = @"C:\logs" };
        user.Shutdown.Enabled = true;
        var svc = ServiceSettings.FromUser(user);
        Assert.Equal(500, svc.PollMs);
        Assert.Equal(1, svc.GpuIndex);
        Assert.True(svc.LogEnabled);
        Assert.Equal(5, svc.LogIntervalSeconds);
        Assert.Equal(@"C:\logs", svc.LogFolder);
        Assert.True(svc.Shutdown.Enabled);
    }

    [Fact]
    public void Save_fails_quietly_without_the_folder_and_works_once_it_exists()
    {
        Assert.False(new ServiceSettings().TrySave());
        Directory.CreateDirectory(ServiceSettings.Folder);
        var s = new ServiceSettings { PollMs = 2000 };
        s.Shutdown.Enabled = true;
        Assert.True(s.TrySave());
        Assert.False(File.Exists(ServiceSettings.FilePath + ".tmp"));

        var loaded = ServiceSettings.Load();
        Assert.Equal(2000, loaded.PollMs);
        Assert.True(loaded.Shutdown.Enabled);
    }

    [Fact]
    public void Missing_or_broken_file_runs_disarmed_with_defaults()
    {
        Assert.False(ServiceSettings.Load().Shutdown.Enabled);
        Directory.CreateDirectory(ServiceSettings.Folder);
        File.WriteAllText(ServiceSettings.FilePath, "garbage");
        Assert.Equal(1000, ServiceSettings.Load().PollMs);
        File.WriteAllText(ServiceSettings.FilePath, "{\"PollMs\": 1, \"LogFolder\": \"\", \"Shutdown\": null}");
        var s = ServiceSettings.Load();
        Assert.Equal(250, s.PollMs);
        Assert.Equal(Path.Combine(ServiceSettings.Folder, "Logs"), s.LogFolder);
        Assert.NotNull(s.Shutdown);
    }
}
