using Sprocket.Core.Model;
using Xunit;

namespace Sprocket.Core.Tests;

/// <summary>
/// Core-side tests for the math-based (non-DJI) input color transform profiles of PLAN.md step 52:
/// <see cref="ColorProfileCurves"/>'s decode curves and gamut matrices, and <see cref="ColorProfiles"/>'s
/// classification/detection of the new profiles. Each profile's decode is checked three ways since there is
/// no camera footage to validate against in CI: it must be continuous and monotonic across its whole range
/// (catches a wrong sign or a boundary/threshold slip), and it must round-trip against an <em>independently
/// transcribed</em> encode formula written fresh in this file from the same vendor specification (catches a
/// copy/transcription error in the production decode that wouldn't be repeated writing the test).
/// </summary>
public class ColorTransformCurveTests
{
    private static readonly string[] CurveProfiles =
    [
        ColorProfiles.ArriLogC3, ColorProfiles.ArriLogC4, ColorProfiles.SonySLog3, ColorProfiles.PanasonicVLog,
        ColorProfiles.CanonCLog3, ColorProfiles.BlackmagicFilm, ColorProfiles.FujifilmFLog2, ColorProfiles.NikonNLog,
    ];

    // ── Classification ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ColorProfiles.DjiDLog, ColorProfileKind.Lut)]
    [InlineData(ColorProfiles.DjiDLogM, ColorProfileKind.Lut)]
    [InlineData(ColorProfiles.ArriLogC3, ColorProfileKind.Curve)]
    [InlineData(ColorProfiles.ArriLogC4, ColorProfileKind.Curve)]
    [InlineData(ColorProfiles.SonySLog3, ColorProfileKind.Curve)]
    [InlineData(ColorProfiles.PanasonicVLog, ColorProfileKind.Curve)]
    [InlineData(ColorProfiles.CanonCLog3, ColorProfileKind.Curve)]
    [InlineData(ColorProfiles.BlackmagicFilm, ColorProfileKind.Curve)]
    [InlineData(ColorProfiles.FujifilmFLog2, ColorProfileKind.Curve)]
    [InlineData(ColorProfiles.NikonNLog, ColorProfileKind.Curve)]
    public void KindOf_Classifies_Lut_Vs_Curve_Profiles(string profile, ColorProfileKind expected) =>
        Assert.Equal(expected, ColorProfiles.KindOf(profile));

    [Fact]
    public void All_And_DisplayNames_Grew_By_Eight_And_Stayed_Parallel()
    {
        Assert.Equal(10, ColorProfiles.All.Count);
        Assert.Equal(ColorProfiles.All.Count, ColorProfiles.DisplayNames.Count);
        Assert.Equal(ColorProfiles.DjiDLog, ColorProfiles.All[0]); // append-only: DJI keeps its persisted indices
        Assert.Equal(ColorProfiles.DjiDLogM, ColorProfiles.All[1]);
    }

    [Fact]
    public void CurveUniforms_Is_Null_For_Lut_Profiles_And_Present_For_Every_Curve_Profile()
    {
        Assert.Null(ColorProfileCurves.CurveUniforms(ColorProfiles.DjiDLog));
        Assert.Null(ColorProfileCurves.CurveUniforms(ColorProfiles.DjiDLogM));
        foreach (string profile in CurveProfiles)
        {
            LogCurveUniforms? u = ColorProfileCurves.CurveUniforms(profile);
            Assert.NotNull(u);
            Assert.InRange(u!.Value.Kind, 0, 5);
        }
    }

    // ── Detection ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Shot in LogC4", ColorProfiles.ArriLogC4)]
    [InlineData("logc4", ColorProfiles.ArriLogC4)]
    [InlineData("Log C3", ColorProfiles.ArriLogC3)]
    [InlineData("S-Log3", ColorProfiles.SonySLog3)]
    [InlineData("slog3", ColorProfiles.SonySLog3)]
    [InlineData("V-Log", ColorProfiles.PanasonicVLog)]
    [InlineData("C-Log3", ColorProfiles.CanonCLog3)]
    [InlineData("F-Log2", ColorProfiles.FujifilmFLog2)]
    [InlineData("N-Log", ColorProfiles.NikonNLog)]
    [InlineData("Blackmagic Film", ColorProfiles.BlackmagicFilm)]
    [InlineData("BMD Film", ColorProfiles.BlackmagicFilm)]
    [InlineData("D-Log M", ColorProfiles.DjiDLogM)] // the DJI heuristic still runs first
    [InlineData("rec709", "")]
    [InlineData("", "")]
    public void DetectLogProfile_Recognises_Every_Vendor_Spelling(string tagValue, string expected)
    {
        var metadata = new Dictionary<string, string> { ["comment"] = tagValue };
        Assert.Equal(expected, ColorProfiles.DetectLogProfile(metadata));
    }

