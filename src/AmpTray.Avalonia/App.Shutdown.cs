using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AmpTray.Linux;
using AmpTray.Logging;
using AmpTray.Service;

namespace AmpTray.Avalonia;

/// <summary>The auto-shutdown rules in the widget, and their coordination with the amptray-monitor service.</summary>
public partial class App
{
    private readonly ShutdownMonitor _shutdown = new();
    private readonly HashSet<ShutdownRule> _notifiedPending = new();
    private IReadOnlyList<PendingRule> _pending = Array.Empty<PendingRule>();
    private ShutdownCountdownWindow? _countdown;
    private ShutdownRule? _countdownRule;
    private bool _shutdownIssued, _deferredToService;
    private DateTime _shutdownRetryNotBefore = DateTime.MinValue;
    private WakeDetector? _wake;

    public bool ServiceInstalled => SystemdService.IsInstalled();
    public bool ServiceRunning => SystemdService.IsActive();

    private void InitShutdown() { }

    private void ApplyShutdownSettings()
    {
        var s = _settings;
        _shutdown.MaxGap = TimeSpan.FromSeconds(Math.Max(10, s.PollMs * 4 / 1000.0));
        var rules = ShutdownMonitor.ActiveRuleDescriptions(s.Shutdown).ToList();
        _window?.SetShutdownArmed(s.Shutdown.Enabled, rules.Count == 0 ? Loc.T("(no rules enabled)") : string.Join("\n", rules));
        if (!s.Shutdown.Enabled)
        {
            _shutdown.Reset();
            _pending = Array.Empty<PendingRule>();
            _window?.ShowShutdownPending(null, 0);
            _countdown?.Abort();
            _countdown = null;
        }
    }

    /// <summary>Sleep/resume and clock changes make elapsed wall-clock time meaningless for the rules.</summary>
    private void StartWakeDetector()
    {
        _wake = new WakeDetector();
        _wake.Jumped += reason =>
        {
            AppLog.Info("power", "resume", "Clock jump: " + reason + "; reconnecting and resetting rule timers");
            ResetShutdownTimers(reason);
            _poller?.Reconnect();
            _gpu?.Reconnect();
        };
    }

