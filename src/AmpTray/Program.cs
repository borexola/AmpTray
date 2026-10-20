using System;
using System.Linq;
using System.ServiceProcess;
using AmpTray.Service;

namespace AmpTray;

/// <summary>
/// One executable, several modes:
///   (none)              the desktop widget
///   /service            started by the Service Control Manager; headless monitor from boot
///   /install-service    register and start the service (needs elevation; the installer and Settings call it)
///   /uninstall-service  stop and remove the service (needs elevation)
///   /monitor:N          run the headless monitor in this process for N seconds (developer check)
///   /settings[:Page]    open Settings    /test-alert   fire the 5 s test alert
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (Has(args, "/service"))
        {
            ServiceBase.Run(new AmpTrayService());
            return 0;
        }
        if (Has(args, "/install-service")) return ServiceManager.Install();
        if (Has(args, "/uninstall-service")) return ServiceManager.Uninstall();

        var monitor = args.FirstOrDefault(a => a.StartsWith("/monitor", StringComparison.OrdinalIgnoreCase));
        if (monitor != null)
        {
            int seconds = monitor.Contains(':') && int.TryParse(monitor[(monitor.IndexOf(':') + 1)..], out var n) ? n : 30;
            return BackgroundMonitor.RunForSeconds(seconds);
        }

        var app = new App();
        app.InitializeComponent();
        try
        {
            return app.Run();
        }
        catch (Exception ex)
        {
            // Nothing above this frame: log it, hand over to a fresh copy and get out of the way.
            Logging.CrashRecovery.Handle(ex, "run", app.ReleaseSingleInstance);
            return 1;
        }
    }

    private static bool Has(string[] args, string sw) => args.Any(a => a.Equals(sw, StringComparison.OrdinalIgnoreCase));
}
