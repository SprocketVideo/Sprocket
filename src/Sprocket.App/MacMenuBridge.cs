using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Sprocket.App;

/// <summary>
/// Mirrors the editor's inline <see cref="Menu"/> into the macOS system menu bar (PLAN.md step 11).
///
/// <para>macOS puts an app's menus at the top of the screen, not inside the window, so on that platform
/// <see cref="MainWindow.ConfigureWindowChrome"/> hides the in-window menu bar and this bridge re-publishes it
/// as a <see cref="NativeMenu"/>. It is a <i>mirror</i>, not a replacement: every native item forwards its
/// click back to the source <see cref="MenuItem"/>, so all the existing wiring in
/// <c>MainWindow.WireMenu</c>/<c>WireCommandMenus</c> — and the on-open <c>Refresh*Menu</c> passes that drive
/// context enablement, checkmarks and the runtime-populated Effects / Insert / Open Sequence submenus — keeps
/// working untouched. Each submenu is rebuilt from its source on every open, which is what makes the
/// <c>ItemsSource</c> swaps in <c>RefreshEffectsMenu</c> show up natively.</para>
///
/// <para>The three items that macOS convention places in the application menu (About, Preferences, Quit) are
/// hidden in the window menus by the caller and re-hosted here; Avalonia contributes the standard Quit/Hide
/// entries itself (<c>MacOSPlatformOptions.DisableDefaultApplicationMenuItems</c> stays false).</para>
/// </summary>
internal static class MacMenuBridge
{
    /// <summary>Publishes <paramref name="source"/> as <paramref name="window"/>'s native menu bar, and the
    /// About / Preferences items as the application menu. Safe to call once per window — the app menu is an
    /// attached property, so a new session's window replaces the previous mapping rather than duplicating it.</summary>
    public static void Attach(Window window, Menu source, MenuItem? aboutItem, MenuItem? preferencesItem)
    {
        NativeMenu.SetMenu(window, BuildMenuBar(source, () => WindowMenuExtras(window)));

        if (Application.Current is { } app && (aboutItem is not null || preferencesItem is not null))
            NativeMenu.SetMenu(app, ApplicationMenu(aboutItem, preferencesItem));
    }

    /// <summary>The mirroring itself, free of any platform dependency so it is testable off macOS.
    /// <paramref name="windowMenuExtras"/> supplies the Minimize / Zoom commands macOS expects in the Window
    /// menu and Sprocket has no in-window equivalent of, now that the native traffic lights replaced our
    /// caption buttons.</summary>
    internal static NativeMenu BuildMenuBar(Menu source, Func<NativeMenuItemBase[]>? windowMenuExtras = null)
    {
        var root = new NativeMenu();
        foreach (MenuItem top in source.Items.OfType<MenuItem>())
        {
            if (!top.IsVisible)
                continue;
            NativeMenuItemBase[] extras = IsWindowMenu(top) && windowMenuExtras is not null
                ? windowMenuExtras()
                : [];
            root.Items.Add(Mirror(top, forceSubmenu: true, extras));
        }
        return root;
    }

    private static bool IsWindowMenu(MenuItem top) => top.Name == "WindowMenu";

    private static NativeMenuItemBase[] WindowMenuExtras(Window window)
    {
        var separator = new NativeMenuItemSeparator();
        var minimize = new NativeMenuItem("Minimize") { Gesture = KeyGesture.Parse("Cmd+M") };
        minimize.Click += (_, _) => window.WindowState = WindowState.Minimized;
        var zoom = new NativeMenuItem("Zoom");
        zoom.Click += (_, _) => window.WindowState =
            window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        return [separator, minimize, zoom];
    }

    private static NativeMenu ApplicationMenu(MenuItem? aboutItem, MenuItem? preferencesItem)
    {
        var menu = new NativeMenu();
        if (aboutItem is not null)
            menu.Items.Add(Forward(aboutItem, "About Sprocket"));
        if (aboutItem is not null && preferencesItem is not null)
            menu.Items.Add(new NativeMenuItemSeparator());
        if (preferencesItem is not null)
            menu.Items.Add(Forward(preferencesItem, "Preferences…"));
        return menu;
    }