    private void ResetShutdownTimers(string reason)
    {
        _shutdown.Reset();
        _pending = Array.Empty<PendingRule>();
        _notifiedPending.Clear();
        _window?.ShowShutdownPending(null, 0);
        if (_countdown != null)
        {
            PcShutdown.Log(_settings.LogFolder, "Countdown aborted: " + reason);
            _countdown.Abort();
            _countdown = null;
        }
        RefreshMenu();
    }

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
            if (!_notifiedPending.Contains(p.Rule))
                AppLog.Warn("shutdown", "pending", "Shutdown rule counting down: " + p.Condition, ("rule", p.Rule), ("remaining", p.Remaining.ToString(@"m\:ss")));
            if (_notifiedPending.Add(p.Rule))
                Notifier.Send(Loc.T("Auto shutdown pending"),
                    p.Condition + ". " + Loc.F("The PC shuts down in {0} unless it clears.", ShutdownMonitor.Minutes(p.Remaining.TotalMinutes)) + " " +
                    Loc.T("Cancel from the widget or tray menu."), Notifier.Urgency.Critical);
        }
        foreach (var r in _notifiedPending.Where(r => _pending.All(p => p.Rule != r)).ToList())
            AppLog.Info("shutdown", "cleared", "Shutdown rule no longer counting down", ("rule", r));
        _notifiedPending.RemoveWhere(r => _pending.All(p => p.Rule != r));

        // Condition cleared during the on-screen countdown: stand down.
        if (_countdown != null && _countdownRule is ShutdownRule cr && _pending.All(p => p.Rule != cr))
        {
            PcShutdown.Log(_settings.LogFolder, "Countdown aborted: condition cleared");
            AppLog.Info("shutdown", "countdown-aborted", "Countdown aborted: condition cleared");
            _countdown.Abort();
            _countdown = null;
        }

        if (result.Triggered != null && _countdown == null && !_shutdownIssued && now >= _shutdownRetryNotBefore)
        {
            // When the background service is running it issues the shutdown (it also covers the time
            // before sign-in); the widget keeps showing the banner and offers Cancel.
            if (SystemdService.IsActiveCached())
            {
                if (!_deferredToService)
                {
                    PcShutdown.Log(_settings.LogFolder, "Rule triggered in the widget; the background service issues the shutdown");
                    AppLog.Warn("shutdown", "deferred", "Rule triggered; the background service issues the shutdown", ("condition", result.Triggered.Condition));
                }
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
        AppLog.Warn("shutdown", "countdown", "Shutdown countdown started: " + reason, ("seconds", cfg.CountdownSeconds));
        _countdownRule = p.Rule;
        _countdown = new ShutdownCountdownWindow(reason, cfg.CountdownSeconds, test: false);
        _countdown.Elapsed += () => { _countdown = null; Execute(reason); };
        _countdown.Cancelled += () =>
        {
            _countdown = null;
            _shutdown.Suppress(new[] { p.Rule });
            StandDownService(); // in case the service started meanwhile and is timing the same rule
            PcShutdown.Log(folder, "Cancelled by user: " + reason);
            AppLog.Info("shutdown", "cancelled", "Shutdown cancelled by user: " + reason);
            RefreshMenu();
        };
        _countdown.Show();

        void Execute(string why)
        {
            _shutdownIssued = true;
            AppLog.Error("shutdown", "executed", "Shutting down the PC: " + why);
            bool started = PcShutdown.Execute(why, folder, message => Dispatcher.UIThread.Post(() => OnShutdownFailed(message)));
            if (!started) OnShutdownFailed(Loc.T("The power-off command could not be started."));
        }
    }

    /// <summary>The OS refused: warn, and try again in a minute if the rule still holds.</summary>
    private void OnShutdownFailed(string message)
    {
        AppLog.Error("shutdown", "failed", "Shutdown command failed: " + message);
        _shutdownIssued = false;
        _shutdownRetryNotBefore = DateTime.Now.AddSeconds(60);
        Notifier.Send(Loc.T("Auto shutdown FAILED"), message + " " + Loc.T("Retrying in 60 s if the condition persists."), Notifier.Urgency.Critical, 10000);
    }

    private void CancelPendingShutdown()
    {
        bool hadSomething = _pending.Count > 0 || _countdown != null || _deferredToService;
        // Every rule that is timing here, including one in a brief dip that is not shown as pending.
        var rules = _shutdown.SuppressActive().Union(_pending.Select(p => p.Rule)).ToList();
        _shutdown.Suppress(rules);
        if (hadSomething)
        {
            PcShutdown.Log(_settings.LogFolder, "Pending shutdown cancelled by user: " + string.Join("; ", _pending.Select(p => p.Condition)));
            AppLog.Info("shutdown", "cancelled", "Pending shutdown cancelled by user", ("conditions", string.Join("; ", _pending.Select(p => p.Condition))));
        }
        _pending = Array.Empty<PendingRule>();
        _notifiedPending.Clear();
        _deferredToService = false;
        _window?.ShowShutdownPending(null, 0);
        _countdown?.Close(); // raises Cancelled, which suppresses the triggering rule too
        if (hadSomething || SystemdService.IsInstalled()) StandDownService();
        RefreshMenu();
    }

    /// <summary>
    /// The background service evaluates the same rules on its own and would still power the PC off:
    /// tell it to pause its active rules (the cancel flag), and call off a delay this process timed.
    /// </summary>
    private static void StandDownService()
    {
        if (!SystemdService.IsInstalled()) return;
        ServiceSettings.RequestCancel();
        Task.Run(PcShutdown.Abort);
    }

    public void TestShutdown()
    {
        if (_countdown != null) return;
        var cfg = _settings.Shutdown;
        var rules = ShutdownMonitor.ActiveRuleDescriptions(cfg).ToList();
        string what = cfg.CountdownSeconds <= 0
            ? Loc.T("With your settings the PC would power off immediately, with no countdown.")
            : Loc.F("With your settings this countdown would run for {0} s.", cfg.CountdownSeconds);
        string reason = (rules.Count == 0 ? Loc.T("No rules are enabled.") : Loc.T("Rules:") + " " + string.Join("; ", rules) + ".") + " " + what;

        var test = new ShutdownCountdownWindow(reason, Math.Max(5, Math.Min(cfg.CountdownSeconds, 10)), test: true);
        test.Elapsed += () => Notifier.Send(Loc.T("Auto shutdown dry run finished"),
            cfg.Enabled ? Loc.T("This is the point where the PC would be told to power off.") : Loc.T("Auto shutdown is currently disarmed."),
            Notifier.Urgency.Normal, 5000);
        test.Show();
    }
}
