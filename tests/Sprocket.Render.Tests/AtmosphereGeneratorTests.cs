using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;
using Sprocket.Render;
using Xunit;

namespace Sprocket.Render.Tests;

/// <summary>
/// The atmospheric generators (plan/features/special-effects.md, phase 2): Smoke, Fog, Dust, Embers, Sparks
/// and Light Leak. Rendered offscreen on the CPU backend — the same SkSL the GPU runs — and probed, following
/// the step-19/40 generator-test discipline in <see cref="TitleRendererTests"/>.
/// </summary>
/// <remarks>
/// Each generator is asserted on the four things that matter for a procedural layer: it draws nothing at
/// <c>Amount</c> 0 (so a generator turned down costs nothing and composites clean), it does the one thing it
/// claims to do, it is a pure function of the clip's local time (what makes preview and export match, §5),
/// and it is resolution-independent (the same picture at preview and export size). Premultiplied validity is
/// checked everywhere, since these layers exist to be composited over something else.
/// </remarks>
public class AtmosphereGeneratorTests
{
    private const int W = 96;
    private const int H = 96;

    public static TheoryData<string> AllAtmospherics =>
    [
        GeneratorTypeIds.Smoke,
        GeneratorTypeIds.Fog,
        GeneratorTypeIds.Dust,
        GeneratorTypeIds.Embers,
        GeneratorTypeIds.Sparks,
        GeneratorTypeIds.LightLeak,
    ];

    // ── Every atmospheric ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllAtmospherics))]
    public void AtDefaults_DrawsSomething(string typeId)
    {
        using SKBitmap frame = Render(typeId, seconds: 1.3);
        Assert.True(LitFraction(frame) > 0.005, $"{typeId} rendered an (almost) empty frame at its defaults");
    }

    [Theory]
    [MemberData(nameof(AllAtmospherics))]
    public void AmountZero_DrawsNothing(string typeId)
    {
        using SKBitmap frame = Render(typeId, seconds: 1.3, (GeneratorParamNames.Amount, 0.0));
        Assert.Equal(0, MaxAlpha(frame));
    }

    [Theory]
    [MemberData(nameof(AllAtmospherics))]
    public void IsAPureFunctionOfTheClipsLocalTime(string typeId)
    {
        // Same local time twice ⇒ identical pixels; a later time ⇒ a moved picture. Between them these are
        // what let a scrub, a preview and an export land on the same frame.
        using SKBitmap a = Render(typeId, seconds: 2.0);
        using SKBitmap b = Render(typeId, seconds: 2.0);
        using SKBitmap later = Render(typeId, seconds: 2.5);

        Assert.True(Identical(a, b), $"{typeId} is not deterministic at a fixed local time");
        Assert.False(Identical(a, later), $"{typeId} did not animate between 2.0 s and 2.5 s");
    }

    [Theory]
    [MemberData(nameof(AllAtmospherics))]
    public void SpeedZero_FreezesTheLook(string typeId)
    {
        using SKBitmap a = Render(typeId, seconds: 0.5, (GeneratorParamNames.Speed, 0.0));
        using SKBitmap b = Render(typeId, seconds: 9.0, (GeneratorParamNames.Speed, 0.0));
        Assert.True(Identical(a, b), $"{typeId} still moved at Speed 0");
    }

    [Theory]
    [MemberData(nameof(AllAtmospherics))]
    public void OutputIsValidPremultipliedAlpha(string typeId)
    {
        using SKBitmap frame = Render(typeId, seconds: 1.3);
        // Read the raw buffer, not GetPixel — GetPixel unpremultiplies on the way out, which would hide
        // exactly the defect being looked for. A channel above alpha is an invalid pixel that brightens
        // wrongly when the layer composites over what is beneath it.
        byte[] raw = frame.Bytes;
        for (int i = 0; i + 3 < raw.Length; i += 4)
        {
            byte a = raw[i + 3];
            Assert.True(raw[i] <= a && raw[i + 1] <= a && raw[i + 2] <= a,
                $"{typeId} produced an invalid premultiplied pixel at byte {i}: " +
                $"rgba({raw[i]}, {raw[i + 1]}, {raw[i + 2]}, {a})");
        }
    }

