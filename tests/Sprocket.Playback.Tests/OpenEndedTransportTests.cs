using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Playback.Tests;

/// <summary>
/// The timeline playhead may be parked in the empty space past the last clip, the way leading editors' sequence
/// timelines work (Premiere / Resolve / Vegas; Final Cut's magnetic timeline is the outlier). That is opt-in per
/// engine via <see cref="PlaybackEngine.AllowPlayheadPastEnd"/>: the Program (timeline) engine sets it, the Source
/// monitor deliberately does not, because nothing exists past the end of a media file.
/// <para>Decode-free: the project holds a single <see cref="AudioTrack"/> clip, so the timeline has a real
/// duration but the engine builds no video players and never opens a decoder. That leaves the transport logic —
/// clamping, the end-stop, and the play/scrub rules — as the only thing under test.</para>
/// </summary>
public class OpenEndedTransportTests
{
    private static readonly Timecode ContentEnd = Timecode.FromSeconds(10);
    private static readonly Rational Fps30 = new(30, 1);

    private static Project BuildProject()
    {
        var timeline = new Timeline(Fps30, new Resolution(1920, 1080), 48000);
        var track = new AudioTrack { Name = "A1" };
        track.Clips.Add(new Clip(MediaRefId.New(), Timecode.Zero, ContentEnd, Timecode.Zero));
        timeline.Tracks.Add(track);
        return new Project(timeline);
    }

    private static PlaybackEngine BuildEngine(bool openEnded, SoftwareClock clock) =>
        new(BuildProject(), _ => null, clock) { AllowPlayheadPastEnd = openEnded };

    [Fact]
    public async Task Seek_Past_The_Content_End_Is_Honoured_When_Open_Ended()
    {
        await using PlaybackEngine engine = BuildEngine(openEnded: true, new SoftwareClock(() => TimeSpan.Zero));
        Timecode target = ContentEnd + Timecode.FromSeconds(5);

        engine.SeekTo(target);

        Assert.Equal(target.Ticks, engine.Position.Ticks);
        Assert.Equal(ContentEnd.Ticks, engine.Duration.Ticks); // the CONTENT end is unchanged
    }

    /// <summary>The Source-monitor guarantee: an engine that has not opted in still clamps to its duration, so a
    /// source preview can never be scrubbed past the end of its media.</summary>
    [Fact]
    public async Task Seek_Past_The_Content_End_Is_Clamped_When_Not_Open_Ended()
    {
        await using PlaybackEngine engine = BuildEngine(openEnded: false, new SoftwareClock(() => TimeSpan.Zero));

        engine.SeekTo(ContentEnd + Timecode.FromSeconds(5));

        Assert.Equal(ContentEnd.Ticks, engine.Position.Ticks);
        Assert.Equal(ContentEnd.Ticks, engine.NavigableEnd.Ticks);
    }

    [Fact]
    public async Task Navigable_End_Caps_A_Runaway_Seek()
    {
        await using PlaybackEngine engine = BuildEngine(openEnded: true, new SoftwareClock(() => TimeSpan.Zero));

        engine.SeekTo(new Timecode(long.MaxValue / 4)); // e.g. the MCP seek tool handed a garbage tick count

        Assert.Equal((ContentEnd + PlaybackEngine.MaxTrailingSpace).Ticks, engine.Position.Ticks);
    }

    [Fact]
    public async Task Step_Frame_Forward_At_The_Content_End_Enters_The_Trailing_Space()
    {
        await using PlaybackEngine engine = BuildEngine(openEnded: true, new SoftwareClock(() => TimeSpan.Zero));
        engine.SeekTo(ContentEnd);

        engine.StepFrame(+1);

        Assert.Equal((ContentEnd + Timecode.FromFrames(1, Fps30)).Ticks, engine.Position.Ticks);
    }

    /// <summary>Play from the empty trailing space restarts at zero — there is nothing to play out there, and the
    /// engine already replays from the start when parked at the end.</summary>
    [Fact]
    public async Task Play_From_Past_The_Content_End_Restarts_At_Zero()
    {
        await using PlaybackEngine engine = BuildEngine(openEnded: true, new SoftwareClock(() => TimeSpan.Zero));
        engine.SeekTo(ContentEnd + Timecode.FromSeconds(3));

        engine.Play();

        Assert.Equal(0, engine.Position.Ticks);
    }

    /// <summary>Scrubbing into the trailing space while playing stops the transport <em>where the user dropped
    /// the playhead</em>. Without this the pump's end-stop would fire and park it back on the content end.</summary>
    [Fact]
    public async Task Scrub_Past_The_Content_End_While_Playing_Stops_Without_Snapping_Back()
    {
        var elapsed = TimeSpan.Zero;
        await using PlaybackEngine engine = BuildEngine(openEnded: true, new SoftwareClock(() => elapsed));
        Timecode target = ContentEnd + Timecode.FromSeconds(2);

        engine.Play();
        engine.SeekTo(target);

        Assert.Equal(target.Ticks, engine.Position.Ticks);
        Assert.NotEqual(PlaybackState.Playing, engine.State);

        // A pump tick out there must not drag the playhead back to the content end either.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);
        Assert.Equal(target.Ticks, engine.Position.Ticks);
    }

    /// <summary>Playback still auto-stops at the CONTENT end and parks exactly on it — opening the far end must
    /// not turn normal playback into a run through empty space.</summary>
    [Fact]
    public async Task Playback_Still_Auto_Stops_And_Parks_At_The_Content_End()
    {
        var elapsed = TimeSpan.Zero;
        await using PlaybackEngine engine = BuildEngine(openEnded: true, new SoftwareClock(() => elapsed));

        int ended = 0;
        engine.PlaybackEnded += () => Interlocked.Increment(ref ended);

        engine.Play();
        elapsed = TimeSpan.FromSeconds(11); // the clock free-runs past the content end
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await engine.PumpOnceAsync(forcePresent: false, cts.Token);

        Assert.Equal(1, Volatile.Read(ref ended));
        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.Equal(ContentEnd.Ticks, engine.Position.Ticks);
    }
}
