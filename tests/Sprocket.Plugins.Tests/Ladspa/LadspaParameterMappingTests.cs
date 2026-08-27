using Sprocket.Core.Model;
using Sprocket.Plugins.Ladspa;
using Xunit;

namespace Sprocket.Plugins.Tests.Ladspa;

/// <summary>
/// Pure tests of the LADSPA → Sprocket parameter mapping (PLAN.md step 59): control-port range hints become
/// typed <see cref="EffectParameterDescriptor"/>s, and a plugin becomes an Audio <see cref="EffectDescriptor"/>.
/// No native memory involved — the mapping is where all the hint arithmetic lives.
/// </summary>
public sealed class LadspaParameterMappingTests
{
    private static LadspaPortInfo Ctrl(int hint, float lower = 0, float upper = 0, int index = 0, string name = "Param") =>
        new(index, LadspaAbi.PortControl | LadspaAbi.PortInput, name, hint, lower, upper);

    private static LadspaPluginInfo Plugin(ulong id, string label, params LadspaPortInfo[] extra)
    {
        var ports = new List<LadspaPortInfo>
        {
            new(100, LadspaAbi.PortAudio | LadspaAbi.PortInput, "In", 0, 0, 0),
            new(101, LadspaAbi.PortAudio | LadspaAbi.PortOutput, "Out", 0, 0, 0),
        };
        ports.AddRange(extra);
        return new LadspaPluginInfo(id, label, "Nice Plugin", "Me", "GPL", ports);
    }

    [Fact]
    public void Bounded_range_maps_directly()
    {
        EffectParameterDescriptor p = LadspaParameterMapping.ToParameter(
            Ctrl(LadspaAbi.HintBoundedBelow | LadspaAbi.HintBoundedAbove | LadspaAbi.HintDefaultMinimum, 2f, 8f));
        Assert.Equal(2.0, p.Min, 3);
        Assert.Equal(8.0, p.Max, 3);
        Assert.Equal(2.0, p.Default, 3);
        Assert.Equal(ParameterKind.Continuous, p.Kind);
    }

    [Theory]
    [InlineData(LadspaAbi.HintDefaultMinimum, 0.0)]
    [InlineData(LadspaAbi.HintDefaultLow, 2.5)]
    [InlineData(LadspaAbi.HintDefaultMiddle, 5.0)]
    [InlineData(LadspaAbi.HintDefaultHigh, 7.5)]
    [InlineData(LadspaAbi.HintDefaultMaximum, 10.0)]
    public void Default_hints_interpolate_linear(int defaultBits, double expected)
    {
        int hint = LadspaAbi.HintBoundedBelow | LadspaAbi.HintBoundedAbove | defaultBits;
        EffectParameterDescriptor p = LadspaParameterMapping.ToParameter(Ctrl(hint, 0f, 10f));
        Assert.Equal(expected, p.Default, 3);
    }

    [Theory]
    [InlineData(LadspaAbi.HintDefault0, 0.0)]
    [InlineData(LadspaAbi.HintDefault1, 1.0)]
    [InlineData(LadspaAbi.HintDefault100, 100.0)]
    [InlineData(LadspaAbi.HintDefault440, 440.0)]
    public void Fixed_default_hints_are_literal(int defaultBits, double expected)
    {
        // A wide range so the literal default sits inside it and is not clamped.
        int hint = LadspaAbi.HintBoundedBelow | LadspaAbi.HintBoundedAbove | defaultBits;
        EffectParameterDescriptor p = LadspaParameterMapping.ToParameter(Ctrl(hint, 0f, 1000f));
        Assert.Equal(expected, p.Default, 3);
    }

    [Fact]
    public void Logarithmic_middle_is_geometric_mean()
    {
        int hint = LadspaAbi.HintBoundedBelow | LadspaAbi.HintBoundedAbove
            | LadspaAbi.HintLogarithmic | LadspaAbi.HintDefaultMiddle;
        EffectParameterDescriptor p = LadspaParameterMapping.ToParameter(Ctrl(hint, 100f, 10000f));
        Assert.Equal(1000.0, p.Default, 1); // exp((ln100 + ln10000)/2)
    }

