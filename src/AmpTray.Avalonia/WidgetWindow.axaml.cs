using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmpTray.Device;
using AmpTray.Ui;

namespace AmpTray.Avalonia;

/// <summary>The floating widget, a 1:1 port of the WPF WidgetWindow. The controller (App) feeds it samples.</summary>
public partial class WidgetWindow : Window
{
    private const double CompactWidth = 292;
    private const double ExpandedWidth = 440;
    private static readonly string[] TempLabels = { "IN", "OUT", "TS1", "TS2" };
    private static readonly string[] PinHeaderKeys = { "AMPS", "VOLTS", "WATTS", "PEAK" };

    private readonly PinRow[] _pins = new PinRow[SensorSnapshot.PinCount];
    private readonly TempCell[] _temps = new TempCell[4];
    private readonly (Ellipse Dot, TextBlock State)[] _faultCells = new (Ellipse, TextBlock)[6];
    private readonly TextBlock[] _faultNames = new TextBlock[6];
    private readonly TextBlock[] _pinHeaderCells = new TextBlock[PinHeaderKeys.Length];
    private bool _expanded, _topmost = true, _opaqueOnHover = true;
    private double _baseOpacity = 1;
    private SensorSnapshot? _last;
    private SessionStats? _stats;
    private string _port = "";
    private UpdateInfo? _update;

    private readonly DispatcherTimer _moveSettle = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private double _lastWidth;

    public event Action? MenuRequested;
    /// <summary>The window has settled at a new position (logical pixels).</summary>
    public event Action<double, double>? Moved;
    public event Action? ExpandToggleRequested;
    public event Action? SettingsRequested;
    public event Action? AcknowledgeRequested;
    public event Action? ResetStatsRequested;
    public event Action? ShutdownCancelRequested;
    public event Action? UpdateDownloadRequested;
    public event Action? UpdateDismissed;
    public event Action? HiddenByUser;

    public WidgetWindow()
    {
        InitializeComponent();
        BuildPinHeader();
        BuildPinRows();
        BuildTempCells();
        BuildFaultTable();
        BuildGpuRows();
        BuildGpuDetails();
        ApplyLanguage();

        _moveSettle.Tick += (_, _) =>
        {
            _moveSettle.Stop();
            double s = RenderScaling;
            Moved?.Invoke(Position.X / s, Position.Y / s);
        };
        PositionChanged += (_, _) => { _moveSettle.Stop(); _moveSettle.Start(); };

        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        // Hover buttons take no header space while hidden, so badges like ARMED aren't clipped.
        PointerEntered += (_, _) =>
        {
            HeaderButtons.IsVisible = true;
            if (_opaqueOnHover) ApplyAlpha(1);
        };
        PointerExited += (_, _) =>
        {
            HeaderButtons.IsVisible = false;
            ApplyAlpha(_baseOpacity);
        };

        ExpandButton.Click += (_, _) => ExpandToggleRequested?.Invoke();
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        AckButton.Click += (_, _) => AcknowledgeRequested?.Invoke();
        ResetStatsButton.Click += (_, _) => ResetStatsRequested?.Invoke();
        ShutdownCancelButton.Click += (_, _) => ShutdownCancelRequested?.Invoke();
        UpdateDownloadButton.Click += (_, _) => UpdateDownloadRequested?.Invoke();
        UpdateDismissButton.Click += (_, _) => { HideUpdate(); UpdateDismissed?.Invoke(); };
        Panel.SizeChanged += KeepRightEdgeAnchored;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Properties;
        if (!p.IsLeftButtonPressed) return;
        if (IsInteractive(e.Source)) return; // buttons and the graph handle their own clicks
        if (e.ClickCount == 2)
        {
            ExpandToggleRequested?.Invoke();
            e.Handled = true;
            return;
        }
        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        MenuRequested?.Invoke();
        ContextMenu?.Open(this);
        e.Handled = true;
    }

