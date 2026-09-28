using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Toy Cassette Camera (plan/features/toy-cassette-camera.md, phase 3): the picture side of the late-1980s toy
/// camcorder that recorded black-and-white video onto an audio cassette, as one ordered SkSL stage — cheaper
/// than chaining Mosaic + Black &amp; White + noise, because every tap of a chained stage re-evaluates the
/// stages below it (§7). A registry effect, so preview and export run the same program (§5).
/// </summary>
/// <remarks>
/// <para><b>Stage order.</b> (1) <em>Pixel grid</em>: a Horizontal × Vertical Pixels grid; each picture pixel is
/// the average of up to 2×2 taps. (2) <em>Monochrome + low-range tone</em>: a sensor-response luma of the
/// (display-encoded) average → contrast about mid-grey → Black Crush remaps the bottom of the range to solid
/// black → a soft highlight shoulder. Deliberately our own small curve — anyone wanting Black &amp; White's channel mixer can stack
/// <c>builtin.blackwhite</c> ahead of this. The luma weights lean red (0.30 / 0.60 / 0.10 rather than Rec.709's
/// 0.21 / 0.72 / 0.07): the camera's CCD had no infrared-cut filter, so reds and skin read lighter than a
/// standard conversion — and a red subject does not collapse to black under the crush. (3) <em>Highlight
/// bloom</em>: tone above a 0.65 knee glows into the pixel itself and its left/right neighbours (the sensor bleeds
/// highlights along its scan lines). (4) <em>Spatial smear</em>: pixels brighter than Smear Threshold leave a
/// trail of up to eight picture pixels to their right, fading linearly and combined with the <c>max</c> operator
/// so only bright things trail and a trail never darkens — the phase-1–5 stand-in for the true temporal lag that
/// <c>builtin.echo</c> brings in phase 6. (5) <em>Tape noise</em>, per picture-pixel row and hashed on (row,
/// <c>sprocket_time</c>, Seed): a noise line replaces a random run of the row with streaky grey/white snow that
/// fades in and out; a dropout — rarer — is a solid white streak, brightest at its head and fading along its
/// tail. (6) <em>Grain</em>: per-frame shimmer, correlated along each row (short horizontal runs plus a little
/// row-to-row flicker) like analog video noise, not independent per-pixel "digital" speckle. (7) <em>4:3
/// border</em>: everything outside the picture window is black; Border Softness feathers and slightly rounds the
/// window's corners, like a CRT's bezel.</para>
/// <para>Stages 3–6 run per picture pixel, before the Pixel Softness blend across neighbouring picture pixels, so
/// grain, lines and trails are as soft as the pixels themselves. The blend is full-width horizontally but half as
/// wide vertically — soft along each scan line (the recorded signal was band-limited), firmer between lines.
/// Every hash keys on <c>sprocket_time</c> — which the planner quantizes when the clip carries Posterize Time —
/// so the noise steps at the posterized rate, and the same frame time always yields the same pixels (preview
/// matches export).</para>
/// <para><b>Framing.</b> The picture is the largest centred 4:3 area of the reserved <c>sprocket_bounds</c> layer
/// rect, <em>scaled down</em> into a window shrunk by Border Size (a fraction of that area) — so the border never
/// crops the shot further, it frames the whole 4:3 picture the way the camera's image sat inside a TV's black
/// surround. The grid spans the picture, so picture pixels are square and follow the layer's transform and
/// crop.</para>
/// <para><b>Tap budget.</b> A picture-pixel value costs n×n upstream taps (n = 2, or 1 when a picture pixel is
/// under two source pixels). Each evaluated row needs its two target pixels, one bloom neighbour either side,
/// and <c>round(8 × Smear Length)</c> trailing pixels — at most 11 picture pixels. A pixel reads one row, or two
/// inside a vertical Pixel Softness blend zone. <b>Worst case: 2 rows × 11 picture pixels × 4 taps = 88 upstream
/// evaluations per output pixel</b> (Smear Length 100%, inside a blend zone); at the defaults (3 smear taps →
/// 6 picture pixels per row) it is 24 per row, 48 at most. Smear Length 0 drops a row to 4 picture pixels
/// (16 taps). Border pixels cost a single tap (for the layer's alpha).</para>
/// <para>Output is premultiplied grey (r = g = b = grey × alpha) with <c>rgb ≤ a</c>; alpha is the averaged
/// alpha of the picture pixels, so a layer's silhouette pixelates with its content.</para>
/// </remarks>
public sealed class ToyCamEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float horizontalPixels; // picture pixels across the 4:3 picture, whole number >= 1
uniform float verticalPixels;   // picture pixels down the 4:3 picture, whole number >= 1
uniform float pixelSoftness;    // [0, 1] width of the blend between neighbouring picture pixels
uniform float contrast;         // around mid-grey, 1 = unchanged
uniform float blackCrush;       // [0, 0.6] share of the tone range crushed to black
uniform float highlightBloom;   // [0, 1]
uniform float smearTaps;        // whole number [0, 8] of trailing picture pixels
uniform float smearThreshold;   // [0, 1] tone above which a picture pixel trails
uniform float noiseLines;       // [0, 1]
uniform float dropouts;         // [0, 1]
uniform float grainAmount;      // [0, 1]
uniform float borderSize;       // [0, 0.5] how far the picture window shrinks inside the largest 4:3 fit
uniform float borderSoftness;   // [0, 1] feather + corner rounding of the picture window
uniform float seed;             // whole number [0, 999]
uniform float sprocket_time;    // reserved: frame time in seconds (noise seed) — auto-bound by the pipeline
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline
" + SkslSnippets.CellHash + SkslSnippets.BlockGrid + @"
const float BLOOM_KNEE = 0.65;
const float SHOULDER_KNEE = 0.75;