    [Fact]
    public void Sample_rate_hint_scales_bounds_by_nominal_rate()
    {
        int hint = LadspaAbi.HintBoundedBelow | LadspaAbi.HintBoundedAbove
            | LadspaAbi.HintSampleRate | LadspaAbi.HintDefaultMaximum;
        EffectParameterDescriptor p = LadspaParameterMapping.ToParameter(Ctrl(hint, 0f, 0.5f));
        Assert.Equal(0.5 * LadspaParameterMapping.NominalSampleRate, p.Max, 3); // 24000 at 48 kHz
        Assert.Equal(p.Max, p.Default, 3);
    }

    [Fact]
    public void Toggled_port_becomes_a_toggle()
    {
        EffectParameterDescriptor on = LadspaParameterMapping.ToParameter(
            Ctrl(LadspaAbi.HintToggled | LadspaAbi.HintDefault1));
        Assert.Equal(ParameterKind.Toggle, on.Kind);
        Assert.Equal(0.0, on.Min);
        Assert.Equal(1.0, on.Max);
        Assert.Equal(1.0, on.Default);

        EffectParameterDescriptor off = LadspaParameterMapping.ToParameter(Ctrl(LadspaAbi.HintToggled));
        Assert.Equal(0.0, off.Default);
    }

    [Fact]
    public void Integer_port_becomes_an_integer_with_unit_step()
    {
        EffectParameterDescriptor p = LadspaParameterMapping.ToParameter(
            Ctrl(LadspaAbi.HintBoundedBelow | LadspaAbi.HintBoundedAbove | LadspaAbi.HintInteger | LadspaAbi.HintDefaultMiddle, 0f, 7f));
        Assert.Equal(ParameterKind.Integer, p.Kind);
        Assert.Equal(1.0, p.Step, 3);
        Assert.Equal(4.0, p.Default, 3); // 3.5 rounded
    }

    [Fact]
    public void Unbounded_port_gets_a_finite_fallback_range()
    {
        EffectParameterDescriptor p = LadspaParameterMapping.ToParameter(Ctrl(LadspaAbi.HintDefaultNone));
        Assert.True(p.Max > p.Min);
        Assert.InRange(p.Default, p.Min, p.Max);
    }

    [Fact]
    public void EffectId_prefers_unique_id_then_label()
    {
        Assert.Equal("plugin.ladspa.1234", LadspaParameterMapping.EffectId(Plugin(1234, "amp")));
        Assert.Equal("plugin.ladspa.amp", LadspaParameterMapping.EffectId(Plugin(0, "Amp!")));
    }

    [Fact]
    public void ToEffectDescriptor_is_audio_with_one_parameter_per_control_input()
    {
        LadspaPluginInfo info = Plugin(1234, "amp",
            Ctrl(LadspaAbi.HintBoundedBelow | LadspaAbi.HintBoundedAbove, 0f, 1f, index: 2, name: "Drive"),
            Ctrl(LadspaAbi.HintToggled, index: 3, name: "Bypass"));

        EffectDescriptor d = LadspaParameterMapping.ToEffectDescriptor(info);

        Assert.Equal("plugin.ladspa.1234", d.Id);
        Assert.Equal(EffectCategory.Audio, d.Category);
        Assert.Equal("Nice Plugin", d.DisplayName);
        Assert.Equal(2, d.Parameters.Count);
        Assert.Equal("port2", d.Parameters[0].Name);
        Assert.Equal("Drive", d.Parameters[0].DisplayName);
        Assert.Equal("port3", d.Parameters[1].Name);
    }

    [Fact]
    public void ParameterName_is_stable_per_port_index() =>
        Assert.Equal("port7", LadspaParameterMapping.ParameterName(7));
}
