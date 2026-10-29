using System;
using System.Linq;
using Avalonia.Threading;
using AmpTray.Device;
using AmpTray.Logging;

namespace AmpTray.Avalonia;

/// <summary>Fault alerts: widget flash and banner, alarm sound, desktop notification, acknowledge, test.</summary>
public partial class App
{
    private readonly AlarmPlayer _alarm = new();
    private ushort _lastFaultStatus;
    private ushort _lastLoggedFaults;
    private ushort _ackMask;
    private bool _alerting, _testing;
    private string _alertMessage = "";
    private DispatcherTimer? _testTimer;

    public bool AlarmAvailable => AlarmPlayer.IsAvailable;
    public bool NotificationsAvailable => Notifier.IsAvailable;

    private void InitFaults() { }

    private void ApplyAlertSettings()
    {
        if (_alerting) StartAlert(_alertMessage, raise: false); // re-run with the new alert options
    }

    private void HandleFaults(SensorSnapshot s)
    {
        ushort active = s.FaultStatus;
        ushort fresh = (ushort)(active & ~_lastFaultStatus);
        _lastFaultStatus = active;

        // A fault that clears and comes back should alert again.
        _ackMask &= active;

        if (fresh != 0) AppLog.Warn("fault", "raised", "Fault reported: " + Names(fresh), ("status", $"0x{active:X4}"));
        else if (active == 0 && _lastLoggedFaults != 0) AppLog.Info("fault", "cleared", "All faults cleared");
        _lastLoggedFaults = active;
        if (fresh != 0 && _settings.Notify)
            Notifier.Send(Loc.T("WireView fault"), Names(fresh), Notifier.Urgency.Critical, 5000);

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
        if (_settings.SoundAlarm) _alarm.Start(_settings.AlarmVolume); else _alarm.Stop();
        // Alarm and notification work with the widget hidden; this option additionally un-hides and raises the widget.
        if (raise && _settings.BringToFront && _window != null)
        {
            if (!_window.IsVisible) SetVisible(true);
            _window.BringToFront();
        }
        RefreshMenu();
        UpdateTray(force: true);
    }

    private void StopAlert()
    {
        _alerting = false;
        _testing = false;
        _testTimer?.Stop();
        _window?.HideAlert();
        _alarm.Stop();
        RefreshMenu();
        UpdateTray(force: true);
    }

    private void Acknowledge()
    {
        _ackMask = _last?.FaultStatus ?? 0;
        if (_alerting) AppLog.Info("fault", "acknowledged", "Fault alert acknowledged");
        StopAlert();
    }

    public void TestAlert()
    {
        AppLog.Info("fault", "test", "Test alert started");
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
}
