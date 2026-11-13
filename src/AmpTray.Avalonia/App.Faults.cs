using System;
using System.Linq;
using Avalonia.Threading;
using AmpTray.Device;
using AmpTray.Logging;

namespace AmpTray.Avalonia;

/// <summary>Fault alerts: widget flash and banner, alarm sound, desktop notification, acknowledge, test; clearing the device's faults.</summary>
public partial class App
{
    private readonly AlarmPlayer _alarm = new();
    private ushort _lastFaultStatus;
    private ushort _lastLoggedFaults;
    private ushort _ackMask;
    private bool _alerting, _testing;
    private bool _canClear, _clearSent;
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
        if (_clearSent)
        {
            // First sample after a clear request: the device sends no reply, so this is the answer.
            _clearSent = false;
            if (s.FaultStatus == 0 && s.FaultLog == 0) AppLog.Info("fault", "clear-done", "The device cleared its faults");
            else AppLog.Warn("fault", "clear-unchanged", "The device still reports faults after the clear request",
                ("status", $"0x{s.FaultStatus:X4}"), ("log", $"0x{s.FaultLog:X4}"));
        }
        bool canClear = CanClearNow(s);
        if (canClear != _canClear)
        {
            _canClear = canClear;
            RefreshMenu(); // the tray's "Clear device faults" entry follows the device
        }

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

    /// <summary>Clearing is offered only when the user allowed it in Settings and the device reports a fault.</summary>
    private bool CanClearNow(SensorSnapshot? s) =>
        _settings.CanClearFaults && s != null && (s.FaultStatus != 0 || s.FaultLog != 0);

    /// <summary>
    /// Asks the device to clear its faults (widget button and tray menu). The device layer sends nothing
    /// while a fault the device is set to power the PC off for is active, so this never holds off its shutdown.
    /// </summary>
    private void ClearDeviceFaults()
    {
        if (_poller == null || _last is not { } s || !CanClearNow(s)) return;
        AppLog.Info("fault", "clear", "Clear faults requested", ("status", $"0x{s.FaultStatus:X4}"), ("log", $"0x{s.FaultLog:X4}"));
        _poller.ClearFaults();
    }

    private void OnClearFaultsDone(ClearFaultsResult r)
    {
        _clearSent = r.Outcome == ClearFaultsOutcome.Cleared;
        string? note = null;
        switch (r.Outcome)
        {
            case ClearFaultsOutcome.NotSent:
                AppLog.Warn("fault", "clear-failed", "The clear request could not be sent to the device");
                note = Loc.T("The request could not be sent to the device.");
                break;
            case ClearFaultsOutcome.SettingsUnreadable:
                AppLog.Warn("fault", "clear-refused", "The device's settings could not be read, so nothing was cleared");
                note = Loc.T("The device's settings could not be read, so nothing was cleared.");
                break;
            case ClearFaultsOutcome.LeftAlone:
                AppLog.Info("fault", "clear-left-alone", "Nothing cleared: active with a device power-off action: " + Names(r.BlockedBy),
                    ("faults", $"0x{r.BlockedBy:X4}"));
                note = Loc.F("Nothing was cleared: {0} is active and the device is set to power the PC off for it. AmpTray sends no clear while such a fault is active.", Names(r.BlockedBy));
                break;
        }
        // The press must not look like it did nothing.
        if (note != null) Notifier.Send(Loc.T("Clear device faults"), note, Notifier.Urgency.Normal, 10000);
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
