using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AmpTray.Gpu;

namespace AmpTray;

/// <summary>GPU strip (top) and GPU panel of the widget.</summary>
public partial class WidgetWindow
{
    private static readonly string[] GpuBarKeys = { "LOAD", "POWER", "VRAM" };
    private static readonly string[] GpuDetailKeys =
        { "Core clock", "Memory clock", "Fan", "Memory temp", "Encode / decode", "PCIe link", "Power limit", "Driver" };

    private GpuSnapshot? _lastGpu;
    private GpuStats? _gpuStats;
    private readonly StatRow[] _gpuRows = new StatRow[GpuBarKeys.Length];
    private readonly Dictionary<string, TextBlock> _gpuDetail = new();
    private readonly Dictionary<string, TextBlock> _gpuDetailLabels = new();

    private sealed class StatRow
    {
        public required TextBlock Label;
        public required ScaleTransform Fill;
        public required Border FillBar;
        public required TextBlock Value;
    }

    private double? _fps;
    private string? _fpsProcess;
    private bool _fpsServiceRunning;
    private bool _showTemp = true, _showFps = true;

    /// <summary>Which parts of the top bar are shown; the bar disappears when both are off.</summary>
    public void SetTopBar(bool gpuTemp, bool fps)
    {
        _showTemp = gpuTemp;
        _showFps = fps;
        TempSlot.Visibility = Vis(gpuTemp);
        FpsSlot.Visibility = Vis(fps);
        FpsSlot.Margin = new Thickness(0, 0, gpuTemp ? 12 : 0, 0);
        UpdateStripVisibility();
    }

    private void UpdateStripVisibility()
    {
        bool show = _lastGpu != null && (_showTemp || _showFps);
        GpuStrip.Visibility = Vis(show);
        GpuStripDivider.Visibility = Vis(show);
    }

    /// <summary>Frames per second of the foreground (or busiest) process, measured by the background service.</summary>
    public void ShowFps(double? fps, string? processName, bool serviceRunning)
    {
        _fps = fps;
        _fpsProcess = processName;
        _fpsServiceRunning = serviceRunning;
        if (fps is double f)
        {
            FpsText.Text = f.ToString("0");
            Ref(FpsText, TextBlock.ForegroundProperty, "TextPrimary");
            FpsSlot.ToolTip = Loc.F("Frames per second presented by {0}", processName ?? "?");
        }
        else
        {
            FpsText.Text = "n/a";
            Ref(FpsText, TextBlock.ForegroundProperty, "TextMuted");
            FpsSlot.ToolTip = serviceRunning
                ? Loc.T("No game is presenting frames right now")
                : Loc.T("The FPS counter needs the background service (Settings → Window → Background monitoring)");
        }
    }

    public void SetGpuGraph(PowerHistory history)
    {
        Graph.GpuHistory = history;
        Graph.InvalidateVisual();
    }

    private void ApplyGpuLanguage()
    {
        for (int i = 0; i < GpuBarKeys.Length; i++) _gpuRows[i].Label.Text = Loc.T(GpuBarKeys[i]);
        foreach (var (key, label) in _gpuDetailLabels) label.Text = Loc.T(key);
        ShowFps(_fps, _fpsProcess, _fpsServiceRunning);
    }

    public void ShowGpu(GpuSnapshot? g, GpuStats stats)
    {
        _lastGpu = g;
        _gpuStats = stats;

        bool has = g != null;
        UpdateStripVisibility();
        GpuNone.Visibility = Vis(!has);
        GpuBars.Visibility = Vis(has);
        if (g == null)
        {
            GpuClockText.Text = "";
            GpuThrottle.Visibility = Visibility.Collapsed;
            GpuPowerPath.Text = "";
            GpuSession.Text = "";
            return;
        }

        // ===== Top strip: temperatures =====
        GpuName.Text = g.Name.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase);
        GpuTempText.Text = $"{g.TempC:0}°";
        Ref(GpuTempText, TextBlock.ForegroundProperty, g.TempLevel == "OkBrush" ? "TextPrimary" : g.TempLevel);
        GpuGauge.Value = g.SlowdownC > 0 ? g.TempC / g.SlowdownC : 0;
        GpuGauge.BrushKey = g.TempLevel;

