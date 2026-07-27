using Avalonia;
using Avalonia.Controls;
using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The pure geometry decisions behind the session-swap window placement (<see cref="WindowPlacement"/>).
/// File ▸ New / Open / Open Sample build a replacement <c>MainWindow</c>, so the outgoing window's live state
/// has to be carried across — the regression this covers is a shell maximized (or resized) since launch
/// shrinking back to the XAML default the moment the sample project loaded. Applying the placement to a real
/// window rests on manual verification, like the shell's other chrome.
/// </summary>
public class WindowPlacementTests
{
    private static WindowPlacement Placement(
        WindowState state, WindowState beforeFullScreen = WindowState.Normal) =>
        new(state, beforeFullScreen, new PixelPoint(120, 40), new Size(1600, 900));

    /// <summary>A window minimized at the moment of the swap must not reopen minimized — the user asked to see
    /// a project.</summary>
    [Fact]
    public void MinimizedCarriesTheStateItWasMinimizedFrom()
    {
        Assert.Equal(WindowState.Maximized,
            WindowPlacement.StateToCarry(WindowState.Minimized, WindowState.Maximized));
        Assert.Equal(WindowState.Normal,
            WindowPlacement.StateToCarry(WindowState.Minimized, WindowState.Normal));
    }

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    public void EveryOtherStateIsCarriedAsIs(WindowState live)
    {
        Assert.Equal(live, WindowPlacement.StateToCarry(live, WindowState.Normal));
    }

    /// <summary>Only a Normal window's size is a restore rectangle; a maximized / full-screen size describes the
    /// screen, and applying it would leave an un-maximized window filling the display.</summary>
    [Fact]
    public void SizeIsCarriedOnlyWhileNormal()
    {
        Assert.True(Placement(WindowState.Normal).CarriesSize);
        Assert.False(Placement(WindowState.Maximized).CarriesSize);
        Assert.False(Placement(WindowState.FullScreen).CarriesSize);
    }

    /// <summary>Full screen is transient and never persisted, so a swap while full-screen still hands on the
    /// maximized-or-not the window would have gone back to.</summary>
    [Fact]
    public void FullScreenPersistsAsItsPreFullScreenState()
    {
        Assert.Equal(WindowState.Maximized,
            Placement(WindowState.FullScreen, WindowState.Maximized).PersistableState);
        Assert.Equal(WindowState.Normal,
            Placement(WindowState.FullScreen, WindowState.Normal).PersistableState);
    }

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    public void OtherStatesPersistThemselves(WindowState state)
    {
        Assert.Equal(state, Placement(state, WindowState.Maximized).PersistableState);
    }
}
