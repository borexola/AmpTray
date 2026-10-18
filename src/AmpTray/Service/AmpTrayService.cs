using System;
using System.ServiceProcess;

namespace AmpTray.Service;

/// <summary>Windows service host: the same exe started by the Service Control Manager with /service.</summary>
public sealed class AmpTrayService : ServiceBase
{
    private BackgroundMonitor? _monitor;

    public AmpTrayService()
    {
        ServiceName = ServiceManager.Name;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        try
        {
            _monitor = new BackgroundMonitor();
            _monitor.Start();
        }
        catch (Exception ex)
        {
            ServiceSettings.Log("Service failed to start: " + ex);
            ExitCode = 1;
            throw;
        }
    }

    protected override void OnStop() => StopMonitor();

    protected override void OnShutdown() => StopMonitor();

    private void StopMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;
    }
}
