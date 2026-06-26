using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Core.Tests;

public class ClipTests
{
    private static Clip MakeClip(double srcIn, double srcOut, double tlStart) =>
        new(MediaRefId.New(),
            Timecode.FromSeconds(srcIn),
            Timecode.FromSeconds(srcOut),
            Timecode.FromSeconds(tlStart));

    [Fact]
    public void Duration_Derives_From_Trim()
    {
        Clip clip = MakeClip(2, 5, 10);
        Assert.Equal(Timecode.FromSeconds(3), clip.Duration);
        Assert.Equal(Timecode.FromSeconds(13), clip.TimelineEnd);
    }

    [Fact]
    public void Contains_Is_Start_Inclusive_End_Exclusive()
    {
        Clip clip = MakeClip(0, 4, 10); // timeline [10, 14)
        Assert.False(clip.Contains(Timecode.FromSeconds(9.999)));
        Assert.True(clip.Contains(Timecode.FromSeconds(10)));
        Assert.True(clip.Contains(Timecode.FromSeconds(13.999)));
        Assert.False(clip.Contains(Timecode.FromSeconds(14)));
    }

    [Fact]
    public void MapToSource_Offsets_By_Trim()
    {
        Clip clip = MakeClip(2, 5, 10); // source in-point 2s, placed at 10s
        // 1s into the clip on the timeline -> 1s past the source in-point.
        Assert.Equal(Timecode.FromSeconds(3), clip.MapToSource(Timecode.FromSeconds(11)));
    }

    [Fact]
    public void Invalid_Trim_Throws()
    {
        Assert.Throws<ArgumentException>(() => MakeClip(5, 2, 0));
    }
}

public class TrackResolutionTests
{
    [Fact]
    public void ResolveActiveClip_Returns_Null_In_Gap()
    {
        var track = new VideoTrack();
        track.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.Zero));
        Assert.Null(track.ResolveActiveClip(Timecode.FromSeconds(5)));
    }

    [Fact]
    public void ResolveActiveClip_Picks_The_Containing_Clip()
    {
        var track = new VideoTrack();
        var a = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.Zero);
        var b = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(2), Timecode.FromSeconds(5));
        track.Clips.Add(a);
        track.Clips.Add(b);
        Assert.Same(a, track.ResolveActiveClip(Timecode.FromSeconds(1)));
        Assert.Same(b, track.ResolveActiveClip(Timecode.FromSeconds(6)));
    }

    [Fact]
    public void ResolveActiveClip_On_Overlap_Last_Wins()
    {
        var track = new VideoTrack();
        var under = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero);
        var over = new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero);
        track.Clips.Add(under);
        track.Clips.Add(over);
        Assert.Same(over, track.ResolveActiveClip(Timecode.FromSeconds(1)));
    }
}

public class MediaPoolTests
{
    private static MediaRef MakeMedia() =>
        new(MediaRefId.New(), "/tmp/a.mp4",
            new ProbedMediaInfo(Timecode.FromSeconds(10), true, new Rational(30, 1), 1920, 1080, true, 48000, 2));

    [Fact]
    public void Add_And_Get_RoundTrip()
    {
        var pool = new MediaPool();
        MediaRef m = pool.Add(MakeMedia());
        Assert.Same(m, pool.Get(m.Id));
        Assert.True(pool.TryGet(m.Id, out MediaRef found));
        Assert.Same(m, found);
    }

    [Fact]
    public void Duplicate_Id_Throws()
    {
        var pool = new MediaPool();
        MediaRef m = MakeMedia();
        pool.Add(m);
        Assert.Throws<InvalidOperationException>(() => pool.Add(m));
    }

    [Fact]
    public void Get_Missing_Returns_Null()
    {
        Assert.Null(new MediaPool().Get(MediaRefId.New()));
    }
}

public class TimelineTests
{
    [Fact]
    public void Duration_Is_Latest_Clip_End_Across_Tracks()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        var v = new VideoTrack();
        v.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(4), Timecode.Zero));
        var a = new AudioTrack();
        a.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, Timecode.FromSeconds(3), Timecode.FromSeconds(8)));
        timeline.Tracks.Add(v);
        timeline.Tracks.Add(a);

        Assert.Equal(Timecode.FromSeconds(11), timeline.Duration);
    }

    [Fact]
    public void Empty_Timeline_Has_Zero_Duration()
    {
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), 48000);
        Assert.Equal(Timecode.Zero, timeline.Duration);
    }
}
