using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AmpTray.Logging;
using Forms = System.Windows.Forms;

namespace AmpTray;

/// <summary>What the settings window needs from the app. Every change is applied live.</summary>
public interface ISettingsHost
{
    void ApplySettings();
    void TestAlert();
    void TestShutdown();
    void PreviewOpacity(double opacity);
    bool ServiceInstalled { get; }
    bool ServiceRunning { get; }
    bool SetServiceInstalled(bool on);
    bool StartupEnabled { get; set; }
    string LogStatus { get; }
    /// <summary>A release found by a manual check: show it on the widget and in the tray menu as well.</summary>
    void ShowUpdate(UpdateInfo info);
}

public partial class SettingsWindow : Window
{
    // Page keys are English; the navigation shows them translated.
    private static readonly string[] PageKeys = { "Panels", "Theme", "Alerts", "Logging", "Auto shutdown", "Window", "Logs", "About" };

    // Result of the last manual update check, kept for the life of the process so reopening Settings shows it.
    private static UpdateCheck? _lastCheck;
    private static DateTime? _lastCheckedAt;
    private static bool _checking;

    private readonly Settings _s;
    private readonly ISettingsHost _host;
    private readonly Dictionary<string, Action> _pages;
    private readonly Dictionary<string, RadioButton> _nav = new();
    private readonly List<Action> _pendingCommits = new();
    private string _current = "Panels";
    private bool _serviceChangeFailed;

