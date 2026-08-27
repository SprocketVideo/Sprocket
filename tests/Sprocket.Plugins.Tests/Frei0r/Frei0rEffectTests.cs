using System.Runtime.InteropServices;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Plugins.Frei0r;
using Xunit;

namespace Sprocket.Plugins.Tests.Frei0r;

/// <summary>The frei0r binding, info reader, parameter mapping and instance lifecycle end to end against the in-process
/// <see cref="FakeFrei0rPlugin"/> (PLAN.md step 59). These tests share the fake's static state, so they run serially.</summary>
[Collection("frei0r-fake")]
public sealed unsafe class Frei0rEffectTests
{
    private static readonly string FakePath = Path.Combine(Path.GetTempPath(), "fake_invert.so");

    private static Frei0rLibrary LoadFake(int colorModel = Frei0rAbi.ColorRgba8888, int type = Frei0rAbi.TypeFilter, int api = Frei0rAbi.MajorVersion) =>
        Frei0rLibrary.FromFunctions(FakePath, FakeFrei0rPlugin.Functions(colorModel, type, api), "fake_invert");

    private static ResolvedEffect Params(params (string Key, double Value)[] values) =>
        new("plugin.frei0r.fake_invert", values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void Info_reader_reads_identity_and_typed_parameters()
    {
        Frei0rLibrary lib = LoadFake();
        Frei0rPluginInfo info = lib.Info;

        Assert.Equal("Fake Tinted Invert", info.Name);
        Assert.Equal("Sprocket Tests", info.Author);
        Assert.Equal("fake_invert", info.Key);
        Assert.Equal("2.3", info.Version);
        Assert.True(info.IsFilter);
        Assert.Equal(5, info.Params.Count);
        Assert.Equal(Frei0rAbi.ParamColor, info.Params[2].Type);
        Assert.Equal("Skip the invert.", info.Params[1].Explanation);
        Assert.NotNull(lib.Effect);
        Assert.Empty(lib.Skipped);
    }

    [Fact]
    public void Mapping_decomposes_color_and_position_and_drops_string_params()
    {
        EffectDescriptor d = Frei0rParameterMapping.ToEffectDescriptor(LoadFake().Info);

        Assert.Equal("plugin.frei0r.fake_invert", d.Id);
        Assert.Equal(EffectCategory.Video, d.Category);
        Assert.Contains("CPU effect", d.Description);
        Assert.Contains("Text parameters", d.Description);
        Assert.Equal(["p0", "p1", "p2.r", "p2.g", "p2.b", "p3.x", "p3.y"], d.Parameters.Select(p => p.Name));
        Assert.Equal(ParameterKind.Toggle, d.Parameters[1].Kind);
        Assert.Equal("Tint R", d.Parameters[2].DisplayName);
        Assert.All(d.Parameters, p => Assert.True(p.Min == 0.0 && p.Max == 1.0));
    }

    [Fact]
    public void Defaults_come_from_a_freshly_constructed_plugin_instance()
    {
        // The fake's f0r_construct sets Amount 1.0, Bypass 0, Tint white, Center (0, 0) — not the generic 0.5 / centre.
        int before = FakeFrei0rPlugin.LiveInstances;
        EffectDescriptor d = LoadFake().Effect!.Descriptor;

        Assert.Equal(before, FakeFrei0rPlugin.LiveInstances); // the probe instance was destructed
        Assert.Equal(1.0, d.Parameters.Single(p => p.Name == "p0").Default);
        Assert.Equal(0.0, d.Parameters.Single(p => p.Name == "p1").Default);
        Assert.Equal(1.0, d.Parameters.Single(p => p.Name == "p2.g").Default);
        Assert.Equal(0.0, d.Parameters.Single(p => p.Name == "p3.x").Default);
    }

    [Fact]
    public void Generic_defaults_apply_when_the_plugin_has_no_getter()
    {
        Frei0rFunctions functions = FakeFrei0rPlugin.Functions();
        functions.GetParamValue = nint.Zero;
        EffectDescriptor d = Frei0rLibrary.FromFunctions(FakePath, functions, "fake_invert").Effect!.Descriptor;
        Assert.Equal(0.5, d.Parameters.Single(p => p.Name == "p0").Default);
        Assert.Equal(0.5, d.Parameters.Single(p => p.Name == "p3.x").Default);
    }

    [Fact]
    public void SizeGranularity_is_eight() =>
        Assert.Equal(8, LoadFake().Effect!.SizeGranularity);

    [Fact]
    public void Color_model_selects_the_pixel_format()
    {
        Assert.Equal(CpuPixelFormat.Rgba8888, LoadFake(Frei0rAbi.ColorRgba8888).Effect!.PixelFormat);
        Assert.Equal(CpuPixelFormat.Bgra8888, LoadFake(Frei0rAbi.ColorBgra8888).Effect!.PixelFormat);
        Assert.Equal(CpuPixelFormat.Rgba8888, LoadFake(Frei0rAbi.ColorPacked32).Effect!.PixelFormat);
    }

    [Fact]
    public void Sources_mixers_and_wrong_api_versions_are_skipped_with_a_note()
    {
        Frei0rLibrary source = LoadFake(type: Frei0rAbi.TypeSource);
        Assert.Null(source.Effect);
        Assert.Contains(source.Skipped, s => s.Contains("source"));

        Frei0rLibrary mixer = LoadFake(type: Frei0rAbi.TypeMixer2);
        Assert.Null(mixer.Effect);

        Frei0rLibrary future = LoadFake(api: 2);
        Assert.Null(future.Effect);
        Assert.Contains(future.Skipped, s => s.Contains("API version 2"));
    }

    [Fact]
    public void Instance_processes_a_frame_pushes_changed_params_only_and_destructs()
    {
        ICpuVideoEffect effect = LoadFake().Effect!;
        int before = FakeFrei0rPlugin.LiveInstances;
        const int W = 4, H = 2;
        byte* input = (byte*)NativeMemory.AllocZeroed(W * H * 4);
        byte* output = (byte*)NativeMemory.AllocZeroed(W * H * 4);
        try
        {
            for (int i = 0; i < W * H; i++) { input[4 * i] = 10; input[4 * i + 1] = 20; input[4 * i + 2] = 30; input[4 * i + 3] = 200; }

            using (ICpuVideoEffectInstance instance = effect.CreateInstance(W, H))
            {
                Assert.Equal(before + 1, FakeFrei0rPlugin.LiveInstances);
                Assert.Equal(W, instance.Width);
                Assert.Equal(H, instance.Height);

                FakeFrei0rPlugin.SetParamCalls = 0;
                instance.Process((nint)input, (nint)output, 1.25, Params(("p2.r", 0.5), ("p3.x", 0.25), ("p3.y", 0.75)));
                Assert.Equal(4, FakeFrei0rPlugin.SetParamCalls); // amount, bypass, tint, center — string skipped
                Assert.Equal(1.25, FakeFrei0rPlugin.LastTime);
                Assert.Equal(0.25, FakeFrei0rPlugin.LastPosition.X);
                Assert.Equal(0.75, FakeFrei0rPlugin.LastPosition.Y);

                // Inverted then tinted: R (255-10)*0.5 = 122, G 235, B 225, alpha untouched.
                Assert.Equal(122, output[0]);
                Assert.Equal(235, output[1]);
                Assert.Equal(225, output[2]);
                Assert.Equal(200, output[3]);

                // Same parameters again → no pushes; a changed bool → exactly one push and the invert is bypassed.
                instance.Process((nint)input, (nint)output, 1.5, Params(("p2.r", 0.5), ("p3.x", 0.25), ("p3.y", 0.75)));
                Assert.Equal(4, FakeFrei0rPlugin.SetParamCalls);
                instance.Process((nint)input, (nint)output, 1.75, Params(("p1", 1.0), ("p2.r", 0.5), ("p3.x", 0.25), ("p3.y", 0.75)));
                Assert.Equal(5, FakeFrei0rPlugin.SetParamCalls);
                Assert.Equal(5, output[0]); // 10 * 0.5, not inverted
            }
            Assert.Equal(before, FakeFrei0rPlugin.LiveInstances); // f0r_destruct ran
        }
        finally
        {
            NativeMemory.Free(input);
            NativeMemory.Free(output);
        }
    }

    [Fact]
    public void Host_Add_exposes_the_effect_by_id_and_Forget_drops_it()
    {
        var host = new Frei0rHost();
        Frei0rLibraryLoad load = host.Add(LoadFake());

        Assert.NotNull(load.Descriptor);
        Assert.Equal("2.3", load.Version);
        Assert.Same(load.Effect, host.FindEffect("plugin.frei0r.fake_invert"));
        Assert.Null(host.FindEffect("plugin.frei0r.other"));

        host.Forget(FakePath);
        Assert.Null(host.FindEffect("plugin.frei0r.fake_invert"));
    }

    [Fact]
    public void Missing_mandatory_entry_point_is_not_a_frei0r_library()
    {
        Frei0rFunctions functions = FakeFrei0rPlugin.Functions();
        functions.Update = nint.Zero;
        Assert.Throws<EntryPointNotFoundException>(() => Frei0rLibrary.FromFunctions("/tmp/x.so", functions, "x"));
    }
}

[CollectionDefinition("frei0r-fake", DisableParallelization = true)]
public sealed class Frei0rFakeCollection;