    /// <summary>A native item that carries its own header but delegates to <paramref name="src"/>.</summary>
    private static NativeMenuItem Forward(MenuItem src, string header)
    {
        var item = new NativeMenuItem(header) { Gesture = src.InputGesture, IsEnabled = src.IsEnabled };
        item.Click += (_, _) => Invoke(src);
        return item;
    }

    /// <summary>Mirrors one source item. Items with children (or <paramref name="forceSubmenu"/> — a top-level
    /// menu whose children only arrive on open, like Effects) become submenus that re-read the source each
    /// time macOS asks for an update; leaf items forward their click.</summary>
    private static NativeMenuItem Mirror(MenuItem src, bool forceSubmenu = false, NativeMenuItemBase[]? extras = null)
    {
        var item = new NativeMenuItem(Header(src))
        {
            IsEnabled = src.IsEnabled,
            Gesture = src.InputGesture,
            ToggleType = src.ToggleType,
            IsChecked = src.IsChecked,
        };

        if (forceSubmenu || Children(src).Any())
        {
            var submenu = new NativeMenu();
            item.Menu = submenu;
            submenu.NeedsUpdate += (_, _) =>
            {
                // Let the window's own SubmenuOpened handlers (RefreshClipMenu, RefreshEffectsMenu, …) bring
                // the source items up to date, then mirror whatever they produced.
                src.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
                Rebuild(submenu, src, extras);
            };
            Rebuild(submenu, src, extras);
            return item;
        }

        item.Click += (_, _) =>
        {
            // Avalonia toggles a checkable MenuItem's IsChecked *before* its Click handlers run, and the View
            // menu's handlers read it (MainWindow.WireCommandMenus) — reproduce that ordering.
            if (src.ToggleType != MenuItemToggleType.None)
                src.IsChecked = !src.IsChecked;
            Invoke(src);
        };
        return item;
    }

    private static void Rebuild(NativeMenu target, MenuItem src, NativeMenuItemBase[]? extras)
    {
        var built = new List<NativeMenuItemBase>();
        foreach (object? child in Children(src))
        {
            switch (child)
            {
                case Separator:
                    built.Add(new NativeMenuItemSeparator());
                    break;
                case MenuItem { IsVisible: true } childItem:
                    built.Add(Mirror(childItem));
                    break;
            }
        }
        built.AddRange(extras ?? []);

        target.Items.Clear();
        foreach (NativeMenuItemBase item in Tidy(built))
            target.Items.Add(item);
    }

    /// <summary>Drops leading, trailing and repeated separators. Hiding the items macOS moves to the
    /// application menu (About, Preferences, Exit) otherwise strands the separator that preceded them.</summary>
    private static IEnumerable<NativeMenuItemBase> Tidy(List<NativeMenuItemBase> items)
    {
        bool anyEmitted = false;
        bool separatorPending = false;
        foreach (NativeMenuItemBase item in items)
        {
            if (item is NativeMenuItemSeparator)
            {
                separatorPending = anyEmitted;
                continue;
            }
            if (separatorPending)
            {
                yield return new NativeMenuItemSeparator();
                separatorPending = false;
            }
            anyEmitted = true;
            yield return item;
        }
    }

    /// <summary>The source item's children. Submenus built at runtime (Effects, Clip ▸ Insert, Sequence ▸ Open
    /// Sequence) assign an <see cref="ItemsControl.ItemsSource"/> wholesale rather than editing Items.</summary>
    private static IEnumerable<object?> Children(MenuItem src) =>
        (src.ItemsSource ?? src.Items).Cast<object?>();

    private static void Invoke(MenuItem src) => src.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static string Header(MenuItem src) => StripMnemonic(src.Header as string ?? src.Header?.ToString() ?? "");

    /// <summary>Drops the Avalonia access-key markers — macOS menus don't use mnemonics. A doubled underscore
    /// is Avalonia's escape for a literal one.</summary>
    private static string StripMnemonic(string header)
    {
        if (!header.Contains('_'))
            return header;
        var sb = new StringBuilder(header.Length);
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] != '_')
            {
                sb.Append(header[i]);
            }
            else if (i + 1 < header.Length && header[i + 1] == '_')
            {
                sb.Append('_');
                i++;
            }
        }
        return sb.ToString();
    }
}
