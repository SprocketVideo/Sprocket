using System.Runtime.InteropServices;
using Sprocket.Core.Rendering;
using Sprocket.Plugins.Ladspa;
using Xunit;

namespace Sprocket.Plugins.Tests.Ladspa;

/// <summary>
/// End-to-end tests of the LADSPA binding, descriptor reader and <see cref="LadspaEffect"/> DSP (PLAN.md step
/// 59), driven by an in-process <see cref="FakeLadspaPlugin"/> so no native <c>.so</c> is required. They prove
/// the interop (function-pointer calls, control/audio port connection), dual-mono channel handling, gain
/// application, cross-buffer state continuity, and reset.
/// </summary>
public sealed class LadspaEffectTests
{
    private static ResolvedEffect Gain(double gain) =>
        new("plugin.ladspa.1234", new Dictionary<string, double> { ["port0"] = gain });

    [Fact]
    public void Reader_parses_ports_and_identity()
    {
        using var fake = new FakeLadspaPlugin(uniqueId: 1234, label: "test_gain_delay");

        LadspaPluginInfo info = LadspaDescriptorReader.Read(fake.DescriptorPtr);

        Assert.Equal(1234ul, info.UniqueId);
        Assert.Equal("test_gain_delay", info.Label);
        Assert.Equal("Test Gain Delay", info.Name);
        Assert.Equal("Sprocket Tests", info.Maker);
        Assert.Equal(3, info.Ports.Count);
        Assert.True(info.Ports[0].IsControlInput);
        Assert.Equal("Gain", info.Ports[0].Name);
        Assert.True(info.Ports[1].IsAudioInput);
        Assert.True(info.Ports[2].IsAudioOutput);
        Assert.Equal(1, info.AudioInputCount);
        Assert.Equal(1, info.AudioOutputCount);
    }

    [Fact]
    public void Reader_rejects_null_pointer() =>
        Assert.Throws<ArgumentException>(() => LadspaDescriptorReader.Read(nint.Zero));