// Sensor response: a CCD without an infrared-cut filter sees reds lighter than Rec.709 luma does.
const float3 SENSOR_LUMA = float3(0.30, 0.60, 0.10);

// The largest 4:3 rect centred in the layer rect — the area the camera 'sees'.
float4 pictureFit() {
    float2 size = sprocket_bounds.zw;
    float2 fit = size.x * 3.0 > size.y * 4.0 ? float2(size.y * 4.0 / 3.0, size.y) : float2(size.x, size.x * 0.75);
    return float4(sprocket_bounds.xy + (size - fit) * 0.5, fit);
}

// The window the picture is shown in: the fit shrunk about its centre by borderSize.
float4 pictureWindow(float4 fit) {
    float2 size = fit.zw * (1.0 - borderSize);
    return float4(fit.xy + (fit.zw - size) * 0.5, size);
}

// 1-D value noise along a row: smooth between hashed lattice points, so noise forms short horizontal runs.
float rowNoise(float x, float row, float key) {
    float i = floor(x);
    float t = fract(x);
    t = t * t * (3.0 - 2.0 * t);
    return mix(cellHash(float2(i, row + key)), cellHash(float2(i + 1.0, row + key)), t);
}

// Stages 1–2: one picture pixel's tone in [0, 1] (and its alpha) — the premultiplied mean of up to 2×2 taps
// over the picture area, unpremultiplied, to sensor luma, then contrast about mid-grey and the black crush.
float pixelTone(float2 cell, float4 fit, float2 blocks, out float alpha) {
    cell = gridClampCell(cell, blocks);
    float2 n = clamp(floor(gridBlockSize(fit, blocks)), float2(1.0), float2(2.0));
    float4 sum = float4(0.0);
    for (int j = 0; j < 2; j++) {
        for (int i = 0; i < 2; i++) {
            if (float(i) < n.x && float(j) < n.y) {
                float2 f = (float2(float(i), float(j)) + 0.5) / n;
                sum += float4(src.eval(gridCellPoint(cell, f, fit, blocks)));
            }
        }
    }
    sum /= n.x * n.y;
    alpha = sum.a;
    if (sum.a <= 0.0) {
        return 0.0;
    }
    float y = dot(clamp(sum.rgb / sum.a, 0.0, 1.0), SENSOR_LUMA);
    y = (y - 0.5) * contrast + 0.5;
    y = (y - blackCrush) / max(1.0 - blackCrush, 1e-3);
    // Soft shoulder above SHOULDER_KNEE: a rational roll-off (slope 1 at the knee) keeps detail in bright
    // subjects — white lands near 0.92 — leaving the last stretch to the bloom.
    if (y > SHOULDER_KNEE) {
        float e = (y - SHOULDER_KNEE) / (1.0 - SHOULDER_KNEE);
        y = SHOULDER_KNEE + (1.0 - SHOULDER_KNEE) * e / (1.0 + 0.5 * e);
    }
    return clamp(y, 0.0, 1.0);
}

float bloomExcess(float y) {
    return max(y - BLOOM_KNEE, 0.0) / (1.0 - BLOOM_KNEE);
}

// The smear key: a picture pixel's tone once it clears the threshold (a short smooth ramp, so a pixel hovering at
// the threshold does not pop), zero below it.
float smearKey(float y) {
    return y * smoothstep(smearThreshold - 0.04, smearThreshold + 0.04, y);
}

