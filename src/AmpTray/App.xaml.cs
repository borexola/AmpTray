using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using AmpTray.Device;
using AmpTray.Gpu;
using AmpTray.Service;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.SolidColorBrush;

namespace AmpTray;

public partial class App : System.Windows.Application, ISettingsHost
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AmpTray";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private Settings _settings = new();
    private WidgetWindow? _window;
    private SettingsWindow? _settingsWindow;
    private DevicePoller? _poller;
    private GpuPoller? _gpu;
    private readonly DataLogger _logger = new();
    private readonly PowerHistory _history = new();
    private readonly PowerHistory _gpuHistory = new();
    private readonly SessionStats _stats = new();
    private readonly GpuStats _gpuStats = new();
    private readonly FaultAlert _alert = new();

    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _menu;
    private Forms.ToolStripMenuItem? _showItem, _expandedItem, _topmostItem, _clickThroughItem, _logItem, _ackItem;
    private Forms.ToolStripMenuItem? _wiresItem, _tempsItem, _graphItem, _faultsItem, _gpuItem, _themeMenu;
    private Forms.ToolStripMenuItem? _panelsMenu, _openLogItem, _settingsItem, _exitItem, _updateItem;
    private UpdateInfo? _update;

    private string? _port;
    private SensorSnapshot? _last;
    private GpuSnapshot? _lastGpu;
    private ushort _lastFaultStatus;
    private ushort _ackMask;
    private bool _alerting, _testing;
    private string _alertMessage = "";
    private DispatcherTimer? _testTimer, _fpsTimer;
    private readonly ShutdownMonitor _shutdown = new();
    private readonly System.Collections.Generic.HashSet<ShutdownRule> _notifiedPending = new();
    private System.Collections.Generic.IReadOnlyList<PendingRule> _pending = Array.Empty<PendingRule>();
    private ShutdownCountdownWindow? _countdown;
    private ShutdownRule? _countdownRule;
    private bool _shutdownIssued, _mutexWarned, _deferredToService;
    private DateTime _shutdownRetryNotBefore = DateTime.MinValue;
    private Forms.ToolStripMenuItem? _armItem, _cancelShutdownItem;
    private string _iconKey = "";
    private IntPtr _iconHandle;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Second launch: ask the running instance to show its widget, then leave.
        _singleInstance = new Mutex(true, @"Local\AmpTray.SingleInstance", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AmpTray.Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            try
            {
                while (_showSignal.WaitOne())
                    Dispatcher.BeginInvoke(() => { SetVisible(true); _window?.BringToFront(); });
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "Show signal" }.Start();

        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogError(ex.ExceptionObject as Exception);
        try
        {
            StartUp(e);
        }
        catch (Exception ex)
        {
            // Startup must either fully succeed or visibly fail; a half-started tray process would be a mystery.
            LogError(ex);
            MessageBox.Show(Loc.T("AmpTray could not start:") + $"\n\n{ex.Message}\n\n" + Loc.F("Details are in {0}", Path.Combine(Settings.AppDataFolder, "errors.log")),
                "AmpTray", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        // From here on, a bug in one update must not take the whole widget down; log it and carry on.
        DispatcherUnhandledException += (_, ex) => { LogError(ex.Exception); ex.Handled = true; };
    }

    private void StartUp(StartupEventArgs e)
    {
        _settings = Settings.Load();
        _settings.Expanded = false; // always start compact; expanded is a per-session view
        ApplyInstallerLanguage(_settings);
        if (!_settings.StartupConfigured)
        {
            // Start with Windows is on by default; Settings → Window turns it off.
            // Never replace an entry that already exists (e.g. the installed copy's, when a portable copy runs).
            try { if (!StartupEnabled) StartupEnabled = true; } catch { }
            _settings.StartupConfigured = true;
            _settings.Save();
        }
        Theme.Apply(Theme.Resolve(_settings));
        Loc.Apply(_settings.Language);
        Loc.Changed += OnLanguageChanged;

        _window = new WidgetWindow();
        _window.MenuRequested += () => _menu?.Show(Forms.Cursor.Position);
        _window.Moved += SavePosition;
        _window.ExpandToggleRequested += () => { _settings.Expanded = !_settings.Expanded; ApplySettings(); };
        _window.SettingsRequested += OpenSettings;
        _window.AcknowledgeRequested += Acknowledge;
        _window.ShutdownCancelRequested += CancelPendingShutdown;
        _window.UpdateDownloadRequested += OpenUpdatePage;
        _window.UpdateDismissed += () => { _update = null; RefreshMenu(); };
        _window.ResetStatsRequested += () =>
        {
            _stats.Reset();
            _gpuStats.Reset();
            if (_last != null) _window.ShowSample(_last, _port ?? "?", _stats);
            if (_lastGpu != null) _window.ShowGpu(_lastGpu, _gpuStats);
        };
        PlaceWindow();

        BuildTray();

        _poller = new DevicePoller(_settings.PollMs);
        _poller.ConnectionChanged += port => Dispatcher.BeginInvoke(() => OnConnectionChanged(port));
        _poller.Sample += s => Dispatcher.BeginInvoke(() => OnSample(s));

        _gpu = new GpuPoller(_settings.PollMs, _settings.GpuIndex);
        _gpu.Sample += g => Dispatcher.BeginInvoke(() => OnGpuSample(g));

        // Sleep/resume and clock changes make elapsed wall-clock time meaningless for the shutdown rules.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        ApplySettings();
        if (_settings.Visible) _window.Show();
        _poller.Start();
        _gpu.Start();
        if (_settings.CheckUpdates) _ = CheckForUpdatesAsync();

        // FPS comes from the background service's fps.json; show the foreground process, else the busiest one.
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fpsTimer.Tick += (_, _) => UpdateFps();
        _fpsTimer.Start();

        // Optional switches: /settings[:Page] opens the settings window, /test-alert fires the 5 s test alert.
        var settingsArg = e.Args.FirstOrDefault(a => a.StartsWith("/settings", StringComparison.OrdinalIgnoreCase));
        if (settingsArg != null) OpenSettings(settingsArg.Contains(':') ? settingsArg[(settingsArg.IndexOf(':') + 1)..] : null);
        if (e.Args.Contains("/test-alert", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(TestAlert, DispatcherPriority.ApplicationIdle);
    }

    private void OnLanguageChanged()
    {
        _window?.ApplyLanguage();
        ApplyMenuTexts();
        UpdateRecording();
        _iconKey = "";
        UpdateTray();
        if (_alerting) _window?.ShowAlert(_alertMessage, _settings.FlashWidget);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => ResetShutdownTimers("resumed from sleep"));
    }

    private void OnTimeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => ResetShutdownTimers("system clock changed"));

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { if (_alerting) StartAlert(_alertMessage, raise: false); }); // re-cover the monitors

    private void ResetShutdownTimers(string why)
    {
        bool hadSomething = _pending.Count > 0 || _countdown != null;
        _shutdown.Reset();
        _pending = Array.Empty<PendingRule>();
        _window?.ShowShutdownPending(null, 0);
        _countdown?.Abort();
        _countdown = null;
        if (hadSomething) PcShutdown.Log(_settings.LogFolder, $"Shutdown timers reset: {why}");
        RefreshMenu();
    }

    // ===== FPS =====

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private void UpdateFps()
    {
        if (_window == null || !_window.IsVisible) return;
        var snap = FpsSnapshot.TryRead();
        FpsEntry? pick = null;
        if (snap != null && snap.Processes.Count > 0)
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
            pick = snap.Processes.FirstOrDefault(p => p.Pid == (int)fg) ?? snap.Processes[0];
        }
        _window.ShowFps(pick?.Fps, pick?.Name, snap != null || ServiceManager.IsRunningCached());
    }

    // ===== Updates =====

    private async System.Threading.Tasks.Task CheckForUpdatesAsync()
    {
        var info = await UpdateChecker.CheckAsync();
        if (info == null) return;
        await Dispatcher.InvokeAsync(() =>
        {
            _update = info;
            _window?.ShowUpdate(info);
            RefreshMenu();
            if (_window?.IsVisible != true && _settings.Notify)
                _tray?.ShowBalloonTip(8000, "AmpTray", Loc.F("AmpTray {0} is available", info.Tag), Forms.ToolTipIcon.Info);
        });
    }

    private void OpenUpdatePage()
    {
        if (_update == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_update.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    /// <summary>The installer stores the language picked on its first page; take it once, then clear it.</summary>
    private static void ApplyInstallerLanguage(Settings s)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\borexola\AmpTray", writable: true);
            if (key?.GetValue("Language") is not string lang || lang.Length == 0) return;
            if (lang == Loc.Auto || Loc.Languages.Any(l => l.Code == lang))
            {
                s.Language = lang;
                s.Save();
            }
            key.DeleteValue("Language", false);
        }
        catch
        {
            // Registry unavailable: keep whatever the settings say.
        }
    }

    private static void LogError(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Settings.AppDataFolder);
            File.AppendAllText(Path.Combine(Settings.AppDataFolder, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing more we can do.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _poller?.Dispose();
        _gpu?.Dispose();
        _alert.Dispose();
        _logger.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        if (_iconHandle != IntPtr.Zero) Native.DestroyIcon(_iconHandle);
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // ===== Settings =====
    /// <summary>Pushes every setting to the widget, poller, logger, theme and tray, then saves.</summary>
    public void ApplySettings()
    {
        if (_window == null) return;
        var s = _settings;

        Theme.Apply(Theme.Resolve(s));
        Loc.Apply(s.Language);
        _window.SetTopmost(s.Topmost);
        _window.SetClickThrough(s.ClickThrough);
        _window.SetOpacity(Math.Clamp(s.Opacity, Settings.MinOpacity, 1.0), s.OpaqueOnHover);
        // Panel choices apply to the compact view; expanded always shows everything.
        if (s.Expanded) _window.SetPanels(true, true, true, true, true);
        else _window.SetPanels(s.ShowWires, s.ShowTemps, s.ShowGraph, s.ShowFaults, s.ShowGpu);
        _window.SetGraph(_history, s.GraphWindowSeconds, s.PollMs);
        _window.SetGpuGraph(_gpuHistory);
        _window.SetExpanded(s.Expanded);
        _window.RefreshTheme();
        if (_poller != null) _poller.IntervalMs = s.PollMs;
        _shutdown.MaxGap = TimeSpan.FromSeconds(Math.Max(10, s.PollMs * 4 / 1000.0));
        if (_gpu != null)
        {
            _gpu.IntervalMs = s.PollMs;
            _gpu.DeviceIndex = s.GpuIndex;
        }
        if (!s.LogEnabled) _logger.Close();
        UpdateRecording();

        if (_alerting) StartAlert(_alertMessage, raise: false); // re-run with the new alert options

        var rules = ShutdownMonitor.ActiveRuleDescriptions(s.Shutdown).ToList();
        _window.SetShutdownArmed(s.Shutdown.Enabled, rules.Count == 0 ? Loc.T("(no rules enabled)") : string.Join("\n", rules));
        if (!s.Shutdown.Enabled)
        {
            _shutdown.Reset();
            _pending = Array.Empty<PendingRule>();
            _window.ShowShutdownPending(null, 0);
            _countdown?.Abort();
            _countdown = null;
        }

        RefreshMenu();
        _iconKey = "";
        UpdateTray();
        s.Save();

        // The background service follows the same rules, logging and refresh rate.
        if (ServiceManager.IsInstalled()) ServiceSettings.FromUser(s).TrySave();
    }

    public bool ServiceInstalled => ServiceManager.IsInstalled();
    public bool ServiceRunning => ServiceManager.IsRunning();

    /// <summary>Installs or removes the service through an elevated copy of this exe (UAC prompt).</summary>
    public bool SetServiceInstalled(bool on)
    {
        bool ok = ServiceManager.RunElevated(on ? "/install-service" : "/uninstall-service");
        if (ok && on) ServiceSettings.FromUser(_settings).TrySave();
        return ok;
    }

    /// <summary>Live preview while the opacity slider is being dragged; the value is saved by ApplySettings afterwards.</summary>
    public void PreviewOpacity(double opacity) =>
        _window?.SetOpacity(Math.Clamp(opacity, Settings.MinOpacity, 1.0), _settings.OpaqueOnHover);

    public bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (value) key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValueName, false);
        }
    }

    public string LogStatus =>
        !_settings.LogEnabled ? Loc.T("Logging is off.")
        : _logger.LastError != null ? Loc.F("Can't write the log right now: {0}", _logger.LastError)
        : _logger.CurrentFile != null ? Loc.F("Writing to {0}", _logger.CurrentFile)
        : Loc.T("Logging starts with the next sample from the device.");

    private void OpenSettings() => OpenSettings(null);

    private void OpenSettings(string? page)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, this, page);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ===== Device events (UI thread) =====
    private void OnConnectionChanged(string? port)
    {
        _port = port;
        if (port != null && !_mutexWarned && _poller?.MutexProblem is string problem)
        {
            _mutexWarned = true;
            LogError(new InvalidOperationException("Shared USB-sensor mutex unavailable; polling without it. " + problem));
        }
        if (port == null)
        {
            _last = null;
            _window?.ShowDisconnected(null);
            // Device rules lose their data and reset; a GPU rule keeps timing.
            EvaluateShutdown(deviceFresh: false, gpuFresh: false);
            UpdateTray();
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

    // ===== Auto shutdown =====
    private void EvaluateShutdown(bool deviceFresh, bool gpuFresh)
    {
        var cfg = _settings.Shutdown;
        var now = DateTime.Now;
        // A GPU reading older than a few polls is stale (driver hiccup); don't let it keep a rule alive.
        var gpu = _lastGpu != null && (now - _lastGpu.Timestamp).TotalSeconds < 10 ? _lastGpu : null;
        var result = _shutdown.Evaluate(_last, gpu, now, cfg, deviceFresh, gpuFresh);
        _pending = result.Pending;

        var next = _pending.OrderBy(p => p.Remaining).FirstOrDefault();
        _window?.ShowShutdownPending(next, Math.Max(0, _pending.Count - 1));

        // Tell the user once when a rule starts counting down (matters when the widget is hidden).
        foreach (var p in _pending)
        {
            if (_notifiedPending.Add(p.Rule) && _tray != null)
                _tray.ShowBalloonTip(8000, Loc.T("Auto shutdown pending"),
                    p.Condition + ". " + Loc.F("The PC shuts down in {0} unless it clears.", ShutdownMonitor.Minutes(p.Remaining.TotalMinutes)) + " " +
                    Loc.T("Cancel from the widget or tray menu."), Forms.ToolTipIcon.Warning);
        }
        _notifiedPending.RemoveWhere(r => _pending.All(p => p.Rule != r));

        // Condition cleared during the on-screen countdown: stand down.
        if (_countdown != null && _countdownRule is ShutdownRule cr && _pending.All(p => p.Rule != cr))
        {
            PcShutdown.Log(_settings.LogFolder, "Countdown aborted: condition cleared");
            _countdown.Abort();
            _countdown = null;
        }

        if (result.Triggered != null && _countdown == null && !_shutdownIssued && now >= _shutdownRetryNotBefore)
        {
            // When the background service is running it issues the shutdown (it also covers the time
            // before sign-in); the widget keeps showing the banner and offers Cancel.
            if (ServiceManager.IsRunningCached())
            {
                if (!_deferredToService) PcShutdown.Log(_settings.LogFolder, "Rule triggered in the widget; the background service issues the shutdown");
                _deferredToService = true;
            }
            else
            {
                TriggerShutdown(result.Triggered);
            }
        }
        else if (result.Triggered == null)
        {
            _deferredToService = false;
        }

        RefreshMenu();
    }

    private void TriggerShutdown(PendingRule p)
    {
        var cfg = _settings.Shutdown;
        string reason = $"{ShutdownMonitor.Describe(p.Rule, cfg)}: {p.Condition}";
        string folder = _settings.LogFolder;

        if (cfg.CountdownSeconds <= 0)
        {
            Execute(reason);
            return;
        }

        PcShutdown.Log(folder, "Countdown started: " + reason);
        _countdownRule = p.Rule;
        _countdown = new ShutdownCountdownWindow(reason, cfg.CountdownSeconds, test: false);
        _countdown.Elapsed += () => { _countdown = null; Execute(reason); };
        _countdown.Cancelled += () =>
        {
            _countdown = null;
            _shutdown.Suppress(new[] { p.Rule });
            PcShutdown.Log(folder, "Cancelled by user: " + reason);
            RefreshMenu();
        };
        _countdown.Show();

        void Execute(string why)
        {
            _shutdownIssued = true;
            bool started = PcShutdown.Execute(why, folder, message => Dispatcher.BeginInvoke(() => OnShutdownFailed(message)));
            if (!started) OnShutdownFailed("shutdown.exe could not be started.");
        }
    }

    /// <summary>Windows refused (e.g. a restart is already scheduled): warn, and try again in a minute if the rule still holds.</summary>
    private void OnShutdownFailed(string message)
    {
        _shutdownIssued = false;
        _shutdownRetryNotBefore = DateTime.Now.AddSeconds(60);
        _tray?.ShowBalloonTip(10000, Loc.T("Auto shutdown FAILED"), message + " " + Loc.T("Retrying in 60 s if the condition persists."), Forms.ToolTipIcon.Error);
    }

    private void CancelPendingShutdown()
    {
        if (_pending.Count == 0 && _countdown == null) return;
        _shutdown.Suppress(_pending.Select(p => p.Rule));
        PcShutdown.Log(_settings.LogFolder, "Pending shutdown cancelled by user: " + string.Join("; ", _pending.Select(p => p.Condition)));
        _pending = Array.Empty<PendingRule>();
        _window?.ShowShutdownPending(null, 0);
        _countdown?.Close(); // raises Cancelled, which suppresses the triggering rule too
        // A shutdown the service already scheduled (shutdown /t N) can still be called off.
        if (ServiceManager.IsRunningCached()) PcShutdown.Abort();
        RefreshMenu();
    }

    public void TestShutdown()
    {
        if (_countdown != null) return;
        var cfg = _settings.Shutdown;
        var rules = ShutdownMonitor.ActiveRuleDescriptions(cfg).ToList();
        string what = cfg.CountdownSeconds <= 0
            ? Loc.T("With your settings Windows would shut down immediately, with no countdown.")
            : Loc.F("With your settings this countdown would run for {0} s.", cfg.CountdownSeconds);
        string reason = (rules.Count == 0 ? Loc.T("No rules are enabled.") : Loc.T("Rules:") + " " + string.Join("; ", rules) + ".") + " " + what;

        var test = new ShutdownCountdownWindow(reason, Math.Max(5, Math.Min(cfg.CountdownSeconds, 10)), test: true);
        test.Elapsed += () => _tray?.ShowBalloonTip(5000, Loc.T("Auto shutdown dry run finished"),
            cfg.Enabled ? Loc.T("This is the point where Windows would be told to shut down (forced).") : Loc.T("Auto shutdown is currently disarmed."),
            Forms.ToolTipIcon.Info);
        test.Show();
    }

    // ===== Fault alerts =====
    private void HandleFaults(SensorSnapshot s)
    {
        ushort active = s.FaultStatus;
        ushort fresh = (ushort)(active & ~_lastFaultStatus);
        _lastFaultStatus = active;

        // A fault that clears and comes back should alert again.
        _ackMask &= active;

        if (fresh != 0 && _settings.Notify && _tray != null)
            _tray.ShowBalloonTip(5000, Loc.T("WireView fault"), Names(fresh), Forms.ToolTipIcon.Warning);

        if (_testing) return;

        ushort unacknowledged = (ushort)(active & ~_ackMask);
        if (unacknowledged != 0)
        {
            string message = Names(active);
            if (!_alerting) StartAlert(message, raise: true);
            else if (message != _alertMessage) { _alertMessage = message; _window?.ShowAlert(message, _settings.FlashWidget); }
        }
        else if (_alerting)
        {
            StopAlert();
        }
    }

    private void StartAlert(string message, bool raise)
    {
        _alerting = true;
        _alertMessage = message;
        _window?.HideAlert();
        _window?.ShowAlert(message, _settings.FlashWidget);
        _alert.Start(message, _settings.FlashScreen, _settings.SoundAlarm, _settings.AlarmVolume);
        // Screen flash, alarm and notification work with the widget hidden (tray only);
        // this option additionally un-hides and raises the widget.
        if (raise && _settings.BringToFront && _window != null)
        {
            if (!_window.IsVisible) SetVisible(true);
            _window.BringToFront();
        }
        RefreshMenu();
    }

    private void StopAlert()
    {
        _alerting = false;
        _testing = false;
        _testTimer?.Stop();
        _window?.HideAlert();
        _alert.Stop();
        RefreshMenu();
    }

    private void Acknowledge()
    {
        _ackMask = _last?.FaultStatus ?? 0;
        StopAlert();
    }

    public void TestAlert()
    {
        _testing = true;
        StartAlert(Loc.T("TEST") + ": " + SensorSnapshot.Describe(Fault.WireOverCurrent) + " " + Loc.T("(simulated)"), raise: true);
        _testTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _testTimer.Tick -= OnTestTimer;
        _testTimer.Tick += OnTestTimer;
        _testTimer.Stop();
        _testTimer.Start();
    }

    private void OnTestTimer(object? sender, EventArgs e)
    {
        if (_testing) StopAlert();
    }

    private static string Names(ushort bits) => string.Join(", ", SensorSnapshot.Decode(bits).Select(SensorSnapshot.Describe));

    // ===== Window placement =====
    private void PlaceWindow()
    {
        if (_window == null) return;
        var work = SystemParameters.WorkArea;
        const double approxWidth = 312; // SizeToContent sets the real size once shown
        double left = _settings.Left ?? work.Right - approxWidth - 12;
        double top = _settings.Top ?? work.Top + 12;

        // Keep it on screen if monitors changed since last run.
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        left = Math.Clamp(left, vl, Math.Max(vl, vr - 120));
        top = Math.Clamp(top, vt, Math.Max(vt, vb - 80));

        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Left = left;
        _window.Top = top;
    }

    private void SavePosition()
    {
        if (_window == null) return;
        _settings.Left = _window.Left;
        _settings.Top = _window.Top;
        _settings.Save();
    }

    private void SetVisible(bool visible)
    {
        if (_window == null) return;
        if (visible) _window.Show(); else _window.Hide();
        _settings.Visible = visible;
        _settings.Save();
        RefreshMenu();
    }

    private void UpdateRecording()
    {
        _window?.SetRecording(_settings.LogEnabled, LogStatus);
    }

    // ===== Tray =====
    private void BuildTray()
    {
        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };

        Forms.ToolStripMenuItem Item(string text, Action onClick)
        {
            var item = new Forms.ToolStripMenuItem(text);
            item.Click += (_, _) => onClick();
            return item;
        }

        _ackItem = Item("", Acknowledge);
        _showItem = Item("", () => SetVisible(!(_window?.IsVisible ?? false)));
        _expandedItem = Item("", () => { _settings.Expanded = !_settings.Expanded; ApplySettings(); });

        _panelsMenu = new Forms.ToolStripMenuItem();
        _wiresItem = Item("", () => { _settings.ShowWires = !_settings.ShowWires; ApplySettings(); });
        _tempsItem = Item("", () => { _settings.ShowTemps = !_settings.ShowTemps; ApplySettings(); });
        _graphItem = Item("", () => { _settings.ShowGraph = !_settings.ShowGraph; ApplySettings(); });
        _faultsItem = Item("", () => { _settings.ShowFaults = !_settings.ShowFaults; ApplySettings(); });
        _gpuItem = Item("", () => { _settings.ShowGpu = !_settings.ShowGpu; ApplySettings(); });
        _panelsMenu.DropDownItems.AddRange(new Forms.ToolStripItem[] { _wiresItem, _tempsItem, _gpuItem, _graphItem, _faultsItem });

        _themeMenu = new Forms.ToolStripMenuItem();
        foreach (var name in Theme.Names)
        {
            string n = name;
            _themeMenu.DropDownItems.Add(Item(n, () => { _settings.ThemeName = n; ApplySettings(); }));
        }

        _topmostItem = Item("", () => { _settings.Topmost = !_settings.Topmost; ApplySettings(); });
        _clickThroughItem = Item("", () => { _settings.ClickThrough = !_settings.ClickThrough; ApplySettings(); });
        _logItem = Item("", () => { _settings.LogEnabled = !_settings.LogEnabled; ApplySettings(); });
        _openLogItem = Item("", () => SettingsWindow.OpenLogFolder(_settings.LogFolder));
        _armItem = Item("", () => { _settings.Shutdown.Enabled = !_settings.Shutdown.Enabled; ApplySettings(); });
        _cancelShutdownItem = Item("", CancelPendingShutdown);
        _settingsItem = Item("", OpenSettings);
        _exitItem = Item("", Shutdown);
        _updateItem = Item("", OpenUpdatePage);

        _menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _updateItem,
            _cancelShutdownItem,
            _ackItem,
            _showItem,
            _expandedItem,
            _panelsMenu,
            _themeMenu,
            new Forms.ToolStripSeparator(),
            _topmostItem,
            _clickThroughItem,
            new Forms.ToolStripSeparator(),
            _logItem,
            _openLogItem,
            new Forms.ToolStripSeparator(),
            _armItem,
            new Forms.ToolStripSeparator(),
            _settingsItem,
            _exitItem,
        });

        _tray = new Forms.NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) SetVisible(!(_window?.IsVisible ?? false));
        };
        ApplyMenuTexts();
        RefreshMenu();
        UpdateTray();
    }

    private void ApplyMenuTexts()
    {
        if (_menu == null) return;
        _cancelShutdownItem!.Text = Loc.T("Cancel pending shutdown");
        _ackItem!.Text = Loc.T("Acknowledge fault alert");
        _showItem!.Text = Loc.T("Show widget");
        _expandedItem!.Text = Loc.T("Expanded view");
        _panelsMenu!.Text = Loc.T("Panels");
        _wiresItem!.Text = Loc.T("Wires");
        _tempsItem!.Text = Loc.T("Temperatures");
        _gpuItem!.Text = Loc.T("GPU");
        _graphItem!.Text = Loc.T("Power graph");
        _faultsItem!.Text = Loc.T("Faults");
        _themeMenu!.Text = Loc.T("Theme");
        foreach (Forms.ToolStripMenuItem t in _themeMenu.DropDownItems)
            if (t.Tag == null) { t.Tag = t.Text; } // remember the preset key once
        foreach (Forms.ToolStripMenuItem t in _themeMenu.DropDownItems)
            t.Text = (string)t.Tag! == Theme.Custom ? Loc.T("Custom") : (string)t.Tag!;
        _topmostItem!.Text = Loc.T("Always on top");
        _clickThroughItem!.Text = Loc.T("Lock (click-through)");
        _logItem!.Text = Loc.T("Log data to CSV");
        _openLogItem!.Text = Loc.T("Open log folder");
        _armItem!.Text = Loc.T("Auto shutdown armed");
        _settingsItem!.Text = Loc.T("Settings…");
        _exitItem!.Text = Loc.T("Exit");
        _updateItem!.Text = _update == null ? "" : Loc.F("Update available: {0}", _update.Tag);
    }

    private void RefreshMenu()
    {
        if (_menu == null) return;
        var s = _settings;
        _ackItem!.Visible = _alerting;
        _showItem!.Checked = _window?.IsVisible ?? s.Visible;
        _expandedItem!.Checked = s.Expanded;
        _wiresItem!.Checked = s.ShowWires;
        _tempsItem!.Checked = s.ShowTemps;
        _graphItem!.Checked = s.ShowGraph;
        _faultsItem!.Checked = s.ShowFaults;
        _gpuItem!.Checked = s.ShowGpu;
        foreach (Forms.ToolStripMenuItem t in _themeMenu!.DropDownItems) t.Checked = (string?)t.Tag == s.ThemeName;
        _topmostItem!.Checked = s.Topmost;
        _clickThroughItem!.Checked = s.ClickThrough;
        _logItem!.Checked = s.LogEnabled;
        _armItem!.Checked = s.Shutdown.Enabled;
        _cancelShutdownItem!.Visible = _pending.Count > 0 || _countdown != null;
        _updateItem!.Visible = _update != null;
        if (_update != null) _updateItem.Text = Loc.F("Update available: {0}", _update.Tag);
    }

    private void UpdateTray()
    {
        if (_tray == null) return;

        string text;
        if (_last == null)
        {
            text = "WireView: " + Loc.T("searching for device…");
        }
        else
        {
            var s = _last;
            string temp = double.IsNaN(s.MaxTempC) ? "" : $" · {s.MaxTempC:0.0} °C";
            string faults = s.HasActiveFault ? Loc.T("FAULT:") + " " + Names(s.FaultStatus)
                : s.HasLoggedFault ? Loc.T("Faults logged earlier") : Loc.T("No faults");
            text = "WireView: " + Loc.F("{0} W · peak wire {1} A", s.TotalPowerW.ToString("0"), s.MaxPinCurrentA.ToString("0.00")) + temp + "\n" + faults;
        }
        if (_lastGpu is GpuSnapshot g)
            text += $"\nGPU {g.TempC:0} °C · {g.LoadPct:0} % · {g.PowerW:0} W";
        _tray.Text = text.Length > 127 ? text[..127] : text;

        RenderTrayIcon(_last);
    }

    /// <summary>Tiny live bar chart of the six wires, tinted by overall status, in theme colours.</summary>
    private void RenderTrayIcon(SensorSnapshot? s)
    {
        const int size = 32;
        System.Drawing.Color Res(string key)
        {
            var c = ((WpfBrush)Resources[key]).Color;
            return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }

        var color = Res(s == null ? "IdleBrush" : _alerting ? "CritBrush" : WidgetWindow.OverallLevel(s));
        var background = Res("PanelSolidBrush");

        // Bars use the same scale as the widget: full height = one wire's share of the cable rating.
        double limit = s == null ? 1 : WidgetWindow.WireLimitA(s);
        var heights = Enumerable.Range(0, SensorSnapshot.PinCount)
            .Select(i => s == null ? 3 : 3 + (int)Math.Round(19 * Math.Clamp(s.PinCurrent[i] / limit, 0, 1)))
            .ToArray();

        string key = $"{color.ToArgb()}:{background.ToArgb()}:{string.Join(",", heights)}";
        if (key == _iconKey) return;
        _iconKey = key;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(background);
            using var path = RoundedRect(new Rectangle(0, 0, size - 1, size - 1), 7);
            g.FillPath(bg, path);

            using var bar = new SolidBrush(color);
            const int barW = 3, gap = 1, left = 4, bottom = 27;
            for (int i = 0; i < heights.Length; i++)
                g.FillRectangle(bar, left + i * (barW + gap), bottom - heights[i], barW, heights[i]);
        }

        var handle = bmp.GetHicon();
        var old = _tray!.Icon;
        _tray.Icon = Icon.FromHandle(handle);
        old?.Dispose();
        if (_iconHandle != IntPtr.Zero) Native.DestroyIcon(_iconHandle);
        _iconHandle = handle;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