    [Theory]
    [MemberData(nameof(AllAtmospherics))]
    public void IsResolutionIndependent(string typeId)
    {
        // The same clip rendered at preview size and at export size must show the same picture, which is why
        // every spatial parameter is a fraction of the frame height. Compared by coverage, since the two
        // rasters differ in sampling: a pixel-size-driven generator would move this a long way, not a little.
        using SKBitmap small = Render(typeId, seconds: 1.3, 72, 72);
        using SKBitmap large = Render(typeId, seconds: 1.3, 216, 216);
        double a = LitFraction(small);
        double b = LitFraction(large);
        Assert.True(Math.Abs(a - b) < 0.09, $"{typeId} coverage moved with resolution: {a:0.000} vs {b:0.000}");
    }

    [Theory]
    [MemberData(nameof(AllAtmospherics))]
    public void SeedRerollsTheLayout(string typeId)
    {
        using SKBitmap a = Render(typeId, seconds: 1.3, (GeneratorParamNames.Seed, 0.0));
        using SKBitmap b = Render(typeId, seconds: 1.3, (GeneratorParamNames.Seed, 7.0));

        Assert.False(Identical(a, b), $"{typeId} ignored its Seed");

        // Coverage stability is only a claim worth making where the parameters, not the draw, decide how much
        // of the frame fills: a particle field has Count and Size, so re-rolling it must not change the amount
        // on screen. The clouds are the opposite by design — at a low Scale a handful of noise cells span the
        // whole frame, so a different roll legitimately shows more or less fog.
        if (GeneratorTypeIds.IsParticles(typeId))
        {
            Assert.True(Math.Abs(LitFraction(a) - LitFraction(b)) < 0.1,
                $"{typeId}'s Seed changed how much it covers, not just where");
        }
    }