    public SettingsWindow(Settings settings, ISettingsHost host, string? page = null)
    {
        InitializeComponent();
        _s = settings;
        _host = host;

        _pages = new()
        {
            ["Panels"] = BuildPanelsPage,
            ["Theme"] = BuildThemePage,
            ["Alerts"] = BuildAlertsPage,
            ["Logging"] = BuildLoggingPage,
            ["Auto shutdown"] = BuildShutdownPage,
            ["Window"] = BuildWindowPage,
            ["Logs"] = BuildLogsPage,
            ["About"] = BuildAboutPage,
        };
        _current = PageKeys.FirstOrDefault(k => k.Replace(" ", "").Equals(page?.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) ?? _current;

        foreach (var key in PageKeys)
        {
            string k = key;
            var item = new RadioButton { GroupName = "nav", Style = Res("NavItem"), IsChecked = k == _current };
            item.Checked += (_, _) => ShowPage(k);
            Nav.Children.Add(item);
            _nav[k] = item;
        }

        TitleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        CloseButton.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // A number typed but not yet confirmed with Enter/Tab must not be lost on Close or Escape.
        Closing += (_, _) => CommitPending();

        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => { Loc.Changed -= OnLanguageChanged; LeaveLogsPage(); };
        SourceInitialized += (_, _) => Native.RoundCorners(new System.Windows.Interop.WindowInteropHelper(this).Handle);

        ApplyLanguage();
        ShowPage(_current);
    }

    private void OnLanguageChanged()
    {
        ApplyLanguage();
        ShowPage(_current);
    }

    private void ApplyLanguage()
    {
        TitleText.Text = Loc.T("Settings");
        CloseButton.ToolTip = Loc.T("Close");
        foreach (var (key, item) in _nav) item.Content = Loc.T(key);
    }

    private void ShowPage(string name)
    {
        CommitPending();
        LeaveLogsPage();
        _current = name;
        Page.Children.Clear();
        _pages[name]();
    }

    private void CommitPending()
    {
        foreach (var commit in _pendingCommits.ToList()) commit();
        _pendingCommits.Clear();
    }

    private void Changed()
    {
        _host.ApplySettings();
    }

    // ===== Pages =====
    private void BuildPanelsPage()
    {
        Header(Loc.T("Panels"), Loc.T("Choose what the compact view shows. The expanded view always shows every panel, and the header with total power is always visible."));
        Toggle(Loc.T("Wires"), Loc.T("Current and voltage on each of the six wires"), () => _s.ShowWires, v => _s.ShowWires = v);
        Toggle(Loc.T("Temperatures"), Loc.T("Onboard in/out and the two external probes"), () => _s.ShowTemps, v => _s.ShowTemps = v);
        Toggle(Loc.T("GPU"), Loc.T("NVIDIA GPU load, power and VRAM. GPU temperatures always show at the top when an NVIDIA GPU is present."),
            () => _s.ShowGpu, v => _s.ShowGpu = v);
        Toggle(Loc.T("Power graph"), Loc.T("Realtime line graph of total power draw"), () => _s.ShowGraph, v => _s.ShowGraph = v);
        Toggle(Loc.T("Faults"), Loc.T("Active and logged device faults"), () => _s.ShowFaults, v => _s.ShowFaults = v);

        Header(Loc.T("Top bar"), Loc.T("Shown in both views when an NVIDIA GPU is present."));
        Toggle(Loc.T("GPU temperature at the top"), Loc.T("Gauge with the GPU (and memory) temperature next to the GPU name"), () => _s.ShowGpuTemp, v => _s.ShowGpuTemp = v);
        Toggle(Loc.T("FPS counter at the top"), Loc.T("Frames per second of the foreground game, measured by the background service"), () => _s.ShowFps, v => _s.ShowFps = v);
        var screens = Forms.Screen.AllScreens;
        if (screens.Length > 1)
        {
            var options = new List<(string, string)> { (Loc.T("Foreground app"), "") };
            for (int i = 0; i < screens.Length; i++)
            {
                var sc = screens[i];
                options.Add(($"{i + 1}: {sc.Bounds.Width}×{sc.Bounds.Height}" + (sc.Primary ? " (" + Loc.T("primary") + ")" : ""), sc.DeviceName));
            }
            Segmented(Loc.T("FPS of the game on"), options.ToArray(), () => _s.Monitor, v => _s.Monitor = v);
            Note(Loc.T("With a monitor selected, the counter follows the app with the largest window on that screen, whatever is in the foreground."));
        }

        Header(Loc.T("View"), null);
        Segmented(Loc.T("Layout"), new[] { (Loc.T("Compact"), false), (Loc.T("Expanded"), true) }, () => _s.Expanded, v => _s.Expanded = v);
        Note(Loc.T("Expanded adds per-wire watts and peaks, temperature maxima, GPU details, a taller graph with session stats, and a full fault table with recent events. Double-click the widget to switch.") + " " + Loc.T("The widget always starts compact."));

        Header(Loc.T("Power graph"), null);
        Segmented(Loc.T("Time window"), new[] { (Loc.F("{0} min", 1), 60), (Loc.F("{0} min", 5), 300), (Loc.F("{0} min", 15), 900), (Loc.F("{0} h", 1), 3600) },
            () => _s.GraphWindowSeconds, v => _s.GraphWindowSeconds = v);
    }

    private void BuildThemePage()
    {
        Header(Loc.T("Theme"), Loc.T("Pick a preset, or change any colour to make it your own."));

        var cards = new WrapPanel();
        foreach (var (name, colors) in Theme.Presets)
            cards.Children.Add(ThemeCard(name, colors));
        cards.Children.Add(ThemeCard(Theme.Custom, _s.CustomColors));
        Page.Children.Add(cards);

        Header(Loc.T("Colours"), _s.ThemeName == Theme.Custom ? null : Loc.F("Editing a colour switches from “{0}” to Custom.", _s.ThemeName));
        var current = Theme.Resolve(_s);
        foreach (var (label, get, set) in ThemeColors.Fields)
            ColorRow(Loc.T(label), get(current), hex =>
            {
                if (_s.ThemeName != Theme.Custom)
                {
                    _s.CustomColors = Theme.Resolve(_s);
                    _s.ThemeName = Theme.Custom;
                }
                set(_s.CustomColors, hex);
                Changed();
                ShowPage("Theme");
            });
    }

    private void BuildAlertsPage()
    {
        Header(Loc.T("When the device reports a fault"),
            Loc.T("Alerts run until you press Acknowledge on the widget (or in the tray menu), or the fault clears. A different new fault alerts again."));
        Toggle(Loc.T("Flash the screen"), Loc.T("Pulsing red frame and banner on every monitor (click-through)"), () => _s.FlashScreen, v => _s.FlashScreen = v);
        Toggle(Loc.T("Flash the widget"), Loc.T("Widget pulses red and shows the fault banner"), () => _s.FlashWidget, v => _s.FlashWidget = v);
        Toggle(Loc.T("Sound alarm"), Loc.T("Looping two-tone alarm"), () => _s.SoundAlarm, v => _s.SoundAlarm = v);
        Segmented(Loc.T("Alarm volume"), new[] { (Loc.T("Low"), 30), (Loc.T("Medium"), 60), (Loc.T("High"), 100) }, () => _s.AlarmVolume, v => _s.AlarmVolume = v);
        Toggle(Loc.T("Windows notification"), Loc.T("Toast from the tray icon naming the fault"), () => _s.Notify, v => _s.Notify = v);
        Toggle(Loc.T("Bring widget to front"), Loc.T("Show the widget if hidden and raise it above other windows"), () => _s.BringToFront, v => _s.BringToFront = v);

        var test = new Button { Content = Loc.T("Test alert (5 s)"), Style = Res("PillButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(14, 6, 14, 7) };
        test.Click += (_, _) => _host.TestAlert();
        Page.Children.Add(test);
        Note(Loc.T("Uses your current settings. Nothing is sent to the device."));
    }

    private void BuildLoggingPage()
    {
        Header(Loc.T("CSV logging"),
            Loc.T("Writes one file per day (wireview_yyyy-MM-dd.csv) with every wire's volts, amps and watts, all temperatures, the fault registers and GPU stats."));
        Toggle(Loc.T("Log data"), null, () => _s.LogEnabled, v => _s.LogEnabled = v);
        Segmented(Loc.T("Interval"), new[] { (Loc.T("Every sample"), 0), (Loc.F("{0} s", 5), 5), (Loc.F("{0} s", 10), 10), (Loc.F("{0} min", 1), 60) },
            () => _s.LogIntervalSeconds, v => _s.LogIntervalSeconds = v);

        Label(Loc.T("Folder"));
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Text = _s.LogFolder, Style = Res("Field") };
        box.LostFocus += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text) && box.Text != _s.LogFolder) { _s.LogFolder = box.Text.Trim(); Changed(); }
        };
        var browse = new Button { Content = Loc.T("Browse…"), Style = Res("PillButton"), Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            using var dlg = new Forms.FolderBrowserDialog { SelectedPath = _s.LogFolder, UseDescriptionForTitle = true, Description = Loc.T("Log folder") };
            if (dlg.ShowDialog() == Forms.DialogResult.OK) { _s.LogFolder = dlg.SelectedPath; box.Text = dlg.SelectedPath; Changed(); }
        };
        var open = new Button { Content = Loc.T("Open"), Style = Res("PillButton"), Margin = new Thickness(6, 0, 0, 0) };
        open.Click += (_, _) => OpenFolder(_s.LogFolder);
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(open, 2);
        row.Children.Add(box);
        row.Children.Add(browse);
        row.Children.Add(open);
        Page.Children.Add(row);
        Note(_host.LogStatus);
    }

    private void BuildShutdownPage()
    {
        var c = _s.Shutdown;
        Header(Loc.T("Automatic PC shutdown"),
            Loc.T("Forces Windows to shut down (open apps are closed without saving) when a rule below holds for its duration.") + " " +
            Loc.F("Every rule also needs at least {0} s and {1} readings in a row, so one bad reading can't trigger it, and time the PC spends asleep never counts.",
                ShutdownMonitor.MinGrace.TotalSeconds.ToString("0"), ShutdownMonitor.MinConsecutive));
        Toggle(Loc.T("Armed"), Loc.T("Master switch, also in the tray menu. The widget shows ARMED while it's on."), () => c.Enabled, v => c.Enabled = v);
        Segmented(Loc.T("Before shutting down"),
            new[] { (Loc.T("Immediately"), 0), (Loc.F("{0} s countdown", 10), 10), (Loc.F("{0} s countdown", 30), 30), (Loc.F("{0} s countdown", 60), 60) },
            () => c.CountdownSeconds, v => c.CountdownSeconds = v);
        Note(Loc.T("A countdown shows a full-size Cancel button. While a rule is still timing, the widget shows an amber “PC shuts down in…” banner with Cancel, and you get a Windows notification."));

        // Temperature
        var temp = RuleCard(Loc.T("Temperature"), () => c.TempRule, v => c.TempRule = v);
        var sensors = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        string group = Guid.NewGuid().ToString();
        for (int i = 0; i < ShutdownSettings.SensorKeys.Length; i++)
        {
            int idx = i;
            var rb = new RadioButton { Content = ShutdownSettings.SensorName(i), GroupName = group, Style = Res("Segment"), IsChecked = c.TempSensor == i };
            rb.Click += (_, _) => { c.TempSensor = idx; Changed(); };
            sensors.Children.Add(rb);
        }
        temp.Children.Add(sensors);
        temp.Children.Add(Sentence(Loc.T("at or above"), Number(c.TempAboveC, -20, 150, v => c.TempAboveC = v), Loc.T("°C for"), Minutes(() => c.TempMinutes, v => c.TempMinutes = v), Loc.T("min")));

        // Wires
        var wires = RuleCard(Loc.T("Wire current"), () => c.WireRule, v => c.WireRule = v);
        wires.Children.Add(Sentence(Number(c.WireCount, 1, 6, v => c.WireCount = (int)Math.Round(v), "0", 34), Loc.T("or more wires ≥"),
            Number(c.WireAboveA, 0.5, 30, v => c.WireAboveA = v), Loc.T("A for"), Minutes(() => c.WireMinutes, v => c.WireMinutes = v), Loc.T("min")));
        RuleNote(wires, Loc.T("Each 12V-2x6 pin is rated 9.2 A."));

        // Device faults
        var faults = RuleCard(Loc.T("Device fault"), () => c.FaultRule, v => c.FaultRule = v);
        var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var f in Enum.GetValues<Device.Fault>())
        {
            ushort bit = (ushort)(1 << (int)f);
            var chip = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = Device.SensorSnapshot.Describe(f),
                Style = Res("Segment"),
                IsChecked = (c.FaultMask & bit) != 0,
            };
            chip.Click += (_, _) =>
            {
                c.FaultMask = chip.IsChecked == true ? (ushort)(c.FaultMask | bit) : (ushort)(c.FaultMask & ~bit);
                Changed();
            };
            chips.Children.Add(chip);
        }
        faults.Children.Add(chips);
        faults.Children.Add(Sentence(Loc.T("Any selected fault active for"), Minutes(() => c.FaultMinutes, v => c.FaultMinutes = v), Loc.T("min")));
        RuleNote(faults, Loc.T("Only active faults count, not ones latched in the device log."));

        // Total power
        var power = RuleCard(Loc.T("Total power"), () => c.PowerRule, v => c.PowerRule = v);
        power.Children.Add(Sentence(Loc.T("at or above"), Number(c.PowerAboveW, 10, 2000, v => c.PowerAboveW = v, "0"), Loc.T("W for"),
            Minutes(() => c.PowerMinutes, v => c.PowerMinutes = v), Loc.T("min")));

        // GPU temperature (NVIDIA)
        var gpu = RuleCard(Loc.T("GPU temperature"), () => c.GpuTempRule, v => c.GpuTempRule = v);
        gpu.Children.Add(Sentence(Loc.T("at or above"), Number(c.GpuTempAboveC, 40, 120, v => c.GpuTempAboveC = v, "0"), Loc.T("°C for"),
            Minutes(() => c.GpuTempMinutes, v => c.GpuTempMinutes = v), Loc.T("min")));
        RuleNote(gpu, Loc.T("NVIDIA only. The driver throttles the GPU itself near 90 °C; this is a last resort above that."));

        var test = new Button { Content = Loc.T("Dry run test"), Style = Res("PillButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(14, 6, 14, 7) };
        test.Click += (_, _) => _host.TestShutdown();
        Page.Children.Add(test);
        Note(Loc.T("Shows what would happen without shutting down.") + " " +
             Loc.F("Durations accept decimals (0.5 = 30 s); anything under {0} s is raised to {0} s. A dip below the threshold shorter than {1} s doesn't reset a timer; a device that stops reporting does.",
                 ShutdownMonitor.MinGrace.TotalSeconds.ToString("0"), ShutdownMonitor.ClearGrace.TotalSeconds.ToString("0")) + " " +
             Loc.F("Every countdown, cancel and shutdown is written to {0}.", PcShutdown.LogFile(_s.LogFolder)));
    }

    private void BuildWindowPage()
    {
        Header(Loc.T("Window"), null);
        Toggle(Loc.T("Always on top"), Loc.T("Off: behaves like a normal window and goes behind others when you click elsewhere"),
            () => _s.Topmost, v => _s.Topmost = v);
        Toggle(Loc.T("Lock (click-through)"), Loc.T("Clicks pass through the widget. Unlock from the tray menu."),
            () => _s.ClickThrough, v => _s.ClickThrough = v);
        OpacityRow();
        Toggle(Loc.T("Opaque on hover"), Loc.T("Fade back to full opacity while the mouse is over the widget"),
            () => _s.OpaqueOnHover, v => _s.OpaqueOnHover = v);
        Segmented(Loc.T("Refresh rate"), new[] { (Loc.F("{0} s", 0.5.ToString(CultureInfo.CurrentCulture)), 500), (Loc.F("{0} s", 1), 1000), (Loc.F("{0} s", 2), 2000) },
            () => _s.PollMs, v => _s.PollMs = v);

        var startup = new CheckBox { Style = Res("Switch"), IsChecked = _host.StartupEnabled, Content = Labelled(Loc.T("Start with Windows"), null) };
        startup.Click += (_, _) => { _host.StartupEnabled = startup.IsChecked == true; startup.IsChecked = _host.StartupEnabled; };
        Page.Children.Add(startup);

        Header(Loc.T("Background monitoring"), null);
        bool installed = _host.ServiceInstalled;
        var svc = new CheckBox
        {
            Style = Res("Switch"),
            IsChecked = installed,
            Content = Labelled(Loc.T("Run before anyone signs in (Windows service)"),
                Loc.T("Keeps CSV logging and the auto-shutdown rules running from boot, with nobody signed in. Turning this on or off asks for administrator approval.")),
        };
        svc.Click += (_, _) =>
        {
            bool want = svc.IsChecked == true;
            _serviceChangeFailed = !_host.SetServiceInstalled(want);
            ShowPage("Window");
        };
        Page.Children.Add(svc);
        Note(installed
            ? (_host.ServiceRunning ? Loc.T("Service installed and running.") : Loc.T("Service installed but not running."))
            : Loc.T("Service not installed."));
        if (_serviceChangeFailed)
        {
            _serviceChangeFailed = false;
            Note(Loc.T("The change was not applied: administrator approval was declined or the command failed."));
        }
        Note(Loc.T("The service follows the settings above (rules, logging, refresh rate) and picks up changes within a few seconds. While it runs, it is the one that shuts the PC down; the widget shows the countdown and can still cancel."));

        Toggle(Loc.T("Check for updates at startup"), Loc.T("Asks api.github.com for the latest release once per launch; nothing about this PC is sent."),
            () => _s.CheckUpdates, v => _s.CheckUpdates = v);

        Header(Loc.T("Language"), null);
        var languages = new List<(string, string)> { (Loc.T("System"), Loc.Auto) };
        languages.AddRange(Loc.Languages.Select(l => (l.Name, l.Code)));
        Segmented(Loc.T("Language"), languages.ToArray(), () => _s.Language, v => _s.Language = v);

        Header(Loc.T("Device access"), null);
        Note(Loc.T("Read-only. The widget only identifies the device and reads its sensors; it never changes device settings, clears faults or touches firmware. It shares the port with the official WireView app."));
    }

    // ===== Logs page =====
    // Filter choices survive leaving the page; the readers and timer live only while it is shown.
    private static LogLevel _logMinLevel = LogLevel.Info;
    private static string _logSource = "";
    private static string _logText = "";
    private static bool _logIncludeService = true;
    private const int MaxLogRows = 3000;

    private readonly List<LogEvent> _logEvents = new();
    private EventLogReader? _widgetLog, _serviceLog;
    private DispatcherTimer? _logTimer;
    private ListBox? _logList;
    private ScrollViewer? _logScroll;
    private TextBlock? _logCount;
    private WrapPanel? _logSources;
    private bool _logAtBottom = true;
    private Action? _logWrittenHandler;

    private sealed class LogRow
    {
        public string Time { get; init; } = "";
        public string Level { get; init; } = "";
        public string Source { get; init; } = "";
        public string Message { get; init; } = "";
        public string Tip { get; init; } = "";
        public Brush LevelBrush { get; init; } = Brushes.Gray;
    }

    private void BuildLogsPage()
    {
        Header(Loc.T("Event log"), Loc.T("What the widget and the background service have been doing. New events appear at the bottom and the list follows them while you are scrolled to the end."));

        // Level and text filters on one row.
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var levels = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        string group = Guid.NewGuid().ToString();
        foreach (var (text, level) in new[] { (Loc.T("All"), LogLevel.Debug), (Loc.T("Info"), LogLevel.Info), (Loc.T("Warnings"), LogLevel.Warning), (Loc.T("Errors"), LogLevel.Error) })
        {
            var rb = new RadioButton { Content = text, GroupName = group, Style = Res("Segment"), IsChecked = _logMinLevel == level };
            rb.Click += (_, _) => { _logMinLevel = level; RefreshLogList(); };
            levels.Children.Add(rb);
        }
        DockPanel.SetDock(levels, Dock.Left);
        row.Children.Add(levels);
        var search = new TextBox { Style = Res("Field"), Text = _logText, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 120, ToolTip = Loc.T("Search events") };
        search.TextChanged += (_, _) => { _logText = search.Text; RefreshLogList(); };
        row.Children.Add(search);
        Page.Children.Add(row);

        // Sources seen so far; rebuilt as new ones show up.
        _logSources = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        Page.Children.Add(_logSources);

        var service = new CheckBox { Style = Res("Switch"), IsChecked = _logIncludeService, Content = Labelled(Loc.T("Include service log"), null), Margin = new Thickness(0, 0, 0, 8) };
        service.Click += (_, _) => { _logIncludeService = service.IsChecked == true; ReloadLogs(); };
        Page.Children.Add(service);

        // The list itself: virtualised rows, newest last.
        _logList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemTemplate = LogRowTemplate(),
            ItemContainerStyle = LogItemStyle(),
            Height = LogListHeight(),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_logList, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_logList, true);
        VirtualizingPanel.SetVirtualizationMode(_logList, VirtualizationMode.Recycling);
        _logList.Loaded += (_, _) =>
        {
            _logScroll = FindScrollViewer(_logList);
            if (_logScroll == null) return;
            _logScroll.ScrollChanged += OnLogScrolled;
            _logScroll.ScrollToEnd();
        };
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = _logList };
        frame.SetResourceReference(Border.BorderBrushProperty, "PanelBorderBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        Page.Children.Add(frame);
        PageScroll.SizeChanged += OnPageScrollSized;

        // Footer: count and the two actions.
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var open = new Button { Content = Loc.T("Open log folder"), Style = Res("PillButton"), Padding = new Thickness(12, 4, 12, 5) };
        open.Click += (_, _) => Open(Settings.AppDataFolder);
        var clear = new Button { Content = Loc.T("Clear widget log"), Style = Res("PillButton"), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        clear.Click += (_, _) => { AppLog.Current.Clear(); AppLog.Info("app", "log-cleared", "Event log cleared from Settings"); ReloadLogs(); };
        buttons.Children.Add(open);
        buttons.Children.Add(clear);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        _logCount = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _logCount.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        footer.Children.Add(_logCount);
        Page.Children.Add(footer);

        ReloadLogs();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _logTimer.Tick += (_, _) => PollLogs();
        _logTimer.Start();
        _logWrittenHandler = () => Dispatcher.BeginInvoke(new Action(() => PollLogs()));
        AppLog.Current.Written += _logWrittenHandler;
    }

    private void LeaveLogsPage()
    {
        if (_logTimer == null && _logList == null) return;
        _logTimer?.Stop();
        _logTimer = null;
        if (_logWrittenHandler != null) AppLog.Current.Written -= _logWrittenHandler;
        _logWrittenHandler = null;
        if (_logScroll != null) _logScroll.ScrollChanged -= OnLogScrolled;
        PageScroll.SizeChanged -= OnPageScrollSized;
        _logScroll = null;
        _logList = null;
        _logCount = null;
        _logSources = null;
        _widgetLog = null;
        _serviceLog = null;
        _logEvents.Clear();
        _logAtBottom = true;
    }

    private double LogListHeight() => Math.Max(220, PageScroll.ViewportHeight - 236);

    private void OnPageScrollSized(object sender, SizeChangedEventArgs e)
    {
        if (_logList != null) _logList.Height = LogListHeight();
    }

    /// <summary>User scrolling decides whether the list keeps following new events; growth of the list does not.</summary>
    private void OnLogScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) return;
        var sv = (ScrollViewer)sender;
        _logAtBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 0.5;
    }

    private void ReloadLogs()
    {
        _widgetLog = new EventLogReader(AppLog.WidgetLogPath);
        _serviceLog = _logIncludeService ? new EventLogReader(AppLog.ServiceLogPath) : null;
        _logEvents.Clear();
        _logAtBottom = true;
        PollLogs(force: true);
    }

    private void PollLogs(bool force = false)
    {
        if (_widgetLog == null || _logList == null) return;
        bool restart = _widgetLog.Poll(out var fromWidget);
        var fromService = new List<LogEvent>();
        if (_serviceLog != null && _serviceLog.Poll(out fromService)) restart = true;
        if (restart)
        {
            // A trimmed or cleared file: read both from the start again.
            _widgetLog = new EventLogReader(AppLog.WidgetLogPath);
            _widgetLog.Poll(out fromWidget);
            if (_serviceLog != null) { _serviceLog = new EventLogReader(AppLog.ServiceLogPath); _serviceLog.Poll(out fromService); }
            _logEvents.Clear();
        }
        if (!force && !restart && fromWidget.Count == 0 && fromService.Count == 0) return;
        _logEvents.AddRange(fromWidget);
        _logEvents.AddRange(fromService);
        if (fromService.Count > 0 || restart) _logEvents.Sort((a, b) => a.Time.CompareTo(b.Time));
        RefreshSources();
        RefreshLogList();
    }

    private void RefreshSources()
    {
        if (_logSources == null) return;
        var names = _logEvents.Select(e => e.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        if (_logSources.Tag is int seen && seen == names.Count) return;
        _logSources.Tag = names.Count;
        _logSources.Children.Clear();
        string group = Guid.NewGuid().ToString();
        if (!string.IsNullOrEmpty(_logSource) && !names.Contains(_logSource, StringComparer.OrdinalIgnoreCase)) _logSource = "";
        foreach (var (text, value) in new[] { (Loc.T("All sources"), "") }.Concat(names.Select(n => (n, n))))
        {
            var rb = new RadioButton { Content = text, GroupName = group, Style = Res("Segment"), IsChecked = string.Equals(_logSource, value, StringComparison.OrdinalIgnoreCase) };
            rb.Click += (_, _) => { _logSource = value; RefreshLogList(); };
            _logSources.Children.Add(rb);
        }
    }

    private void RefreshLogList()
    {
        if (_logList == null || _logCount == null) return;
        var matching = _logEvents.Where(e => LogFilter.Matches(e, _logMinLevel, _logSource, _logText)).ToList();
        var shown = matching.Count > MaxLogRows ? matching.Skip(matching.Count - MaxLogRows).ToList() : matching;
        _logList.ItemsSource = shown.Select(ToRow).ToList();
        _logCount.Text = matching.Count == 0
            ? Loc.T("No events match the filter.")
            : Loc.F("{0} of {1} events", matching.Count, _logEvents.Count) + (shown.Count < matching.Count ? "  ·  " + Loc.F("Showing the newest {0}", shown.Count) : "");
        if (_logAtBottom && _logScroll != null) _logScroll.ScrollToEnd();
        else if (_logAtBottom && shown.Count > 0) _logList.ScrollIntoView(_logList.Items[^1]);
    }

    private static LogRow ToRow(LogEvent e)
    {
        string level = e.Level switch { LogLevel.Error => "CritBrush", LogLevel.Warning => "WarnBrush", LogLevel.Info => "OkBrush", _ => "TextMuted" };
        string tip = e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + e.Level + "  " + e.Source + " / " + e.Event + Environment.NewLine + e.Message;
        if (e.Data != null)
            foreach (var (k, v) in e.Data) tip += Environment.NewLine + k + ": " + (v.Length > 600 ? v[..600] + "…" : v);
        return new LogRow
        {
            Time = e.Time.LocalDateTime.ToString("HH:mm:ss"),
            Level = Loc.T(e.Level switch { LogLevel.Error => "ERR", LogLevel.Warning => "WARN", LogLevel.Info => "INFO", _ => "DBG" }),
            Source = e.Source,
            Message = e.Message,
            Tip = tip,
            LevelBrush = Application.Current.Resources[level] as Brush ?? Brushes.Gray,
        };
    }

    private DataTemplate LogRowTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
        {
            var col = new FrameworkElementFactory(typeof(ColumnDefinition));
            col.SetValue(ColumnDefinition.WidthProperty, width);
            grid.AppendChild(col);
        }

        var time = new FrameworkElementFactory(typeof(TextBlock));
        time.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogRow.Time)));
        time.SetValue(TextBlock.FontSizeProperty, 11.0);
        time.SetValue(TextBlock.MarginProperty, new Thickness(2, 0, 10, 0));
        time.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        time.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        grid.AppendChild(time);

        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(Grid.ColumnProperty, 1);
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        badge.SetValue(Border.PaddingProperty, new Thickness(5, 0, 5, 1));
        badge.SetValue(Border.MarginProperty, new Thickness(0, 0, 10, 0));
        badge.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        badge.SetValue(Border.MinWidthProperty, 42.0);
        badge.SetBinding(Border.BackgroundProperty, new Binding(nameof(LogRow.LevelBrush)));
        var badgeText = new FrameworkElementFactory(typeof(TextBlock));
        badgeText.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogRow.Level)));
        badgeText.SetValue(TextBlock.FontSizeProperty, 9.5);
        badgeText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        badgeText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "PanelSolidBrush");
        badge.AppendChild(badgeText);
        grid.AppendChild(badge);

        var source = new FrameworkElementFactory(typeof(TextBlock));
        source.SetValue(Grid.ColumnProperty, 2);
        source.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogRow.Source)));
        source.SetValue(TextBlock.FontSizeProperty, 11.0);
        source.SetValue(TextBlock.MinWidthProperty, 58.0);
        source.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 10, 0));
        source.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        source.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        grid.AppendChild(source);

        var message = new FrameworkElementFactory(typeof(TextBlock));
        message.SetValue(Grid.ColumnProperty, 3);
        message.SetBinding(TextBlock.TextProperty, new Binding(nameof(LogRow.Message)));
        message.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(LogRow.Tip)));
        message.SetValue(TextBlock.FontSizeProperty, 11.5);
        message.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        message.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        message.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        grid.AppendChild(message);

        return new DataTemplate { VisualTree = grid };
    }

    /// <summary>Rows are not selectable: no highlight, no focus rectangle, just padding.</summary>
    private static Style LogItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void BuildAboutPage()
    {
        // Identity card: icon, name, version chip and the one-line description.
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 6, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top, Source = AppIcon(56) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        grid.Children.Add(icon);

        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = "AmpTray", FontSize = 22, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("DisplayFont"), VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(10, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        chip.SetResourceReference(Border.BackgroundProperty, "OkBrush");
        var chipText = new TextBlock { Text = Loc.F("Version {0}", UpdateChecker.Display(UpdateChecker.Current)), FontSize = 11, FontWeight = FontWeights.SemiBold };
        chipText.SetResourceReference(TextBlock.ForegroundProperty, "PanelSolidBrush");
        chip.Child = chipText;
        titleRow.Children.Add(chip);
        text.Children.Add(titleRow);
        var sub = new TextBlock { Text = Loc.T("for WireView Pro II"), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        text.Children.Add(sub);
        var desc = new TextBlock { Text = Loc.T("Unofficial, read-only desktop widget for the Thermal Grizzly WireView Pro II, with NVIDIA GPU stats."), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        text.Children.Add(desc);
        var legal = new TextBlock { Text = Loc.T("Not affiliated with or endorsed by Thermal Grizzly or NVIDIA."), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        legal.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        text.Children.Add(legal);
        grid.Children.Add(text);
        card.Child = grid;
        Page.Children.Add(card);

        // Updates: one button, one status line that reflects the last manual check.
        Header(Loc.T("Updates"), null);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var check = new Button { Content = Loc.T("Check for updates"), Style = Res("PillButton"), Padding = new Thickness(14, 6, 14, 7), IsEnabled = !_checking };
        var download = new Button { Content = Loc.T("Open release page"), Style = Res("PillButton"), Padding = new Thickness(14, 6, 14, 7), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
        download.SetResourceReference(Button.BackgroundProperty, "OkBrush");
        download.SetResourceReference(Button.ForegroundProperty, "PanelSolidBrush");
        row.Children.Add(check);
        row.Children.Add(download);
        Page.Children.Add(row);
        var status = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Page.Children.Add(status);

        void Show()
        {
            download.Visibility = Visibility.Collapsed;
            status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            if (_checking) { status.Text = Loc.T("Checking…"); return; }
            if (_lastCheck == null) { status.Text = Loc.T("Compares this build with the latest release on GitHub. Nothing about this PC is sent.") + " " + Loc.T("Updates are never installed automatically; you download them from the release page yourself."); return; }
            string when = _lastCheckedAt is DateTime at ? "  ·  " + Loc.F("Checked at {0}", at.ToString("t", CultureInfo.CurrentCulture)) : "";
            if (!_lastCheck.Succeeded)
            {
                status.Text = Loc.T("Could not reach GitHub. Check your connection and try again.") + when;
                status.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            }
            else if (_lastCheck.Update is UpdateInfo u)
            {
                status.Text = Loc.F("AmpTray {0} is available", u.Tag) + when + Environment.NewLine
                    + Loc.T("Download the installer from the release page and run it. It replaces this version and keeps your settings.");
                status.SetResourceReference(TextBlock.ForegroundProperty, "OkBrush");
                download.Visibility = Visibility.Visible;
            }
            else
            {
                status.Text = Loc.T("You are running the latest version.") + when;
            }
        }
        Show();

        check.Click += async (_, _) =>
        {
            if (_checking) return;
            _checking = true;
            check.IsEnabled = false;
            Show();
            var result = await UpdateChecker.CheckDetailedAsync();
            _lastCheck = result;
            _lastCheckedAt = DateTime.Now;
            _checking = false;
            if (result.Update is UpdateInfo found) _host.ShowUpdate(found);
            if (!IsLoaded || _current != "About") return;
            check.IsEnabled = true;
            Show();
        };
        download.Click += (_, _) => { if (_lastCheck?.Update is UpdateInfo u) Open(u.Url); };

        // Links and the folder that holds settings, logs and the error log.
        Header(Loc.T("Links"), null);
        Link(Loc.T("Project on GitHub"), "https://github.com/" + UpdateChecker.Repo);
        Link(Loc.T("Releases"), "https://github.com/" + UpdateChecker.Repo + "/releases");
        Link(Loc.T("Report a problem"), "https://github.com/" + UpdateChecker.Repo + "/issues");
        Link(Loc.T("MIT License"), "https://github.com/" + UpdateChecker.Repo + "/blob/main/LICENSE");
        Link(Loc.T("Open the settings and logs folder"), Settings.AppDataFolder);
        Note(Loc.T("Open source under the MIT License. Device access is read-only, and nothing about this PC is sent anywhere apart from the update check."), bottom: 8);
    }

    private void Link(string text, string target)
    {
        var tb = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand };
        var arrow = new Run("↗  ");
        arrow.SetResourceReference(Run.ForegroundProperty, "TextMuted");
        var label = new Run(text);
        label.SetResourceReference(Run.ForegroundProperty, "OkBrush");
        tb.Inlines.Add(arrow);
        tb.Inlines.Add(label);
        tb.ToolTip = target;
        tb.MouseLeftButtonUp += (_, _) => Open(target);
        Page.Children.Add(tb);
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* no browser or folder: nothing sensible to do */ }
    }

    /// <summary>The frame of the application icon closest to the wanted size, so it stays crisp.</summary>
    private static ImageSource? AppIcon(int size)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size * 2)).ThenByDescending(f => f.PixelWidth).FirstOrDefault();
        }
        catch { return null; }
    }

    // ===== Building blocks =====
    private void Header(string title, string? description)
    {
        Page.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, Page.Children.Count == 0 ? 6 : 18, 0, description == null ? 6 : 2),
        });
        if (description != null) Note(description, bottom: 8);
    }

    private void Note(string text, double bottom = 4)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, bottom) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Page.Children.Add(t);
    }

    private void Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 8, 0, 5) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Page.Children.Add(t);
    }

    private static StackPanel Labelled(string label, string? description)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label });
        if (description != null)
        {
            var d = new TextBlock { Text = description, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            d.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            sp.Children.Add(d);
        }
        return sp;
    }

    private void Toggle(string label, string? description, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Style = Res("Switch"), IsChecked = get(), Content = Labelled(label, description) };
        cb.Click += (_, _) => { set(cb.IsChecked == true); Changed(); };
        Page.Children.Add(cb);
    }

    private void Segmented<T>(string label, (string Text, T Value)[] options, Func<T> get, Action<T> set)
    {
        Label(label);
        var panel = new WrapPanel();
        string group = Guid.NewGuid().ToString();
        foreach (var (text, value) in options)
        {
            var rb = new RadioButton { Content = text, GroupName = group, Style = Res("Segment"), IsChecked = EqualityComparer<T>.Default.Equals(get(), value) };
            rb.Click += (_, _) => { set(value); Changed(); };
            panel.Children.Add(rb);
        }
        Page.Children.Add(panel);
    }

    /// <summary>Opacity slider from almost invisible to solid; previews live, saves shortly after the last change.</summary>
    private void OpacityRow()
    {
        Label(Loc.T("Opacity"));
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var slider = new Slider
        {
            Style = Res("Slider"),
            Minimum = Settings.MinOpacity * 100,
            Maximum = 100,
            Value = Math.Round(_s.Opacity * 100),
            SmallChange = 1,
            LargeChange = 10,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var value = new TextBlock { Text = $"{slider.Value:0} %", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        value.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetColumn(value, 1);

        var save = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        save.Tick += (_, _) => { save.Stop(); Changed(); };
        slider.ValueChanged += (_, e) =>
        {
            _s.Opacity = Math.Round(e.NewValue) / 100;
            value.Text = $"{e.NewValue:0} %";
            _host.PreviewOpacity(_s.Opacity);
            save.Stop();
            save.Start();
        };

        row.Children.Add(slider);
        row.Children.Add(value);
        Page.Children.Add(row);
        Note(Loc.T("At very low values the widget is easy to lose; it still responds to the mouse unless click-through is on."));
    }

    private StackPanel RuleCard(string title, Func<bool> get, Action<bool> set)
    {
        var body = new StackPanel();
        var cb = new CheckBox { Style = Res("Switch"), IsChecked = get(), Content = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold }, Margin = new Thickness(0, 0, 0, 4) };
        body.Children.Add(cb);
        var details = new StackPanel { Opacity = get() ? 1 : 0.45 };
        cb.Click += (_, _) => { set(cb.IsChecked == true); details.Opacity = cb.IsChecked == true ? 1 : 0.45; Changed(); };
        body.Children.Add(details);

        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 10, 0, 0), Child = body };
        card.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        Page.Children.Add(card);
        return details;
    }

    private static void RuleNote(Panel parent, string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        parent.Children.Add(t);
    }

    /// <summary>Inline row mixing words (strings) and input boxes.</summary>
    private static WrapPanel Sentence(params object[] parts)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var part in parts)
        {
            if (part is string s)
                row.Children.Add(new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            else if (part is FrameworkElement fe)
            {
                fe.Margin = new Thickness(0, 0, 6, 0);
                row.Children.Add(fe);
            }
        }
        return row;
    }

    private TextBox Minutes(Func<double> get, Action<double> set) => Number(get(), 0, 600, set, "0.##");

    /// <summary>Numeric box: commits on Enter or focus loss (or when the page/window closes), reverts invalid input.</summary>
    private TextBox Number(double value, double min, double max, Action<double> set, string format = "0.0#", double width = 52)
    {
        var box = new TextBox
        {
            Text = value.ToString(format, CultureInfo.CurrentCulture),
            Style = Res("Field"),
            Width = width,
            TextAlignment = TextAlignment.Right,
            ToolTip = $"{min.ToString(CultureInfo.CurrentCulture)} to {max.ToString(CultureInfo.CurrentCulture)}",
        };
        double current = value;
        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && v >= min && v <= max)
            {
                if (v != current) { current = v; set(v); Changed(); }
                box.Text = v.ToString(format, CultureInfo.CurrentCulture);
            }
            else
            {
                box.Text = current.ToString(format, CultureInfo.CurrentCulture);
            }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        _pendingCommits.Add(Commit);
        return box;
    }

    private RadioButton ThemeCard(string name, ThemeColors c)
    {
        Brush B(string hex) => new SolidColorBrush(Theme.Parse(hex, Colors.Gray));
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var hex in new[] { c.Accent, c.Warning, c.Critical, c.Graph })
            dots.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = B(hex), Margin = new Thickness(0, 0, 5, 0) });

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = name == Theme.Custom ? Loc.T("Custom") : name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(c.Text) });
        body.Children.Add(dots);

        var card = new RadioButton
        {
            GroupName = "theme",
            Style = Res("ThemeCard"),
            IsChecked = _s.ThemeName == name,
            Content = new Border
            {
                Width = 120,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(8),
                Background = B(c.Background),
                BorderBrush = new SolidColorBrush(Theme.WithAlpha(Theme.Parse(c.Text, Colors.White), 0x30)),
                BorderThickness = new Thickness(1),
                Child = body,
            },
        };
        card.Click += (_, _) => { _s.ThemeName = name; Changed(); ShowPage("Theme"); };
        return card;
    }

    private void ColorRow(string label, string hex, Action<string> set)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var box = new TextBox { Text = hex.ToUpperInvariant(), Style = Res("Field"), FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        void Commit()
        {
            if (box.Text.Equals(hex, StringComparison.OrdinalIgnoreCase)) return;
            var candidate = box.Text.Trim();
            if (!candidate.StartsWith('#')) candidate = "#" + candidate;
            if (Theme.TryParse(candidate, out var c)) set(Theme.ToHex(c));
            else box.Text = hex;
        }

        var swatch = new Button
        {
            Width = 30,
            Height = 26,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = Loc.T("Pick colour"),
            Template = SwatchTemplate(Theme.Parse(hex, Colors.Gray)),
        };
        swatch.Click += (_, _) =>
        {
            var cur = Theme.Parse(hex, Colors.Gray);
            using var dlg = new Forms.ColorDialog { Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B), FullOpen = true };
            if (dlg.ShowDialog() == Forms.DialogResult.OK)
                set(Theme.ToHex(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B)));
        };

        Grid.SetColumn(box, 1);
        Grid.SetColumn(swatch, 2);
        row.Children.Add(text);
        row.Children.Add(box);
        row.Children.Add(swatch);
        Page.Children.Add(row);
    }

    private static ControlTemplate SwatchTemplate(Color c)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(c));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private Style Res(string key) => (Style)FindResource(key);

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
            // Nothing useful to do if Explorer can't be launched.
        }
    }

    public static void OpenLogFolder(string folder) => OpenFolder(folder);
}