    [Fact]
    public void DetectLogProfile_Prefers_Dji_Detection_When_Both_Would_Match()
    {
        // A pathological tag naming both a DJI and a non-DJI profile — DetectDjiLog is tried first, matching
        // the existing precedence for "the most specific signal wins" within DJI's own pair.
        var metadata = new Dictionary<string, string> { ["comment"] = "D-Log and also S-Log3 notes" };
        Assert.Equal(ColorProfiles.DjiDLog, ColorProfiles.DetectLogProfile(metadata));
    }

    // ── Gamut matrices preserve white (every row of a valid primaries-conversion matrix sums to 1) ────

    [Theory]
    [MemberData(nameof(CurveProfileData))]
    public void Gamut_Matrix_Preserves_A_Neutral_Gray(string profile)
    {
        // Tolerance is 1e-3, not tighter: some vendors (e.g. Blackmagic) publish their own D65 white point to
        // a slightly different rounding than the bare 0.3127/0.3290 used to derive the Rec.709 side of the
        // matrix, so exact (1.0-row-sum) white preservation isn't quite achieved — a real transcription bug
        // (wrong primary, swapped row) is at least an order of magnitude larger than that rounding noise.
        GamutMatrix m = ColorProfileCurves.GamutOf(profile);
        (double r, double g, double b) = m.Apply(0.5, 0.5, 0.5);
        Assert.True(Math.Abs(r - 0.5) < 1e-3, $"{profile}: R {r}");
        Assert.True(Math.Abs(g - 0.5) < 1e-3, $"{profile}: G {g}");
        Assert.True(Math.Abs(b - 0.5) < 1e-3, $"{profile}: B {b}");
    }

    // ── Continuity + monotonicity across the whole normalized [0,1] domain ─────────────────────────────

    [Theory]
    [MemberData(nameof(CurveProfileData))]
    public void Decode_Is_Monotonic_Across_The_Encoded_Range(string profile)
    {
        double previous = double.NegativeInfinity;
        for (double v = 0.0; v <= 1.0; v += 0.001)
        {
            double l = ColorProfileCurves.Decode(profile, v);
            Assert.True(l >= previous - 1e-9, $"{profile}: decode must not decrease (at v={v}, got {l} after {previous})");
            previous = l;
        }
    }

    // A transcription slip at a piecewise boundary (wrong constant on only one side) shows up as a jump right
    // at that boundary. These curves are log-shaped — legitimately very steep away from the boundary near the
    // top of their range (that's the point of a log curve: compress a huge highlight range into a small code
    // range) — so a global "no large step anywhere" bound would either miss a real bug or reject a correct,
    // steep curve. Testing continuity exactly at each profile's own published threshold(s) targets the actual
    // risk without either failure mode.
    public static TheoryData<string, double> PiecewiseThresholds() => new()
    {
        { ColorProfiles.ArriLogC3, 5.367655 * 0.010591 + 0.092809 },
        { ColorProfiles.PanasonicVLog, 0.181 },
        { ColorProfiles.FujifilmFLog2, 0.100686685370811 },
        { ColorProfiles.ArriLogC4, 0.0 },
        { ColorProfiles.SonySLog3, 171.2102946929 / 1023.0 },
        { ColorProfiles.CanonCLog3, 0.097465473 },
        { ColorProfiles.CanonCLog3, 0.15277891 },
        { ColorProfiles.BlackmagicFilm, 8.283605932402494 * 0.005 + 0.09246575342465753 },
        { ColorProfiles.NikonNLog, 452.0 / 1023.0 },
    };

    [Theory]
    [MemberData(nameof(PiecewiseThresholds))]
    public void Decode_Is_Continuous_At_Its_Piecewise_Threshold(string profile, double threshold)
    {
        const double eps = 1e-6;
        double below = ColorProfileCurves.Decode(profile, threshold - eps);
        double above = ColorProfileCurves.Decode(profile, threshold + eps);
        Assert.True(Math.Abs(above - below) < 1e-3,
            $"{profile}: decode should be continuous at its published threshold {threshold} (got {below} vs {above})");
    }

    public static TheoryData<string> CurveProfileData() => new(CurveProfiles);

    // ── Round-trip against an independently transcribed encode (same vendor spec, written fresh here) ──

    [Fact]
    public void ArriLogC3_Round_Trips()
    {
        AssertRoundTrip(ColorProfiles.ArriLogC3, L =>
        {
            const double cut = 0.010591, a = 5.555556, b = 0.052272, c = 0.247190, d = 0.385537, e = 5.367655, f = 0.092809;
            return L > cut ? c * Math.Log10(a * L + b) + d : e * L + f;
        }, 0.001, 0.18, 1.0, 4.0);
    }