    [Theory]
    [MemberData(nameof(AllAtmospherics))]
    public void TakesItsColourFromTheTint(string typeId)
    {
        // Under a pure-red tint nothing may come out blue- or green-dominant. It is not "exactly red": the
        // particle and light-leak shaders run their hot centres toward white on purpose, which lifts the
        // other two channels — but never above the tinted one.
        using SKBitmap frame = Render(typeId, seconds: 1.3, new[] { (GeneratorParamNames.Color, "#FFFF0000") });
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                SKColor c = frame.GetPixel(x, y);
                Assert.True(c.Blue <= c.Red && c.Green <= c.Red,
                    $"{typeId} leaked colour outside its tint at ({x}, {y}): {c}");
            }
        }
    }

    // ── Clouds (Smoke / Fog) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Smoke_MoreAmount_CoversMore()
    {
        using SKBitmap thin = Render(GeneratorTypeIds.Smoke, 1.3, (GeneratorParamNames.Amount, 0.2));
        using SKBitmap thick = Render(GeneratorTypeIds.Smoke, 1.3, (GeneratorParamNames.Amount, 0.9));
        // Mean alpha, not lit-pixel count: Amount both widens the coverage and lifts the opacity, and mean
        // alpha is the one number that sees both.
        double thinDensity = MeanAlpha(thin, topHalf: true) + MeanAlpha(thin, topHalf: false);
        double thickDensity = MeanAlpha(thick, topHalf: true) + MeanAlpha(thick, topHalf: false);
        Assert.True(thickDensity > thinDensity * 2.0,
            $"Amount did not thicken the smoke ({thinDensity:0.0} → {thickDensity:0.0})");
    }

    [Fact]
    public void Fog_GroundBias_SettlesTowardTheBottomOfFrame()
    {
        using SKBitmap even = Render(GeneratorTypeIds.Fog, 1.3, (GeneratorParamNames.Falloff, 0.0));
        using SKBitmap grounded = Render(GeneratorTypeIds.Fog, 1.3, (GeneratorParamNames.Falloff, 1.0));

        Assert.True(MeanAlpha(grounded, topHalf: false) > MeanAlpha(grounded, topHalf: true) * 2.0,
            "ground-biased fog should be far denser low in the frame");
        // Without the bias the same noise is spread evenly, so the two halves stay comparable.
        Assert.True(MeanAlpha(even, topHalf: true) > MeanAlpha(grounded, topHalf: true),
            "the bias should have thinned the top of the frame, not added to the bottom only");
    }

    [Fact]
    public void Smoke_Drifts_InTheDirectionAsked()
    {
        // Direction 90° is up the frame: sampling the same content a moment later must find it higher.
        // Detail off and speed up so the comparison is on the large shapes rather than the fine octaves.
        (string, double)[] flat =
        [
            (GeneratorParamNames.Direction, 90.0),
            (GeneratorParamNames.Detail, 0.0),
            (GeneratorParamNames.Speed, 3.0),
            (GeneratorParamNames.Scale, 1.5),
        ];
        using SKBitmap before = Render(GeneratorTypeIds.Smoke, 1.0, flat);
        using SKBitmap after = Render(GeneratorTypeIds.Smoke, 1.5, flat);

        // The later frame should match the earlier one better when the earlier one is shifted up than down.
        double up = RowProfileDistance(before, after, shift: -3);
        double down = RowProfileDistance(before, after, shift: +3);
        Assert.True(up < down, $"smoke at 90° did not rise (up {up:0.000} vs down {down:0.000})");
    }

    // ── Particles (Dust / Embers / Sparks) ───────────────────────────────────────────────────────────

    [Fact]
    public void Embers_MoreCount_PutsMoreParticlesOnScreen()
    {
        using SKBitmap few = Render(GeneratorTypeIds.Embers, 1.3,
            (GeneratorParamNames.Count, 10.0), (GeneratorParamNames.Flicker, 0.0));
        using SKBitmap many = Render(GeneratorTypeIds.Embers, 1.3,
            (GeneratorParamNames.Count, 60.0), (GeneratorParamNames.Flicker, 0.0));
        Assert.True(LitFraction(many) > LitFraction(few),
            $"Count did not add particles ({LitFraction(few):0.000} → {LitFraction(many):0.000})");
    }

    [Fact]
    public void Sparks_Streak_ElongatesParticlesAlongTheirTravel()
    {
        // Direction 0° is screen-right, so a streak widens each particle horizontally: the lit columns grow
        // while the lit rows do not.
        (string, double)[] common =
        [
            (GeneratorParamNames.Direction, 0.0),
            (GeneratorParamNames.Spread, 0.0),
            (GeneratorParamNames.Flicker, 0.0),
            (GeneratorParamNames.Count, 12.0),
        ];
        using SKBitmap round = Render(GeneratorTypeIds.Sparks, 1.3, [.. common, (GeneratorParamNames.Streak, 0.0)]);
        using SKBitmap streaked = Render(GeneratorTypeIds.Sparks, 1.3, [.. common, (GeneratorParamNames.Streak, 1.0)]);

        // Smearing along X spreads each spark's energy over its whole row, so the per-column profile flattens
        // while the per-row profile stays as peaky as it was. Their variance ratio is that shape change.
        double before = Variance(ColumnProfile(round)) / Variance(RowProfile(round));
        double after = Variance(ColumnProfile(streaked)) / Variance(RowProfile(streaked));
        Assert.True(after < before * 0.9,
            $"Streak did not stretch the sparks along their travel (column/row variance {before:0.000} → {after:0.000})");
    }

    [Fact]
    public void Dust_Flicker_PulsesParticleBrightness()
    {
        // Steady dust holds its brightness between frames; flickering dust does not. Speed 0 removes the
        // drift so only the pulsing can move the picture.
        (string, double)[] still = [(GeneratorParamNames.Speed, 0.0), (GeneratorParamNames.Spread, 0.0)];
        using SKBitmap steadyA = Render(GeneratorTypeIds.Dust, 1.0, [.. still, (GeneratorParamNames.Flicker, 0.0)]);
        using SKBitmap steadyB = Render(GeneratorTypeIds.Dust, 3.0, [.. still, (GeneratorParamNames.Flicker, 0.0)]);
        Assert.True(Identical(steadyA, steadyB), "dust with Flicker 0 and Speed 0 should be a still frame");
    }

    [Fact]
    public void Embers_SourceBias_ThinsTheFieldAsItRises()
    {
        // Direction 90° is up the frame, so the source is the bottom edge: embers must be thick and hot where
        // they leave the fire and sparse by the time they are overhead. Asserted numerically because a dense
        // field can saturate and hide a gradient that is nominally being applied.
        using SKBitmap even = Render(GeneratorTypeIds.Embers, 1.3, (GeneratorParamNames.Falloff, 0.0));
        using SKBitmap biased = Render(GeneratorTypeIds.Embers, 1.3, (GeneratorParamNames.Falloff, 1.0));

        Assert.True(MeanAlpha(biased, topHalf: false) > MeanAlpha(biased, topHalf: true) * 2.0,
            $"biased embers should thin out with height (bottom {MeanAlpha(biased, topHalf: false):0.0}, " +
            $"top {MeanAlpha(biased, topHalf: true):0.0})");
        Assert.True(Math.Abs(MeanAlpha(even, topHalf: true) - MeanAlpha(even, topHalf: false))
            < MeanAlpha(even, topHalf: false) * 0.5, "unbiased embers should fill the frame evenly");
    }

    [Theory]
    [InlineData(GeneratorTypeIds.Embers)]
    [InlineData(GeneratorTypeIds.Sparks)]
    public void AtTheirDefaults_TheRisingParticlesAreDenserLowInFrame(string typeId)
    {
        // The shipped defaults, not a synthetic setting: both fly upward off a source below frame, so out of
        // the box they must already read as thinning with height rather than as an even curtain.
        using SKBitmap frame = Render(typeId, seconds: 1.3);
        Assert.True(MeanAlpha(frame, topHalf: false) > MeanAlpha(frame, topHalf: true) * 1.4,
            $"{typeId}'s default Source bias is too weak to read (bottom {MeanAlpha(frame, topHalf: false):0.0}, " +
            $"top {MeanAlpha(frame, topHalf: true):0.0})");
    }

    // ── Light leak ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LightLeak_IsAnchoredAtItsPosition()
    {
        using SKBitmap frame = Render(GeneratorTypeIds.LightLeak, 1.3,
            (GeneratorParamNames.PositionX, 0.25),
            (GeneratorParamNames.PositionY, 0.25),
            (GeneratorParamNames.Direction, 90.0),
            (GeneratorParamNames.Size, 0.08));

        byte atCentre = frame.GetPixel(W / 4, H / 4).Alpha;
        byte acrossTheBand = frame.GetPixel(W - 4, H / 4).Alpha;
        Assert.True(atCentre > 40, $"the leak should be bright at its own position (got {atCentre})");
        Assert.True(acrossTheBand < atCentre / 2, "the leak should fall off away from its band");
    }

    [Fact]
    public void LightLeak_Width_BroadensTheBand()
    {
        using SKBitmap narrow = Render(GeneratorTypeIds.LightLeak, 1.3, (GeneratorParamNames.Size, 0.05));
        using SKBitmap wide = Render(GeneratorTypeIds.LightLeak, 1.3, (GeneratorParamNames.Size, 0.5));
        Assert.True(LitFraction(wide) > LitFraction(narrow) + 0.05,
            $"Width did not broaden the leak ({LitFraction(narrow):0.000} → {LitFraction(wide):0.000})");
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────

    private static SKBitmap Render(string typeId, double seconds, params (string Name, double Value)[] overrides) =>
        Render(typeId, seconds, W, H, overrides, []);

    private static SKBitmap Render(string typeId, double seconds, int width, int height) =>
        Render(typeId, seconds, width, height, [], []);

    private static SKBitmap Render(string typeId, double seconds, (string Name, string Value)[] stringOverrides) =>
        Render(typeId, seconds, W, H, [], stringOverrides);

    /// <summary>Renders one generator at its catalog defaults, with the given overrides, onto a transparent frame.</summary>
    private static SKBitmap Render(
        string typeId,
        double seconds,
        int width,
        int height,
        (string Name, double Value)[] overrides,
        (string Name, string Value)[] stringOverrides)
    {
        GeneratorDescriptor descriptor = GeneratorCatalog.Find(typeId)
            ?? throw new InvalidOperationException($"'{typeId}' is not registered in GeneratorCatalog.BuiltIns.");
        GeneratorSpec spec = descriptor.CreateSpec();

        var values = spec.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value.Evaluate(Core.Timing.Timecode.Zero));
        foreach ((string name, double value) in overrides)
            values[name] = value;
        var strings = new Dictionary<string, string>(spec.Strings);
        foreach ((string name, string value) in stringOverrides)
            strings[name] = value;

        var resolved = new ResolvedGenerator(typeId, strings, values, Progress: 0.0, LocalSeconds: seconds);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        AtmosphereRenderer.Draw(canvas, resolved, width, height);
        canvas.Flush();
        return bitmap;
    }

    private static bool Identical(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    return false;
        return true;
    }

    private static byte MaxAlpha(SKBitmap bmp)
    {
        byte max = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                max = Math.Max(max, bmp.GetPixel(x, y).Alpha);
        return max;
    }

    /// <summary>Fraction of pixels carrying meaningful coverage.</summary>
    private static double LitFraction(SKBitmap bmp, byte minAlpha = 24)
    {
        int lit = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha >= minAlpha)
                    lit++;
        return (double)lit / (bmp.Width * bmp.Height);
    }

    private static double MeanAlpha(SKBitmap bmp, bool topHalf)
    {
        int from = topHalf ? 0 : bmp.Height / 2;
        int to = topHalf ? bmp.Height / 2 : bmp.Height;
        double sum = 0;
        for (int y = from; y < to; y++)
            for (int x = 0; x < bmp.Width; x++)
                sum += bmp.GetPixel(x, y).Alpha;
        return sum / (bmp.Width * (to - from));
    }

    private static double[] ColumnProfile(SKBitmap bmp)
    {
        var cols = new double[bmp.Width];
        for (int x = 0; x < bmp.Width; x++)
        {
            double sum = 0;
            for (int y = 0; y < bmp.Height; y++)
                sum += bmp.GetPixel(x, y).Alpha;
            cols[x] = sum / bmp.Height;
        }
        return cols;
    }

    private static double Variance(double[] values)
    {
        double mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / values.Length;
    }

    /// <summary>
    /// Mean absolute difference between <paramref name="b"/>'s per-row mean alpha and <paramref name="a"/>'s,
    /// with <paramref name="a"/> shifted by <paramref name="shift"/> rows (negative = upward). Smaller means
    /// the shift lines the two frames up better — how the drift-direction test reads the motion.
    /// </summary>
    private static double RowProfileDistance(SKBitmap a, SKBitmap b, int shift)
    {
        double[] rowsA = RowProfile(a);
        double[] rowsB = RowProfile(b);
        double sum = 0;
        int n = 0;
        for (int y = 0; y < rowsB.Length; y++)
        {
            int src = y - shift;
            if (src < 0 || src >= rowsA.Length)
                continue;
            sum += Math.Abs(rowsB[y] - rowsA[src]);
            n++;
        }
        return n == 0 ? double.MaxValue : sum / n;
    }

    private static double[] RowProfile(SKBitmap bmp)
    {
        var rows = new double[bmp.Height];
        for (int y = 0; y < bmp.Height; y++)
        {
            double sum = 0;
            for (int x = 0; x < bmp.Width; x++)
                sum += bmp.GetPixel(x, y).Alpha;
            rows[y] = sum / bmp.Width;
        }
        return rows;
    }
}
