using System.Diagnostics;
using System.Linq;
using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Tests the audio master clock's device-loss recovery (ARCHITECTURE.md §8): on an unplugged/switched output the
/// engine must never leave the clock silently frozen. It freezes briefly, attempts an in-place reopen, and either
/// re-anchors onto the device (position preserved) or switches <see cref="AudioEngine.Now"/> to a monotonic
/// software time source so the timeline keeps advancing without audio. <see cref="FakeAudioOutput"/> scripts the
/// connection/reopen outcome and a fake <see cref="ISoftwareTimeSource"/> makes the software-fallback clock
/// deterministic. (The OpenAL-specific re-verify — reopen reporting success while still disconnected — lives in
/// <c>OpenAlAudioOutput</c> and rests on manual verification like the rest of that device-bound class.)
/// </summary>
public class AudioEngineRecoveryTests
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

    private static AudioEngine.OutputStatus[] Snapshot(List<AudioEngine.OutputStatus> log)
    {
        lock (log) return log.ToArray();
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(5);
        }
        return condition();
    }

    [Fact]
    public async Task Reopen_Success_Preserves_Position_And_Resumes_On_The_Device()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: new FakeTimeSource());
        var log = new List<AudioEngine.OutputStatus>();
        engine.OutputStatusChanged += s => { lock (log) log.Add(s); };

        engine.Start();
        output.SetPlayedFrames(Rate * 2); // 2 s heard
        output.SetConnected(false);       // device yanked out from under playback

        Assert.True(await WaitUntil(() => Snapshot(log).Contains(AudioEngine.OutputStatus.Recovered), Timeout));

        // Position preserved across the reopen — no jump, no regress.
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, engine.Now.Ticks);
        // Back on the device: further played frames advance Now again.
        output.SetPlayedFrames(Rate * 3);
        Assert.Equal(Timecode.FromSeconds(3.0).Ticks, engine.Now.Ticks);

        Assert.True(output.ReopenCalls >= 1);
        Assert.Equal(
            new[] { AudioEngine.OutputStatus.Recovering, AudioEngine.OutputStatus.Recovered },
            Snapshot(log)); // each transition fires exactly once
    }

    [Fact]
    public async Task Feeding_Resumes_After_A_Successful_Reopen()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: new FakeTimeSource());
        var log = new List<AudioEngine.OutputStatus>();
        engine.OutputStatusChanged += s => { lock (log) log.Add(s); };

        engine.Start();
        output.SetPlayedFrames(Rate);
        output.SetConnected(false);
        Assert.True(await WaitUntil(() => Snapshot(log).Contains(AudioEngine.OutputStatus.Recovered), Timeout));

        int afterRecovery = output.EnqueuedSnapshot().Length;
        // Simulate the reopened device playing out buffers so its queue drains — the feeder must refill it,
        // proving it resumed mixing/enqueuing on the recovered device.
        Assert.True(await WaitUntil(() =>
        {
            output.SetPlayedFrames(output.PlayedFrames + 512);
            return output.EnqueuedSnapshot().Length > afterRecovery;
        }, Timeout));
    }

    [Fact]
    public async Task Reopen_Failure_Enters_Software_Timing_That_Keeps_Advancing()
    {
        var output = NewOutput();
        var time = new FakeTimeSource();
        await using var engine = new AudioEngine(
            output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: time);
        var log = new List<AudioEngine.OutputStatus>();
        engine.OutputStatusChanged += s => { lock (log) log.Add(s); };

        output.SetReopenResult(() => false); // the device will not come back
        engine.Start();
        output.SetPlayedFrames(Rate * 2);    // 2 s heard on the device before the drop
        output.SetConnected(false);

        Assert.True(await WaitUntil(() => Snapshot(log).Contains(AudioEngine.OutputStatus.SoftwareFallback), Timeout));

        // Frozen at the drop position, then advancing on software time even though the dead device's
        // PlayedFrames never moves again.
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, engine.Now.Ticks);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Timecode.FromSeconds(3.0).Ticks, engine.Now.Ticks);

        Assert.Equal(1, output.ReopenCalls); // one attempt, then terminal — no reopen spin
        Assert.Equal(
            new[] { AudioEngine.OutputStatus.Recovering, AudioEngine.OutputStatus.SoftwareFallback },
            Snapshot(log));
    }

    [Fact]
    public async Task No_Audio_Is_Enqueued_After_Software_Fallback()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: new FakeTimeSource());
        var log = new List<AudioEngine.OutputStatus>();
        engine.OutputStatusChanged += s => { lock (log) log.Add(s); };

        output.SetReopenResult(() => false);
        engine.Start();
        output.SetPlayedFrames(Rate);
        output.SetConnected(false);
        Assert.True(await WaitUntil(() => Snapshot(log).Contains(AudioEngine.OutputStatus.SoftwareFallback), Timeout));

        int queued = output.EnqueuedSnapshot().Length;
        await Task.Delay(100); // ample time for the feeder to (wrongly) push more at a dead device
        Assert.Equal(queued, output.EnqueuedSnapshot().Length);
    }

    [Fact]
    public async Task Seek_During_Recovery_Is_Respected()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: new FakeTimeSource());
        var recovered = false;
        engine.OutputStatusChanged += s => { if (s == AudioEngine.OutputStatus.Recovered) recovered = true; };

        // Seek fires from inside the (off-lock) reopen window — i.e. while the engine is mid-recovery. Recovery must
        // re-anchor to the sought position, not clobber it, and must not deadlock against the transport call.
        output.SetReopenResult(() => { engine.Seek(Timecode.FromSeconds(30)); return true; });

        engine.Start();
        output.SetPlayedFrames(Rate * 2);
        output.SetConnected(false);

        Assert.True(await WaitUntil(() => recovered, Timeout));
        Assert.Equal(Timecode.FromSeconds(30).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task Pause_During_Recovery_Holds_At_The_Frozen_Position()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: new FakeTimeSource());
        var recovered = false;
        engine.OutputStatusChanged += s => { if (s == AudioEngine.OutputStatus.Recovered) recovered = true; };

        output.SetReopenResult(() => { engine.Pause(); return true; });

        engine.Start();
        output.SetPlayedFrames(Rate * 2);
        output.SetConnected(false);

        Assert.True(await WaitUntil(() => recovered, Timeout));
        Assert.False(engine.IsRunning);
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task SwitchOutputDevice_Repoints_In_Place_Keeping_The_Playhead()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: new FakeTimeSource());

        engine.Start();
        output.SetPlayedFrames(Rate * 2); // 2 s heard on the current device

        bool switched = engine.SwitchOutputDevice("Speakers (USB Audio)");

        Assert.True(switched);
        Assert.Equal("Speakers (USB Audio)", output.LastReopenSpecifier); // the named device was requested
        Assert.Equal(1, output.ReopenCalls);
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, engine.Now.Ticks); // playhead preserved across the switch
        // Still on the (reopened) device: further played frames advance Now.
        output.SetPlayedFrames(Rate * 3);
        Assert.Equal(Timecode.FromSeconds(3.0).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task SwitchOutputDevice_Returns_False_And_Keeps_Playing_When_Reopen_Fails()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: new FakeTimeSource());
        output.SetReopenResult(() => false); // the requested device can't be opened

        engine.Start();
        output.SetPlayedFrames(Rate);

        Assert.False(engine.SwitchOutputDevice("Nonexistent Device"));
        // The current device keeps driving the clock (no software fallback, no freeze).
        output.SetPlayedFrames(Rate * 2);
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task SwitchOutputDevice_Is_A_No_Op_In_Software_Fallback()
    {
        var output = NewOutput();
        await using var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: new FakeTimeSource());
        var log = new List<AudioEngine.OutputStatus>();
        engine.OutputStatusChanged += s => { lock (log) log.Add(s); };
        output.SetReopenResult(() => false);

        engine.Start();
        output.SetConnected(false);
        Assert.True(await WaitUntil(() => Snapshot(log).Contains(AudioEngine.OutputStatus.SoftwareFallback), Timeout));

        int reopenCallsBefore = output.ReopenCalls;
        Assert.False(engine.SwitchOutputDevice("Speakers (USB Audio)")); // can't switch a dead session's device
        Assert.Equal(reopenCallsBefore, output.ReopenCalls);            // and it didn't even attempt a reopen
    }

    [Fact]
    public async Task Dispose_After_Device_Loss_Does_Not_Hang()
    {
        var output = NewOutput();
        var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: 512, timeSource: new FakeTimeSource());
        output.SetReopenResult(() => false);

        engine.Start();
        output.SetPlayedFrames(Rate);
        output.SetConnected(false); // recovery may be in flight as we tear down

        await engine.DisposeAsync().AsTask().WaitAsync(Timeout);
    }
}