    [Fact]
    public void PanasonicVLog_Round_Trips()
    {
        AssertRoundTrip(ColorProfiles.PanasonicVLog, L =>
        {
            const double cut1 = 0.01, b = 0.00873, c = 0.241514, d = 0.598206;
            return L < cut1 ? 5.6 * L + 0.125 : c * Math.Log10(L + b) + d;
        }, 0.001, 0.18, 1.0, 4.0);
    }

    [Fact]
    public void FujifilmFLog2_Round_Trips()
    {
        AssertRoundTrip(ColorProfiles.FujifilmFLog2, L =>
        {
            const double cut1 = 0.000889, a = 5.555556, b = 0.064829, c = 0.245281, d = 0.384316, e = 8.799461, f = 0.092864;
            return L < cut1 ? e * L + f : c * Math.Log10(a * L + b) + d;
        }, 0.0001, 0.18, 1.0, 4.0);
    }

    [Fact]
    public void ArriLogC4_Round_Trips()
    {
        double a = (Math.Pow(2, 18) - 16) / 117.45;
        double b = (1023.0 - 95.0) / 1023.0;
        double c = 95.0 / 1023.0;
        double s = 7.0 * Math.Log(2.0) * Math.Pow(2.0, 7.0 - 14.0 * c / b) / (a * b);
        double t = (Math.Pow(2.0, -14.0 * c / b + 6.0) - 64.0) / a;

        AssertRoundTrip(ColorProfiles.ArriLogC4, L =>
            L < t ? (L - t) / s : b * (Math.Log2(a * L + 64.0) - 6.0) / 14.0 + c,
            0.00001, 0.18, 1.0, 4.0);
    }

    [Fact]
    public void SonySLog3_Round_Trips()
    {
        AssertRoundTrip(ColorProfiles.SonySLog3, L =>
        {
            const double p = 171.2102946929;
            return L < 0.01125
                ? (L * (p - 95.0) / 0.01125 + 95.0) / 1023.0
                : (420.0 + Math.Log10((L + 0.01) / (0.18 + 0.01)) * 261.5) / 1023.0;
        }, 0.001, 0.18, 1.0, 4.0);
    }

    [Fact]
    public void CanonCLog3_Round_Trips()
    {
        AssertRoundTrip(ColorProfiles.CanonCLog3, L =>
        {
            const double a = 0.36726845, k = 14.98325, gain = 0.9;
            if (L < -0.0126) return -a * Math.Log10(1.0 - k * L / gain) + 0.12783901;
            if (L <= 0.0126) return L * 1.9754798 / gain + 0.12512219;
            return a * Math.Log10(1.0 + k * L / gain) + 0.12240537;
        }, -0.005, 0.005, 0.18, 1.0, 4.0);
    }

    [Fact]
    public void BlackmagicFilm_Round_Trips()
    {
        AssertRoundTrip(ColorProfiles.BlackmagicFilm, L =>
        {
            const double a = 0.08692876065491224, b = 0.005494072432257808, c = 0.5300133392291939;
            const double d = 8.283605932402494, e = 0.09246575342465753, linCut = 0.005;
            return L < linCut ? d * L + e : a * Math.Log(L + b) + c;
        }, 0.001, 0.18, 1.0, 4.0);
    }

    [Fact]
    public void NikonNLog_Round_Trips_And_Matches_The_Spec_18_Percent_Reference()
    {
        // Nikon's own spec states "y = 0.18 is equivalent to Stop 0" — a real external anchor, not just
        // self-consistency: encode 0.18 via their reflectance->code-value formula and decode it back.
        double x18 = EncodeNLog(0.18);
        double v18 = x18 / 1023.0;
        Assert.Equal(0.18, ColorProfileCurves.Decode(ColorProfiles.NikonNLog, v18), precision: 6);

        AssertRoundTrip(ColorProfiles.NikonNLog, y => EncodeNLog(y) / 1023.0, 0.01, 0.18, 1.0, 4.0);

        static double EncodeNLog(double y) => y < 0.328
            ? 650.0 * Math.Pow(y + 0.0075, 1.0 / 3.0)
            : 150.0 * Math.Log(y) + 619.0;
    }

    private static void AssertRoundTrip(string profile, Func<double, double> encode, params double[] linearValues)
    {
        foreach (double linear in linearValues)
        {
            double v = encode(linear);
            double decoded = ColorProfileCurves.Decode(profile, v);
            Assert.True(Math.Abs(decoded - linear) < 1e-6,
                $"{profile}: round trip at L={linear} (V={v}) decoded to {decoded}");
        }
    }
}
