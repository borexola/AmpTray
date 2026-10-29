using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AmpTray.Device;
using AmpTray.Gpu;
using AmpTray.Linux;
using AmpTray.Logging;
using AmpTray.Service;

namespace AmpTray.Avalonia;

/// <summary>
/// The controller: owns the settings, the widget window, the pollers, stats, logger, tray, alerts
/// and the shutdown rules. The Linux counterpart of the WPF App.xaml.cs (split into partials).
/// </summary>
public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Settings _settings = new();
    private WidgetWindow? _window;
    private IDisposable? _showWatch;

    private DevicePoller? _poller;
    private GpuPoller? _gpu;
    private readonly DataLogger _logger = new();
    private readonly PowerHistory _history = new();
    private readonly PowerHistory _gpuHistory = new();
    private readonly SessionStats _stats = new();
    private readonly GpuStats _gpuStats = new();
    private UpdateInfo? _update;

    private string? _port;
    private SensorSnapshot? _last;
    private GpuSnapshot? _lastGpu;
    private bool _mutexWarned;

    public Settings Settings => _settings;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            StartUp(desktop.Args ?? Array.Empty<string>());
            desktop.Exit += (_, _) => ShutDown();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void StartUp(string[] args)
    {
        bool recovered = args.Contains(CrashRecovery.RecoveredSwitch, StringComparer.OrdinalIgnoreCase);
        AppLog.Info("app", recovered ? "recovered" : "started", recovered ? "AmpTray restarted after a crash" : "AmpTray started",
            ("version", UpdateChecker.Current.ToString(3)), ("os", "linux"));
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => CrashRecovery.Handle(ex.ExceptionObject as Exception, "background", ReleaseSingleInstance);
        TaskScheduler.UnobservedTaskException += (_, ex) => { ex.SetObserved(); AppLog.Error("app", "task", "Unobserved task exception", ex.Exception); };

        _settings = Settings.Load();
        _settings.Expanded = false; // always start compact; expanded is a per-session view
        AppTheme.Apply(AppTheme.Resolve(_settings));
        Loc.Apply(_settings.Language);
        Loc.Changed += OnLanguageChanged;

        _window = new WidgetWindow();
        _window.MenuRequested += () => _window.ContextMenu = BuildMenu();
        _window.Moved += (l, t) => { _settings.Left = l; _settings.Top = t; _settings.Save(); };
        _window.ExpandToggleRequested += () => { _settings.Expanded = !_settings.Expanded; ApplySettings(); };
        _window.SettingsRequested += () => OpenSettings(null);
        _window.AcknowledgeRequested += Acknowledge;
        _window.ShutdownCancelRequested += CancelPendingShutdown;
        _window.UpdateDownloadRequested += OpenUpdatePage;
        _window.UpdateDismissed += () => { _update = null; RefreshMenu(); };
        _window.HiddenByUser += () => { _settings.Visible = false; _settings.Save(); RefreshMenu(); };
        _window.ResetStatsRequested += () =>
        {
            _stats.Reset();
            _gpuStats.Reset();
            if (_last != null) _window.ShowSample(_last, _port ?? "?", _stats);
            if (_lastGpu != null) _window.ShowGpu(_lastGpu, _gpuStats);
        };
        _desktop!.MainWindow = _window;

        InitShutdown();
        InitFaults();
        BuildTray();

        _poller = new DevicePoller(_settings.PollMs);
        _poller.ConnectionChanged += port => Dispatcher.UIThread.Post(() => OnConnectionChanged(port));
        _poller.Sample += s => Dispatcher.UIThread.Post(() => OnSample(s));

        _gpu = new GpuPoller(_settings.PollMs, _settings.GpuIndex);
        _gpu.Sample += g => Dispatcher.UIThread.Post(() => OnGpuSample(g));

        ApplySettings();
        if (_settings.Visible) ShowWidget();
        _poller.Start();
        _gpu.Start();
        if (_settings.CheckUpdates) _ = CheckForUpdatesAsync();
        StartWakeDetector();

        if (OperatingSystem.IsLinux())
            _showWatch = SingleInstance.WatchShow(() => Dispatcher.UIThread.Post(() => SetVisible(true)));

        // Startup allocations (XAML, JIT, first samples) are done after a few seconds: hand the pages back.
        var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        trim.Tick += (_, _) => { trim.Stop(); Platform.PlatformServices.Current.TrimMemory(); };
        trim.Start();

        // Optional switches: --settings[:Page] opens the settings window, --test-alert fires the 5 s test alert,
        // --screenshot:<file.png> (developer) saves the widget after 8 s and exits.
        var settingsArg = args.FirstOrDefault(a => a.StartsWith("--settings", StringComparison.OrdinalIgnoreCase));
        if (settingsArg != null) OpenSettings(settingsArg.Contains(':') ? settingsArg[(settingsArg.IndexOf(':') + 1)..] : null);
        if (args.Contains("--test-alert", StringComparer.OrdinalIgnoreCase))
            Dispatcher.UIThread.Post(TestAlert, DispatcherPriority.ApplicationIdle);
        var shot = args.FirstOrDefault(a => a.StartsWith("--screenshot:", StringComparison.OrdinalIgnoreCase));
        if (shot != null) ScheduleScreenshot(shot["--screenshot:".Length..], args.Contains("--expanded", StringComparer.OrdinalIgnoreCase));
    }

    private void OnLanguageChanged()
    {
        _window?.ApplyLanguage();
        RefreshMenu();
        if (_last != null) _window?.ShowSample(_last, _port ?? "?", _stats);
        if (_lastGpu != null) _window?.ShowGpu(_lastGpu, _gpuStats);
        UpdateRecording();
    }

    private void ShowWidget()
    {
        if (_window == null) return;
        if (!_window.IsVisible)
        {
            _window.Show();
            _window.PlaceWindow(_settings.Left, _settings.Top);
        }
    }

    public void SetVisible(bool on)
    {
        if (_window == null) return;
        if (on) ShowWidget(); else _window.Hide();
        _settings.Visible = on;
        _settings.Save();
        RefreshMenu();
    }

    // ===== Settings =====
    public void ApplySettings()
    {
        if (_window == null) return;
        var s = _settings;

        AppTheme.Apply(AppTheme.Resolve(s));
        Loc.Apply(s.Language);
        _window.SetTopmost(s.Topmost);
        _window.SetOpacity(Math.Clamp(s.Opacity, Settings.MinOpacity, 1.0), s.OpaqueOnHover);
        // Panel choices apply to the compact view; expanded always shows everything.
        if (s.Expanded) _window.SetPanels(true, true, true, true, true);
        else _window.SetPanels(s.ShowWires, s.ShowTemps, s.ShowGraph, s.ShowFaults, s.ShowGpu);
        _window.SetTopBar(s.ShowGpuTemp);
        _window.SetGraph(_history, s.GraphWindowSeconds, s.PollMs);
        _window.SetGpuGraph(_gpuHistory);
        _window.SetExpanded(s.Expanded);
        _window.RefreshTheme();
        if (_poller != null) _poller.IntervalMs = s.PollMs;
        if (_gpu != null)
        {
            _gpu.IntervalMs = s.PollMs;
            _gpu.DeviceIndex = s.GpuIndex;
        }
        if (!s.LogEnabled) _logger.Close();
        UpdateRecording();

        ApplyAlertSettings();
        ApplyShutdownSettings();

        RefreshMenu();
        UpdateTray(force: true);
        s.Save();

        // The background service follows the same rules, logging and refresh rate.
        if (SystemdService.IsInstalled()) SystemdService.ForService(s).TrySave();
    }

    private void UpdateRecording()
    {
        if (_window == null) return;
        bool on = _settings.LogEnabled;
        string? tip = !on ? null
            : _logger.LastError != null ? Loc.F("Can't write the log right now: {0}", _logger.LastError)
            : _logger.CurrentFile != null ? Loc.F("Logging to {0}", _logger.CurrentFile)
            : Loc.T("Logging starts with the next sample");
        _window.SetRecording(on, tip);
    }

    public string LogStatus =>
        !_settings.LogEnabled ? Loc.T("Logging is off.")
        : _logger.LastError != null ? Loc.F("Can't write the log right now: {0}", _logger.LastError)
        : _logger.CurrentFile != null ? Loc.F("Writing to {0}", _logger.CurrentFile)
        : Loc.T("Logging starts with the next sample from the device.");

    // ===== Device events (UI thread) =====
    private void OnConnectionChanged(string? port)
    {
        _port = port;
        if (port != null) AppLog.Info("device", "connected", "WireView connected on " + port, ("port", port));
        else AppLog.Warn("device", "disconnected", "WireView disconnected");
        if (port != null && !_mutexWarned && _poller?.MutexProblem is string problem)
        {
            _mutexWarned = true;
            AppLog.Warn("device", "lock", "Shared device lock unavailable; polling without it", ("problem", problem));
        }
        if (port == null)
        {
            _last = null;
            _window?.ShowDisconnected(null);
            // Device rules lose their data and reset; a GPU rule keeps timing.
            EvaluateShutdown(deviceFresh: false, gpuFresh: false);
            UpdateTray(force: true);
        }
    }

    private void OnSample(SensorSnapshot s)
    {
        _last = s;
        _stats.Add(s);
        _history.Add(s.Timestamp, s.TotalPowerW);
        _window?.ShowSample(s, _port ?? "?", _stats);

        if (_settings.LogEnabled)
        {
            bool hadError = _logger.LastError != null;
            _logger.Write(s, _lastGpu, _settings.LogFolder, _settings.LogIntervalSeconds);
            if (hadError != (_logger.LastError != null)) UpdateRecording();
        }

        HandleFaults(s);
        EvaluateShutdown(deviceFresh: true, gpuFresh: false);
        UpdateTray();
    }

    private void OnGpuSample(GpuSnapshot? g)
    {
        _lastGpu = g;
        if (g != null)
        {
            _gpuStats.Add(g);
            _gpuHistory.Add(g.Timestamp, g.PowerW);
        }
        _window?.ShowGpu(g, _gpuStats);
        EvaluateShutdown(deviceFresh: false, gpuFresh: g != null);
        UpdateTray();
    }

    // ===== Updates =====
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info == null) return;
            await Dispatcher.UIThread.InvokeAsync(() => ShowUpdate(info));
        }
        catch (Exception ex)
        {
            AppLog.Warn("update", "check-failed", "Update check failed", ("error", ex.Message));
        }
    }

    public void ShowUpdate(UpdateInfo info)
    {
        _update = info;
        _window?.ShowUpdate(info);
        RefreshMenu();
    }

    private void OpenUpdatePage()
    {
        if (_update != null) Shell.Open(_update.Url);
    }

    // ===== Lifetime =====
    public void ExitApp()
    {
        _settings.Save();
        _desktop?.Shutdown();
    }

    private void ShutDown()
    {
        _showWatch?.Dispose();
        _poller?.Dispose();
        _gpu?.Dispose();
        _logger.Dispose();
        StopAlert();
        DisposeTray();
        AppLog.Info("app", "exit", "AmpTray exiting");
    }

    private static void ReleaseSingleInstance()
    {
        if (OperatingSystem.IsLinux()) SingleInstance.Release();
    }

    /// <summary>Developer aid: renders the widget to a PNG from inside the app (the compositor hides it from X11 grabs).</summary>
    private void ScheduleScreenshot(string path, bool expanded)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try
            {
                if (expanded) { _settings.Expanded = true; ApplySettings(); }
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (_window?.Content is Visual root && root.Bounds.Width > 0)
                        {
                            double scale = _window.RenderScaling;
                            var size = new PixelSize((int)Math.Ceiling(root.Bounds.Width * scale), (int)Math.Ceiling(root.Bounds.Height * scale));
                            using var rtb = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                            rtb.Render(root);
                            rtb.Save(path);
                            Console.WriteLine($"screenshot: {path} ({size.Width}x{size.Height})");
                        }
                    }
                    catch (Exception ex) { Console.Error.WriteLine("screenshot failed: " + ex); }
                    ExitApp();
                }, DispatcherPriority.Background);
            }
            catch (Exception ex) { Console.Error.WriteLine("screenshot failed: " + ex); ExitApp(); }
        };
        t.Start();
    }
}