    private static bool IsInteractive(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is Button) return true;
        return false;
    }

    /// <summary>
    /// When the widget sits on the right half of its monitor, grow/shrink leftwards so expanding
    /// doesn't push it off screen.
    /// </summary>
    private void KeepRightEdgeAnchored(object? sender, SizeChangedEventArgs e)
    {
        double old = _lastWidth;
        _lastWidth = e.NewSize.Width;
        if (old <= 0 || Math.Abs(e.NewSize.Width - old) < 0.5 || !IsVisible) return;

        double s = RenderScaling;
        int oldCenter = Position.X + (int)(old * s / 2);
        var screen = Screens.ScreenFromPoint(new PixelPoint(oldCenter, Position.Y + (int)(20 * s)));
        if (screen == null) return;
        double screenCenter = screen.WorkingArea.X + screen.WorkingArea.Width / 2.0;
        if (oldCenter > screenCenter)
            Position = new PixelPoint(Position.X - (int)Math.Round((e.NewSize.Width - old) * s), Position.Y);
    }

    // ===== Window behaviour =====
    /// <summary>On top: stays above everything. Not on top: an ordinary window that drops behind others.</summary>
    public void SetTopmost(bool on)
    {
        _topmost = on;
        Topmost = on;
    }

    /// <summary>Resting opacity (down to almost invisible) and whether hovering brings the widget back to full.</summary>
    public void SetOpacity(double value, bool opaqueOnHover)
    {
        _baseOpacity = value;
        _opaqueOnHover = opaqueOnHover;
        ApplyAlpha(opaqueOnHover && IsPointerOver ? 1 : value);
    }

    /// <summary>Fades only the panel background; text, bars, gauges and banners keep their full colour.</summary>
    private void ApplyAlpha(double opacity) => AppTheme.SetPanelAlpha(Math.Clamp(opacity, 0, 1));

    /// <summary>Raises the widget above other windows once, without pinning it there.</summary>
    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (_topmost) return;
        Topmost = true;
        Topmost = false;
    }

    /// <summary>Puts the window at a saved position, or near the top-right of the primary screen, always on a screen.</summary>
    public void PlaceWindow(double? left, double? top)
    {
        double s = RenderScaling;
        var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        int w = (int)Math.Round((Panel.Bounds.Width > 0 ? Panel.Bounds.Width : CompactWidth) * s);
        int h = (int)Math.Round((Bounds.Height > 0 ? Bounds.Height : 200) * s);
        int x = left is double l ? (int)Math.Round(l * s) : area.Right - w - (int)(24 * s);
        int y = top is double t ? (int)Math.Round(t * s) : area.Y + (int)(24 * s);

        // Keep at least a hand's width of the widget on some screen.
        bool onAScreen = false;
        foreach (var sc in Screens.All)
        {
            var r = sc.Bounds;
            if (x + w - 40 > r.X && x + 40 < r.Right && y + 40 > r.Y && y + 40 < r.Bottom) { onAScreen = true; break; }
        }
        if (!onAScreen)
        {
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - w));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - h));
        }
        Position = new PixelPoint(x, y);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing (Alt+F4, the window manager) only hides the widget; Exit in the menu quits.
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
            HiddenByUser?.Invoke();
        }
        base.OnClosing(e);
    }

    // ===== Configuration =====
    /// <summary>Assigns every fixed text in the current language and re-renders the live values.</summary>
    public void ApplyLanguage()
    {
        ToolTip.SetTip(SettingsButton, Loc.T("Settings"));
        ArmedText.Text = Loc.T("ARMED");
        AckButton.Content = Loc.T("Acknowledge");
        ToolTip.SetTip(AckButton, Loc.T("Stop the flashing and alarm until a new fault occurs"));
        ShutdownCancelButton.Content = Loc.T("Cancel");
        ToolTip.SetTip(ShutdownCancelButton, Loc.T("Don't shut down for this condition until it clears"));
        WiresLabel.Text = Loc.T("WIRES");
        TempsLabel.Text = Loc.T("TEMPERATURES");
        GpuLabel.Text = Loc.T("GPU");
        PowerLabel.Text = Loc.T("POWER");
        FaultsLabel.Text = Loc.T("FAULTS");
        UpdateDownloadButton.Content = Loc.T("Download");
        ToolTip.SetTip(UpdateDismissButton, Loc.T("Dismiss until next launch"));
        if (_update != null) ShowUpdate(_update);
        ResetStatsButton.Content = Loc.T("Reset");
        ToolTip.SetTip(ResetStatsButton, Loc.T("Reset session min/max/energy (widget only)"));
        GpuNone.Text = Loc.T("No NVIDIA GPU detected");
        ToolTip.SetTip(GpuPowerPath, Loc.T("Driver power is a ~1 s average; the WireView reading is near-instant, so small gaps are normal."));
        for (int i = 0; i < PinHeaderKeys.Length; i++) _pinHeaderCells[i].Text = Loc.T(PinHeaderKeys[i]);
        foreach (var f in Enum.GetValues<Fault>()) _faultNames[(int)f].Text = SensorSnapshot.Describe(f);
        ApplyGpuLanguage();

        SetExpanded(_expanded); // also re-renders a live sample
        if (_last == null) ShowDisconnected(null);
    }

    public void SetPanels(bool wires, bool temps, bool graph, bool faults, bool gpu)
    {
        WiresSection.IsVisible = wires;
        TempsSection.IsVisible = temps;
        GraphSection.IsVisible = graph;
        FaultsSection.IsVisible = faults;
        GpuSection.IsVisible = gpu;
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        Panel.Width = expanded ? ExpandedWidth : CompactWidth;
        ExpandButton.Content = expanded ? "⤡" : "⤢";
        ToolTip.SetTip(ExpandButton, expanded ? Loc.T("Compact view (double-click)") : Loc.T("Expanded view (double-click)"));

        PinHeader.IsVisible = expanded;
        foreach (var p in _pins)
        {
            p.Watts.IsVisible = expanded;
            p.Peak.IsVisible = expanded;
        }
        foreach (var t in _temps) t.Max.IsVisible = expanded;
        Graph.Height = expanded ? 130 : 64;
        SessionRow.IsVisible = expanded;
        FaultPanel.IsVisible = !expanded;
        FaultTable.IsVisible = expanded;
        FaultEvents.IsVisible = expanded;
        SetGpuExpanded(expanded);

        InvalidateFaults();
        if (_last != null && _stats != null) ShowSample(_last, _port, _stats);
    }

    public void SetGraph(PowerHistory history, int windowSeconds, int pollMs)
    {
        Graph.History = history;
        Graph.WindowSeconds = windowSeconds;
        Graph.GapSeconds = Math.Max(5, pollMs * 4 / 1000.0);
        Graph.InvalidateVisual();
    }

    public void SetRecording(bool on, string? tooltip)
    {
        RecBadge.IsVisible = on;
        ToolTip.SetTip(RecBadge, tooltip);
    }

    /// <summary>Re-applies theme-derived values that are not resource references.</summary>
    public void RefreshTheme()
    {
        Graph.InvalidateVisual();
        GpuGauge.InvalidateVisual();
        InvalidateFaults();
        if (_last != null && _stats != null) ShowSample(_last, _port, _stats);
        else ShowDisconnected(null);
        if (_lastGpu != null && _gpuStats != null) ShowGpu(_lastGpu, _gpuStats);
    }

    // ===== Auto shutdown =====
    public void SetShutdownArmed(bool armed, string? rules)
    {
        ArmedBadge.IsVisible = armed;
        ToolTip.SetTip(ArmedBadge, rules == null ? null : Loc.T("Auto shutdown armed:") + "\n" + rules);
    }

    /// <summary>Shows the countdown banner for the rule closest to firing, or hides it.</summary>
    public void ShowShutdownPending(PendingRule? next, int more)
    {
        if (next == null)
        {
            ShutdownBanner.IsVisible = false;
            return;
        }
        var r = next.Remaining;
        string when = r <= TimeSpan.Zero ? Loc.T("now") : Loc.F("in {0}", $"{(int)r.TotalMinutes}:{r.Seconds:00}");
        ShutdownText.Text = Loc.F("PC shuts down {0}: {1}", when, next.Condition) + (more > 0 ? " " + Loc.F("(+{0} more)", more) : "");
        ShutdownBanner.IsVisible = true;
    }

    // ===== Updates =====
    public void ShowUpdate(UpdateInfo info)
    {
        _update = info;
        UpdateText.Text = Loc.F("AmpTray {0} is available", info.Tag) + (info.Name != info.Tag ? $": {info.Name}" : "");
        UpdateBanner.IsVisible = true;
    }

    public void HideUpdate()
    {
        _update = null;
        UpdateBanner.IsVisible = false;
    }

    // ===== Alerts =====
    private CancellationTokenSource? _flash;

    public void ShowAlert(string message, bool flash)
    {
        AlertText.Text = "⚠  " + message;
        AlertBanner.IsVisible = true;
        if (!flash) return;
        Ref(Panel, Border.BorderBrushProperty, "CritBrush");
        if (_flash != null) return;
        _flash = new CancellationTokenSource();
        var pulse = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(450),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.32) } },
            },
        };
        _ = pulse.RunAsync(FlashLayer, _flash.Token);
    }

    public void HideAlert()
    {
        AlertBanner.IsVisible = false;
        Ref(Panel, Border.BorderBrushProperty, "PanelBorderBrush");
        _flash?.Cancel();
        _flash = null;
        FlashLayer.Opacity = 0;
    }

    // ===== Data =====
    public void ShowDisconnected(string? reason)
    {
        _last = null;
        Ref(StatusDot, Shape.FillProperty, "IdleBrush");
        StatusText.Text = reason ?? Loc.T("Searching for device…");
        TotalPower.Text = "n/a";
        Ref(TotalPower, TextBlock.ForegroundProperty, "TextMuted");
        TotalCurrent.Text = "";
        ScaleText.Text = "";
        SpreadText.Text = "";
        GraphStatsText.Text = "";
        foreach (var p in _pins)
        {
            Animate(p.Fill, 0);
            Ref(p.FillBar, Border.BackgroundProperty, "IdleBrush");
            p.Amps.Text = "n/a";
            Ref(p.Amps, TextBlock.ForegroundProperty, "TextMuted");
            p.Volts.Text = p.Watts.Text = p.Peak.Text = "";
        }
        foreach (var t in _temps)
        {
            t.Value.Text = "n/a";
            Ref(t.Value, TextBlock.ForegroundProperty, "TextMuted");
            t.Max.Text = "";
        }
        InvalidateFaults();
        FaultPanel.Children.Clear();
        FaultPanel.Children.Add(Chip(Loc.T("No data"), "TextMuted", "TextMuted"));
        foreach (var (dot, state) in _faultCells)
        {
            Ref(dot, Shape.FillProperty, "IdleBrush");
            state.Text = "n/a";
        }
        UpdateGpuPowerPath();
    }

    public void ShowSample(SensorSnapshot s, string port, SessionStats stats)
    {
        _last = s;
        _stats = stats;
        _port = port;

        Ref(StatusDot, Shape.FillProperty, OverallLevel(s));
        StatusText.Text = _expanded && s.CableRatingW > 0
            ? $"{port} · {s.Timestamp:HH:mm:ss} · " + Loc.F("Cable {0} W", s.CableRatingW)
            : $"{port} · {s.Timestamp:HH:mm:ss}";

        TotalPower.Text = $"{s.TotalPowerW:0.0} W";
        Ref(TotalPower, TextBlock.ForegroundProperty, "TextPrimary");
        TotalCurrent.Text = $"{s.TotalCurrentA:0.00} A · {s.PinVoltage.Average():0.00} V";

        UpdateWires(s, stats);
        UpdateTemps(s, stats);
        UpdateGraph(s, stats);
        UpdateFaults(s, stats);
        UpdateGpuPowerPath();
    }

    private void UpdateWires(SensorSnapshot s, SessionStats stats)
    {
        // Full bar = what one wire may carry under the cable rating.
        double limit = WireLimitA(s);
        ScaleText.Text = s.CableRatingW > 0
            ? Loc.F("0 to {0} A per wire ({1} W cable)", limit.ToString("0.0#"), s.CableRatingW)
            : Loc.F("0 to {0} A per wire (pin rating)", limit.ToString("0.0"));
        double mean = s.PinCurrent.Average();

        for (int i = 0; i < _pins.Length; i++)
        {
            double a = s.PinCurrent[i];
            var p = _pins[i];
            string key = WireLevel(a, limit);
            Animate(p.Fill, Math.Clamp(a / limit, 0, 1));
            Ref(p.FillBar, Border.BackgroundProperty, key);
            p.Amps.Text = $"{a:0.00} A";
            Ref(p.Amps, TextBlock.ForegroundProperty, key == "OkBrush" ? "TextPrimary" : key);
            p.Volts.Text = $"{s.PinVoltage[i]:0.00} V";
            p.Watts.Text = $"{s.PinVoltage[i] * a:0.0} W";
            p.Peak.Text = $"{stats.PinMaxA[i]:0.00} A";
            ToolTip.SetTip(p.Row, mean > 0.05 ? Loc.F("Wire {0}: {1}% vs average", i + 1, ((a - mean) / mean * 100).ToString("+0;-0;0")) : null);
        }

        double spread = s.PinCurrent.Max() - s.PinCurrent.Min();
        SpreadText.Text = _expanded && mean > 0.05
            ? Loc.F("spread {0} A · max deviation {1}%", spread.ToString("0.00"), (s.PinCurrent.Max(a => Math.Abs(a - mean)) / mean * 100).ToString("0"))
            : Loc.F("spread {0} A", spread.ToString("0.00"));
    }

    private void UpdateTemps(SensorSnapshot s, SessionStats stats)
    {
        double[] temps = { s.TempInC, s.TempOutC, s.TempTs1C, s.TempTs2C };
        for (int i = 0; i < temps.Length; i++)
        {
            var t = _temps[i];
            if (!SensorSnapshot.IsTempPresent(temps[i]))
            {
                t.Value.Text = "n/a";
                Ref(t.Value, TextBlock.ForegroundProperty, "TextMuted");
                t.Max.Text = Loc.T("not connected");
                continue;
            }
            t.Value.Text = $"{temps[i]:0.0}°";
            Ref(t.Value, TextBlock.ForegroundProperty, TempLevel(temps[i]) ?? "TextPrimary");
            t.Max.Text = double.IsNaN(stats.TempMaxC[i]) ? "" : Loc.F("max {0}°", stats.TempMaxC[i].ToString("0.0"));
        }
    }

    private void UpdateGraph(SensorSnapshot s, SessionStats stats)
    {
        Graph.CableRatingW = s.CableRatingW;
        Graph.InvalidateVisual();
        var window = Graph.History?.Since(DateTime.Now.AddSeconds(-Graph.WindowSeconds)).ToList();
        if (window == null || window.Count == 0)
        {
            GraphStatsText.Text = "";
            return;
        }
        string span = Controls.PowerGraph.FormatSpan(Graph.WindowSeconds);
        GraphStatsText.Text = _expanded
            ? Loc.F("last {0} · avg {1} W · max {2} W", span, window.Average(p => p.W).ToString("0"), window.Max(p => p.W).ToString("0"))
            : Loc.F("last {0} · max {1} W", span, window.Max(p => p.W).ToString("0"));
        SessionText.Text = Loc.F("Since {0} · peak {1} W · avg {2} W · {3}", stats.Since.ToString("HH:mm"), stats.PeakPowerW.ToString("0"), stats.AvgPowerW.ToString("0"), FormatEnergy(stats.EnergyWh));
    }

    private int _faultKey = -1;

    /// <summary>Forces the next sample to rebuild the fault chips (layout, language or theme changed).</summary>
    private void InvalidateFaults() => _faultKey = -1;

    private void UpdateFaults(SensorSnapshot s, SessionStats stats)
    {
        // Chips and the event list are only rebuilt when the fault state changes, not every second.
        int faultKey = (s.FaultStatus << 16) | s.FaultLog | (stats.FaultEvents.Count << 20);
        if (faultKey == _faultKey) return;
        _faultKey = faultKey;

        var active = SensorSnapshot.Decode(s.FaultStatus).ToList();
        var loggedOnly = SensorSnapshot.Decode((ushort)(s.FaultLog & ~s.FaultStatus)).ToList();

        // Compact: chips.
        FaultPanel.Children.Clear();
        foreach (var f in active)
            FaultPanel.Children.Add(Chip(SensorSnapshot.Describe(f), "CritBrush", "CritBrush", filled: true));
        foreach (var f in loggedOnly)
        {
            var chip = Chip(SensorSnapshot.Describe(f) + " · " + Loc.T("logged"), "WarnBrush", "WarnBrush");
            ToolTip.SetTip(chip, Loc.T("Occurred earlier and is latched in the device log. Clear it in the official WireView app."));
            FaultPanel.Children.Add(chip);
        }
        if (active.Count == 0 && loggedOnly.Count == 0)
            FaultPanel.Children.Add(Chip("✓  " + Loc.T("No faults"), "OkBrush", "OkBrush"));

        // Expanded: every fault with its state, plus recent transitions.
        foreach (var f in Enum.GetValues<Fault>())
        {
            var (dot, state) = _faultCells[(int)f];
            bool isActive = active.Contains(f), isLogged = loggedOnly.Contains(f);
            string key = isActive ? "CritBrush" : isLogged ? "WarnBrush" : "OkBrush";
            Ref(dot, Shape.FillProperty, key);
            state.Text = isActive ? Loc.T("ACTIVE") : isLogged ? Loc.T("logged") : Loc.T("ok");
            Ref(state, TextBlock.ForegroundProperty, isActive || isLogged ? key : "TextMuted");
        }

        FaultEvents.Children.Clear();
        var header = new TextBlock { Text = Loc.T("RECENT EVENTS") };
        header.Classes.Add("SectionLabel");
        FaultEvents.Children.Add(header);
        var recent = stats.FaultEvents.AsEnumerable().Reverse().Take(5).ToList();
        if (recent.Count == 0)
        {
            var none = new TextBlock { Text = Loc.T("No fault changes this session"), FontSize = 11 };
            Ref(none, TextBlock.ForegroundProperty, "TextMuted");
            FaultEvents.Children.Add(none);
        }
        foreach (var ev in recent)
        {
            var line = new TextBlock { FontSize = 11, Margin = new Thickness(0, 1, 0, 1) };
            line.Inlines!.Add(new Run($"{ev.Time:HH:mm:ss}   ") { Foreground = AppTheme.BrushOf("TextMuted") });
            line.Inlines.Add(new Run($"{SensorSnapshot.Describe(ev.Fault)} {(ev.Active ? Loc.T("raised") : Loc.T("cleared"))}")
            {
                Foreground = AppTheme.BrushOf(ev.Active ? "CritBrush" : "TextSecondary"),
            });
            FaultEvents.Children.Add(line);
        }
    }

    // Thresholds and level keys are shared with the tray icon and the Windows widget (AmpTray.Ui.Levels).
    public static string OverallLevel(SensorSnapshot s) => Levels.OverallLevel(s);
    public static double WireLimitA(SensorSnapshot s) => Levels.WireLimitA(s);
    public static string WireLevel(double amps, double limitA) => Levels.WireLevel(amps, limitA);
    private static string? TempLevel(double c) => Levels.TempLevel(c);
    private static string FormatEnergy(double wh) => Readouts.FormatEnergy(wh);

    // ===== Layout construction =====
    private sealed class PinRow
    {
        public required Grid Row;
        public required ScaleTransform Fill;
        public required Border FillBar;
        public required TextBlock Amps, Volts, Watts, Peak;
    }

    private sealed class TempCell
    {
        public required TextBlock Value, Max;
    }

    private static Grid PinGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition(14, GridUnitType.Pixel));
        g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (int i = 0; i < 4; i++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        return g;
    }

    private static TextBlock Cell(Grid g, int col, double minWidth, double size, string fgKey, bool bold = false)
    {
        var t = new TextBlock
        {
            FontSize = size,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            MinWidth = minWidth,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Ref(t, TextBlock.ForegroundProperty, fgKey);
        Grid.SetColumn(t, col);
        g.Children.Add(t);
        return t;
    }

    private void BuildPinHeader()
    {
        var g = PinGrid();
        double[] widths = { 58, 54, 58, 62 };
        for (int i = 0; i < PinHeaderKeys.Length; i++)
            _pinHeaderCells[i] = Cell(g, i + 2, widths[i], 9.5, "TextMuted");
        PinHeader.Children.Add(g);
    }

    private void BuildPinRows()
    {
        for (int i = 0; i < _pins.Length; i++)
        {
            var grid = PinGrid();
            grid.Margin = new Thickness(0, 2, 0, 2);
            grid.Background = Brushes.Transparent; // so the tooltip works across the whole row

            var label = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            Ref(label, TextBlock.ForegroundProperty, "TextMuted");
            grid.Children.Add(label);

            var (track, fill, scale) = BuildBar(new Thickness(2, 0, 10, 0));
            Grid.SetColumn(track, 1);
            grid.Children.Add(track);

            _pins[i] = new PinRow
            {
                Row = grid,
                Fill = scale,
                FillBar = fill,
                Amps = Cell(grid, 2, 58, 13.5, "TextPrimary", bold: true),
                Volts = Cell(grid, 3, 54, 11.5, "TextSecondary"),
                Watts = Cell(grid, 4, 58, 11.5, "TextSecondary"),
                Peak = Cell(grid, 5, 62, 11.5, "TextMuted"),
            };
            _pins[i].Watts.IsVisible = _pins[i].Peak.IsVisible = false;
            PinRows.Children.Add(grid);
        }
    }

    /// <summary>A 6 px rounded track with a fill that scales from the left.</summary>
    private static (Grid Track, Border Fill, ScaleTransform Scale) BuildBar(Thickness margin)
    {
        var scale = new ScaleTransform(0, 1);
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            RenderTransform = scale,
        };
        var trackBg = new Border { CornerRadius = new CornerRadius(3) };
        Ref(trackBg, Border.BackgroundProperty, "TrackBrush");
        var track = new Grid { Height = 6, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(trackBg);
        track.Children.Add(fill);
        return (track, fill, scale);
    }

    private void BuildTempCells()
    {
        for (int i = 0; i < TempLabels.Length; i++)
        {
            var cell = new StackPanel();
            var label = new TextBlock { Text = TempLabels[i], FontSize = 10 };
            Ref(label, TextBlock.ForegroundProperty, "TextMuted");
            var value = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold };
            var max = new TextBlock { FontSize = 10, IsVisible = false };
            Ref(max, TextBlock.ForegroundProperty, "TextMuted");
            cell.Children.Add(label);
            cell.Children.Add(value);
            cell.Children.Add(max);
            TempGrid.Children.Add(cell);
            _temps[i] = new TempCell { Value = value, Max = max };
        }
    }

    private void BuildFaultTable()
    {
        FaultTable.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        FaultTable.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (int r = 0; r < 3; r++) FaultTable.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        foreach (var f in Enum.GetValues<Fault>())
        {
            int i = (int)f;
            var cell = new Grid { Margin = new Thickness(0, 2, i % 2 == 0 ? 12 : 0, 2) };
            cell.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            cell.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            cell.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            _faultNames[i] = name;
            var state = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(state, 2);
            cell.Children.Add(dot);
            cell.Children.Add(name);
            cell.Children.Add(state);

            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            FaultTable.Children.Add(cell);
            _faultCells[i] = (dot, state);
        }
    }

    // ===== Helpers =====
    /// <summary>Binds a property to a theme resource so it follows theme changes (WPF's SetResourceReference).</summary>
    private static void Ref(AvaloniaObject e, AvaloniaProperty p, string key) => e[!p] = new DynamicResourceExtension(key);

    /// <summary>Bars move without animation: an animation would redraw the whole window at 60 fps for its duration.</summary>
    private static void Animate(ScaleTransform t, double to)
    {
        if (Math.Abs(t.ScaleX - to) < 0.002) return;
        t.ScaleX = to;
    }

    private static Border Chip(string text, string fgKey, string accentKey, bool filled = false)
    {
        var accent = AppTheme.ColorOf(accentKey, Colors.Gray);
        var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold };
        if (filled) label.Foreground = Brushes.White;
        else Ref(label, TextBlock.ForegroundProperty, fgKey);
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 4),
            Margin = new Thickness(0, 0, 6, 4),
            Background = new ImmutableSolidColorBrush(Color.FromArgb(filled ? (byte)0xE0 : (byte)0x22, accent.R, accent.G, accent.B)),
            Child = label,
        };
    }
}
