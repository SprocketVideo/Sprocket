using System.Diagnostics;
using Sprocket.Core.Audio;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Tests the audio master clock (<see cref="AudioEngine"/>): <see cref="AudioEngine.Now"/> derived from the
/// device's played-frame count, transport (start/pause/seek) re-anchoring, and that the feeder actually mixes
/// and enqueues audio while playing. Clock semantics are deterministic because <see cref="FakeAudioOutput"/>
/// lets the test set the played-frame count directly; the feeder integration uses a bounded poll.
/// </summary>
public class AudioEngineTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static Project EmptyProject() =>
        new(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate));

    private static AudioMixer SilentMixer() => new(Rate, Channels, _ => null);

    private static FakeAudioOutput NewOutput()
    {
        var output = new FakeAudioOutput();
        output.Configure(Rate, Channels);
        return output;
    }

    [Fact]
    public async Task Starts_Paused_At_Zero()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512);

        Assert.False(engine.IsRunning);
        Assert.Equal(0, engine.Now.Ticks);
    }

    [Fact]
    public async Task Now_Advances_With_Played_Frames_While_Running()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512);

        engine.Start();
        output.SetPlayedFrames(Rate * 2); // device has played 2 s

        Assert.True(engine.IsRunning);
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task Now_Freezes_While_Paused()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512);

        engine.Start();
        output.SetPlayedFrames(Rate);     // 1 s
        engine.Pause();
        output.SetPlayedFrames(Rate * 5); // further device progress must be ignored while paused

        Assert.False(engine.IsRunning);
        Assert.Equal(Timecode.FromSeconds(1.0).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task Seek_Repositions_And_Advances_From_The_New_Anchor()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512);

        engine.Start();
        output.SetPlayedFrames(Rate); // 1 s played

        engine.Seek(Timecode.FromSeconds(10.0));
        Assert.Equal(Timecode.FromSeconds(10.0).Ticks, engine.Now.Ticks); // re-anchored, no further frames yet

        output.SetPlayedFrames(Rate * 2); // one more second of device progress past the seek anchor
        Assert.Equal(Timecode.FromSeconds(11.0).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task Feeder_Mixes_And_Enqueues_Audio_While_Playing()
    {
        var id = MediaRefId.New();
        var timeline = new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate);
        var project = new Project(timeline);
        var track = new AudioTrack { Name = "A1" };
        track.Clips.Add(new Clip(id, Timecode.Zero, Timecode.FromSeconds(10), Timecode.Zero));
        timeline.Tracks.Add(track);

        var reader = new FakePcmReader(Rate, Channels, 0.5f);
        var mixer = new AudioMixer(Rate, Channels, x => x == id ? reader : null);
        var output = NewOutput();
        await using var engine = new AudioEngine(output, mixer, project, bufferFrames: 1024);

        engine.Start();

        // The feeder runs on its own thread; poll (bounded) until it has enqueued at least one buffer.
        var sw = Stopwatch.StartNew();
        float[][] buffers = [];
        while (sw.Elapsed < Timeout && buffers.Length == 0)
        {
            await Task.Delay(10);
            buffers = output.EnqueuedSnapshot();
        }

        Assert.NotEmpty(buffers);
        float peak = 0;
        foreach (float s in buffers[0])
            peak = Math.Max(peak, Math.Abs(s));
        Assert.Equal(0.5f, peak, 0.0001); // the mixed (non-silent) source landed in the device queue
    }
}
