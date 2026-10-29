using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using AmpTray.Ui;

namespace AmpTray.Avalonia;

/// <summary>Topmost "Shutting down in N s" panel with a Cancel button. Raises Elapsed or Cancelled once.</summary>
public sealed class ShutdownCountdownWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = CountdownClock.TickInterval };
    private readonly TextBlock _count;
    private CountdownClock _clock;
    private bool _done;

    public event Action? Elapsed;
    public event Action? Cancelled;

    public ShutdownCountdownWindow(string reason, int seconds, bool test)
    {
        _clock = new CountdownClock(seconds, DateTime.Now);

        Title = "AmpTray auto shutdown";
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)Application.Current!.FindResource("DisplayFont")!;

        var stack = new StackPanel { Width = 460 };
        stack.Children.Add(Text(test ? Loc.T("TEST: this PC will NOT shut down") : "⚠  " + Loc.T("Shutting down this PC"), 20, FontWeight.Bold, "TextPrimary"));
        stack.Children.Add(Text(reason, 13, FontWeight.Normal, "TextSecondary", new Thickness(0, 6, 0, 0)));
        _count = Text("", 64, FontWeight.SemiBold, "CritBrush", new Thickness(0, 10, 0, 6));
        _count.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(_count);

        var cancel = new Button
        {
            Content = Loc.T("Cancel shutdown"),
            FontSize = 15,
            Padding = new Thickness(24, 9, 24, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Brushes.White,
            IsCancel = true,
        };
        cancel.Classes.Add("PillButton");
        cancel[!BackgroundProperty] = new DynamicResourceExtension("CritBrush");
        cancel.Click += (_, _) => Finish(cancelled: true);
        stack.Children.Add(cancel);

        var note = Text(test
                ? Loc.T("Dry run of your auto-shutdown settings.")
                : Loc.T("Cancelling pauses this rule until its condition clears. Apps will be force-closed."),
            11, FontWeight.Normal, "TextMuted", new Thickness(0, 12, 0, 0));
        note.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(note);

        var panel = new Border
        {
            Padding = new Thickness(28, 22, 28, 20),
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2),
            BoxShadow = BoxShadows.Parse("0 3 24 0 #80000000"),
            Child = stack,
        };
        panel[!Border.BackgroundProperty] = new DynamicResourceExtension("PanelSolidBrush");
        panel[!Border.BorderBrushProperty] = new DynamicResourceExtension("CritBrush");
        Content = panel;

        _timer.Tick += (_, _) => Tick();
        Opened += (_, _) => { _clock = new CountdownClock(_clock.Remaining, DateTime.Now); Tick(); _timer.Start(); Activate(); };
        Closed += (_, _) => { _timer.Stop(); if (!_done) { _done = true; Cancelled?.Invoke(); } };
    }

    /// <summary>Closes without raising either event (e.g. the condition cleared on its own).</summary>
    public void Abort()
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Close();
    }

    private void Tick()
    {
        bool elapsed = _clock.Tick(DateTime.Now);
        _count.Text = _clock.DisplayText;
        if (elapsed) Finish(cancelled: false);
    }

    private void Finish(bool cancelled)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Close();
        if (cancelled) Cancelled?.Invoke(); else Elapsed?.Invoke();
    }

    private static TextBlock Text(string s, double size, FontWeight weight, string brushKey, Thickness? margin = null)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        t[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(brushKey);
        return t;
    }
}