    [Fact]
    public void Effect_applies_gain_per_channel_dual_mono()
    {
        using var fake = new FakeLadspaPlugin();
        LadspaPluginInfo info = LadspaDescriptorReader.Read(fake.DescriptorPtr);
        using var effect = new LadspaEffect(fake.DescriptorPtr, info);

        // Interleaved stereo: L = 1,2,3,4  R = 10,20,30,40. A mono plugin runs one instance per channel.
        float[] buffer = [1, 10, 2, 20, 3, 30, 4, 40];
        effect.Process(buffer, frames: 4, sampleRate: 48000, channels: 2, Gain(2.0));

        // out[i] = prev*gain, prev starts at 0 and carries the previous input sample of the SAME channel.
        float[] expected = [0, 0, 2, 20, 4, 40, 6, 60];
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Effect_carries_state_across_buffers()
    {
        using var fake = new FakeLadspaPlugin();
        LadspaPluginInfo info = LadspaDescriptorReader.Read(fake.DescriptorPtr);
        using var effect = new LadspaEffect(fake.DescriptorPtr, info);

        float[] block1 = [1, 10, 2, 20, 3, 30, 4, 40];
        effect.Process(block1, 4, 48000, 2, Gain(2.0));

        // Block 2's first output sample per channel uses the LAST input of block 1 (prevL=4, prevR=40).
        float[] block2 = [5, 50, 6, 60, 7, 70, 8, 80];
        effect.Process(block2, 4, 48000, 2, Gain(2.0));

        float[] expected = [8, 80, 10, 100, 12, 120, 14, 140];
        Assert.Equal(expected, block2);
    }

    [Fact]
    public void Reset_clears_carried_state()
    {
        using var fake = new FakeLadspaPlugin();
        LadspaPluginInfo info = LadspaDescriptorReader.Read(fake.DescriptorPtr);
        using var effect = new LadspaEffect(fake.DescriptorPtr, info);

        effect.Process([1, 10, 2, 20, 3, 30, 4, 40], 4, 48000, 2, Gain(2.0));
        effect.Reset(); // deactivate→activate zeroes the carried prev

        float[] block2 = [5, 50, 6, 60, 7, 70, 8, 80];
        effect.Process(block2, 4, 48000, 2, Gain(2.0));

        // With state cleared the first sample is prev(0)*gain = 0, not the carried 4*2 / 40*2.
        float[] expected = [0, 0, 10, 100, 12, 120, 14, 140];
        Assert.Equal(expected, block2);
    }

    [Fact]
    public void Reset_clears_state_even_when_plugin_has_no_deactivate()
    {
        // deactivate() is optional in LADSPA; reset must still work through activate() alone.
        using var fake = new FakeLadspaPlugin(withDeactivate: false);
        LadspaPluginInfo info = LadspaDescriptorReader.Read(fake.DescriptorPtr);
        using var effect = new LadspaEffect(fake.DescriptorPtr, info);

        effect.Process([1, 2, 3, 4], 4, 48000, 1, Gain(2.0)); // mono; leaves prev = 4
        effect.Reset();

        float[] block2 = [5, 6, 7, 8];
        effect.Process(block2, 4, 48000, 1, Gain(2.0));
        Assert.Equal(new float[] { 0, 10, 12, 14 }, block2); // first sample is prev(0)*2, not the carried 4*2
    }

    [Fact]
    public void Effect_uses_control_default_when_parameter_missing()
    {
        using var fake = new FakeLadspaPlugin();
        LadspaPluginInfo info = LadspaDescriptorReader.Read(fake.DescriptorPtr);
        using var effect = new LadspaEffect(fake.DescriptorPtr, info);

        // No "port0" supplied → the DEFAULT_1 hint gives gain 1.0.
        float[] buffer = [2, 4, 6, 8]; // mono
        effect.Process(buffer, 4, 48000, 1, new ResolvedEffect("plugin.ladspa.1234", new Dictionary<string, double>()));

        Assert.Equal(new float[] { 0, 2, 4, 6 }, buffer); // out[i] = in[i-1] * 1.0
    }

    [Fact]
    public void Effect_reallocates_on_channel_and_blocksize_change_without_error()
    {
        using var fake = new FakeLadspaPlugin();
        LadspaPluginInfo info = LadspaDescriptorReader.Read(fake.DescriptorPtr);
        using var effect = new LadspaEffect(fake.DescriptorPtr, info);

        float[] mono = [1, 2, 3, 4];
        effect.Process(mono, 4, 48000, 1, Gain(1.0));

        // Grow the block and switch to stereo — the set is rebuilt; must not throw or corrupt.
        float[] stereo = new float[16];
        for (int i = 0; i < 16; i++) stereo[i] = i;
        effect.Process(stereo, 8, 48000, 2, Gain(1.0));
        Assert.Equal(0f, stereo[0]); // first sample per channel = prev(0) after rebuild
        Assert.Equal(0f, stereo[1]);
    }

    [Fact]
    public void Missing_plugin_yields_null_effect_for_passthrough()
    {
        var host = new LadspaHost();
        Assert.Null(host.CreateAudioEffect("plugin.ladspa.does-not-exist"));
    }

    [Fact]
    public unsafe void Reader_rejects_an_implausible_port_count()
    {
        // A malformed/hostile descriptor advertising a huge PortCount would otherwise walk its port arrays off
        // the end into unmapped memory (an uncatchable access violation at startup discovery). It must be
        // rejected as a recorded load error instead.
        nint descriptor = (nint)NativeMemory.AllocZeroed((nuint)sizeof(LadspaDescriptor));
        try
        {
            ((LadspaDescriptor*)descriptor)->PortCount = new CULong(1_000_000);
            Assert.Throws<BadImageFormatException>(() => LadspaDescriptorReader.Read(descriptor));
        }
        finally
        {
            NativeMemory.Free((void*)descriptor);
        }
    }
}