// Stages 3–4 for the horizontally adjacent picture pixels (cx, cy) and (cx + 1, cy): bloomed tone, max'd with the
// smear trail from the pixels to their left. v[i] holds picture pixel cx - 8 + i; only the ones actually needed
// (the smear run, plus one bloom neighbour either side) are evaluated.
float2 rowPair(float cx, float cy, float4 fit, float2 blocks, out float2 alpha) {
    float v[11];
    float a[11];
    for (int i = 0; i < 11; i++) {
        v[i] = 0.0;
        a[i] = 0.0;
        if (float(i) >= min(7.0, 8.0 - smearTaps)) {
            float al;
            v[i] = pixelTone(float2(cx - 8.0 + float(i), cy), fit, blocks, al);
            a[i] = al;
        }
    }
    float b0 = v[8] + highlightBloom * (0.5 * bloomExcess(v[8]) + 0.5 * (bloomExcess(v[7]) + bloomExcess(v[9])));
    float b1 = v[9] + highlightBloom * (0.5 * bloomExcess(v[9]) + 0.5 * (bloomExcess(v[8]) + bloomExcess(v[10])));
    float t0 = 0.0;
    float t1 = 0.0;
    for (int k = 1; k <= 8; k++) {
        if (float(k) <= smearTaps) {
            float fall = 1.0 - float(k) / (smearTaps + 1.0);
            t0 = max(t0, smearKey(v[8 - k]) * fall);
            t1 = max(t1, smearKey(v[9 - k]) * fall);
        }
    }
    alpha = float2(a[8], a[9]);
    return float2(max(clamp(b0, 0.0, 1.0), t0), max(clamp(b1, 0.0, 1.0), t1));
}

// Stages 5–6 for one picture pixel: tape noise lines and dropouts (per row, per frame), then grain.
float tape(float y, float2 cell, float2 blocks, float frameKey, float seedKey) {
    cell = gridClampCell(cell, blocks);
    float x = cell.x;
    // Noise line: a run of streaky snow that fades in and out over two picture pixels at each end.
    if (cellHash(float2(cell.y + 0.5, frameKey + seedKey)) < noiseLines * 0.15) {
        float start = cellHash(float2(cell.y + 7.3, frameKey + 1.7)) * blocks.x - 2.0;
        float end = start + (0.15 + 0.85 * cellHash(float2(cell.y + 3.1, frameKey + 4.2))) * blocks.x * 0.6;
        float envelope = smoothstep(start, start + 2.0, x) * (1.0 - smoothstep(end - 2.0, end, x));
        float snow = rowNoise(x / 2.5, cell.y * 1.37, frameKey + seedKey);
        y = mix(y, 0.3 + 0.7 * snow, envelope * 0.6);
    }
    // Dropout: a solid white streak, brightest at its head and fading along the tail.
    if (cellHash(float2(cell.y + 11.9, frameKey + seedKey + 9.4)) < dropouts * 0.015) {
        float start = cellHash(float2(cell.y + 5.7, frameKey + 2.9)) * blocks.x;
        float len = (0.05 + 0.35 * cellHash(float2(cell.y + 8.3, frameKey + 6.1))) * blocks.x;
        float t = (x - start) / len;
        float inRun = step(0.0, t) * step(t, 1.0);
        float tail = 1.0 - t;
        y = mix(y, 1.0, inRun * (0.15 + 0.85 * tail * tail));
    }
    // Grain: short horizontal runs within the row plus a little whole-row flicker.
    if (grainAmount > 0.0) {
        float g = 0.75 * (rowNoise(x / 1.7, cell.y * 1.13 + 5.3, frameKey * 1.3 + seedKey) - 0.5)
                + 0.25 * (cellHash(float2(cell.y + 2.2, frameKey + seedKey + 3.3)) - 0.5);
        y += g * grainAmount * 0.3;
    }
    return clamp(y, 0.0, 1.0);
}

