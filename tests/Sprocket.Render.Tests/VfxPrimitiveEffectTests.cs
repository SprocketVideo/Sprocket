using System;
using System.Collections.Generic;
using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Core.Timing;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// The action-VFX primitives (plan/features/special-effects.md, phase 1): Glow, Directional Blur, Zoom Blur,
/// Heat Distortion, Shockwave, Chromatic Aberration, Impact Shake and Flicker — all registry-backed SkSL
/// stages. Rendered on the offscreen CPU backend (the same SkSL the GPU runs) like
/// <see cref="GradingEffectTests"/>, but over patterned sources, since a geometric effect is invisible on a
/// flat colour.
/// </summary>
/// <remarks>
/// Every primitive is asserted on three things: it is a true pass-through at its neutral setting (so an
/// effect sitting unset in a stack costs nothing visible), it does the one thing it claims to do, and — for
/// the time-driven ones — it is a pure function of the frame's time, which is what makes preview and export
/// match frame for frame (§5).
/// </remarks>
public sealed class VfxPrimitiveEffectTests
{
    private const int Size = 32;

    // ── Glow (builtin.glow) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Glow_IntensityZero_IsPassThrough()
    {
        using SKBitmap source = CenterDot();
        AssertSameAsNoEffect(source, Effect(EffectTypeIds.Glow,
            (EffectParamNames.Threshold, 0.5), (EffectParamNames.Radius, 0.1), (EffectParamNames.Intensity, 0.0)));
    }

    [Fact]
    public void Glow_BrightSpot_BleedsIntoItsNeighbours()
    {
        using SKBitmap source = CenterDot();
        using SKBitmap plain = Render(source, []);
        using SKBitmap glowed = Render(source, [Effect(EffectTypeIds.Glow,
            (EffectParamNames.Threshold, 0.5), (EffectParamNames.Radius, 0.12), (EffectParamNames.Intensity, 2.0))]);

        // A few pixels out from the dot is black without the glow and lit with it.
        byte before = plain.GetPixel(Size / 2 + 4, Size / 2).Red;
        byte after = glowed.GetPixel(Size / 2 + 4, Size / 2).Red;
        Assert.InRange(before, 0, 2);
        Assert.True(after > 10, $"the glow should spill onto neighbouring pixels (got {after})");
    }

    [Fact]
    public void Glow_BelowTheThreshold_LeavesTheImageAlone()
    {
        // Mid-grey (luma ≈ 0.5) sits well under a 0.9 threshold, so nothing qualifies as a highlight.
        using SKBitmap source = Solid(new SKColor(128, 128, 128, 255));
        AssertSameAsNoEffect(source, Effect(EffectTypeIds.Glow,
            (EffectParamNames.Threshold, 0.9), (EffectParamNames.Radius, 0.1), (EffectParamNames.Intensity, 2.0)),
            tolerance: 2);
    }

    // ── Directional Blur (builtin.directionalblur) ───────────────────────────────────────────────────

    [Fact]
    public void DirectionalBlur_ZeroLength_IsPassThrough()
    {
        using SKBitmap source = VerticalEdge();
        AssertSameAsNoEffect(source, Effect(EffectTypeIds.DirectionalBlur,
            (EffectParamNames.Angle, 0.0), (EffectParamNames.BlurLength, 0.0)));
    }

    [Fact]
    public void DirectionalBlur_AcrossAnEdge_SoftensIt()
    {
        using SKBitmap source = VerticalEdge();
        using SKBitmap blurred = Render(source, [Effect(EffectTypeIds.DirectionalBlur,
            (EffectParamNames.Angle, 0.0), (EffectParamNames.BlurLength, 0.15))]);

        // Just inside the black half the horizontal smear has dragged white across.
        byte justLeft = blurred.GetPixel(Size / 2 - 1, Size / 2).Red;
        Assert.True(justLeft > 40, $"a horizontal smear should carry the white edge leftward (got {justLeft})");
    }

