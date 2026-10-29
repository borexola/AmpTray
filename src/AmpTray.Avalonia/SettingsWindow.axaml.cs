using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AmpTray.Logging;

namespace AmpTray.Avalonia;

/// <summary>The settings window: eight pages built in code, every change applied live. Port of the WPF SettingsWindow.</summary>
public partial class SettingsWindow : Window
{
    // Page keys are English; the navigation shows them translated.
    private static readonly string[] PageKeys = { "Panels", "Theme", "Alerts", "Logging", "Auto shutdown", "Window", "Logs", "About" };

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
            var item = new RadioButton { GroupName = "nav", IsChecked = k == _current };
            item.Classes.Add("NavItem");
            item.IsCheckedChanged += (_, _) => { if (item.IsChecked == true && _current != k) ShowPage(k); };
            Nav.Children.Add(item);
            _nav[k] = item;
        }

        TitleBar.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        CloseButton.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        // A number typed but not yet confirmed with Enter/Tab must not be lost on Close or Escape.
        Closing += (_, _) => CommitPending();

        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => { Loc.Changed -= OnLanguageChanged; LeaveLogsPage(); };

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
        ToolTip.SetTip(CloseButton, Loc.T("Close"));
        foreach (var (key, item) in _nav) item.Content = Loc.T(key);
    }

    private void ShowPage(string name)
    {
        CommitPending();
        LeaveLogsPage();
        _current = name;
        if (_nav.TryGetValue(name, out var nav) && nav.IsChecked != true) nav.IsChecked = true;
        Page.Children.Clear();
        _pages[name]();
    }

    private void CommitPending()
    {
        foreach (var commit in _pendingCommits.ToList()) commit();
        _pendingCommits.Clear();
    }

    private void Changed() => _host.ApplySettings();

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
        foreach (var (name, colors) in AppTheme.Presets)
            cards.Children.Add(ThemeCard(name, colors));
        cards.Children.Add(ThemeCard(AppTheme.Custom, _s.CustomColors));
        Page.Children.Add(cards);

        Header(Loc.T("Colours"), _s.ThemeName == AppTheme.Custom ? null : Loc.F("Editing a colour switches from “{0}” to Custom.", _s.ThemeName));
        var current = AppTheme.Resolve(_s);
        foreach (var (label, get, set) in ThemeColors.Fields)
            ColorRow(Loc.T(label), get(current), hex =>
            {
                if (_s.ThemeName != AppTheme.Custom)
                {
                    _s.CustomColors = AppTheme.Resolve(_s);
                    _s.ThemeName = AppTheme.Custom;
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
        Toggle(Loc.T("Flash the widget"), Loc.T("Widget pulses red and shows the fault banner"), () => _s.FlashWidget, v => _s.FlashWidget = v);
        if (_host.AlarmAvailable)
        {
            Toggle(Loc.T("Sound alarm"), Loc.T("Looping two-tone alarm"), () => _s.SoundAlarm, v => _s.SoundAlarm = v);
            Segmented(Loc.T("Alarm volume"), new[] { (Loc.T("Low"), 30), (Loc.T("Medium"), 60), (Loc.T("High"), 100) }, () => _s.AlarmVolume, v => _s.AlarmVolume = v);
        }
        else
        {
            Note(Loc.T("No sound alarm: none of pw-play, paplay or aplay was found on this system."));
        }
        if (_host.NotificationsAvailable)
            Toggle(Loc.T("Desktop notification"), Loc.T("Notification naming the fault (notify-send)"), () => _s.Notify, v => _s.Notify = v);
        else
            Note(Loc.T("No desktop notifications: notify-send was not found on this system."));
        Toggle(Loc.T("Bring widget to front"), Loc.T("Show the widget if hidden and raise it above other windows"), () => _s.BringToFront, v => _s.BringToFront = v);

        var test = Pill(Loc.T("Test alert (5 s)"), () => _host.TestAlert());
        test.Margin = new Thickness(0, 12, 0, 0);
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
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6), ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var box = Field(_s.LogFolder);
        box.LostFocus += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(box.Text) && box.Text != _s.LogFolder) { _s.LogFolder = box.Text.Trim(); Changed(); }
        };
        var browse = Pill(Loc.T("Browse…"), null);
        browse.Margin = new Thickness(6, 0, 0, 0);
        browse.Click += async (_, _) =>
        {
            try
            {
                var start = await StorageProvider.TryGetFolderFromPathAsync(_s.LogFolder);
                var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("Log folder"), AllowMultiple = false, SuggestedStartLocation = start });
                if (picked.Count > 0 && picked[0].TryGetLocalPath() is string path)
                {
                    _s.LogFolder = path;
                    box.Text = path;
                    Changed();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("settings", "folder-picker", "Folder picker failed", ("error", ex.Message));
            }
        };
        var open = Pill(Loc.T("Open"), () => OpenFolder(_s.LogFolder));
        open.Margin = new Thickness(6, 0, 0, 0);
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
            Loc.T("Powers the PC off (open apps are closed without saving) when a rule below holds for its duration.") + " " +
            Loc.F("Every rule also needs at least {0} s and {1} readings in a row, so one bad reading can't trigger it, and time the PC spends asleep never counts.",
                ShutdownMonitor.MinGrace.TotalSeconds.ToString("0"), ShutdownMonitor.MinConsecutive));
        Toggle(Loc.T("Armed"), Loc.T("Master switch, also in the tray menu. The widget shows ARMED while it's on."), () => c.Enabled, v => c.Enabled = v);
        Segmented(Loc.T("Before shutting down"),
            new[] { (Loc.T("Immediately"), 0), (Loc.F("{0} s countdown", 10), 10), (Loc.F("{0} s countdown", 30), 30), (Loc.F("{0} s countdown", 60), 60) },
            () => c.CountdownSeconds, v => c.CountdownSeconds = v);
        Note(Loc.T("A countdown shows a full-size Cancel button. While a rule is still timing, the widget shows an amber “PC shuts down in…” banner with Cancel, and you get a desktop notification."));

        // Temperature
        var temp = RuleCard(Loc.T("Temperature"), () => c.TempRule, v => c.TempRule = v);
        var sensors = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        string group = Guid.NewGuid().ToString();
        for (int i = 0; i < ShutdownSettings.SensorKeys.Length; i++)
        {
            int idx = i;
            var rb = Segment(ShutdownSettings.SensorName(i), group, c.TempSensor == i);
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
            var chip = new ToggleButton { Content = Device.SensorSnapshot.Describe(f), IsChecked = (c.FaultMask & bit) != 0 };
            chip.Classes.Add("Segment");
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

        var test = Pill(Loc.T("Dry run test"), () => _host.TestShutdown());
        test.Margin = new Thickness(0, 14, 0, 0);
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
        OpacityRow();
        Toggle(Loc.T("Opaque on hover"), Loc.T("Fade back to full opacity while the mouse is over the widget"),
            () => _s.OpaqueOnHover, v => _s.OpaqueOnHover = v);
        Segmented(Loc.T("Refresh rate"), new[] { (Loc.F("{0} s", 0.5.ToString(CultureInfo.CurrentCulture)), 500), (Loc.F("{0} s", 1), 1000), (Loc.F("{0} s", 2), 2000) },
            () => _s.PollMs, v => _s.PollMs = v);

        var startup = Switch(_host.StartupEnabled, Labelled(Loc.T("Start at login"), Loc.T("Adds an entry to the desktop's autostart folder (~/.config/autostart).")));
        startup.Click += (_, _) => { _host.StartupEnabled = startup.IsChecked == true; startup.IsChecked = _host.StartupEnabled; };
        Page.Children.Add(startup);
        if (!_host.TrayAvailable)
            Note(Loc.T("No tray icon: this desktop has no status-notifier host (on GNOME, enable the AppIndicator extension). Everything is in the widget's right-click menu; a hidden widget comes back by starting AmpTray again."));

        Header(Loc.T("Background monitoring"), null);
        bool installed = _host.ServiceInstalled;
        var svc = Switch(installed, Labelled(Loc.T("Run from boot as a systemd service (amptray-monitor)"),
            Loc.T("Keeps CSV logging and the auto-shutdown rules running from boot, with nobody signed in. Turning this on or off asks for your password.")));
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
            Note(Loc.T("The change was not applied: the password prompt was declined or the command failed."));
        }
        if (installed && !_host.ServiceSettingsWritable)
            Note(Loc.T("This user cannot write the service's settings yet: log out and back in so the amptray group membership from the installation takes effect."));
        Note(Loc.T("The service follows the settings above (rules, logging, refresh rate) and picks up changes within a few seconds. While it runs, it is the one that shuts the PC down; the widget shows the countdown and can still cancel."));

        Toggle(Loc.T("Check for updates at startup"), Loc.T("Asks api.github.com for the latest release once per launch; nothing about this PC is sent."),
            () => _s.CheckUpdates, v => _s.CheckUpdates = v);

        Header(Loc.T("Language"), null);
        var languages = new List<(string, string)> { (Loc.T("System"), Loc.Auto) };
        languages.AddRange(Loc.Languages.Select(l => (l.Name, l.Code)));
        Segmented(Loc.T("Language"), languages.ToArray(), () => _s.Language, v => _s.Language = v);

        Header(Loc.T("Device access"), null);
        Note(Loc.T("Read-only. The widget only identifies the device and reads its sensors; it never changes device settings, clears faults or touches firmware.") + " " +
             Loc.T("The udev rule from install.sh lets members of the dialout group and the signed-in user open /dev/ttyACM*; the widget and the service share the port through a lock file."));
    }

    // ===== Building blocks =====
    private void Header(string title, string? description)
    {
        Page.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, Page.Children.Count == 0 ? 6 : 18, 0, description == null ? 6 : 2),
        });
        if (description != null) Note(description, bottom: 8);
    }

    private void Note(string text, double bottom = 4)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, bottom) };
        Ref(t, TextBlock.ForegroundProperty, "TextSecondary");
        Page.Children.Add(t);
    }

    private void Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 8, 0, 5) };
        Ref(t, TextBlock.ForegroundProperty, "TextSecondary");
        Page.Children.Add(t);
    }

    private static StackPanel Labelled(string label, string? description)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (description != null)
        {
            var d = new TextBlock { Text = description, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            Ref(d, TextBlock.ForegroundProperty, "TextMuted");
            sp.Children.Add(d);
        }
        return sp;
    }

    private static CheckBox Switch(bool isChecked, object content)
    {
        var cb = new CheckBox { IsChecked = isChecked, Content = content };
        cb.Classes.Add("Switch");
        return cb;
    }

    private void Toggle(string label, string? description, Func<bool> get, Action<bool> set)
    {
        var cb = Switch(get(), Labelled(label, description));
        cb.Click += (_, _) => { set(cb.IsChecked == true); Changed(); };
        Page.Children.Add(cb);
    }

    private static RadioButton Segment(string text, string group, bool isChecked)
    {
        var rb = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked };
        rb.Classes.Add("Segment");
        return rb;
    }

    private void Segmented<T>(string label, (string Text, T Value)[] options, Func<T> get, Action<T> set)
    {
        Label(label);
        var panel = new WrapPanel();
        string group = Guid.NewGuid().ToString();
        foreach (var (text, value) in options)
        {
            var rb = Segment(text, group, EqualityComparer<T>.Default.Equals(get(), value));
            rb.Click += (_, _) => { set(value); Changed(); };
            panel.Children.Add(rb);
        }
        Page.Children.Add(panel);
    }

    private static Button Pill(string text, Action? click)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 7) };
        b.Classes.Add("PillButton");
        if (click != null) b.Click += (_, _) => click();
        return b;
    }

    private static TextBox Field(string text)
    {
        var box = new TextBox { Text = text };
        box.Classes.Add("Field");
        return box;
    }

    /// <summary>Opacity slider from almost invisible to solid; previews live, saves shortly after the last change.</summary>
    private void OpacityRow()
    {
        Label(Loc.T("Opacity"));
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4), ColumnDefinitions = new ColumnDefinitions("*,52") };

        var slider = new Slider
        {
            Minimum = Settings.MinOpacity * 100,
            Maximum = 100,
            Value = Math.Round(_s.Opacity * 100),
            SmallChange = 1,
            LargeChange = 10,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new TextBlock { Text = $"{slider.Value:0} %", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        Ref(value, TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetColumn(value, 1);

        var save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
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
        Note(Loc.T("At very low values the widget is easy to lose; it still responds to the mouse."));
    }

    private StackPanel RuleCard(string title, Func<bool> get, Action<bool> set)
    {
        var body = new StackPanel();
        var cb = Switch(get(), new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        cb.Margin = new Thickness(0, 0, 0, 4);
        body.Children.Add(cb);
        var details = new StackPanel { Opacity = get() ? 1 : 0.45 };
        cb.Click += (_, _) => { set(cb.IsChecked == true); details.Opacity = cb.IsChecked == true ? 1 : 0.45; Changed(); };
        body.Children.Add(details);

        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 10, 0, 0), Child = body };
        Ref(card, Border.BackgroundProperty, "HoverBrush");
        Page.Children.Add(card);
        return details;
    }

    private static void RuleNote(Panel parent, string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        Ref(t, TextBlock.ForegroundProperty, "TextMuted");
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
            else if (part is Control c)
            {
                c.Margin = new Thickness(0, 0, 6, 0);
                c.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(c);
            }
        }
        return row;
    }

    private TextBox Minutes(Func<double> get, Action<double> set) => Number(get(), 0, 600, set, "0.##");

    /// <summary>Numeric box: commits on Enter or focus loss (or when the page/window closes), reverts invalid input.</summary>
    private TextBox Number(double value, double min, double max, Action<double> set, string format = "0.0#", double width = 52)
    {
        var box = Field(value.ToString(format, CultureInfo.CurrentCulture));
        box.Width = width;
        box.TextAlignment = TextAlignment.Right;
        ToolTip.SetTip(box, $"{min.ToString(CultureInfo.CurrentCulture)} to {max.ToString(CultureInfo.CurrentCulture)}");
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
        IBrush B(string hex) => new ImmutableSolidColorBrush(AppTheme.Parse(hex, Colors.Gray));
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var hex in new[] { c.Accent, c.Warning, c.Critical, c.Graph })
            dots.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = B(hex), Margin = new Thickness(0, 0, 5, 0) });

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = name == AppTheme.Custom ? Loc.T("Custom") : name, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = B(c.Text) });
        body.Children.Add(dots);

        var card = new RadioButton
        {
            GroupName = "theme",
            IsChecked = _s.ThemeName == name,
            Content = new Border
            {
                Width = 120,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(8),
                Background = B(c.Background),
                BorderBrush = new ImmutableSolidColorBrush(AppTheme.WithAlpha(AppTheme.Parse(c.Text, Colors.White), 0x30)),
                BorderThickness = new Thickness(1),
                Child = body,
            },
        };
        card.Classes.Add("ThemeCard");
        card.Click += (_, _) => { _s.ThemeName = name; Changed(); ShowPage("Theme"); };
        return card;
    }

    private void ColorRow(string label, string hex, Action<string> set)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3), ColumnDefinitions = new ColumnDefinitions("*,96,Auto") };

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var box = Field(hex.ToUpperInvariant());
        box.FontFamily = new FontFamily("monospace");
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        void Commit()
        {
            if ((box.Text ?? "").Equals(hex, StringComparison.OrdinalIgnoreCase)) return;
            var candidate = (box.Text ?? "").Trim();
            if (!candidate.StartsWith('#')) candidate = "#" + candidate;
            if (AppTheme.TryParse(candidate, out var c)) set(AppTheme.ToHex(c));
            else box.Text = hex;
        }

        var current = AppTheme.Parse(hex, Colors.Gray);
        var swatch = new Button
        {
            Width = 30,
            Height = 26,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            Background = new ImmutableSolidColorBrush(current),
            BorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        ToolTip.SetTip(swatch, Loc.T("Pick colour"));
        var picker = new ColorView { Color = current, ColorModel = ColorModel.Rgba, IsAlphaEnabled = false, IsAlphaVisible = false, Width = 300 };
        var flyout = new Flyout { Content = picker, Placement = PlacementMode.Bottom };
        flyout.Closed += (_, _) =>
        {
            string picked = AppTheme.ToHex(picker.Color);
            if (!picked.Equals(hex, StringComparison.OrdinalIgnoreCase)) set(picked);
        };
        swatch.Flyout = flyout;

        Grid.SetColumn(box, 1);
        Grid.SetColumn(swatch, 2);
        row.Children.Add(text);
        row.Children.Add(box);
        row.Children.Add(swatch);
        Page.Children.Add(row);
    }

    private static void Ref(AvaloniaObject e, AvaloniaProperty p, string key) => e[!p] = new DynamicResourceExtension(key);

    private static void OpenFolder(string folder)
    {
        try { System.IO.Directory.CreateDirectory(folder); } catch { }
        Shell.Open(folder);
    }
}
