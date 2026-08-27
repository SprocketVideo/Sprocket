using Sprocket.Core.Model;
using Sprocket.Plugins.Lv2;
using Xunit;

namespace Sprocket.Plugins.Tests.Lv2;

/// <summary>The pure LV2 port → parameter mapping (PLAN.md step 59).</summary>
public sealed class Lv2ParameterMappingTests
{
    private static Lv2PortInfo Control(
        int index = 0, string symbol = "gain", string name = "Gain",
        double? def = null, double? min = null, double? max = null,
        string? unit = null, IReadOnlyList<Lv2ScalePoint>? scalePoints = null, params string[] properties) =>
        new(index, symbol, name,
            new HashSet<string> { Lv2Ns.InputPort, Lv2Ns.ControlPort },
            new HashSet<string>(properties),
            def, min, max, unit, scalePoints ?? []);

    private static Lv2PluginInfo Plugin(params Lv2PortInfo[] ports) =>
        new("http://example.org/amp", "Amp", "Maker", "", "", "amp.so", "/b", 1, 0, [], ports);

    [Fact]
    public void Bounded_continuous_port_maps_range_default_unit_and_symbol_key()
    {
        EffectParameterDescriptor p = Lv2ParameterMapping.ToParameter(Control(def: 0.0, min: -90.0, max: 24.0, unit: Lv2Ns.Units + "db"));

        Assert.Equal("gain", p.Name);
        Assert.Equal("Gain", p.DisplayName);
        Assert.Equal(-90.0, p.Min);
        Assert.Equal(24.0, p.Max);
        Assert.Equal(0.0, p.Default);
        Assert.Equal("dB", p.Unit);
        Assert.Equal(ParameterKind.Continuous, p.Kind);
    }

    [Fact]
    public void Toggled_port_becomes_a_Toggle()
    {
        EffectParameterDescriptor p = Lv2ParameterMapping.ToParameter(Control(def: 1.0, min: 0, max: 1, properties: Lv2Ns.Toggled));
        Assert.Equal(ParameterKind.Toggle, p.Kind);
        Assert.Equal(1.0, p.Default);
    }

    [Fact]
    public void Integer_port_becomes_an_Integer_with_step_1()
    {
        EffectParameterDescriptor p = Lv2ParameterMapping.ToParameter(Control(def: 2.4, min: 0, max: 10, properties: Lv2Ns.Integer));
        Assert.Equal(ParameterKind.Integer, p.Kind);
        Assert.Equal(1.0, p.Step);
        Assert.Equal(2.0, p.Default);
    }

    [Fact]
    public void Index_enumeration_becomes_a_Dropdown_with_labels_in_value_order()
    {
        Lv2ScalePoint[] points = [new("Clip", 2), new("Soft", 0), new("Hard", 1)];
        EffectParameterDescriptor p = Lv2ParameterMapping.ToParameter(Control(def: 1, min: 0, max: 2, scalePoints: points, properties: Lv2Ns.Enumeration));

        Assert.Equal(ParameterKind.Dropdown, p.Kind);
        Assert.Equal(["Soft", "Hard", "Clip"], p.Choices);
        Assert.Equal(1.0, p.Default);
        Assert.Equal(2.0, p.Max);
    }

    [Fact]
    public void Non_index_enumeration_falls_back_to_an_integer_slider_bounded_by_its_scale_points()
    {
        Lv2ScalePoint[] points = [new("44.1k", 44100), new("48k", 48000), new("96k", 96000)];
        EffectParameterDescriptor p = Lv2ParameterMapping.ToParameter(Control(def: 48000, scalePoints: points, properties: Lv2Ns.Enumeration));

        Assert.Equal(ParameterKind.Integer, p.Kind);
        Assert.Null(p.Choices);
        Assert.Equal(44100, p.Min);
        Assert.Equal(96000, p.Max);
    }

    [Fact]
    public void SampleRate_property_scales_bounds_for_display_only()
    {
        EffectParameterDescriptor p = Lv2ParameterMapping.ToParameter(Control(def: 1000, min: 0.0, max: 0.5, properties: Lv2Ns.SampleRate));
        Assert.Equal(0.0, p.Min);
        Assert.Equal(0.5 * Lv2ParameterMapping.NominalSampleRate, p.Max);
        Assert.Equal(1000, p.Default); // the value itself is not scaled
    }

    [Fact]
    public void Missing_bounds_are_invented_around_the_default()
    {
        EffectParameterDescriptor p = Lv2ParameterMapping.ToParameter(Control(def: 5.0));
        Assert.True(p.Min <= 5.0 && p.Max >= 5.0 && p.Max > p.Min);
        Assert.Equal(5.0, p.Default);

        EffectParameterDescriptor none = Lv2ParameterMapping.ToParameter(Control());
        Assert.Equal(0.0, none.Default);
        Assert.True(none.Max > none.Min);
    }

    [Fact]
    public void Missing_symbol_falls_back_to_the_port_index_key()
    {
        Assert.Equal("port7", Lv2ParameterMapping.ParameterName(Control(index: 7, symbol: "")));
    }

    [Fact]
    public void Descriptor_uses_the_plugin_uri_id_audio_category_and_skips_notOnGUI_ports()
    {
        Lv2PortInfo hidden = Control(index: 1, symbol: "hidden", properties: Lv2Ns.PortPropsNotOnGui);
        EffectDescriptor d = Lv2ParameterMapping.ToEffectDescriptor(Plugin(Control(def: 0, min: -1, max: 1), hidden));

        Assert.Equal("plugin.lv2.http://example.org/amp", d.Id);
        Assert.Equal("Amp", d.DisplayName);
        Assert.Equal(EffectCategory.Audio, d.Category);
        Assert.Contains("Maker", d.Description);
        Assert.Single(d.Parameters);
        Assert.Equal("gain", d.Parameters[0].Name);
    }

    [Theory]
    [InlineData("hz", "Hz")]
    [InlineData("pc", "%")]
    [InlineData("ms", "ms")]
    [InlineData("nonsense", null)]
    public void Unit_suffixes(string unit, string? expected) =>
        Assert.Equal(expected, Lv2ParameterMapping.UnitSuffix(Lv2Ns.Units + unit));

    [Fact]
    public void Unsupported_required_features_are_reported()
    {
        Assert.Empty(Lv2Features.Unsupported([Lv2Ns.UridMap, Lv2Ns.UridUnmap, Lv2Ns.HardRtCapable, Lv2Ns.InPlaceBroken]));
        Assert.Equal(["http://lv2plug.in/ns/ext/worker#schedule"],
            Lv2Features.Unsupported([Lv2Ns.UridMap, "http://lv2plug.in/ns/ext/worker#schedule"]));
    }
}
