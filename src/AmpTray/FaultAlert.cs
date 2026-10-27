using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;

namespace AmpTray;

/// <summary>
/// Full-screen fault alert: a pulsing red frame on every monitor (click-through, never steals focus)
/// plus an optional looping alarm tone.
/// </summary>
public sealed class FaultAlert : IDisposable
{
    private readonly List<Window> _overlays = new();
    private SoundPlayer? _player;
    private MemoryStream? _wav;
    private int _wavVolume = -1;

    public bool IsActive { get; private set; }

    public void Start(string message, bool flashScreen, bool sound, int volumePercent)
    {
        Stop();
        IsActive = true;
        if (flashScreen)
        {
            foreach (var screen in Forms.Screen.AllScreens)
                _overlays.Add(CreateOverlay(screen.Bounds, message));
        }
        if (sound) PlayAlarm(volumePercent);
    }

    public void Stop()
    {
        IsActive = false;
        foreach (var w in _overlays) w.Close();
        _overlays.Clear();
        _player?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _player?.Dispose();
        _wav?.Dispose();
    }

    // ===== Overlay =====
    private static Window CreateOverlay(System.Drawing.Rectangle bounds, string message)
    {
        var crit = ((SolidColorBrush)Application.Current.Resources["CritBrush"]).Color;

        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(crit),
            BorderThickness = new Thickness(14),
            Background = new RadialGradientBrush(Theme.WithAlpha(crit, 0x00), Theme.WithAlpha(crit, 0x40))
            {
                RadiusX = 0.75,
                RadiusY = 0.75,
            },
        };
        var banner = new Border
        {
            Background = new SolidColorBrush(crit),
            CornerRadius = new CornerRadius(0, 0, 12, 12),
            Padding = new Thickness(28, 10, 28, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "⚠  " + Loc.T("WIREVIEW FAULT") + ": " + message,
                Foreground = Brushes.White,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            },
        };
        var root = new Grid();
        root.Children.Add(frame);
        root.Children.Add(banner);

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Content = root,
            Title = "WireView fault alert",
        };
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE)
                | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        };
        win.Show();
        // Position in device pixels (same coordinate space as Screen.Bounds) so every monitor is covered exactly.
        Native.SetWindowPos(new WindowInteropHelper(win).Handle, Native.HWND_TOPMOST,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE);
        TopmostKeeper.Register(win); // stays above a borderless game; released when the overlay closes

        var pulse = new DoubleAnimation(1.0, 0.15, TimeSpan.FromMilliseconds(450))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        root.BeginAnimation(UIElement.OpacityProperty, pulse);
        return win;
    }

    // ===== Alarm sound =====
    private void PlayAlarm(int volumePercent)
    {
        volumePercent = Math.Clamp(volumePercent, 5, 100);
        if (_player == null || volumePercent != _wavVolume)
        {
            _player?.Dispose();
            _wav?.Dispose();
            _wav = BuildAlarmWav(volumePercent / 100.0);
            _player = new SoundPlayer(_wav);
            _player.Load();
            _wavVolume = volumePercent;
        }
        _player.PlayLooping();
    }

    /// <summary>One loop of a two-tone alarm: 960 Hz / 720 Hz beeps with a short pause.</summary>
    private static MemoryStream BuildAlarmWav(double volume)
    {
        const int rate = 22050;
        var tones = new (double Hz, double Seconds)[] { (960, 0.18), (0, 0.04), (720, 0.18), (0, 0.04), (960, 0.18), (0, 0.04), (720, 0.18), (0, 0.36) };
        var samples = new List<short>();
        foreach (var (hz, seconds) in tones)
        {
            int n = (int)(rate * seconds);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - i) / (rate * 0.008)); // 8 ms fade to avoid clicks
                double v = hz == 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / rate) * env * volume * 0.9;
                // Add a touch of the 3rd harmonic so it cuts through like a buzzer.
                if (hz > 0) v += Math.Sin(6 * Math.PI * hz * i / rate) * env * volume * 0.1;
                samples.Add((short)(v * short.MaxValue));
            }
        }

        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Count * 2;
            bw.Write("RIFF"u8); bw.Write(36 + dataBytes); bw.Write("WAVE"u8);
            bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
            bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
            bw.Write("data"u8); bw.Write(dataBytes);
            foreach (var s in samples) bw.Write(s);
        }
        ms.Position = 0;
        return ms;
    }
}

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    public const int WS_EX_LAYERED = 0x80000;
    public const uint LWA_ALPHA = 0x2;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    /// <summary>
    /// Extends the DWM frame over the entire client area. Together with a transparent composition
    /// target this makes a plain WPF window show the desktop through its translucent pixels, without
    /// the bitmap round trip of a layered (AllowsTransparency) window.
    /// </summary>
    public static void ExtendFrameIntoClientArea(IntPtr hWnd)
    {
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        try { DwmExtendFrameIntoClientArea(hWnd, ref m); } catch { }
    }

    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Collects garbage and gives unused pages back to the OS. Called once things have settled after
    /// startup and after the settings window closes; pages that are still needed come back on demand.
    /// </summary>
    public static void TrimMemory()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // Purely cosmetic; never fail over it.
        }
    }

    /// <summary>Asks DWM for rounded corners (Windows 11; silently ignored on Windows 10).</summary>
    public static void RoundCorners(IntPtr hWnd)
    {
        int pref = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch { }
    }

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
}
