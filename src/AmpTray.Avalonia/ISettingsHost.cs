namespace AmpTray.Avalonia;

/// <summary>What the settings window needs from the app. Every change is applied live.</summary>
public interface ISettingsHost
{
    void ApplySettings();
    void TestAlert();
    void TestShutdown();
    void PreviewOpacity(double opacity);
    bool ServiceInstalled { get; }
    bool ServiceRunning { get; }
    /// <summary>Enables and starts, or stops and disables, the amptray-monitor service (asks for the password through pkexec).</summary>
    bool SetServiceInstalled(bool on);
    /// <summary>Whether this user may write the service's settings folder (group membership takes a re-login).</summary>
    bool ServiceSettingsWritable { get; }
    bool StartupEnabled { get; set; }
    string LogStatus { get; }
    bool AlarmAvailable { get; }
    bool NotificationsAvailable { get; }
    bool TrayAvailable { get; }
    /// <summary>A release found by a manual check: show it on the widget and in the tray menu as well.</summary>
    void ShowUpdate(UpdateInfo info);
}
