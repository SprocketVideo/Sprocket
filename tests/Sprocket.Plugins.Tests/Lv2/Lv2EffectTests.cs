using Sprocket.Core.Rendering;
using Sprocket.Plugins.Lv2;
using Xunit;

namespace Sprocket.Plugins.Tests.Lv2;

/// <summary>The LV2 binding + DSP plumbing end to end against the in-process <see cref="FakeLv2Plugin"/> (PLAN.md step 59).</summary>
public sealed class Lv2EffectTests
{
    private const int Rate = 48000;

    private static ResolvedEffect Params(double gain) =>
        new("plugin.lv2." + FakeLv2Plugin.Uri, new Dictionary<string, double> { ["gain"] = gain });

    [Fact]
    public void Dual_mono_gain_delay_processes_each_channel_independently()
    {
        using var plugin = new FakeLv2Plugin();
        using var effect = new Lv2Effect(plugin.DescriptorPtr, FakeLv2Plugin.Info());

        float[] pcm = [1f, 10f, 2f, 20f, 3f, 30f]; // L: 1 2 3, R: 10 20 30 — 3 frames, 2 channels
        effect.Process(pcm, frames: 3, Rate, channels: 2, Params(2.0));

        Assert.Equal([0f, 0f, 2f, 20f, 4f, 40f], pcm); // out[i] = in[i-1] * 2, per channel
    }

    [Fact]
    public void State_carries_across_buffers_and_Reset_clears_it()
    {
        using var plugin = new FakeLv2Plugin();
        using var effect = new Lv2Effect(plugin.DescriptorPtr, FakeLv2Plugin.Info());

        float[] a = [5f, 6f];
        effect.Process(a, 2, Rate, 1, Params(1.0));
        float[] b = [0f, 0f];
        effect.Process(b, 2, Rate, 1, Params(1.0));
        Assert.Equal(6f, b[0]); // the previous block's last input

        effect.Reset();
        float[] c = [0f, 0f];
        effect.Process(c, 2, Rate, 1, Params(1.0));
        Assert.Equal(0f, c[0]);
    }

    [Fact]
    public void Reset_works_when_the_plugin_has_no_deactivate()
    {
        using var plugin = new FakeLv2Plugin(withDeactivate: false);
        using var effect = new Lv2Effect(plugin.DescriptorPtr, FakeLv2Plugin.Info());
        float[] a = [5f, 6f];
        effect.Process(a, 2, Rate, 1, Params(1.0));
        effect.Reset();
        float[] c = [0f, 0f];
        effect.Process(c, 2, Rate, 1, Params(1.0));
        Assert.Equal(0f, c[0]);
    }

    [Fact]
    public void Missing_parameter_uses_the_port_default()
    {
        using var plugin = new FakeLv2Plugin();
        using var effect = new Lv2Effect(plugin.DescriptorPtr, FakeLv2Plugin.Info());
        float[] pcm = [3f, 0f];
        effect.Process(pcm, 2, Rate, 1, new ResolvedEffect("x", new Dictionary<string, double>()));
        Assert.Equal(3f, pcm[1]); // default gain 1.0
    }

    [Fact]
    public void Instantiate_receives_the_host_features_and_a_slash_terminated_bundle_path()
    {
        using var plugin = new FakeLv2Plugin();
        string bundle = Path.Combine(Path.GetTempPath(), "fake.lv2");
        using var set = new Lv2InstanceSet(plugin.DescriptorPtr, FakeLv2Plugin.Info(bundle), Rate, 1, 64);

        Assert.NotEqual(0u, FakeLv2Plugin.LastMappedUrid);
        Assert.Equal(FakeLv2Plugin.MappedUri, Lv2Features.UnmapUri(FakeLv2Plugin.LastMappedUrid));
        Assert.Equal(FakeLv2Plugin.LastMappedUrid, Lv2Features.MapUri(FakeLv2Plugin.MappedUri)); // stable
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), FakeLv2Plugin.LastBundlePath);
        Assert.StartsWith(bundle, FakeLv2Plugin.LastBundlePath);
    }

    [Fact]
    public void Urid_map_is_process_stable_and_unmap_rejects_unknown_ids()
    {
        uint a = Lv2Features.MapUri("http://sprocket.test/a");
        uint b = Lv2Features.MapUri("http://sprocket.test/b");
        Assert.NotEqual(a, b);
        Assert.Equal(a, Lv2Features.MapUri("http://sprocket.test/a"));
        Assert.Equal("http://sprocket.test/a", Lv2Features.UnmapUri(a));
        Assert.Null(Lv2Features.UnmapUri(0));
        Assert.Null(Lv2Features.UnmapUri(uint.MaxValue));
    }

    [Fact]
    public void Format_change_rebuilds_the_instance_set()
    {
        using var plugin = new FakeLv2Plugin();
        using var effect = new Lv2Effect(plugin.DescriptorPtr, FakeLv2Plugin.Info());
        float[] mono = [1f, 2f];
        effect.Process(mono, 2, Rate, 1, Params(1.0));
        float[] stereo = [1f, 1f, 2f, 2f, 3f, 3f];
        effect.Process(stereo, 3, Rate, 2, Params(1.0));
        Assert.Equal([0f, 0f, 1f, 1f, 2f, 2f], stereo); // fresh state, dual-mono
    }

    [Fact]
    public void Descriptor_mapping_of_the_fake_matches_its_ports()
    {
        var provider = new Lv2EffectProvider(nint.Zero, FakeLv2Plugin.Info());
        Assert.Equal("plugin.lv2." + FakeLv2Plugin.Uri, provider.Descriptor.Id);
        Assert.Single(provider.Descriptor.Parameters); // only the control input
        Assert.Equal("gain", provider.Descriptor.Parameters[0].Name);
    }
}
