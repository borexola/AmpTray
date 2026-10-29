using System;
using System.Linq;
using Avalonia;
using AmpTray.Linux;
using AmpTray.Logging;
using AmpTray.Platform;

namespace AmpTray.Avalonia;

/// <summary>
/// amptray: the desktop widget for Linux.
///   (none)              the widget
///   --settings[:Page]   open Settings    --test-alert   fire the 5 s test alert
///   --version           print the version and exit
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(UpdateChecker.Current.ToString(3));
            return 0;
        }

        // Before anything touches the device or the shutdown rules.
        PlatformServices.Current = new LinuxPlatform();

        // A second copy just asks the first to show its widget.
        if (OperatingSystem.IsLinux() && !SingleInstance.TryAcquire()) return 0;

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, global::Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            // Nothing above this frame: log it, hand over to a fresh copy and get out of the way.
            CrashRecovery.Handle(ex, "run", ReleaseInstance);
            return 1;
        }
        finally
        {
            ReleaseInstance();
        }
    }

    private static void ReleaseInstance()
    {
        if (OperatingSystem.IsLinux()) SingleInstance.Release();
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .With(new X11PlatformOptions
        {
            WmClass = "amptray",
            EnableIme = false,
            EnableMultiTouch = false,
            OverlayPopups = false, // the menu is wider than the compact widget
        })
        .LogToTrace();
}
