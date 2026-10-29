using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AmpTray.Logging;

namespace AmpTray.Avalonia;

/// <summary>The Logs and About pages of the settings window.</summary>
public partial class SettingsWindow
{
    // Filter choices survive leaving the page; the readers and timer live only while it is shown.
    private static LogLevel _logMinLevel = LogLevel.Info;
    private static string _logSource = "";
    private static string _logText = "";
    private static bool _logIncludeService = true;
    private const int MaxLogRows = 3000;

    // Result of the last manual update check, kept for the life of the process so reopening Settings shows it.
    private static UpdateCheck? _lastCheck;
    private static DateTime? _lastCheckedAt;
    private static bool _checking;

    private readonly List<LogEvent> _logEvents = new();
    private EventLogReader? _widgetLog, _serviceLog;
    private DispatcherTimer? _logTimer;
    private ListBox? _logList;
    private ScrollViewer? _logScroll;
    private TextBlock? _logCount;
    private WrapPanel? _logSources;
    private bool _logAtBottom = true;
    private Action? _logWrittenHandler;
    private EventHandler<SizeChangedEventArgs>? _pageSized;

    private sealed class LogRow
    {
        public string Time { get; init; } = "";
        public string Level { get; init; } = "";
        public string Source { get; init; } = "";
        public string Message { get; init; } = "";
        public string Tip { get; init; } = "";
        public IBrush LevelBrush { get; init; } = Brushes.Gray;
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
            var rb = Segment(text, group, _logMinLevel == level);
            rb.Click += (_, _) => { _logMinLevel = level; RefreshLogList(); };
            levels.Children.Add(rb);
        }
        DockPanel.SetDock(levels, Dock.Left);
        row.Children.Add(levels);
        var search = Field(_logText);
        search.Margin = new Thickness(10, 0, 0, 4);
        search.VerticalAlignment = VerticalAlignment.Center;
        search.MinWidth = 120;
        search.Watermark = Loc.T("Search events");
        search.TextChanged += (_, _) => { _logText = search.Text ?? ""; RefreshLogList(); };
        row.Children.Add(search);
        Page.Children.Add(row);

        // Sources seen so far; rebuilt as new ones show up.
        _logSources = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        Page.Children.Add(_logSources);

        var service = Switch(_logIncludeService, Labelled(Loc.T("Include service log"), null));
        service.Margin = new Thickness(0, 0, 0, 8);
        service.Click += (_, _) => { _logIncludeService = service.IsChecked == true; ReloadLogs(); };
        Page.Children.Add(service);

