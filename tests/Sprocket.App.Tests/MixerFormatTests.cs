using Sprocket.App;
using Xunit;

namespace Sprocket.App.Tests;

/// <summary>Covers the pure audio-mixer / loudness-meter formatting (PLAN.md step 30, UI.md §3.3): gain and pan
/// labels, LUFS / dBTP read-outs, and the meter fill fraction.</summary>
public class MixerFormatTests
{
    [Theory]
    [InlineData(0.0, "0.0 dB")]
    [InlineData(3.0, "+3.0 dB")]
    [InlineData(-6.0, "-6.0 dB")]
    [InlineData(-60.0, "-∞ dB")]
    [InlineData(double.NegativeInfinity, "-∞ dB")]
    public void GainDbLabel_signs_and_floors(double db, string expected) =>
        Assert.Equal(expected, MixerFormat.GainDbLabel(db));

    [Fact]
    public void GainDbLabel_does_not_render_negative_zero()
    {
        Assert.Equal("0.0 dB", MixerFormat.GainDbLabel(-0.01));
    }

    [Theory]
    [InlineData(0.0, "C")]
    [InlineData(-1.0, "L100")]
    [InlineData(1.0, "R100")]
    [InlineData(-0.5, "L50")]
    [InlineData(0.25, "R25")]
    public void PanLabel_names_the_side(double pan, string expected) =>
        Assert.Equal(expected, MixerFormat.PanLabel(pan));

    // ── TryParseGainDb / TryParsePan: the strip read-outs are typed into, not just dragged ───────────────

    [Theory]
    [InlineData("-6", -6.0)]
    [InlineData("-6.0 dB", -6.0)]     // exactly what GainDbLabel renders
    [InlineData("+3dB", 3.0)]         // no space
    [InlineData("  +3.0 DB ", 3.0)]   // any case, surrounding whitespace
    [InlineData("0", 0.0)]
    [InlineData("-∞", MixerFormat.SilenceFloorDb)]      // the silence sentinel commits the fader floor…
    [InlineData("-∞ dB", MixerFormat.SilenceFloorDb)]
    [InlineData("-inf", MixerFormat.SilenceFloorDb)]    // …in the spellings a keyboard can produce
    [InlineData("-Infinity", MixerFormat.SilenceFloorDb)]
    public void TryParseGainDb_accepts_typed_and_displayed_values(string text, double expected)
    {
        Assert.True(MixerFormat.TryParseGainDb(text, out double db));
        Assert.Equal(expected, db, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("loud")]
    [InlineData("dB")]     // unit only, no number
    [InlineData("∞")]      // unsigned infinity is ambiguous, not silence
    public void TryParseGainDb_rejects_nonsense(string? text) =>
        Assert.False(MixerFormat.TryParseGainDb(text, out _));

    [Theory]
    [InlineData("C", 0.0)]
    [InlineData("c", 0.0)]
    [InlineData("L50", -0.5)]      // exactly what PanLabel renders
    [InlineData("R25", 0.25)]
    [InlineData("l100", -1.0)]
    [InlineData("R 25", 0.25)]
    [InlineData("-50", -0.5)]      // bare -100..100, the Premiere pan-field convention
    [InlineData("25", 0.25)]
    [InlineData("0", 0.0)]
    [InlineData("R500", 1.0)]      // out of range clamps rather than rejecting
    public void TryParsePan_accepts_sides_and_bare_percentages(string text, double expected)
    {
        Assert.True(MixerFormat.TryParsePan(text, out double pan));
        Assert.Equal(expected, pan, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("X9")]
    [InlineData("L")]        // side with no magnitude
    [InlineData("Lleft")]
    [InlineData("centre")]
    public void TryParsePan_rejects_nonsense(string? text) =>
        Assert.False(MixerFormat.TryParsePan(text, out _));

    [Theory]
    [InlineData(-6.0)]
    [InlineData(0.0)]
    [InlineData(3.5)]
    [InlineData(MixerFormat.SilenceFloorDb)]
    public void GainDbLabel_round_trips_through_TryParseGainDb(double db)
    {
        Assert.True(MixerFormat.TryParseGainDb(MixerFormat.GainDbLabel(db), out double back));
        Assert.Equal(db, back, 6);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.5)]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(1.0)]
    public void PanLabel_round_trips_through_TryParsePan(double pan)
    {
        Assert.True(MixerFormat.TryParsePan(MixerFormat.PanLabel(pan), out double back));
        Assert.Equal(pan, back, 6);
    }

    [Theory]
    [InlineData(-14.2, "-14.2 LUFS")]
    [InlineData(double.NegativeInfinity, "-∞ LUFS")]
    public void LufsLabel_formats(double lufs, string expected) => Assert.Equal(expected, MixerFormat.LufsLabel(lufs));

    [Theory]
    [InlineData(-1.0, "-1.0 dBTP")]
    [InlineData(double.NegativeInfinity, "-∞ dBTP")]
    public void DbtpLabel_formats(double dbtp, string expected) => Assert.Equal(expected, MixerFormat.DbtpLabel(dbtp));

    [Theory]
    [InlineData(0.0, 1.0)]                        // at ceiling → full
    [InlineData(-60.0, 0.0)]                      // at floor → empty
    [InlineData(-30.0, 0.5)]                      // half-way (default -60..0)
    [InlineData(double.NegativeInfinity, 0.0)]    // silence → empty
    [InlineData(6.0, 1.0)]                        // above ceiling clamps
    public void MeterFillFraction_maps_db_to_zero_one(double db, double expected) =>
        Assert.Equal(expected, MixerFormat.MeterFillFraction(db), 6);
}
