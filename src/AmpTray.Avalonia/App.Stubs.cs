using System;
using Avalonia.Controls;
using AmpTray.Device;

namespace AmpTray.Avalonia;

// Placeholders that the next steps replace: tray (5), alerts (5), shutdown (6), settings window (7), wake detector (6).
public partial class App
{
    private void InitShutdown() { }
    private void InitFaults() { }
    private void BuildTray() { }
    private void DisposeTray() { }
    private void UpdateTray(bool force = false) { }
    private void ApplyAlertSettings() { }
    private void ApplyShutdownSettings()
    {
        var rules = ShutdownMonitor.ActiveRuleDescriptions(_settings.Shutdown);
        var list = new System.Collections.Generic.List<string>(rules);
        _window?.SetShutdownArmed(_settings.Shutdown.Enabled, list.Count == 0 ? Loc.T("(no rules enabled)") : string.Join("\n", list));
    }
    private void HandleFaults(SensorSnapshot s) { }
    private void EvaluateShutdown(bool deviceFresh, bool gpuFresh) { }
    private void Acknowledge() { }
    private void CancelPendingShutdown() { }
    private void TestAlert() { }
    private void StopAlert() { }
    private void StartWakeDetector() { }
    private void OpenSettings(string? page) { }

    private void RefreshMenu() { }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action click, bool? check = null)
        {
            var m = new MenuItem { Header = text };
            if (check is bool c) { m.ToggleType = MenuItemToggleType.CheckBox; m.IsChecked = c; }
            m.Click += (_, _) => click();
            return m;
        }
        menu.Items.Add(Item(Loc.T("Show widget"), () => SetVisible(_window?.IsVisible != true), _window?.IsVisible == true));
        menu.Items.Add(Item(Loc.T("Expanded view"), () => { _settings.Expanded = !_settings.Expanded; ApplySettings(); }, _settings.Expanded));
        var panels = new MenuItem { Header = Loc.T("Panels") };
        panels.Items.Add(Item(Loc.T("Wires"), () => { _settings.ShowWires = !_settings.ShowWires; ApplySettings(); }, _settings.ShowWires));
        panels.Items.Add(Item(Loc.T("Temperatures"), () => { _settings.ShowTemps = !_settings.ShowTemps; ApplySettings(); }, _settings.ShowTemps));
        panels.Items.Add(Item(Loc.T("GPU"), () => { _settings.ShowGpu = !_settings.ShowGpu; ApplySettings(); }, _settings.ShowGpu));
        panels.Items.Add(Item(Loc.T("Power graph"), () => { _settings.ShowGraph = !_settings.ShowGraph; ApplySettings(); }, _settings.ShowGraph));
        panels.Items.Add(Item(Loc.T("Faults"), () => { _settings.ShowFaults = !_settings.ShowFaults; ApplySettings(); }, _settings.ShowFaults));
        menu.Items.Add(panels);
        var theme = new MenuItem { Header = Loc.T("Theme") };
        foreach (var name in AppTheme.Names)
        {
            string n = name;
            theme.Items.Add(Item(n == AppTheme.Custom ? Loc.T("Custom") : n, () => { _settings.ThemeName = n; ApplySettings(); }, _settings.ThemeName == n));
        }
        menu.Items.Add(theme);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("Always on top"), () => { _settings.Topmost = !_settings.Topmost; ApplySettings(); }, _settings.Topmost));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("Log data to CSV"), () => { _settings.LogEnabled = !_settings.LogEnabled; ApplySettings(); }, _settings.LogEnabled));
        menu.Items.Add(Item(Loc.T("Open log folder"), () => Shell.Open(_settings.LogFolder)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("Auto shutdown armed"), () => { _settings.Shutdown.Enabled = !_settings.Shutdown.Enabled; ApplySettings(); }, _settings.Shutdown.Enabled));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("Settings…"), () => OpenSettings(null)));
        menu.Items.Add(Item(Loc.T("Exit"), ExitApp));
        return menu;
    }
}
