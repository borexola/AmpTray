using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AmpTray;

/// <summary>Topmost "Shutting down in N s" panel with a Cancel button. Raises Elapsed or Cancelled once.</summary>
public sealed class ShutdownCountdownWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBlock _count;
    private double _remaining;
    private DateTime _lastTick = DateTime.Now;
    private bool _done;

    public event Action? Elapsed;
    public event Action? Cancelled;

    public ShutdownCountdownWindow(string reason, int seconds, bool test)
    {
        _remaining = seconds;

        Title = "AmpTray auto shutdown";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;

        var stack = new StackPanel { Width = 460 };
        stack.Children.Add(Text(test ? Loc.T("TEST: this PC will NOT shut down") : "⚠  " + Loc.T("Shutting down this PC"), 20, FontWeights.Bold, "TextPrimary"));
        stack.Children.Add(Text(reason, 13, FontWeights.Normal, "TextSecondary", new Thickness(0, 6, 0, 0)));
        _count = Text("", 64, FontWeights.SemiBold, "CritBrush", new Thickness(0, 10, 0, 6));
        _count.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        _count.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(_count);

        var cancel = new Button
        {
            Content = Loc.T("Cancel shutdown"),
            Style = (Style)Application.Current.FindResource("PillButton"),
            FontSize = 15,
            Padding = new Thickness(24, 9, 24, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsCancel = true,
        };
        cancel.SetResourceReference(BackgroundProperty, "CritBrush");
        cancel.Foreground = Brushes.White;
        cancel.Click += (_, _) => Finish(cancelled: true);
        stack.Children.Add(cancel);

        var note = Text(test
                ? Loc.T("Dry run of your auto-shutdown settings.")
                : Loc.T("Cancelling pauses this rule until its condition clears. Apps will be force-closed."),
            11, FontWeights.Normal, "TextMuted", new Thickness(0, 12, 0, 0));
        note.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(note);

        var panel = new Border
        {
            Padding = new Thickness(28, 22, 28, 20),
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 3, Opacity = 0.5 },
        };
        panel.SetResourceReference(Border.BackgroundProperty, "PanelSolidBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "CritBrush");
        Content = panel;

        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { _lastTick = DateTime.Now; Tick(); _timer.Start(); Activate(); };
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

    /// <summary>
    /// Counts down by the time that actually passed between ticks, capped at one second per tick,
    /// so a PC that was asleep (or had its clock moved) does not find the countdown already expired.
    /// </summary>
    private void Tick()
    {
        var now = DateTime.Now;
        double elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (elapsed > 0) _remaining -= Math.Min(1.0, elapsed);

        _count.Text = Math.Max(0, Math.Ceiling(_remaining)).ToString("0");
        if (_remaining <= 0) Finish(cancelled: false);
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
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }
}