    [Fact]
    public void DirectionalBlur_AlongAnEdge_LeavesItSharp()
    {
        // Smearing parallel to a vertical edge moves nothing across it — the discriminating case for angle.
        using SKBitmap source = VerticalEdge();
        using SKBitmap blurred = Render(source, [Effect(EffectTypeIds.DirectionalBlur,
            (EffectParamNames.Angle, 90.0), (EffectParamNames.BlurLength, 0.15))]);

        Assert.InRange(blurred.GetPixel(Size / 2 - 1, Size / 2).Red, 0, 2);
        Assert.InRange(blurred.GetPixel(Size / 2 + 1, Size / 2).Red, 253, 255);
    }

    // ── Zoom Blur (builtin.zoomblur) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ZoomBlur_ZeroAmount_IsPassThrough()
    {
        using SKBitmap source = CenterDot();
        AssertSameAsNoEffect(source, Effect(EffectTypeIds.ZoomBlur,
            (EffectParamNames.Amount, 0.0), (EffectParamNames.CenterX, 0.5), (EffectParamNames.CenterY, 0.5)));
    }

    [Fact]
    public void ZoomBlur_StreaksABrightBlockAlongItsRay()
    {
        // The streak grows with distance from the centre, so the block sits out toward the frame edge.
        using SKBitmap source = OffsetBlock();
        using SKBitmap plain = Render(source, []);
        using SKBitmap blurred = Render(source, [Effect(EffectTypeIds.ZoomBlur,
            (EffectParamNames.Amount, 1.0), (EffectParamNames.CenterX, 0.5), (EffectParamNames.CenterY, 0.5))]);

        Assert.InRange(plain.GetPixel(Size - 3, Size / 2).Red, 0, 2);
        Assert.True(blurred.GetPixel(Size - 3, Size / 2).Red > 10,
            "the zoom should streak the block outward from the centre");
    }

    [Fact]
    public void ZoomBlur_LeavesAFlatFieldUnchanged()
    {
        // The sample scale sweeps symmetrically around 1.0, so nothing grows or shrinks.
        using SKBitmap source = Solid(new SKColor(90, 140, 200, 255));
        AssertSameAsNoEffect(source, Effect(EffectTypeIds.ZoomBlur,
            (EffectParamNames.Amount, 1.0), (EffectParamNames.CenterX, 0.5), (EffectParamNames.CenterY, 0.5)),
            tolerance: 2);
    }

    // ── Heat Distortion (builtin.heatdistortion) ─────────────────────────────────────────────────────

    [Fact]
    public void HeatDistortion_ZeroAmount_IsPassThrough()
    {
        using SKBitmap source = HorizontalRamp();
        AssertSameAsNoEffect(source, Heat(0.0, 1.0, frameSeconds: 1.0));
    }

