using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AmpTray.Device;

namespace AmpTray;

public partial class WidgetWindow : Window
{
    // 12V-2x6 terminals are rated 9.2 A each; warn a bit before that.
    public const double PinWarnA = 8.0;
    public const double PinCritA = 9.2;
    public const double TempWarnC = 65;
    public const double TempCritC = 80;

    private const double CompactWidth = 292;
    private const double ExpandedWidth = 440;
    private static readonly string[] TempLabels = { "IN", "OUT", "TS1", "TS2" };

    private static readonly string[] PinHeaderKeys = { "AMPS", "VOLTS", "WATTS", "PEAK" };

    private readonly PinRow[] _pins = new PinRow[SensorSnapshot.PinCount];
    private readonly TempCell[] _temps = new TempCell[4];
    private readonly (Ellipse Dot, TextBlock State)[] _faultCells = new (Ellipse, TextBlock)[6];
    private readonly TextBlock[] _faultNames = new TextBlock[6];
    private readonly TextBlock[] _pinHeaderCells = new TextBlock[PinHeaderKeys.Length];
    private bool _expanded, _topmost = true, _clickThrough, _opaqueOnHover = true;
    private string? _pendingMonitor;
    private double _baseOpacity = 1;
    private SensorSnapshot? _last;
    private SessionStats? _stats;
    private string _port = "";

    public event Action? MenuRequested;
    public event Action? Moved;
    public event Action? ExpandToggleRequested;
    public event Action? SettingsRequested;
    public event Action? AcknowledgeRequested;
    public event Action? ResetStatsRequested;
    public event Action? ShutdownCancelRequested;
    public event Action? UpdateDownloadRequested;
    public event Action? UpdateDismissed;

