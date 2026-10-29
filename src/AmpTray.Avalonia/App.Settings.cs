using System;
using AmpTray.Linux;

namespace AmpTray.Avalonia;

/// <summary>The settings window and what it needs from the app (ISettingsHost).</summary>
public partial class App : ISettingsHost
{
    private SettingsWindow? _settingsWindow;

    private void OpenSettings(string? page)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, this, page);
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; Platform.PlatformServices.Current.TrimMemory(); };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Live preview while the opacity slider is being dragged; the value is saved by ApplySettings afterwards.</summary>
    public void PreviewOpacity(double opacity) =>
        _window?.SetOpacity(Math.Clamp(opacity, Settings.MinOpacity, 1.0), _settings.OpaqueOnHover);

    /// <summary>Turns the systemd service on or off through pkexec (password prompt).</summary>
    public bool SetServiceInstalled(bool on)
    {
        bool ok = on ? SystemdService.Install() : SystemdService.Uninstall();
        if (ok && on) SystemdService.ForService(_settings).TrySave();
        return ok;
    }

    public bool ServiceSettingsWritable => SystemdService.SettingsFolderWritable();

    public bool StartupEnabled
    {
        get => XdgAutostart.IsEnabled();
        set { if (value) XdgAutostart.Enable(); else XdgAutostart.Disable(); }
    }
}
