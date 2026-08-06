using Sprocket.Core.Model;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Audio.Tests;

/// <summary>
/// Tests the master clock's device-clock smoothing estimator (ARCHITECTURE.md §8). Real audio backends report
/// <see cref="IAudioOutput.PlayedFrames"/> quantized to the device update period (OpenAL Soft: 960 frames =
/// 20 ms per mixer update, measured; the legacy Creative router ~25 ms with transient regressions at underrun),
/// so <see cref="AudioEngine.Now"/> interpolates between raw updates on the injected time source under an
/// explicit contract: bounded lead (at most one mix buffer past the last raw reading), monotonic output, stalls
/// and backward raw readings never move the anchor, and every re-anchor point (start, seek, device switch,
/// recovery) resets the estimator. Fully deterministic: raw frames via <see cref="FakeAudioOutput"/>, elapsed
/// time via <see cref="FakeTimeSource"/>.
/// </summary>
public class AudioEngineClockSmoothingTests
{
    private const int Rate = 48000;
    private const int Channels = 2;
    private const int BufferFrames = 2048; // the estimator's maximum interpolation lead (~42.7 ms at 48 kHz)

    private static Project EmptyProject() =>
        new(new Timeline(new Rational(30, 1), new Resolution(1920, 1080), Rate));

    private static AudioMixer SilentMixer() => new(Rate, Channels, _ => null);

    private static (AudioEngine engine, FakeAudioOutput output, FakeTimeSource time) NewEngine()
    {
        var output = new FakeAudioOutput();
        output.Configure(Rate, Channels);
        var time = new FakeTimeSource();
        var engine = new AudioEngine(output, SilentMixer(), EmptyProject(), bufferFrames: BufferFrames,
            timeSource: time);
        return (engine, output, time);
    }

    private static Timecode Samples(long frames) => Timecode.FromSamples(frames, Rate);

    [Fact]
    public async Task Now_Interpolates_Smoothly_Between_Stepped_Raw_Readings()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;

        engine.Start();

