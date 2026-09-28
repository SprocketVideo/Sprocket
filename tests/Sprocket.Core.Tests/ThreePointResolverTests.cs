using Sprocket.Core.Commands;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// <see cref="ThreePointResolver.Resolve(Timecode?, Timecode?, Timecode, bool, Timecode?, Timecode?, Timecode)"/>
/// across the combinations of the four marks (Premiere's precedence), backtiming, short-source clamping, stills, and
/// the 4-point note. Media is 10s long; the playhead sits at 20s.
/// </summary>
public class ThreePointResolverTests
{
    private static Timecode S(double seconds) => Timecode.FromSeconds(seconds);

    private static ThreePointRange Resolve(
        double? srcIn, double? srcOut, double? seqIn, double? seqOut, double length = 10, bool unbounded = false) =>
        ThreePointResolver.Resolve(
            srcIn is { } a ? S(a) : null, srcOut is { } b ? S(b) : null, S(length), unbounded,
            seqIn is { } c ? S(c) : null, seqOut is { } d ? S(d) : null, S(20))!;

    // (srcIn, srcOut, seqIn, seqOut) → (sourceIn, sourceOut, recordIn)
    [Theory]
    [InlineData(null, null, null, null, 0, 10, 20)]   // no marks: whole media at the playhead
    [InlineData(2.0, null, null, null, 2, 10, 20)]    // source In only
    [InlineData(null, 6.0, null, null, 0, 6, 20)]     // source Out only
    [InlineData(2.0, 6.0, null, null, 2, 6, 20)]      // source range at the playhead
    [InlineData(2.0, 6.0, 30.0, null, 2, 6, 30)]      // sequence In: starts there, source duration
    [InlineData(2.0, 6.0, null, 40.0, 2, 6, 36)]      // sequence Out only: backtimed to end there
    [InlineData(2.0, null, 30.0, 33.0, 2, 5, 30)]     // sequence range fixes the duration from the source In
    [InlineData(null, 8.0, 30.0, 33.0, 5, 8, 30)]     // only source Out: backtimed from it
    [InlineData(null, null, 30.0, 33.0, 0, 3, 30)]    // sequence range, no source marks
    [InlineData(2.0, 6.0, 30.0, 33.0, 2, 5, 30)]      // 4-point: source Out ignored
    public void Resolves_Premiere_Precedence(
        double? srcIn, double? srcOut, double? seqIn, double? seqOut, double expIn, double expOut, double expRecord)
    {
        ThreePointRange r = Resolve(srcIn, srcOut, seqIn, seqOut);

        Assert.Equal(S(expIn), r.SourceIn);
        Assert.Equal(S(expOut), r.SourceOut);
        Assert.Equal(S(expRecord), r.RecordIn);
        Assert.Equal(r.RecordIn + (r.SourceOut - r.SourceIn), r.RecordOut);
    }

    [Fact]
    public void Four_Point_Edit_Notes_The_Ignored_Source_Out()
    {
        Assert.Contains(ThreePointResolver.SourceOutIgnoredNote, Resolve(2, 6, 30, 33).Notes);
        Assert.Empty(Resolve(2, null, 30, 33).Notes);
    }

    [Fact]
    public void Short_Source_Is_Clamped_With_A_Note()
    {
        ThreePointRange forward = Resolve(8, null, 30, 35); // needs 5s from 8s of a 10s clip
        Assert.Equal((S(8), S(10)), (forward.SourceIn, forward.SourceOut));
        Assert.Contains(ThreePointResolver.InsufficientSourceNote, forward.Notes);

        ThreePointRange backtimed = Resolve(null, 2, 30, 35); // needs 5s before a 2s Out
        Assert.Equal((S(0), S(2)), (backtimed.SourceIn, backtimed.SourceOut));
        Assert.Contains(ThreePointResolver.InsufficientSourceNote, backtimed.Notes);
    }

    [Fact]
    public void Still_Fills_Any_Sequence_Range()
    {
        ThreePointRange r = Resolve(null, null, 30, 42, length: 5, unbounded: true);

        Assert.Equal((S(0), S(12)), (r.SourceIn, r.SourceOut));
        Assert.Empty(r.Notes);
        Assert.Equal(S(5), Resolve(null, null, null, null, length: 5, unbounded: true).Duration);
    }

    [Fact]
    public void Stale_Or_Inverted_Marks_Fall_Back_Sensibly()
    {
        ThreePointRange stale = Resolve(12, 15, null, null); // marks past a shorter, relinked source
        Assert.Equal((S(0), S(10)), (stale.SourceIn, stale.SourceOut));

        ThreePointRange inverted = Resolve(null, null, 30, 25); // sequence Out before In: the In alone
        Assert.Equal((S(30), S(10)), (inverted.RecordIn, inverted.Duration));
    }

    [Fact]
    public void Backtimed_Edit_Before_The_Sequence_Start_Trims_The_Source_Head()
    {
        ThreePointRange r = Resolve(2, 6, null, 3); // 4s ending at 3s would start at -1s

        Assert.Equal(S(0), r.RecordIn);
        Assert.Equal((S(3), S(6)), (r.SourceIn, r.SourceOut));
        Assert.Equal(S(3), r.RecordOut);
    }
}
