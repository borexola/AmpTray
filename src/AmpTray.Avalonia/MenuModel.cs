using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace AmpTray.Avalonia;

/// <summary>One entry of the app menu; the same list renders as the tray's native menu and the widget's context menu.</summary>
public sealed class MenuEntry
{
    public static readonly MenuEntry Separator = new() { IsSeparator = true, Text = () => "" };

    public required Func<string> Text { get; init; }
    public Func<bool>? Visible { get; init; }
    public Func<bool>? Checked { get; init; }
    public Func<bool>? Enabled { get; init; }
    public Action? Click { get; init; }
    public List<MenuEntry>? Children { get; init; }
    public bool IsSeparator { get; init; }
}

/// <summary>Builds Avalonia menus from <see cref="MenuEntry"/> lists. Texts and checks are read at build time, so rebuild to refresh.</summary>
public static class MenuModel
{
    public static ContextMenu ToContextMenu(IEnumerable<MenuEntry> entries)
    {
        var menu = new ContextMenu();
        foreach (var item in Items(entries)) menu.Items.Add(item);
        return menu;
    }

    private static IEnumerable<Control> Items(IEnumerable<MenuEntry> entries)
    {
        foreach (var e in entries)
        {
            if (e.Visible != null && !e.Visible()) continue;
            if (e.IsSeparator) { yield return new Separator(); continue; }
            var m = new MenuItem { Header = e.Text() };
            if (e.Checked != null) { m.ToggleType = MenuItemToggleType.CheckBox; m.IsChecked = e.Checked(); }
            if (e.Enabled != null) m.IsEnabled = e.Enabled();
            if (e.Children != null) foreach (var c in Items(e.Children)) m.Items.Add(c);
            if (e.Click is Action click) m.Click += (_, _) => click();
            yield return m;
        }
    }

    public static NativeMenu ToNativeMenu(IEnumerable<MenuEntry> entries)
    {
        var menu = new NativeMenu();
        foreach (var e in entries)
        {
            if (e.Visible != null && !e.Visible()) continue;
            if (e.IsSeparator) { menu.Items.Add(new NativeMenuItemSeparator()); continue; }
            var m = new NativeMenuItem(e.Text());
            if (e.Checked != null) { m.ToggleType = NativeMenuItemToggleType.CheckBox; m.IsChecked = e.Checked(); }
            if (e.Enabled != null) m.IsEnabled = e.Enabled();
            if (e.Children != null) m.Menu = ToNativeMenu(e.Children);
            if (e.Click is Action click) m.Click += (_, _) => click();
            menu.Items.Add(m);
        }
        return menu;
    }
}