    [Fact]
    public void HeatDistortion_RefractsTheImage()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap plain = Render(source, []);
        using SKBitmap shimmered = Render(source, [Heat(0.05, 1.0, frameSeconds: 1.0)]);
        Assert.True(MaxDifference(plain, shimmered) > 4, "the shimmer should move the picture");
    }

    [Fact]
    public void HeatDistortion_IsAPureFunctionOfFrameTime()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap a1 = Render(source, [Heat(0.05, 1.0, frameSeconds: 1.0)]);
        using SKBitmap a2 = Render(source, [Heat(0.05, 1.0, frameSeconds: 1.0)]);
        using SKBitmap b = Render(source, [Heat(0.05, 1.0, frameSeconds: 2.5)]);

        Assert.Equal(0, MaxDifference(a1, a2));               // same frame time → identical pixels
        Assert.True(MaxDifference(a1, b) > 2, "the shimmer should animate across frames");
    }

    [Fact]
    public void HeatDistortion_SpeedZero_FreezesTheShimmer()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap a = Render(source, [Heat(0.05, 0.0, frameSeconds: 0.0)]);
        using SKBitmap b = Render(source, [Heat(0.05, 0.0, frameSeconds: 9.0)]);
        Assert.Equal(0, MaxDifference(a, b));
    }

    // ── Shockwave (builtin.shockwave) ────────────────────────────────────────────────────────────────

    [Fact]
    public void Shockwave_ZeroAmplitude_IsPassThrough()
    {
        using SKBitmap source = HorizontalRamp();
        AssertSameAsNoEffect(source, Shock(radius: 0.4, amplitude: 0.0));
    }

    [Fact]
    public void Shockwave_DisplacesTheImageInsideTheRing()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap plain = Render(source, []);
        using SKBitmap waved = Render(source, [Shock(radius: 0.2, amplitude: 0.2)]);
        Assert.True(MaxDifference(plain, waved) > 4, "the ring should push the picture around");
    }

    [Fact]
    public void Shockwave_LeavesTheFrameOutsideTheRingAlone()
    {
        // The corners sit at a normalised distance of 1.0 — far outside a ring at 0.2 ± 0.1.
        using SKBitmap source = HorizontalRamp();
        using SKBitmap plain = Render(source, []);
        using SKBitmap waved = Render(source, [Shock(radius: 0.2, amplitude: 0.2)]);
        Assert.Equal(plain.GetPixel(0, 0).Red, waved.GetPixel(0, 0).Red);
        Assert.Equal(plain.GetPixel(Size - 1, Size - 1).Red, waved.GetPixel(Size - 1, Size - 1).Red);
    }

    // ── Chromatic Aberration (builtin.chromaticaberration) ───────────────────────────────────────────

    [Fact]
    public void ChromaticAberration_ZeroAmount_IsPassThrough()
    {
        using SKBitmap source = HorizontalRamp();
        AssertSameAsNoEffect(source, Effect(EffectTypeIds.ChromaticAberration,
            (EffectParamNames.Amount, 0.0), (EffectParamNames.CenterX, 0.5), (EffectParamNames.CenterY, 0.5)));
    }

    [Fact]
    public void ChromaticAberration_SplitsRedAndBlueAwayFromTheCentre()
    {
        using SKBitmap source = HorizontalRamp(); // neutral grey ramp: r == g == b everywhere
        using SKBitmap split = Render(source, [Effect(EffectTypeIds.ChromaticAberration,
            (EffectParamNames.Amount, 1.0), (EffectParamNames.CenterX, 0.5), (EffectParamNames.CenterY, 0.5))]);

        SKColor edge = split.GetPixel(Size - 3, Size / 2);
        Assert.True(Math.Abs(edge.Red - edge.Blue) > 2,
            $"red and blue should separate near the frame edge (R {edge.Red}, B {edge.Blue})");
    }

    [Fact]
    public void ChromaticAberration_LeavesTheOpticalCentreUnsplit()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap split = Render(source, [Effect(EffectTypeIds.ChromaticAberration,
            (EffectParamNames.Amount, 1.0), (EffectParamNames.CenterX, 0.5), (EffectParamNames.CenterY, 0.5))]);

        // At the centre the radial offset is zero, so all three channels come from the same sample.
        SKColor c = split.GetPixel(Size / 2, Size / 2);
        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Green, c.Blue);
    }

    // ── Impact Shake (builtin.impactshake) ───────────────────────────────────────────────────────────

    [Fact]
    public void ImpactShake_ZeroAmountAndNoOverscan_IsPassThrough()
    {
        using SKBitmap source = HorizontalRamp();
        AssertSameAsNoEffect(source, Shake(amount: 0.0, overscan: 1.0, frameSeconds: 1.0));
    }

    [Fact]
    public void ImpactShake_MovesTheFrame()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap plain = Render(source, []);
        using SKBitmap shaken = Render(source, [Shake(amount: 1.0, overscan: 1.0, frameSeconds: 1.0)]);
        Assert.True(MaxDifference(plain, shaken) > 4, "the shake should throw the frame around");
    }

    [Fact]
    public void ImpactShake_IsAPureFunctionOfFrameTime()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap a1 = Render(source, [Shake(amount: 1.0, overscan: 1.0, frameSeconds: 1.0)]);
        using SKBitmap a2 = Render(source, [Shake(amount: 1.0, overscan: 1.0, frameSeconds: 1.0)]);
        using SKBitmap b = Render(source, [Shake(amount: 1.0, overscan: 1.0, frameSeconds: 1.37)]);

        Assert.Equal(0, MaxDifference(a1, a2));
        Assert.True(MaxDifference(a1, b) > 2, "the shake should differ frame to frame");
    }

    [Fact]
    public void ImpactShake_OverscanScalesTheFrameUpWithNoShake()
    {
        using SKBitmap source = HorizontalRamp();
        using SKBitmap plain = Render(source, []);
        using SKBitmap zoomed = Render(source, [Shake(amount: 0.0, overscan: 1.5, frameSeconds: 1.0)]);

        // Pure zoom about the centre: the centre column is fixed, the edges pull inward toward it.
        Assert.InRange(zoomed.GetPixel(Size / 2, Size / 2).Red, plain.GetPixel(Size / 2, Size / 2).Red - 2,
            plain.GetPixel(Size / 2, Size / 2).Red + 2);
        Assert.True(zoomed.GetPixel(1, Size / 2).Red > plain.GetPixel(1, Size / 2).Red + 4,
            "the overscan should pull the ramp's darker left edge inward");
    }

    // ── Flicker (builtin.flicker) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Flicker_ZeroAmount_IsPassThrough()
    {
        using SKBitmap source = Solid(new SKColor(120, 120, 120, 255));
        AssertSameAsNoEffect(source, Flick(amount: 0.0, randomness: 0.0, frameSeconds: 0.3));
    }

    [Fact]
    public void Flicker_SteadyPulse_IsUnityAtTheStartOfACycle()
    {
        // With randomness 0 the pulse is a sine sitting at its midpoint when t = 0, so gain is exactly 1.
        using SKBitmap source = Solid(new SKColor(120, 120, 120, 255));
        AssertSameAsNoEffect(source, Flick(amount: 1.0, randomness: 0.0, frameSeconds: 0.0), tolerance: 2);
    }

    [Fact]
    public void Flicker_PulsesExposureOverTime()
    {
        using SKBitmap source = Solid(new SKColor(120, 120, 120, 255));
        byte quarter = Render(source, [Flick(amount: 0.8, randomness: 0.0, frameSeconds: 0.25 / 6.0)])
            .GetPixel(Size / 2, Size / 2).Red; // a quarter cycle in at 6 Hz → the crest
        byte trough = Render(source, [Flick(amount: 0.8, randomness: 0.0, frameSeconds: 0.75 / 6.0)])
            .GetPixel(Size / 2, Size / 2).Red;

        Assert.True(quarter > 120 + 10, $"the crest should brighten (got {quarter})");
        Assert.True(trough < 120 - 10, $"the trough should darken (got {trough})");
    }

    [Fact]
    public void Flicker_IsAPureFunctionOfFrameTime()
    {
        using SKBitmap source = Solid(new SKColor(120, 120, 120, 255));
        using SKBitmap a1 = Render(source, [Flick(amount: 0.8, randomness: 1.0, frameSeconds: 1.1)]);
        using SKBitmap a2 = Render(source, [Flick(amount: 0.8, randomness: 1.0, frameSeconds: 1.1)]);
        using SKBitmap b = Render(source, [Flick(amount: 0.8, randomness: 1.0, frameSeconds: 1.6)]);

        Assert.Equal(0, MaxDifference(a1, a2));
        Assert.True(MaxDifference(a1, b) > 2, "the irregular flicker should differ frame to frame");
    }

    // ── Effect builders ──────────────────────────────────────────────────────────────────────────────

    private static ResolvedEffect Effect(string id, params (string Name, double Value)[] values)
    {
        var parameters = new Dictionary<string, double>();
        foreach ((string name, double value) in values)
            parameters[name] = value;
        return new ResolvedEffect(id, parameters);
    }

    private static ResolvedEffect AtTime(ResolvedEffect effect, double seconds) =>
        effect with { FrameTime = (long)Math.Round(seconds * Timecode.TicksPerSecond) };

    private static ResolvedEffect Heat(double amount, double speed, double frameSeconds) =>
        AtTime(Effect(EffectTypeIds.HeatDistortion,
            (EffectParamNames.Amount, amount), (EffectParamNames.NoiseScale, 12.0), (EffectParamNames.Speed, speed)),
            frameSeconds);

    private static ResolvedEffect Shock(double radius, double amplitude) =>
        Effect(EffectTypeIds.Shockwave,
            (EffectParamNames.Radius, radius), (EffectParamNames.RingWidth, 0.1),
            (EffectParamNames.Amplitude, amplitude),
            (EffectParamNames.CenterX, 0.5), (EffectParamNames.CenterY, 0.5));

    private static ResolvedEffect Shake(double amount, double overscan, double frameSeconds) =>
        AtTime(Effect(EffectTypeIds.ImpactShake,
            (EffectParamNames.Amount, amount), (EffectParamNames.Frequency, 8.0),
            (EffectParamNames.Rotation, 2.0), (EffectParamNames.Overscan, overscan)),
            frameSeconds);

    private static ResolvedEffect Flick(double amount, double randomness, double frameSeconds) =>
        AtTime(Effect(EffectTypeIds.Flicker,
            (EffectParamNames.Amount, amount), (EffectParamNames.Frequency, 6.0),
            (EffectParamNames.Randomness, randomness)),
            frameSeconds);

    // ── Sources ──────────────────────────────────────────────────────────────────────────────────────

    private static SKBitmap Solid(SKColor color)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(color);
        return bitmap;
    }

    /// <summary>Black with a small white block at the centre — the bright spot a glow spreads.</summary>
    private static SKBitmap CenterDot()
    {
        SKBitmap bitmap = Solid(SKColors.Black);
        for (int y = Size / 2 - 1; y <= Size / 2 + 1; y++)
            for (int x = Size / 2 - 1; x <= Size / 2 + 1; x++)
                bitmap.SetPixel(x, y, SKColors.White);
        return bitmap;
    }

    /// <summary>Black with a white block out toward the right edge, where a radial streak has room to run.</summary>
    private static SKBitmap OffsetBlock()
    {
        SKBitmap bitmap = Solid(SKColors.Black);
        for (int y = Size / 2 - 2; y <= Size / 2 + 2; y++)
            for (int x = Size - 10; x <= Size - 6; x++)
                bitmap.SetPixel(x, y, SKColors.White);
        return bitmap;
    }

    /// <summary>Black left half, white right half — a hard vertical edge for the directional blur.</summary>
    private static SKBitmap VerticalEdge()
    {
        SKBitmap bitmap = Solid(SKColors.Black);
        for (int y = 0; y < Size; y++)
            for (int x = Size / 2; x < Size; x++)
                bitmap.SetPixel(x, y, SKColors.White);
        return bitmap;
    }

    /// <summary>A neutral left-to-right grey ramp: any displacement shows up as a value change.</summary>
    private static SKBitmap HorizontalRamp()
    {
        var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                byte v = (byte)(x * 255 / (Size - 1));
                bitmap.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        return bitmap;
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────────────

    private static SKBitmap Render(SKBitmap source, IReadOnlyList<ResolvedEffect> effects)
    {
        using var pipeline = new SkiaEffectPipeline();
        using SKSurface surface = SKSurface.Create(
            new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        pipeline.DrawLayer(surface.Canvas, SKRect.Create(source.Width, source.Height), source.GetPixels(),
            source.RowBytes, source.Width, source.Height, effects, hasAlpha: false);
        surface.Canvas.Flush();
        using SKImage image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    /// <summary>The largest per-pixel red-channel difference between two renders of the same source.</summary>
    private static int MaxDifference(SKBitmap a, SKBitmap b)
    {
        int max = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                max = Math.Max(max, Math.Abs(a.GetPixel(x, y).Red - b.GetPixel(x, y).Red));
        return max;
    }

    /// <summary>Asserts that <paramref name="effect"/> at its neutral setting renders the source unchanged.</summary>
    private static void AssertSameAsNoEffect(SKBitmap source, ResolvedEffect effect, int tolerance = 1)
    {
        using SKBitmap plain = Render(source, []);
        using SKBitmap through = Render(source, [effect]);
        Assert.True(MaxDifference(plain, through) <= tolerance,
            $"'{effect.EffectTypeId}' should be a pass-through here (max difference {MaxDifference(plain, through)})");
    }
}
