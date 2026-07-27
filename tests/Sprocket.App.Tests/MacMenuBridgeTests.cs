using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Tests for the macOS system-menu mirror (<see cref="MacMenuBridge"/>, PLAN.md step 11). The mirroring is a
/// pure transform from the live Avalonia <see cref="Menu"/> to a <see cref="NativeMenu"/>, so it is exercised
/// on any OS; only the <c>NativeMenu.SetMenu</c> attachment in <c>Attach</c> is macOS-specific. What matters is
/// that the mirror stays a *view* of the source: clicks forward back, submenus re-read the source on open (so
/// the on-open <c>Refresh*Menu</c> passes and their <c>ItemsSource</c> swaps show up), and hiding an item
/// doesn't strand the separator that preceded it.
/// </summary>
public class MacMenuBridgeTests
{
    private static Menu BuildSource(out MenuItem file, out MenuItem openRecent, out MenuItem snapping)
    {
        openRecent = new MenuItem { Header = "Open _Recent" };
        openRecent.ItemsSource = new List<MenuItem> { new() { Header = "a.sprocket" } };

        snapping = new MenuItem { Header = "_Snapping", ToggleType = MenuItemToggleType.CheckBox };

        file = new MenuItem { Header = "_File" };
        file.Items.Add(new MenuItem { Header = "_New Project", InputGesture = KeyGesture.Parse("Ctrl+N") });
        file.Items.Add(openRecent);
        file.Items.Add(new Separator());
        file.Items.Add(snapping);

        var menu = new Menu();
        menu.Items.Add(file);
        return menu;
    }

    private static IReadOnlyList<NativeMenuItemBase> ItemsOf(NativeMenu menu) => menu.Items.ToList();

    private static NativeMenuItem Top(NativeMenu root) => Assert.IsType<NativeMenuItem>(ItemsOf(root)[0]);

    /// <summary>Stands in for macOS asking the exporter to refresh a submenu before displaying it. Avalonia
    /// keeps <c>NativeMenu.RaiseNeedsUpdate</c> non-public (only a platform exporter calls it), so the test
    /// reaches it reflectively rather than re-raising the event by hand — the point is to prove the bridge
    /// really subscribed to <c>NeedsUpdate</c>.</summary>
    private static void Open(NativeMenu menu) =>
        typeof(NativeMenu)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(m => m.Name.EndsWith("RaiseNeedsUpdate", StringComparison.Ordinal) && m.GetParameters().Length == 0)
            .Invoke(menu, null);

    /// <summary>Stands in for the user picking a native menu item.</summary>
    private static void Click(NativeMenuItemBase item) =>
        ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();

    [Fact]
    public void Mirrors_Structure_And_Strips_Mnemonics()
    {
        NativeMenu root = MacMenuBridge.BuildMenuBar(BuildSource(out _, out _, out _));

        NativeMenuItem top = Top(root);
        Assert.Equal("File", top.Header);

        IReadOnlyList<NativeMenuItemBase> children = ItemsOf(top.Menu!);
        Assert.Equal("New Project", Assert.IsType<NativeMenuItem>(children[0]).Header);
        Assert.Equal("Open Recent", Assert.IsType<NativeMenuItem>(children[1]).Header);
        Assert.IsType<NativeMenuItemSeparator>(children[2]);
        Assert.Equal("Snapping", Assert.IsType<NativeMenuItem>(children[3]).Header);
    }

    [Fact]
    public void Copies_Gesture_And_ToggleType()
    {
        NativeMenu root = MacMenuBridge.BuildMenuBar(BuildSource(out _, out _, out _));
        IReadOnlyList<NativeMenuItemBase> children = ItemsOf(Top(root).Menu!);

        Assert.Equal(KeyGesture.Parse("Ctrl+N"), Assert.IsType<NativeMenuItem>(children[0]).Gesture);
        Assert.Equal(MenuItemToggleType.CheckBox, Assert.IsType<NativeMenuItem>(children[3]).ToggleType);
    }

