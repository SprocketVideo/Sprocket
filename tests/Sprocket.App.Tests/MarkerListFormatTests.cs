using Sprocket.App;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Tests for the markers-panel row formatter (PLAN.md step 20) — the pure App helper, like
/// <c>TimelineMathTests</c>. The markers-panel drawing + the ruler/clip rendering rest on manual verification
/// (the App is a UI-bound <c>WinExe</c>).
/// </summary>
public class MarkerListFormatTests
{
    [Fact]
    public void Describe_Uses_The_Name_When_Present()
    {
        var marker = new Marker(Timecode.FromSeconds(65), "Intro");
        Assert.Equal("1:05.00 · Intro", MarkerListFormat.Describe(marker, 0));
    }

    [Fact]
    public void Describe_Falls_Back_To_A_Stable_Marker_Number_When_Unnamed()
    {
        var marker = new Marker(Timecode.FromSeconds(2));
        Assert.Equal("0:02.00 · Marker 3", MarkerListFormat.Describe(marker, 2)); // index 2 → "Marker 3"
    }

    [Fact]
    public void Describe_Appends_The_Span_For_A_Range_Marker()
    {
        var marker = new Marker(Timecode.FromSeconds(1), "B-roll", duration: Timecode.FromSeconds(3));
        Assert.Equal("0:01.00 · B-roll  (+0:03.00)", MarkerListFormat.Describe(marker, 0));
    }

    [Fact]
    public void Describe_Trims_Whitespace_Only_Names_To_The_Fallback()
    {
        var marker = new Marker(Timecode.Zero, "   ");
        Assert.Equal("0:00.00 · Marker 1", MarkerListFormat.Describe(marker, 0));
    }
}