half4 main(float2 coord) {
    if (sprocket_bounds.z <= 0.0 || sprocket_bounds.w <= 0.0) {
        return src.eval(coord);
    }

    // Stage 7 first as a mask, so border pixels skip the picture work entirely (one tap, for the layer alpha).
    // The window is a rounded rect (corner radius and feather both grow with borderSoftness).
    float4 fit = pictureFit();
    float4 win = pictureWindow(fit);
    float shortSide = min(win.z, win.w);
    float radius = borderSoftness * 0.08 * shortSide;
    float2 q = abs(coord - (win.xy + win.zw * 0.5)) - (win.zw * 0.5 - radius);
    float edge = -(length(max(q, float2(0.0))) + min(max(q.x, q.y), 0.0) - radius); // > 0 inside
    float feather = borderSoftness * 0.06 * shortSide;
    float mask = feather > 0.0 ? smoothstep(0.0, feather, edge) : step(0.0, edge);
    if (mask <= 0.0) {
        return half4(0.0, 0.0, 0.0, src.eval(coord).a);
    }

    float2 blocks = float2(horizontalPixels, verticalPixels);
    // Per-frame and per-seed keys, folded small before hashing so float precision holds on long timelines.
    float seedKey = seed * 0.0931;
    float frameKey = cellHash(float2(mod(sprocket_time, 211.0) * 1.37, seedKey + 0.61)) * 89.0;

    // Position relative to the picture-pixel centres (in the window), as in Mosaic: c0 is the pixel up-left, f how
    // far toward the next centre (the border between picture pixels sits at f = 0.5). Soft along the scan line,
    // half as soft between lines.
    float2 u = gridPosition(coord, win, blocks) - 0.5;
    float2 c0 = floor(u);
    float2 f = u - c0;
    float2 w;
    if (pixelSoftness > 0.0) {
        float2 soft = float2(pixelSoftness, 0.5 * pixelSoftness);
        w = smoothstep(0.5 - 0.5 * soft, 0.5 + 0.5 * soft, f);
    } else {
        w = step(float2(0.5), f);
    }

    float2 top = float2(0.0);
    float2 bottom = float2(0.0);
    float2 topAlpha = float2(0.0);
    float2 bottomAlpha = float2(0.0);
    if (w.y < 1.0) {
        top = rowPair(c0.x, c0.y, fit, blocks, topAlpha);
        top = float2(tape(top.x, c0, blocks, frameKey, seedKey),
                     tape(top.y, c0 + float2(1.0, 0.0), blocks, frameKey, seedKey));
    }
    if (w.y > 0.0) {
        bottom = rowPair(c0.x, c0.y + 1.0, fit, blocks, bottomAlpha);
        bottom = float2(tape(bottom.x, c0 + float2(0.0, 1.0), blocks, frameKey, seedKey),
                        tape(bottom.y, c0 + float2(1.0, 1.0), blocks, frameKey, seedKey));
    }
    float grey = mix(mix(top.x, top.y, w.x), mix(bottom.x, bottom.y, w.x), w.y);
    float alpha = clamp(mix(mix(topAlpha.x, topAlpha.y, w.x), mix(bottomAlpha.x, bottomAlpha.y, w.x), w.y), 0.0, 1.0);
    grey = clamp(grey, 0.0, 1.0) * mask;
    return half4(half3(grey * alpha), half(alpha));
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.ToyCam)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.ToyCam}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        // Integer parameters are rounded here as well as snapped in the Inspector (the Mosaic rule), so an eased
        // keyframe or an MCP value like 119.6 still yields a whole-pixel grid and a whole seed.
        uniforms.Set("horizontalPixels", (float)Math.Clamp(Math.Round(effect.Get(EffectParamNames.HorizontalPixels, 120.0)), 1.0, 640.0));
        uniforms.Set("verticalPixels", (float)Math.Clamp(Math.Round(effect.Get(EffectParamNames.VerticalPixels, 90.0)), 1.0, 480.0));
        uniforms.Set("pixelSoftness", (float)Math.Clamp(effect.Get(EffectParamNames.PixelSoftness, 0.15), 0.0, 1.0));
        uniforms.Set("contrast", (float)Math.Clamp(effect.Get(EffectParamNames.Contrast, 1.15), 0.0, 2.0));
        uniforms.Set("blackCrush", (float)Math.Clamp(effect.Get(EffectParamNames.BlackCrush, 0.08), 0.0, 0.6));
        uniforms.Set("highlightBloom", (float)Math.Clamp(effect.Get(EffectParamNames.HighlightBloom, 0.5), 0.0, 1.0));
        // Smear Length 0–100% maps to 0–8 trailing picture pixels — the tap cap.
        uniforms.Set("smearTaps", (float)Math.Round(Math.Clamp(effect.Get(EffectParamNames.SmearLength, 0.35), 0.0, 1.0) * 8.0));
        uniforms.Set("smearThreshold", (float)Math.Clamp(effect.Get(EffectParamNames.SmearThreshold, 0.78), 0.0, 1.0));
        uniforms.Set("noiseLines", (float)Math.Clamp(effect.Get(EffectParamNames.NoiseLines, 0.2), 0.0, 1.0));
        uniforms.Set("dropouts", (float)Math.Clamp(effect.Get(EffectParamNames.Dropouts, 0.15), 0.0, 1.0));
        uniforms.Set("grainAmount", (float)Math.Clamp(effect.Get(EffectParamNames.GrainAmount, 0.3), 0.0, 1.0));
        uniforms.Set("borderSize", (float)Math.Clamp(effect.Get(EffectParamNames.BorderSize, 0.25), 0.0, 0.5));
        uniforms.Set("borderSoftness", (float)Math.Clamp(effect.Get(EffectParamNames.BorderSoftness, 0.2), 0.0, 1.0));
        uniforms.Set("seed", (float)Math.Clamp(Math.Round(effect.Get(EffectParamNames.Seed, 0.0)), 0.0, 999.0));
        // sprocket_time / sprocket_bounds are reserved uniforms auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
