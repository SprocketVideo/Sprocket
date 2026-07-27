using System.Collections.Generic;
using Avalonia.Input;
using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// The work-area focus ring's pure arithmetic + key mapping (the focus moves themselves, like the shell's other
/// keyboard behavior, rest on manual verification).
/// </summary>
public class WorkAreaFocusTests
{
    private static bool All(WorkArea area) => true;

    private static Func<WorkArea, bool> Only(params WorkArea[] available)
    {
        var set = new HashSet<WorkArea>(available);
        return set.Contains;
    }

    [Fact]
    public void RingIsTheShellsReadingOrder()
    {
        Assert.Equal(
            new[] { WorkArea.Project, WorkArea.Monitor, WorkArea.Timeline, WorkArea.Inspector },
            WorkAreaFocus.Ring);
    }

    [Fact]
    public void TabWalksTheRingForward()
    {
        Assert.Equal(WorkArea.Monitor, WorkAreaFocus.Advance(WorkArea.Project, forward: true, All));
        Assert.Equal(WorkArea.Timeline, WorkAreaFocus.Advance(WorkArea.Monitor, forward: true, All));
        Assert.Equal(WorkArea.Inspector, WorkAreaFocus.Advance(WorkArea.Timeline, forward: true, All));
    }

    [Fact]
    public void ShiftTabReversesIt()
    {
        Assert.Equal(WorkArea.Timeline, WorkAreaFocus.Advance(WorkArea.Inspector, forward: false, All));
        Assert.Equal(WorkArea.Monitor, WorkAreaFocus.Advance(WorkArea.Timeline, forward: false, All));
        Assert.Equal(WorkArea.Project, WorkAreaFocus.Advance(WorkArea.Monitor, forward: false, All));
    }

    [Fact]
    public void TheRingWrapsInBothDirections()
    {
        Assert.Equal(WorkArea.Project, WorkAreaFocus.Advance(WorkArea.Inspector, forward: true, All));
        Assert.Equal(WorkArea.Inspector, WorkAreaFocus.Advance(WorkArea.Project, forward: false, All));
    }

    /// <summary>View ▸ Show Project / Show Inspector hide a pane; the ring must step straight over it.</summary>
    [Fact]
    public void HiddenAreasAreSkipped()
    {
        Func<WorkArea, bool> noInspector = Only(WorkArea.Project, WorkArea.Monitor, WorkArea.Timeline);
        Assert.Equal(WorkArea.Project, WorkAreaFocus.Advance(WorkArea.Timeline, forward: true, noInspector));

        Func<WorkArea, bool> noProject = Only(WorkArea.Monitor, WorkArea.Timeline, WorkArea.Inspector);
        Assert.Equal(WorkArea.Monitor, WorkAreaFocus.Advance(WorkArea.Inspector, forward: true, noProject));
        Assert.Equal(WorkArea.Inspector, WorkAreaFocus.Advance(WorkArea.Monitor, forward: false, noProject));
    }

    /// <summary>Both side panes hidden leaves a two-area ring that still alternates.</summary>
    [Fact]
    public void BothSidePanesHiddenLeavesMonitorAndTimeline()
    {
        Func<WorkArea, bool> center = Only(WorkArea.Monitor, WorkArea.Timeline);
        Assert.Equal(WorkArea.Timeline, WorkAreaFocus.Advance(WorkArea.Monitor, forward: true, center));
        Assert.Equal(WorkArea.Monitor, WorkAreaFocus.Advance(WorkArea.Timeline, forward: true, center));
        Assert.Equal(WorkArea.Monitor, WorkAreaFocus.Advance(WorkArea.Timeline, forward: false, center));
    }

    /// <summary>The full-screen preview case: Tab stays put rather than parking focus off screen.</summary>
    [Fact]
    public void ASingleAvailableAreaIsANoOp()
    {
        Func<WorkArea, bool> monitorOnly = Only(WorkArea.Monitor);
        Assert.Equal(WorkArea.Monitor, WorkAreaFocus.Advance(WorkArea.Monitor, forward: true, monitorOnly));
        Assert.Equal(WorkArea.Monitor, WorkAreaFocus.Advance(WorkArea.Monitor, forward: false, monitorOnly));
    }

    /// <summary>An area that has just been hidden while it was the active one still finds the next one.</summary>
    [Fact]
    public void AdvanceFromAnUnavailableCurrentAreaStillMoves()
    {
        Func<WorkArea, bool> noProject = Only(WorkArea.Monitor, WorkArea.Timeline, WorkArea.Inspector);
        Assert.Equal(WorkArea.Monitor, WorkAreaFocus.Advance(WorkArea.Project, forward: true, noProject));
        Assert.Equal(WorkArea.Inspector, WorkAreaFocus.Advance(WorkArea.Project, forward: false, noProject));
    }

    [Fact]
    public void NoAvailableAreaLeavesFocusAlone()
    {
        Assert.Null(WorkAreaFocus.Advance(WorkArea.Timeline, forward: true, _ => false));
    }

    /// <summary>Shift+1 Project, Shift+2 Timeline, Shift+3 Monitor, Shift+4 Inspector — number row and numpad.</summary>
    [Theory]
    [InlineData(Key.D1, WorkArea.Project)]
    [InlineData(Key.D2, WorkArea.Timeline)]
    [InlineData(Key.D3, WorkArea.Monitor)]
    [InlineData(Key.D4, WorkArea.Inspector)]
    [InlineData(Key.NumPad1, WorkArea.Project)]
    [InlineData(Key.NumPad2, WorkArea.Timeline)]
    [InlineData(Key.NumPad3, WorkArea.Monitor)]
    [InlineData(Key.NumPad4, WorkArea.Inspector)]
    public void DirectKeysMapToTheirArea(Key key, WorkArea expected)
    {
        Assert.True(WorkAreaFocus.TryDirectKey(key, out WorkArea area));
        Assert.Equal(expected, area);
    }

    /// <summary>Digits past 4 must fall through — 5–9 stay with the multicam angle switcher (PLAN.md step 24).</summary>
    [Theory]
    [InlineData(Key.D5)]
    [InlineData(Key.D9)]
    [InlineData(Key.NumPad5)]
    [InlineData(Key.D0)]
    [InlineData(Key.M)]
    public void OtherKeysAreNotWorkAreaChords(Key key)
    {
        Assert.False(WorkAreaFocus.TryDirectKey(key, out _));
    }
}