    private UpdateInfo? _update;

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

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ExpandToggleRequested?.Invoke();
                return;
            }
            if (e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
            Moved?.Invoke();
        };
        MouseRightButtonUp += (_, _) => MenuRequested?.Invoke();
        // Hover buttons take no header space while hidden, so badges like ARMED aren't clipped.
        MouseEnter += (_, _) =>
        {
            HeaderButtons.Visibility = Visibility.Visible;
            Fade(HeaderButtons, 1);
            if (_opaqueOnHover) AnimateWindowOpacity(1);
        };
        MouseLeave += (_, _) =>
        {
            HeaderButtons.Visibility = Visibility.Collapsed;
            HeaderButtons.Opacity = 0;
            AnimateWindowOpacity(_baseOpacity);
        };

        ExpandButton.Click += (_, _) => ExpandToggleRequested?.Invoke();
        SettingsButton.Click += (_, _) => SettingsRequested?.Invoke();
        AckButton.Click += (_, _) => AcknowledgeRequested?.Invoke();
        ResetStatsButton.Click += (_, _) => ResetStatsRequested?.Invoke();
        ShutdownCancelButton.Click += (_, _) => ShutdownCancelRequested?.Invoke();
        UpdateDownloadButton.Click += (_, _) => UpdateDownloadRequested?.Invoke();
        UpdateDismissButton.Click += (_, _) => { HideUpdate(); UpdateDismissed?.Invoke(); };
        SizeChanged += KeepRightEdgeAnchored;
    }

    /// <summary>
    /// When the widget sits on the right half of its monitor, grow/shrink leftwards so expanding
    /// doesn't push it off screen.
    /// </summary>
    private void KeepRightEdgeAnchored(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || e.PreviousSize.Width <= 0) return;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var toDevice = source.CompositionTarget.TransformToDevice;

        double oldCenter = Left + e.PreviousSize.Width / 2;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)(oldCenter * toDevice.M11), (int)((Top + 20) * toDevice.M22)));
        double screenCenter = (screen.WorkingArea.Left + screen.WorkingArea.Width / 2.0) / toDevice.M11;

        if (oldCenter > screenCenter)
        {
            Left -= e.NewSize.Width - e.PreviousSize.Width;
            Moved?.Invoke();
        }
    }

    // ===== Monitors =====

    /// <summary>Device name of the monitor the widget is on right now (e.g. \\.\DISPLAY2), or null before it is shown.</summary>
    public string? CurrentMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return null;
        return System.Windows.Forms.Screen.FromHandle(hwnd).DeviceName;
    }

    /// <summary>
    /// Puts the widget on the given monitor if it is not already there: top-right corner of its work area,
    /// positioned in physical pixels so monitors with different scaling are handled correctly.
    /// </summary>
    public void MoveToMonitor(string deviceName)
    {
        var target = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == deviceName);
        if (target == null) return; // unplugged: keep the current position
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsLoaded)
        {
            // Called before the window exists (startup): do it once it has been laid out.
            if (_pendingMonitor == null) Loaded += (_, _) => { var m = _pendingMonitor; _pendingMonitor = null; if (m != null) MoveToMonitor(m); };
            _pendingMonitor = deviceName;
            return;
        }
        if (System.Windows.Forms.Screen.FromHandle(hwnd).DeviceName == deviceName) return;

        var source = PresentationSource.FromVisual(this);
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        int widthPx = (int)Math.Round(ActualWidth * scale);
        var area = target.WorkingArea;
        Native.SetWindowPos(hwnd, IntPtr.Zero, area.Right - widthPx - 12, area.Top + 12, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Moved?.Invoke();
    }

    // ===== Window behaviour =====
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyExStyle();
    }

    /// <summary>
    /// On top: stays above everything and never steals focus.
    /// Not on top: an ordinary window that comes forward when clicked and drops behind others.
    /// </summary>
    public void SetTopmost(bool on)
    {
        _topmost = on;
        Topmost = on;
        ApplyExStyle();
    }

    public void SetClickThrough(bool on)
    {
        _clickThrough = on;
        ApplyExStyle();
    }

    /// <summary>Resting opacity (down to almost invisible) and whether hovering brings the widget back to full.</summary>
    public void SetOpacity(double value, bool opaqueOnHover)
    {
        _baseOpacity = value;
        _opaqueOnHover = opaqueOnHover;
        AnimateWindowOpacity(opaqueOnHover && IsMouseOver ? 1 : value);
    }

    // Window.Opacity is always driven by an animation here; a plain assignment would be overridden by it.
    private void AnimateWindowOpacity(double to) =>
        BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)));

    /// <summary>Raises the widget above other windows once, without pinning it there.</summary>
    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (_topmost) return;
        Topmost = true;
        Topmost = false;
    }

    private void ApplyExStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW;
        style = _clickThrough ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT;
        style = _topmost ? style | Native.WS_EX_NOACTIVATE : style & ~Native.WS_EX_NOACTIVATE;
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, style);
    }

    // ===== Configuration =====
    /// <summary>Assigns every fixed text in the current language and re-renders the live values.</summary>
    public void ApplyLanguage()
    {
        SettingsButton.ToolTip = Loc.T("Settings");
        ArmedText.Text = Loc.T("ARMED");
        AckButton.Content = Loc.T("Acknowledge");
        AckButton.ToolTip = Loc.T("Stop the flashing and alarm until a new fault occurs");
        ShutdownCancelButton.Content = Loc.T("Cancel");
        ShutdownCancelButton.ToolTip = Loc.T("Don't shut down for this condition until it clears");
        WiresLabel.Text = Loc.T("WIRES");
        TempsLabel.Text = Loc.T("TEMPERATURES");
        GpuLabel.Text = Loc.T("GPU");
        PowerLabel.Text = Loc.T("POWER");
        FaultsLabel.Text = Loc.T("FAULTS");
        UpdateDownloadButton.Content = Loc.T("Download");
        UpdateDismissButton.ToolTip = Loc.T("Dismiss until next launch");
        if (_update != null) ShowUpdate(_update);
        ResetStatsButton.Content = Loc.T("Reset");
        ResetStatsButton.ToolTip = Loc.T("Reset session min/max/energy (widget only)");
        GpuNone.Text = Loc.T("No NVIDIA GPU detected");
        GpuPowerPath.ToolTip = Loc.T("Driver power is a ~1 s average; the WireView reading is near-instant, so small gaps are normal.");
        for (int i = 0; i < PinHeaderKeys.Length; i++) _pinHeaderCells[i].Text = Loc.T(PinHeaderKeys[i]);
        foreach (var f in Enum.GetValues<Fault>()) _faultNames[(int)f].Text = SensorSnapshot.Describe(f);
        ApplyGpuLanguage();

        SetExpanded(_expanded); // also re-renders a live sample
        if (_last == null) ShowDisconnected(null);
    }

    public void SetPanels(bool wires, bool temps, bool graph, bool faults, bool gpu)
    {
        WiresSection.Visibility = Vis(wires);
        TempsSection.Visibility = Vis(temps);
        GraphSection.Visibility = Vis(graph);
        FaultsSection.Visibility = Vis(faults);
        GpuSection.Visibility = Vis(gpu);
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        Panel.Width = expanded ? ExpandedWidth : CompactWidth;
        ExpandButton.Content = expanded ? "" : "";
        ExpandButton.ToolTip = expanded ? Loc.T("Compact view (double-click)") : Loc.T("Expanded view (double-click)");

        PinHeader.Visibility = Vis(expanded);
        foreach (var p in _pins)
        {
            p.Watts.Visibility = Vis(expanded);
            p.Peak.Visibility = Vis(expanded);
        }
        foreach (var t in _temps) t.Max.Visibility = Vis(expanded);
        Graph.Height = expanded ? 130 : 64;
        SessionRow.Visibility = Vis(expanded);
        FaultPanel.Visibility = Vis(!expanded);
        FaultTable.Visibility = Vis(expanded);
        FaultEvents.Visibility = Vis(expanded);
        SetGpuExpanded(expanded);

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
        RecBadge.Visibility = Vis(on);
        RecBadge.ToolTip = tooltip;
    }

    /// <summary>Re-applies theme-derived values that are not resource references.</summary>
    public void RefreshTheme()
    {
        Graph.InvalidateVisual();
        if (_last != null && _stats != null) ShowSample(_last, _port, _stats);
        else ShowDisconnected(null);
        if (_lastGpu != null && _gpuStats != null) ShowGpu(_lastGpu, _gpuStats);
    }

    // ===== Auto shutdown =====
    public void SetShutdownArmed(bool armed, string? rules)
    {
        ArmedBadge.Visibility = Vis(armed);
        ArmedBadge.ToolTip = rules == null ? null : Loc.T("Auto shutdown armed:") + "\n" + rules;
    }

    /// <summary>Shows the countdown banner for the rule closest to firing, or hides it.</summary>
    public void ShowShutdownPending(PendingRule? next, int more)
    {
        if (next == null)
        {
            ShutdownBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var r = next.Remaining;
        string when = r <= TimeSpan.Zero ? Loc.T("now") : Loc.F("in {0}", $"{(int)r.TotalMinutes}:{r.Seconds:00}");
        ShutdownText.Text = Loc.F("PC shuts down {0}: {1}", when, next.Condition) + (more > 0 ? " " + Loc.F("(+{0} more)", more) : "");
        ShutdownBanner.Visibility = Visibility.Visible;
    }

    // ===== Updates =====
    public void ShowUpdate(UpdateInfo info)
    {
        _update = info;
        UpdateText.Text = Loc.F("AmpTray {0} is available", info.Tag) + (info.Name != info.Tag ? $": {info.Name}" : "");
        UpdateBanner.Visibility = Visibility.Visible;
    }

    public void HideUpdate()
    {
        _update = null;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    // ===== Alerts =====
    public void ShowAlert(string message, bool flash)
    {
        AlertText.Text = "⚠  " + message;
        AlertBanner.Visibility = Visibility.Visible;
        if (flash)
        {
            Panel.SetResourceReference(Border.BorderBrushProperty, "CritBrush");
            FlashLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.32, TimeSpan.FromMilliseconds(450))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
    }

    public void HideAlert()
    {
        AlertBanner.Visibility = Visibility.Collapsed;
        Panel.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        FlashLayer.BeginAnimation(OpacityProperty, null);
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
            p.Row.ToolTip = mean > 0.05 ? Loc.F("Wire {0}: {1}% vs average", i + 1, ((a - mean) / mean * 100).ToString("+0;-0;0")) : null;
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
        string span = PowerGraph.FormatSpan(Graph.WindowSeconds);
        GraphStatsText.Text = _expanded
            ? Loc.F("last {0} · avg {1} W · max {2} W", span, window.Average(p => p.W).ToString("0"), window.Max(p => p.W).ToString("0"))
            : Loc.F("last {0} · max {1} W", span, window.Max(p => p.W).ToString("0"));
        SessionText.Text = Loc.F("Since {0} · peak {1} W · avg {2} W · {3}", stats.Since.ToString("HH:mm"), stats.PeakPowerW.ToString("0"), stats.AvgPowerW.ToString("0"), FormatEnergy(stats.EnergyWh));
    }

    private void UpdateFaults(SensorSnapshot s, SessionStats stats)
    {
        var active = SensorSnapshot.Decode(s.FaultStatus).ToList();
        var loggedOnly = SensorSnapshot.Decode((ushort)(s.FaultLog & ~s.FaultStatus)).ToList();

        // Compact: chips.
        FaultPanel.Children.Clear();
        foreach (var f in active)
            FaultPanel.Children.Add(Chip(SensorSnapshot.Describe(f), "CritBrush", "CritBrush", filled: true));
        foreach (var f in loggedOnly)
        {
            var chip = Chip(SensorSnapshot.Describe(f) + " · " + Loc.T("logged"), "WarnBrush", "WarnBrush");
            chip.ToolTip = Loc.T("Occurred earlier and is latched in the device log. Clear it in the official WireView app.");
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
        FaultEvents.Children.Add(new TextBlock { Text = Loc.T("RECENT EVENTS"), Style = (Style)FindResource("SectionLabel") });
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
            var time = new System.Windows.Documents.Run($"{ev.Time:HH:mm:ss}   ");
            time.SetResourceReference(TextElement.ForegroundProperty, "TextMuted");
            var what = new System.Windows.Documents.Run($"{SensorSnapshot.Describe(ev.Fault)} {(ev.Active ? Loc.T("raised") : Loc.T("cleared"))}");
            what.SetResourceReference(TextElement.ForegroundProperty, ev.Active ? "CritBrush" : "TextSecondary");
            line.Inlines.Add(time);
            line.Inlines.Add(what);
            FaultEvents.Children.Add(line);
        }
    }

    public static string OverallLevel(SensorSnapshot s)
    {
        string wire = WireLevel(s.MaxPinCurrentA, WireLimitA(s));
        if (s.HasActiveFault || wire == "CritBrush" || s.MaxTempC >= TempCritC) return "CritBrush";
        if (s.HasLoggedFault || wire == "WarnBrush" || s.MaxTempC >= TempWarnC) return "WarnBrush";
        return "OkBrush";
    }

    /// <summary>
    /// Current one wire may carry: the cable rating spread over six 12 V wires
    /// (600 W → 8.33 A, 450 W → 6.25 A, 300 W → 4.17 A, 150 W → 2.08 A), never above the 9.2 A pin rating.
    /// Falls back to the pin rating when the device reports no cable rating.
    /// </summary>
    public static double WireLimitA(SensorSnapshot s) =>
        s.CableRatingW > 0 ? Math.Min(PinCritA, s.CableRatingW / 12.0 / SensorSnapshot.PinCount) : PinCritA;

    /// <summary>Amber from the wire's cable allowance (or 8 A, whichever is lower), red at the 9.2 A pin rating.</summary>
    public static string WireLevel(double amps, double limitA) =>
        amps >= PinCritA ? "CritBrush" : amps >= Math.Min(PinWarnA, limitA) ? "WarnBrush" : "OkBrush";

    private static string? TempLevel(double c) => c >= TempCritC ? "CritBrush" : c >= TempWarnC ? "WarnBrush" : null;

    private static string FormatEnergy(double wh) => wh >= 1000 ? $"{wh / 1000:0.00} kWh" : $"{wh:0.0} Wh";

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
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return g;
    }

    private static TextBlock Cell(Grid g, int col, double minWidth, double size, string fgKey, bool bold = false)
    {
        var t = new TextBlock
        {
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
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

            var scale = new ScaleTransform(0, 1);
            var fill = new Border
            {
                CornerRadius = new CornerRadius(3),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = scale,
            };
            var trackBg = new Border { CornerRadius = new CornerRadius(3) };
            Ref(trackBg, Border.BackgroundProperty, "TrackBrush");
            var track = new Grid { Height = 6, Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(trackBg);
            track.Children.Add(fill);
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
            _pins[i].Watts.Visibility = _pins[i].Peak.Visibility = Visibility.Collapsed;
            PinRows.Children.Add(grid);
        }
    }

    private void BuildTempCells()
    {
        for (int i = 0; i < TempLabels.Length; i++)
        {
            var cell = new StackPanel();
            var label = new TextBlock { Text = TempLabels[i], FontSize = 10 };
            Ref(label, TextBlock.ForegroundProperty, "TextMuted");
            var value = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                FontFamily = (FontFamily)FindResource("DisplayFont"),
            };
            var max = new TextBlock { FontSize = 10, Visibility = Visibility.Collapsed };
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
        FaultTable.ColumnDefinitions.Add(new ColumnDefinition());
        FaultTable.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 3; r++) FaultTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        foreach (var f in Enum.GetValues<Fault>())
        {
            int i = (int)f;
            var cell = new Grid { Margin = new Thickness(0, 2, i % 2 == 0 ? 12 : 0, 2) };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cell.ColumnDefinitions.Add(new ColumnDefinition());
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            _faultNames[i] = name;
            var state = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
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
    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static void Ref(FrameworkElement e, DependencyProperty p, string key) => e.SetResourceReference(p, key);

    private static void Fade(UIElement e, double to) =>
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(150)));

    private static void Animate(ScaleTransform t, double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        t.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
    }

    private static Border Chip(string text, string fgKey, string accentKey, bool filled = false)
    {
        var accent = ((SolidColorBrush)Application.Current.Resources[accentKey]).Color;
        var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        if (filled) label.Foreground = Brushes.White;
        else Ref(label, TextBlock.ForegroundProperty, fgKey);
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 4),
            Margin = new Thickness(0, 0, 6, 4),
            Background = new SolidColorBrush(Color.FromArgb(filled ? (byte)0xE0 : (byte)0x22, accent.R, accent.G, accent.B)),
            Child = label,
        };
    }
}