        // The device hasn't reported progress yet; the estimator advances on elapsed time alone.
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Samples(480).Ticks, engine.Now.Ticks);
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Samples(960).Ticks, engine.Now.Ticks);

        // The raw counter steps to exactly where the interpolation had estimated — no jump either way.
        output.SetPlayedFrames(960);
        Assert.Equal(Samples(960).Ticks, engine.Now.Ticks);

        // And interpolation continues from the fresh anchor.
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Samples(1440).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task Lead_Past_The_Last_Raw_Reading_Is_Bounded_By_One_Mix_Buffer()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;

        engine.Start();
        output.SetPlayedFrames(960);
        Assert.Equal(Samples(960).Ticks, engine.Now.Ticks);

        // The device stalls: the estimate may run ahead of the last raw reading by at most one mix buffer,
        // then holds — video must not keep advancing over unheard audio.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Samples(960 + BufferFrames).Ticks, engine.Now.Ticks);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(Samples(960 + BufferFrames).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task A_Forward_Raw_Update_After_A_Stall_Snaps_The_Clock_Forward()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;

        engine.Start();
        time.Advance(TimeSpan.FromMilliseconds(100)); // stall: lead capped at one buffer
        Assert.Equal(Samples(BufferFrames).Ticks, engine.Now.Ticks);

        // The device catches up past the held estimate — the clock corrects forward, never backward.
        output.SetPlayedFrames(9600);
        Assert.Equal(Samples(9600).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task Repeated_And_Backward_Raw_Readings_Never_Rewind_The_Clock()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;

        engine.Start();
        output.SetPlayedFrames(4800);
        Assert.Equal(Samples(4800).Ticks, engine.Now.Ticks);

        // A backward raw reading (observed from the legacy Creative router at an underrun) is treated as a
        // stall: the anchor holds and interpolation continues from it — Now never regresses.
        output.SetPlayedFrames(2400);
        Assert.Equal(Samples(4800).Ticks, engine.Now.Ticks);
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Samples(5280).Ticks, engine.Now.Ticks);

        // A repeated identical reading doesn't restart the lead window either.
        output.SetPlayedFrames(4800);
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Samples(5760).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task Now_Is_Monotonic_Across_Every_Reading_Pattern()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;

        engine.Start();
        long last = -1;
        // Stepped, repeated, stalled, backward, and catch-up raw readings interleaved with uneven time.
        (long raw, double ms)[] script =
            [(0, 5), (960, 5), (960, 30), (720, 10), (960, 10), (2880, 5), (2880, 60), (5760, 1), (5760, 1)];
        foreach (var (raw, ms) in script)
        {
            output.SetPlayedFrames(raw);
            time.Advance(TimeSpan.FromMilliseconds(ms));
            long now = engine.Now.Ticks;
            Assert.True(now >= last, $"Now regressed: {now} < {last} (raw={raw})");
            last = now;
        }
    }

    [Fact]
    public async Task Pause_Freezes_And_Resume_Restarts_The_Estimator_From_The_Paused_Position()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;

        engine.Start();
        time.Advance(TimeSpan.FromMilliseconds(10));
        engine.Pause();

        // Frozen while paused, whatever the device or time source do.
        time.Advance(TimeSpan.FromSeconds(2));
        output.SetPlayedFrames(96000);
        Assert.Equal(Samples(480).Ticks, engine.Now.Ticks);

        // Resume restarts smoothing from the paused position with zero inherited lead.
        engine.Start();
        Assert.Equal(Samples(480).Ticks, engine.Now.Ticks);
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Samples(960).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task Seek_Resets_The_Estimator_And_Does_Not_Inherit_A_Stall_Lead()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;

        engine.Start();
        time.Advance(TimeSpan.FromSeconds(1)); // stall: estimate is holding at the capped lead

        engine.Seek(Timecode.FromSeconds(5.0));
        Assert.Equal(Timecode.FromSeconds(5.0).Ticks, engine.Now.Ticks); // exactly the target, no carried lead
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Timecode.FromSeconds(5.0).Ticks + Samples(480).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task SwitchOutputDevice_Restarts_Smoothing_At_The_Preserved_Position()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;

        engine.Start();
        output.SetPlayedFrames(960);
        Assert.Equal(Samples(960).Ticks, engine.Now.Ticks);

        Assert.True(engine.SwitchOutputDevice("Speakers (USB Audio)"));
        Assert.Equal(Samples(960).Ticks, engine.Now.Ticks); // playhead preserved
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Samples(1440).Ticks, engine.Now.Ticks); // smooth from the new device's anchor
    }

    [Fact]
    public async Task Recovery_Success_Restarts_Smoothing_From_The_Frozen_Position()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;
        var recovered = new TaskCompletionSource();
        engine.OutputStatusChanged += s =>
        {
            if (s == AudioEngine.OutputStatus.Recovered)
                recovered.TrySetResult();
        };

        engine.Start();
        output.SetPlayedFrames(Rate * 2); // 2 s heard
        output.SetConnected(false);       // drop → freeze → in-place reopen (fake default: success)
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, engine.Now.Ticks);
        // Post-recovery the estimator interpolates from the re-anchored position — smooth, not stepped.
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks + Samples(480).Ticks, engine.Now.Ticks);
        // And the recovered device's raw counter drives it forward again once it overtakes the lead.
        output.SetPlayedFrames(Rate * 3);
        Assert.Equal(Timecode.FromSeconds(3.0).Ticks, engine.Now.Ticks);
    }

    [Fact]
    public async Task Software_Fallback_Ignores_The_Dead_Device_Counter_Entirely()
    {
        var (engine, output, time) = NewEngine();
        await using var _ = engine;
        var fellBack = new TaskCompletionSource();
        engine.OutputStatusChanged += s =>
        {
            if (s == AudioEngine.OutputStatus.SoftwareFallback)
                fellBack.TrySetResult();
        };

        output.SetReopenResult(() => false); // the device will not come back
        engine.Start();
        output.SetPlayedFrames(Rate);        // 1 s heard before the drop
        output.SetConnected(false);
        await fellBack.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Timecode.FromSeconds(1.0).Ticks, engine.Now.Ticks);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, engine.Now.Ticks);
        // A twitching dead-device counter must not perturb software timing.
        output.SetPlayedFrames(Rate * 100);
        Assert.Equal(Timecode.FromSeconds(2.0).Ticks, engine.Now.Ticks);
    }
}
