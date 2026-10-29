using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AmpTray.Gpu;

namespace AmpTray.Avalonia;

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
    private bool _showTemp = true;

    private sealed class StatRow
    {
        public required TextBlock Label;
        public required ScaleTransform Fill;
        public required Border FillBar;
        public required TextBlock Value;
    }

    /// <summary>Whether the GPU temperature strip at the top is shown (there is no FPS counter on Linux).</summary>
    public void SetTopBar(bool gpuTemp)
    {
        _showTemp = gpuTemp;
        TempSlot.IsVisible = gpuTemp;
        UpdateStripVisibility();
    }

    private void UpdateStripVisibility()
    {
        bool show = _lastGpu != null && _showTemp;
        GpuStrip.IsVisible = show;
        GpuStripDivider.IsVisible = show;
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
    }

    public void ShowGpu(GpuSnapshot? g, GpuStats stats)
    {
        _lastGpu = g;
        _gpuStats = stats;

        bool has = g != null;
        UpdateStripVisibility();
        GpuNone.IsVisible = !has;
        GpuBars.IsVisible = has;
        if (g == null)
        {
            GpuClockText.Text = "";
            GpuThrottle.IsVisible = false;
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
        GpuMemTempLabel.IsVisible = GpuMemTempText.IsVisible = hasMem;
        if (g.MemTempC is double mt)
        {
            GpuMemTempText.Text = $"{mt:0}°";
            Ref(GpuMemTempText, TextBlock.ForegroundProperty, mt >= g.SlowdownC ? "CritBrush" : mt >= g.SlowdownC - 10 ? "WarnBrush" : "TextPrimary");
        }
        ToolTip.SetTip(GpuStrip, g.Name + "\n"
            + Loc.F("GPU {0} °C. The driver throttles at {1} °C and shuts down at {2} °C", g.TempC.ToString("0"), g.SlowdownC.ToString("0"), g.ShutdownC.ToString("0"))
            + (hasMem ? "\n" + Loc.F("Memory {0} °C", g.MemTempC!.Value.ToString("0")) : ""));

        // ===== Panel header: state, clock and, when throttling, the reason (keeps the panel height constant) =====
        var reasons = g.ThrottleLabels().ToList();
        bool anyThermal = reasons.Any(GpuSnapshot.IsThermalReason);
        GpuClockText.Text = (g.PState >= 0 ? $"P{g.PState} · " : "") + $"{g.CoreMhz:0} MHz"
                            + (reasons.Count > 0 ? " · " + string.Join(", ", reasons) : "");
        Ref(GpuClockText, TextBlock.ForegroundProperty, reasons.Count == 0 ? "TextMuted" : anyThermal ? "CritBrush" : "WarnBrush");

        // ===== Bars =====
        Bar(0, g.LoadPct / 100, $"{g.LoadPct:0} %", "OkBrush");
        string pKey = g.PowerPct >= 100 ? "CritBrush" : g.PowerPct >= 90 ? "WarnBrush" : "OkBrush";
        Bar(1, g.PowerPct / 100, g.PowerLimitW > 0 ? $"{g.PowerW:0} / {g.PowerLimitW:0} W" : $"{g.PowerW:0} W", pKey);
        string vKey = g.MemUsedPct >= 97 ? "CritBrush" : g.MemUsedPct >= 90 ? "WarnBrush" : "OkBrush";
        Bar(2, g.MemUsedPct / 100, $"{GpuSnapshot.FormatBytes(g.MemUsedBytes)} / {GpuSnapshot.FormatBytes(g.MemTotalBytes)}", vKey);

        // ===== Throttling chips: expanded view only, and always exactly one row so nothing jumps =====
        GpuThrottle.Children.Clear();
        if (_expanded)
        {
            foreach (var label in reasons)
            {
                bool thermal = GpuSnapshot.IsThermalReason(label);
                GpuThrottle.Children.Add(Chip(label, thermal ? "CritBrush" : "WarnBrush", thermal ? "CritBrush" : "WarnBrush", filled: thermal));
            }
            if (reasons.Count == 0)
                GpuThrottle.Children.Add(Chip("✓  " + Loc.T("Full clocks"), "OkBrush", "OkBrush"));
        }
        GpuThrottle.IsVisible = _expanded;

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

    // Smoothed board and cable power for the power-path sentence, and when it was last rewritten.
    private double _pathBoardW = double.NaN, _pathCableW = double.NaN;
    private DateTime _pathWritten = DateTime.MinValue, _pathSampled = DateTime.MinValue;
    private bool _pathCableAbove;
    private static readonly TimeSpan PathRefresh = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Relates the driver's board power to what the WireView measures on the 12V-2x6 cable. Both
    /// readings jitter from sample to sample, so the sentence uses a few seconds of smoothing, is
    /// rewritten at most every two seconds, and only changes its wording once the difference has
    /// clearly moved to the other side of zero.
    /// </summary>
    private void UpdateGpuPowerPath()
    {
        if (_lastGpu is not GpuSnapshot g) return;
        if (_last == null)
        {
            GpuPowerPath.Text = Loc.T("Connect the WireView to see how board power splits between the 12V-2x6 cable and the PCIe slot.");
            _pathBoardW = _pathCableW = double.NaN;
            _pathWritten = _pathSampled = DateTime.MinValue;
            return;
        }

        // Exponential moving average with a time constant of about five seconds at any poll rate.
        var now = DateTime.Now;
        double dt = Math.Clamp((now - _pathSampled).TotalSeconds, 0.05, 60);
        double k = 1 - Math.Exp(-dt / 5.0);
        _pathSampled = now;
        _pathBoardW = double.IsNaN(_pathBoardW) ? g.PowerW : _pathBoardW + k * (g.PowerW - _pathBoardW);
        _pathCableW = double.IsNaN(_pathCableW) ? _last.TotalPowerW : _pathCableW + k * (_last.TotalPowerW - _pathCableW);
        if (now - _pathWritten < PathRefresh && (GpuPowerPath.Text?.Length ?? 0) > 0) return;
        _pathWritten = now;

        double board = _pathBoardW, cable = _pathCableW, slot = board - cable;
        if (_pathCableAbove ? slot > 2 : slot < -5) _pathCableAbove = !_pathCableAbove;
        GpuPowerPath.Text = !_pathCableAbove
            ? Loc.F("Board {0} W = 12V-2x6 {1} W + PCIe slot ~{2} W", board.ToString("0"), cable.ToString("0"), Math.Max(0, slot).ToString("0"))
              + (slot > 75 ? "  ⚠ " + Loc.T("slot above its 75 W spec") : "")
            : Loc.F("12V-2x6 reads {0} W, {1} W above the {2} W board power the driver reports", cable.ToString("0"), (-slot).ToString("0"), board.ToString("0"));
    }

    private void SetGpuExpanded(bool expanded)
    {
        GpuDetails.IsVisible = expanded;
        GpuPowerPath.IsVisible = expanded;
        GpuSession.IsVisible = expanded;
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
            grid.ColumnDefinitions.Add(new ColumnDefinition(52, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var label = new TextBlock { FontSize = 10, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Ref(label, TextBlock.ForegroundProperty, "TextMuted");
            grid.Children.Add(label);

            var (track, fill, scale) = BuildBar(new Thickness(0, 0, 10, 0));
            Grid.SetColumn(track, 1);
            grid.Children.Add(track);

            var value = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
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
        GpuDetails.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        GpuDetails.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        int rows = (GpuDetailKeys.Length + 1) / 2;
        for (int r = 0; r < rows; r++) GpuDetails.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int i = 0; i < GpuDetailKeys.Length; i++)
        {
            var cell = new Grid { Margin = new Thickness(0, 2, i % 2 == 0 ? 14 : 0, 2) };
            cell.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            cell.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

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