        // The list itself: virtualised rows, newest last.
        _logList = new ListBox { ItemTemplate = LogRowTemplate(), Height = LogListHeight(), SelectionMode = SelectionMode.Single };
        _logList.Classes.Add("Log");
        ScrollViewer.SetHorizontalScrollBarVisibility(_logList, ScrollBarVisibility.Disabled);
        _logList.Loaded += (_, _) =>
        {
            _logScroll = _logList.Scroll as ScrollViewer;
            if (_logScroll == null) return;
            _logScroll.ScrollChanged += OnLogScrolled;
            _logScroll.ScrollToEnd();
        };
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = _logList };
        Ref(frame, Border.BorderBrushProperty, "PanelBorderBrush");
        Ref(frame, Border.BackgroundProperty, "HoverBrush");
        Page.Children.Add(frame);
        _pageSized = (_, _) => { if (_logList != null) _logList.Height = LogListHeight(); };
        PageScroll.SizeChanged += _pageSized;

        // Footer: count and the two actions.
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var open = Pill(Loc.T("Open log folder"), () => OpenFolder(Settings.AppDataFolder));
        open.Padding = new Thickness(12, 4, 12, 5);
        var clear = Pill(Loc.T("Clear widget log"), () => { AppLog.Current.Clear(); AppLog.Info("app", "log-cleared", "Event log cleared from Settings"); ReloadLogs(); });
        clear.Padding = new Thickness(12, 4, 12, 5);
        clear.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(open);
        buttons.Children.Add(clear);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        _logCount = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Ref(_logCount, TextBlock.ForegroundProperty, "TextSecondary");
        footer.Children.Add(_logCount);
        Page.Children.Add(footer);

        ReloadLogs();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _logTimer.Tick += (_, _) => PollLogs();
        _logTimer.Start();
        _logWrittenHandler = () => Dispatcher.UIThread.Post(() => PollLogs());
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
        if (_pageSized != null) PageScroll.SizeChanged -= _pageSized;
        _pageSized = null;
        _logScroll = null;
        _logList = null;
        _logCount = null;
        _logSources = null;
        _widgetLog = null;
        _serviceLog = null;
        _logEvents.Clear();
        _logAtBottom = true;
    }

    private double LogListHeight() => Math.Max(220, PageScroll.Viewport.Height - 236);

    /// <summary>User scrolling decides whether the list keeps following new events; growth of the list does not.</summary>
    private void OnLogScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) return;
        if (sender is not ScrollViewer sv) return;
        _logAtBottom = sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 0.5;
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
            var rb = Segment(text, group, string.Equals(_logSource, value, StringComparison.OrdinalIgnoreCase));
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
        if (_logAtBottom) Dispatcher.UIThread.Post(() => _logScroll?.ScrollToEnd(), DispatcherPriority.Background);
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
            LevelBrush = AppTheme.BrushOf(level),
        };
    }

    private static IDataTemplate LogRowTemplate() => new FuncDataTemplate<LogRow>((row, _) =>
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };

        var time = new TextBlock { Text = row.Time, FontSize = 11, Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Ref(time, TextBlock.ForegroundProperty, "TextMuted");
        grid.Children.Add(time);

        var badgeText = new TextBlock { Text = row.Level, FontSize = 9.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        Ref(badgeText, TextBlock.ForegroundProperty, "PanelSolidBrush");
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 42, Background = row.LevelBrush, Child = badgeText,
        };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        var source = new TextBlock { Text = row.Source, FontSize = 11, MinWidth = 58, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Ref(source, TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetColumn(source, 2);
        grid.Children.Add(source);

        var message = new TextBlock { Text = row.Message, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Ref(message, TextBlock.ForegroundProperty, "TextPrimary");
        ToolTip.SetTip(message, row.Tip);
        Grid.SetColumn(message, 3);
        grid.Children.Add(message);
        return grid;
    }, supportsRecycling: true);

    // ===== About =====
    private void BuildAboutPage()
    {
        // Identity card: icon, name, version chip and the one-line description.
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 6, 0, 0) };
        Ref(card, Border.BackgroundProperty, "HoverBrush");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var icon = new Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top, Source = AppIcon() };
        RenderOptions.SetBitmapInterpolationMode(icon, BitmapInterpolationMode.HighQuality);
        grid.Children.Add(icon);

        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = "AmpTray", FontSize = 22, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var chipText = new TextBlock { Text = Loc.F("Version {0}", UpdateChecker.Display(UpdateChecker.Current)), FontSize = 11, FontWeight = FontWeight.SemiBold };
        Ref(chipText, TextBlock.ForegroundProperty, "PanelSolidBrush");
        var chip = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(10, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = chipText };
        Ref(chip, Border.BackgroundProperty, "OkBrush");
        titleRow.Children.Add(chip);
        text.Children.Add(titleRow);
        var sub = new TextBlock { Text = Loc.T("for WireView Pro II") + " · Linux", FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
        Ref(sub, TextBlock.ForegroundProperty, "TextSecondary");
        text.Children.Add(sub);
        var desc = new TextBlock { Text = Loc.T("Unofficial, read-only desktop widget for the Thermal Grizzly WireView Pro II, with NVIDIA GPU stats."), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        Ref(desc, TextBlock.ForegroundProperty, "TextSecondary");
        text.Children.Add(desc);
        var legal = new TextBlock { Text = Loc.T("Not affiliated with or endorsed by Thermal Grizzly or NVIDIA."), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        Ref(legal, TextBlock.ForegroundProperty, "TextMuted");
        text.Children.Add(legal);
        grid.Children.Add(text);
        card.Child = grid;
        Page.Children.Add(card);

        // Updates: one button, one status line that reflects the last manual check.
        Header(Loc.T("Updates"), null);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var check = Pill(Loc.T("Check for updates"), null);
        check.IsEnabled = !_checking;
        var download = Pill(Loc.T("Open release page"), null);
        download.Margin = new Thickness(8, 0, 0, 0);
        download.IsVisible = false;
        Ref(download, Button.BackgroundProperty, "OkBrush");
        Ref(download, Button.ForegroundProperty, "PanelSolidBrush");
        row.Children.Add(check);
        row.Children.Add(download);
        Page.Children.Add(row);
        var status = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
        Ref(status, TextBlock.ForegroundProperty, "TextSecondary");
        Page.Children.Add(status);

        void Show()
        {
            download.IsVisible = false;
            Ref(status, TextBlock.ForegroundProperty, "TextSecondary");
            if (_checking) { status.Text = Loc.T("Checking…"); return; }
            if (_lastCheck == null) { status.Text = Loc.T("Compares this build with the latest release on GitHub. Nothing about this PC is sent.") + " " + Loc.T("Updates are never installed automatically; you download them from the release page yourself."); return; }
            string when = _lastCheckedAt is DateTime at ? "  ·  " + Loc.F("Checked at {0}", at.ToString("t", CultureInfo.CurrentCulture)) : "";
            if (!_lastCheck.Succeeded)
            {
                status.Text = Loc.T("Could not reach GitHub. Check your connection and try again.") + when;
                Ref(status, TextBlock.ForegroundProperty, "WarnBrush");
            }
            else if (_lastCheck.Update is UpdateInfo u)
            {
                status.Text = Loc.F("AmpTray {0} is available", u.Tag) + when + Environment.NewLine
                    + Loc.T("Download the Linux tarball from the release page and run install.sh again. It replaces this version and keeps your settings.");
                Ref(status, TextBlock.ForegroundProperty, "OkBrush");
                download.IsVisible = true;
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
        download.Click += (_, _) => { if (_lastCheck?.Update is UpdateInfo u) Shell.Open(u.Url); };

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
        var tb = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 3, 0, 3), Cursor = new Cursor(StandardCursorType.Hand) };
        tb.Inlines!.Add(new Run("↗  ") { Foreground = AppTheme.BrushOf("TextMuted") });
        tb.Inlines.Add(new Run(text) { Foreground = AppTheme.BrushOf("OkBrush") });
        ToolTip.SetTip(tb, target);
        tb.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Shell.Open(target); };
        Page.Children.Add(tb);
    }

    private static Bitmap? AppIcon()
    {
        try { return new Bitmap(AssetLoader.Open(new Uri("avares://amptray/Assets/app.png"))); }
        catch { return null; }
    }
}