        bool hasMem = g.MemTempC is double;
        GpuMemTempLabel.Visibility = GpuMemTempText.Visibility = Vis(hasMem);
        if (g.MemTempC is double mt)
        {
            GpuMemTempText.Text = $"{mt:0}°";
            Ref(GpuMemTempText, TextBlock.ForegroundProperty, mt >= g.SlowdownC ? "CritBrush" : mt >= g.SlowdownC - 10 ? "WarnBrush" : "TextPrimary");
        }
        GpuStrip.ToolTip = g.Name + "\n"
            + Loc.F("GPU {0} °C. The driver throttles at {1} °C and shuts down at {2} °C", g.TempC.ToString("0"), g.SlowdownC.ToString("0"), g.ShutdownC.ToString("0"))
            + (hasMem ? "\n" + Loc.F("Memory {0} °C", g.MemTempC!.Value.ToString("0")) : "");

        // ===== Panel header =====
        GpuClockText.Text = (g.PState >= 0 ? $"P{g.PState} · " : "") + $"{g.CoreMhz:0} MHz";

        // ===== Bars =====
        Bar(0, g.LoadPct / 100, $"{g.LoadPct:0} %", "OkBrush");
        string pKey = g.PowerPct >= 100 ? "CritBrush" : g.PowerPct >= 90 ? "WarnBrush" : "OkBrush";
        Bar(1, g.PowerPct / 100, g.PowerLimitW > 0 ? $"{g.PowerW:0} / {g.PowerLimitW:0} W" : $"{g.PowerW:0} W", pKey);
        string vKey = g.MemUsedPct >= 97 ? "CritBrush" : g.MemUsedPct >= 90 ? "WarnBrush" : "OkBrush";
        Bar(2, g.MemUsedPct / 100, $"{GpuSnapshot.FormatBytes(g.MemUsedBytes)} / {GpuSnapshot.FormatBytes(g.MemTotalBytes)}", vKey);

        // ===== Throttling =====
        GpuThrottle.Children.Clear();
        var reasons = g.ThrottleLabels().ToList();
        foreach (var label in reasons)
        {
            bool thermal = GpuSnapshot.IsThermalReason(label);
            GpuThrottle.Children.Add(Chip(label, thermal ? "CritBrush" : "WarnBrush", thermal ? "CritBrush" : "WarnBrush", filled: thermal));
        }
        if (reasons.Count == 0 && _expanded)
            GpuThrottle.Children.Add(Chip("✓  " + Loc.T("Full clocks"), "OkBrush", "OkBrush"));
        GpuThrottle.Visibility = Vis(GpuThrottle.Children.Count > 0);

        // ===== Expanded details =====
        Detail("Core clock", g.CoreMaxMhz > 0 ? $"{g.CoreMhz:0} / {g.CoreMaxMhz:0} MHz" : $"{g.CoreMhz:0} MHz");
        Detail("Memory clock", $"{g.MemMhz:0} MHz");
        Detail("Fan", g.FanPct is double f ? $"{f:0} %" + (g.FanCount > 1 ? " · " + Loc.F("{0} fans", g.FanCount) : "") : "n/a");
        Detail("Memory temp", hasMem ? $"{g.MemTempC:0} °C" : Loc.T("not reported"));
        Detail("Encode / decode", g.EncoderPct is double e ? $"{e:0} % / {g.DecoderPct ?? 0:0} %" : "n/a");
        Detail("PCIe link", g.PcieGen > 0 ? $"Gen {g.PcieGen} ×{g.PcieWidth}" : "n/a");
        Detail("Power limit", g.PowerLimitW > 0
            ? $"{g.PowerLimitW:0} W" + (g.PowerLimitMaxW > g.PowerLimitW + 0.5 ? " " + Loc.F("(max {0})", g.PowerLimitMaxW.ToString("0")) : "")
            : "n/a");
        Detail("Driver", string.IsNullOrEmpty(g.DriverVersion) ? "n/a" : g.DriverVersion);

