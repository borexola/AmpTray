using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using AmpTray.Device;
using AmpTray.Gpu;
using AmpTray.Logging;
using AmpTray.Ui;

namespace AmpTray.Avalonia;

/// <summary>The tray icon (StatusNotifierItem) with its live six-bar icon, and the menu shared with the widget.</summary>
public partial class App
{
    private TrayIcon? _tray;
    private List<MenuEntry> _menu = new();
    private string _iconKey = "";
    private RenderTargetBitmap? _iconBitmap;
    private DateTime _trayStamp = DateTime.MinValue;
    private string _trayText = "";

    /// <summary>True when a StatusNotifier host (GNOME's AppIndicator extension, KDE, …) is on the session bus.</summary>
    public bool TrayAvailable => _tray != null;

    private void BuildTray()
    {
        _menu = BuildMenuModel();
        if (!TrayHostPresent())
        {
            AppLog.Info("tray", "unavailable", "No status-notifier host on the session bus; the widget's own menu carries every command");
            return;
        }
        try
        {
            _tray = new TrayIcon { ToolTipText = "AmpTray", Menu = MenuModel.ToNativeMenu(_menu), IsVisible = true };
            _tray.Clicked += (_, _) => SetVisible(!(_window?.IsVisible ?? false));
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
        }
        catch (Exception ex)
        {
            AppLog.Warn("tray", "failed", "Could not create the tray icon", ("error", ex.Message));
            _tray = null;
        }
        UpdateTray(force: true);
    }

    private void DisposeTray()
    {
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        _iconBitmap?.Dispose();
        _iconBitmap = null;
    }

    /// <summary>The widget's right-click menu, built fresh each time so texts and checks are current.</summary>
    private ContextMenu BuildMenu() => MenuModel.ToContextMenu(_menu);

    private void RefreshMenu()
    {
        if (_tray != null) _tray.Menu = MenuModel.ToNativeMenu(_menu);
    }

    private List<MenuEntry> BuildMenuModel()
    {
        MenuEntry Toggle(string text, Func<bool> get, Action<bool> set) =>
            new() { Text = () => Loc.T(text), Checked = get, Click = () => { set(!get()); ApplySettings(); } };

        var themes = new List<MenuEntry>();
        foreach (var name in AppTheme.Names)
        {
            string n = name;
            themes.Add(new MenuEntry
            {
                Text = () => n == AppTheme.Custom ? Loc.T("Custom") : n,
                Checked = () => _settings.ThemeName == n,
                Click = () => { _settings.ThemeName = n; ApplySettings(); },
            });
        }

        return new List<MenuEntry>
        {
            new() { Text = () => Loc.F("Update available: {0}", _update?.Tag ?? ""), Visible = () => _update != null, Click = OpenUpdatePage },
            new() { Text = () => Loc.T("Cancel pending shutdown"), Visible = () => _pending.Count > 0 || _countdown != null || _deferredToService, Click = CancelPendingShutdown },
            new() { Text = () => Loc.T("Acknowledge fault alert"), Visible = () => _alerting, Click = Acknowledge },
            new()
            {
                Text = () => Loc.T("Show widget"),
                Checked = () => _window?.IsVisible ?? _settings.Visible,
                // Without a tray there is no way back to a hidden widget, so hiding is disabled there.
                Enabled = () => _tray != null || !(_window?.IsVisible ?? true),
                Click = () => SetVisible(!(_window?.IsVisible ?? false)),
            },
            Toggle("Expanded view", () => _settings.Expanded, v => _settings.Expanded = v),
            new()
            {
                Text = () => Loc.T("Panels"),
                Children = new List<MenuEntry>
                {
                    Toggle("Wires", () => _settings.ShowWires, v => _settings.ShowWires = v),
                    Toggle("Temperatures", () => _settings.ShowTemps, v => _settings.ShowTemps = v),
                    Toggle("GPU", () => _settings.ShowGpu, v => _settings.ShowGpu = v),
                    Toggle("Power graph", () => _settings.ShowGraph, v => _settings.ShowGraph = v),
                    Toggle("Faults", () => _settings.ShowFaults, v => _settings.ShowFaults = v),
                },
            },
            new() { Text = () => Loc.T("Theme"), Children = themes },
            MenuEntry.Separator,
            Toggle("Always on top", () => _settings.Topmost, v => _settings.Topmost = v),
            MenuEntry.Separator,
            Toggle("Log data to CSV", () => _settings.LogEnabled, v => _settings.LogEnabled = v),
            new() { Text = () => Loc.T("Open log folder"), Click = () => Shell.Open(_settings.LogFolder) },
            MenuEntry.Separator,
            Toggle("Auto shutdown armed", () => _settings.Shutdown.Enabled, v => _settings.Shutdown.Enabled = v),
            MenuEntry.Separator,
            new() { Text = () => Loc.T("Settings…"), Click = () => OpenSettings(null) },
            new() { Text = () => Loc.T("Exit"), Click = ExitApp },
        };
    }

    private void UpdateTray(bool force = false)
    {
        if (_tray == null) return;
        // The shell is told about the icon at most every 2 s; status changes still refresh it at once via force.
        if (!force && (DateTime.UtcNow - _trayStamp).TotalSeconds < 2) return;
        _trayStamp = DateTime.UtcNow;

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
        if (text.Length > 127) text = text[..127];
        if (text != _trayText)
        {
            _trayText = text;
            _tray.ToolTipText = text;
        }

        RenderTrayIcon(_last);
    }

    /// <summary>Tiny live bar chart of the six wires, tinted by overall status, in theme colours.</summary>
    private void RenderTrayIcon(SensorSnapshot? s)
    {
        if (_tray == null) return;
        var color = AppTheme.ColorOf(TrayIconModel.ColorKey(s, _alerting), Colors.Gray);
        var background = AppTheme.ColorOf(TrayIconModel.BackgroundKey, Colors.Black);
        double limit = s == null ? 1 : Levels.WireLimitA(s);
        var heights = TrayIconModel.BarHeights(s, limit);

        string key = TrayIconModel.Key(color.ToUInt32(), background.ToUInt32(), heights);
        if (key == _iconKey) return;
        _iconKey = key;

        try
        {
            const int size = TrayIconModel.Size;
            var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext())
            {
                dc.DrawRectangle(new ImmutableSolidColorBrush(background), null, new RoundedRect(new Rect(0, 0, size - 1, size - 1), TrayIconModel.CornerRadius));
                var bar = new ImmutableSolidColorBrush(color);
                for (int i = 0; i < heights.Length; i++)
                    dc.FillRectangle(bar, new Rect(TrayIconModel.BarX(i), TrayIconModel.Bottom - heights[i], TrayIconModel.BarWidth, heights[i]));
            }
            _tray.Icon = new WindowIcon(rtb);
            _iconBitmap?.Dispose();
            _iconBitmap = rtb;
        }
        catch (Exception ex)
        {
            AppLog.Warn("tray", "icon-failed", "Could not render the tray icon", ("error", ex.Message));
        }
    }

    /// <summary>Asks the session bus whether anyone hosts status-notifier items (org.kde.StatusNotifierWatcher).</summary>
    private static bool TrayHostPresent()
    {
        string? gdbus = Notifier.Find("gdbus");
        if (gdbus == null) return true; // cannot tell: let Avalonia try
        try
        {
            var psi = new ProcessStartInfo(gdbus) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus",
                                      "--method", "org.freedesktop.DBus.NameHasOwner", "org.kde.StatusNotifierWatcher" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return true;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return true; }
            return output.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
