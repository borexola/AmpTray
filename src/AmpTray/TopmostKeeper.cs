using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AmpTray;

/// <summary>
/// Keeps our always-on-top windows above games.
///
/// Windows has a single band of "topmost" windows, ordered by who claimed the spot last. Many games
/// (borderless/windowed fullscreen) make their own window topmost when they start or regain focus,
/// which pushes ours underneath even though ours is still marked topmost. This class watches for the
/// foreground window changing, and checks periodically, and puts registered windows back on top when
/// something else is above them. It never activates them, so the game keeps keyboard and mouse focus.
///
/// It cannot draw over a game in exclusive fullscreen: there the game owns the display and no
/// desktop window is composed at all (see <see cref="IsExclusiveFullscreenActive"/>).
/// </summary>
public static class TopmostKeeper
{
    private static readonly List<Window> Windows = new();
    private static DispatcherTimer? _timer;
    private static IntPtr _hook;
    private static WinEventDelegate? _callback; // kept alive for as long as the hook exists
    private static bool _enabled;

    /// <summary>
    /// Follows the user's "Always on top" setting. Off: nothing is pushed back above other apps
    /// (the widget, fault flash and countdown keep plain Windows behaviour). Off by default until
    /// the settings are applied.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value && Windows.Count > 0) { EnsureRunning(); ReassertAll(); }
            else StopRunning();
        }
    }

    /// <summary>Keeps <paramref name="w"/> on top (while <see cref="Enabled"/>) until <see cref="Unregister"/> or it closes.</summary>
    public static void Register(Window w)
    {
        if (Windows.Contains(w)) return;
        Windows.Add(w);
        w.Closed += OnClosed;
        if (!_enabled) return;
        EnsureRunning();
        Reassert(w);
    }

    public static void Unregister(Window w)
    {
        w.Closed -= OnClosed;
        Windows.Remove(w);
        if (Windows.Count == 0) StopRunning();
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is Window w) Unregister(w);
    }

    private static void EnsureRunning()
    {
        if (_timer == null)
        {
            // A game may re-claim the top spot without changing focus (e.g. switching to fullscreen).
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => ReassertAll();
        }
        _timer.Start();

        if (_hook == IntPtr.Zero)
        {
            // Out-of-context events arrive on this (the UI) thread through its message loop.
            var dispatcher = Dispatcher.CurrentDispatcher;
            _callback = (_, _, _, _, _, _, _) =>
            {
                // The new foreground app finishes raising itself after this event: act right after it.
                dispatcher.BeginInvoke(ReassertAll, DispatcherPriority.Background);
            };
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }
    }

    private static void StopRunning()
    {
        _timer?.Stop();
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _callback = null;
        }
    }

    private static void ReassertAll()
    {
        foreach (var w in Windows.ToArray()) Reassert(w);
    }

    private static void Reassert(Window w)
    {
        if (!_enabled || !w.IsVisible) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || !IsCovered(hwnd)) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// True when a visible window of another process sits above ours in the z-order and overlaps it.
    /// Re-ordering only then avoids needless work every second.
    /// </summary>
    private static bool IsCovered(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var mine)) return false;
        uint self = (uint)Environment.ProcessId;
        for (var h = GetWindow(hwnd, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
        {
            if (!IsWindowVisible(h)) continue;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) continue;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            // Click-through overlays (Discord, GeForce, Game Bar, RTSS) draw over everything and fight for
            // the top spot themselves; they don't hide us in a way that matters, so leave them be.
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) continue;
            if (IsShellSurface(h)) continue; // Start, search, notifications: let those cover us while open
            if (!GetWindowRect(h, out var r)) continue;
            if (r.L < mine.R && r.R > mine.L && r.T < mine.B && r.B > mine.T) return true;
        }
        return false;
    }

    private static bool IsShellSurface(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(64);
        if (GetClassName(h, sb, sb.Capacity) == 0) return false;
        string c = sb.ToString();
        return c is "Windows.UI.Core.CoreWindow" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow" or "TopLevelWindowForOverflowXamlIsland"
            or "NotifyIconOverflowWindow" or "MultitaskingViewFrame" or "ForegroundStaging";
    }

    /// <summary>
    /// A Direct3D app is running in exclusive fullscreen: nothing on the desktop can be shown over it,
    /// topmost or not. The game has to run in borderless/windowed fullscreen for the widget to appear.
    /// </summary>
    public static bool IsExclusiveFullscreenActive()
    {
        try { return SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN; }
        catch { return false; }
    }

    // ===== Win32 =====
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint GW_HWNDPREV = 3;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    private const int DWMWA_CLOAKED = 14;
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