        UpdateGpuPowerPath();
        GpuSession.Text = Loc.F("Since {0} · peak {1}° · peak {2} W · {3}", stats.Since.ToString("HH:mm"), stats.PeakTempC.ToString("0"), stats.PeakPowerW.ToString("0"), FormatEnergy(stats.EnergyWh));
    }

    /// <summary>Relates the driver's board power to what the WireView measures on the 12V-2x6 cable.</summary>
    private void UpdateGpuPowerPath()
    {
        if (_lastGpu is not GpuSnapshot g) return;
        if (_last == null)
        {
            GpuPowerPath.Text = Loc.T("Connect the WireView to see how board power splits between the 12V-2x6 cable and the PCIe slot.");
            return;
        }
        double board = g.PowerW, cable = _last.TotalPowerW, slot = board - cable;
        GpuPowerPath.Text = slot >= 0
            ? Loc.F("Board {0} W = 12V-2x6 {1} W + PCIe slot ~{2} W", board.ToString("0"), cable.ToString("0"), slot.ToString("0"))
              + (slot > 75 ? "  ⚠ " + Loc.T("slot above its 75 W spec") : "")
            : Loc.F("12V-2x6 reads {0} W, {1} W above the {2} W board power the driver reports", cable.ToString("0"), (-slot).ToString("0"), board.ToString("0"));
    }

    private void SetGpuExpanded(bool expanded)
    {
        GpuDetails.Visibility = Vis(expanded);
        GpuPowerPath.Visibility = Vis(expanded);
        GpuSession.Visibility = Vis(expanded);
        if (_lastGpu != null && _gpuStats != null) ShowGpu(_lastGpu, _gpuStats);
    }

    private void Bar(int i, double fraction, string text, string key)
    {
        var r = _gpuRows[i];
        Animate(r.Fill, Math.Clamp(fraction, 0, 1));
        Ref(r.FillBar, Border.BackgroundProperty, key);
        r.Value.Text = text;
        Ref(r.Value, TextBlock.ForegroundProperty, key == "OkBrush" ? "TextPrimary" : key);
    }

    private void Detail(string key, string value)
    {
        if (_gpuDetail.TryGetValue(key, out var t)) t.Text = value;
    }

    private void BuildGpuRows()
    {
        for (int i = 0; i < GpuBarKeys.Length; i++)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Ref(label, TextBlock.ForegroundProperty, "TextMuted");
            grid.Children.Add(label);

            var scale = new ScaleTransform(0, 1);
            var fill = new Border { CornerRadius = new CornerRadius(3), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = scale };
            var trackBg = new Border { CornerRadius = new CornerRadius(3) };
            Ref(trackBg, Border.BackgroundProperty, "TrackBrush");
            var track = new Grid { Height = 6, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(trackBg);
            track.Children.Add(fill);
            Grid.SetColumn(track, 1);
            grid.Children.Add(track);

            var value = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                MinWidth = 96,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Ref(value, TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(value, 2);
            grid.Children.Add(value);

            GpuBars.Children.Add(grid);
            _gpuRows[i] = new StatRow { Label = label, Fill = scale, FillBar = fill, Value = value };
        }
    }

    private void BuildGpuDetails()
    {
        GpuDetails.ColumnDefinitions.Add(new ColumnDefinition());
        GpuDetails.ColumnDefinitions.Add(new ColumnDefinition());
        int rows = (GpuDetailKeys.Length + 1) / 2;
        for (int r = 0; r < rows; r++) GpuDetails.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < GpuDetailKeys.Length; i++)
        {
            var cell = new Grid { Margin = new Thickness(0, 2, i % 2 == 0 ? 14 : 0, 2) };
            cell.ColumnDefinitions.Add(new ColumnDefinition());
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
            Ref(label, TextBlock.ForegroundProperty, "TextMuted");
            var value = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            Ref(value, TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(value, 1);
            cell.Children.Add(label);
            cell.Children.Add(value);

            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            GpuDetails.Children.Add(cell);
            _gpuDetail[GpuDetailKeys[i]] = value;
            _gpuDetailLabels[GpuDetailKeys[i]] = label;
        }
    }
}