    [Fact]
    public void Mirrors_ItemsSource_Driven_Submenus()
    {
        NativeMenu root = MacMenuBridge.BuildMenuBar(BuildSource(out _, out MenuItem openRecent, out _));

        NativeMenuItem recent = Assert.IsType<NativeMenuItem>(ItemsOf(Top(root).Menu!)[1]);
        Assert.Equal("a.sprocket", Assert.IsType<NativeMenuItem>(ItemsOf(recent.Menu!)[0]).Header);

        // The runtime-populated menus (Effects, Clip ▸ Insert, Sequence ▸ Open Sequence) replace ItemsSource
        // wholesale from their SubmenuOpened handler; the mirror must pick that up rather than cache it.
        openRecent.ItemsSource = new List<MenuItem> { new() { Header = "b.sprocket" }, new() { Header = "c.sprocket" } };
        Open(recent.Menu!);

        Assert.Equal(2, ItemsOf(recent.Menu!).Count);
        Assert.Equal("b.sprocket", Assert.IsType<NativeMenuItem>(ItemsOf(recent.Menu!)[0]).Header);
    }

    [Fact]
    public void Opening_A_Submenu_Raises_SubmenuOpened_On_The_Source_First()
    {
        NativeMenu root = MacMenuBridge.BuildMenuBar(BuildSource(out MenuItem file, out _, out _));

        // This is what keeps RefreshEditMenu / RefreshClipMenu / RefreshEffectsMenu driving the native menu.
        var opened = 0;
        file.AddHandler(MenuItem.SubmenuOpenedEvent, (_, _) =>
        {
            opened++;
            file.Items.Add(new MenuItem { Header = "Added _On Open" });
        });

        Open(Top(root).Menu!);

        Assert.Equal(1, opened);
        Assert.Equal("Added On Open", Assert.IsType<NativeMenuItem>(ItemsOf(Top(root).Menu!)[4]).Header);
    }

    [Fact]
    public void Click_Forwards_To_The_Source_Item()
    {
        Menu source = BuildSource(out MenuItem file, out _, out _);
        NativeMenu root = MacMenuBridge.BuildMenuBar(source);

        var clicked = 0;
        ((MenuItem)file.Items[0]!).AddHandler(MenuItem.ClickEvent, (_, _) => clicked++);

        Click(ItemsOf(Top(root).Menu!)[0]);

        Assert.Equal(1, clicked);
    }

    [Fact]
    public void Checkable_Click_Toggles_The_Source_Before_Its_Handlers_Run()
    {
        Menu source = BuildSource(out _, out _, out MenuItem snapping);
        NativeMenu root = MacMenuBridge.BuildMenuBar(source);

        // Avalonia flips IsChecked before the Click handlers run and MainWindow's View-menu handlers read it
        // (`_snappingToggle.IsChecked = _snappingMenuItem.IsChecked`), so the mirror must preserve that order.
        bool? seen = null;
        snapping.AddHandler(MenuItem.ClickEvent, (_, _) => seen = snapping.IsChecked);

        Click(ItemsOf(Top(root).Menu!)[3]);

        Assert.True(seen);
        Assert.True(snapping.IsChecked);
    }

    [Fact]
    public void Hidden_Items_Are_Skipped_Without_Stranding_Their_Separator()
    {
        Menu source = BuildSource(out _, out _, out MenuItem snapping);

        // Exactly what MainWindow does on macOS for About / Preferences / Exit: hide the source item, which
        // would otherwise leave the separator that preceded it dangling at the end of the menu.
        snapping.IsVisible = false;

        IReadOnlyList<NativeMenuItemBase> children = ItemsOf(Top(MacMenuBridge.BuildMenuBar(source)).Menu!);

        Assert.Equal(2, children.Count);
        Assert.DoesNotContain(children, c => c is NativeMenuItemSeparator);
    }

    [Fact]
    public void Window_Menu_Gets_The_Mac_Minimize_And_Zoom_Commands()
    {
        var reset = new MenuItem { Header = "_Reset Layout" };
        var windowMenu = new MenuItem { Header = "_Window", Name = "WindowMenu" };
        windowMenu.Items.Add(reset);
        var source = new Menu();
        source.Items.Add(windowMenu);

        NativeMenu root = MacMenuBridge.BuildMenuBar(source, () =>
            [new NativeMenuItemSeparator(), new NativeMenuItem("Minimize"), new NativeMenuItem("Zoom")]);

        IReadOnlyList<NativeMenuItemBase> children = ItemsOf(Top(root).Menu!);
        Assert.Equal("Reset Layout", Assert.IsType<NativeMenuItem>(children[0]).Header);
        Assert.IsType<NativeMenuItemSeparator>(children[1]);
        Assert.Equal("Minimize", Assert.IsType<NativeMenuItem>(children[2]).Header);
        Assert.Equal("Zoom", Assert.IsType<NativeMenuItem>(children[3]).Header);
    }
}
