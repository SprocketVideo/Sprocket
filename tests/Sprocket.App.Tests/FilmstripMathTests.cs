using System;
using Sprocket.App.MediaBrowser;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>
/// Headless tests for the hover-scrub filmstrip's pure geometry (see <see cref="FilmstripMath"/>): the per-slot
/// sample times and the pointer→slot mapping. The decode + gesture wiring rest on manual verification (the App is
/// a UI/IO-bound WinExe), mirroring the steps 15/17 "pure helpers tested + UI manual" split.
/// </summary>
public class FilmstripMathTests
{
    private static readonly Timecode TenSeconds = Timecode.FromSeconds(10);

    // ── SampleTime ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SampleTime_Is_Strictly_Increasing_And_Within_The_Open_Interval()
    {
        const int count = 16;
        long previous = -1;
        for (int i = 0; i < count; i++)
        {
            Timecode t = FilmstripMath.SampleTime(TenSeconds, count, i);
            Assert.True(t.Ticks > 0, "first sample must be past the leader frame at 0");
            Assert.True(t.Ticks < TenSeconds.Ticks, "last sample must be before EOF");
            Assert.True(t.Ticks > previous, "samples must strictly increase");
            previous = t.Ticks;
        }
    }

    [Fact]
    public void SampleTime_With_Count_One_Is_The_Midpoint()
    {
        Timecode t = FilmstripMath.SampleTime(TenSeconds, count: 1, index: 0);
        Assert.Equal(TenSeconds.Ticks / 2, t.Ticks);
    }

    [Fact]
    public void SampleTime_Slices_Are_Evenly_Spaced()
    {
        const int count = 8;
        // Centres sit at (2i+1)/(2*count) of the duration → a uniform step of duration/count between them.
        long step = TenSeconds.Ticks / count;
        long first = FilmstripMath.SampleTime(TenSeconds, count, 0).Ticks;
        long second = FilmstripMath.SampleTime(TenSeconds, count, 1).Ticks;
        Assert.Equal(step, second - first);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SampleTime_Rejects_A_NonPositive_Count(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FilmstripMath.SampleTime(TenSeconds, count, 0));

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void SampleTime_Rejects_An_OutOfRange_Index(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FilmstripMath.SampleTime(TenSeconds, count: 4, index));

    // ── SlotAt ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SlotAt_Maps_The_Left_Edge_To_Slot_Zero() =>
        Assert.Equal(0, FilmstripMath.SlotAt(0, width: 104, count: 16));

    [Fact]
    public void SlotAt_Maps_The_Right_Edge_And_Beyond_To_The_Last_Slot()
    {
        Assert.Equal(15, FilmstripMath.SlotAt(104, width: 104, count: 16));   // at the width
        Assert.Equal(15, FilmstripMath.SlotAt(999, width: 104, count: 16));   // past the width
    }

    [Fact]
    public void SlotAt_Clamps_A_Negative_Position_To_Zero() =>
        Assert.Equal(0, FilmstripMath.SlotAt(-20, width: 104, count: 16));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void SlotAt_Returns_Minus_One_For_A_NonPositive_Width(double width) =>
        Assert.Equal(-1, FilmstripMath.SlotAt(10, width, count: 16));

    [Fact]
    public void SlotAt_Splits_The_Width_Into_Uniform_Slices()
    {
        // width 160, count 4 → 40px slices: [0,40)→0, [40,80)→1, [80,120)→2, [120,160)→3.
        Assert.Equal(0, FilmstripMath.SlotAt(0, 160, 4));
        Assert.Equal(0, FilmstripMath.SlotAt(39, 160, 4));
        Assert.Equal(1, FilmstripMath.SlotAt(40, 160, 4));
        Assert.Equal(2, FilmstripMath.SlotAt(80, 160, 4));
        Assert.Equal(3, FilmstripMath.SlotAt(120, 160, 4));
    }
}
