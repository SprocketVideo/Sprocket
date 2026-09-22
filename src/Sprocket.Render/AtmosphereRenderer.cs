using SkiaSharp;
using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render;

/// <summary>
/// The atmospheric generators (plan/features/special-effects.md, phase 2): Smoke, Fog, Dust, Embers, Sparks
/// and Light Leak, drawn procedurally into a generator clip's transparent frame. Three SkSL programs serve
/// the six generators — fractal noise clouds (Smoke / Fog), a hashed particle field (Dust / Embers / Sparks),
/// and an angled light band — because within each family only the defaults differ, exactly the way the
/// title family shares <see cref="TitleRenderer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Everything the shaders read comes from the <see cref="ResolvedGenerator"/>: the evaluated (and therefore
/// keyframeable) parameters and <see cref="ResolvedGenerator.LocalSeconds"/>, the clip's local clock. There
/// is no hidden particle state, no texture and no RNG — positions and brightnesses are hashed from a cell
/// index — so a frame is a pure function of (project, time), which is what lets preview and export match and
/// lets a scrub land on the identical picture (ARCHITECTURE.md §5).
/// </para>
/// <para>
/// Spatial quantities are fractions of the <em>frame height</em> and rates are per second, so a generator
/// looks the same at preview and export resolution and its motion does not change when the clip is trimmed.
/// Output is premultiplied over a transparent frame, so a generator composites through the ordinary layer
/// path with the track's opacity and blend mode.
/// </para>
/// <para>
/// The programs are compiled once at type init and are immutable; only the small per-draw uniform/shader
/// objects are allocated, and no pixel buffer ever reaches the managed heap (ARCHITECTURE.md §1).
/// </para>
/// </remarks>
public static class AtmosphereRenderer
{
    // Shared SkSL preamble: hashed value noise, the same construction the B&W grain and Heat Distortion use,
    // so nothing is uploaded and every GPU agrees on the result.
    private const string NoisePreamble = @"
float hash21(float2 p) {
    float3 q = fract(float3(p.xyx) * float3(443.897, 441.423, 437.195));
    q += dot(q, q.yzx + 19.19);
    return fract((q.x + q.y) * q.z);
}

float valueNoise(float2 p) {
    float2 i = floor(p);
    float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash21(i), hash21(i + float2(1.0, 0.0)), u.x),
               mix(hash21(i + float2(0.0, 1.0)), hash21(i + float2(1.0, 1.0)), u.x), u.y);
}
";

    // Clouds (Smoke / Fog) — domain-warped fractal value noise thresholded into coverage. The warp is what
    // turns plain fbm into billows rather than a static crumple; `detail` is the octave roll-off, `softness`
    // the width of the coverage threshold, and `falloff` a vertical bias that settles fog toward the ground.
    private const string CloudsSksl = @"
uniform float2 resolution;   // frame size in pixels
uniform float time;          // clip-local seconds
uniform float amount;        // coverage / opacity, 0-1
uniform float scale;         // cells across the frame height
uniform float speed;
uniform float2 flowDir;      // unit vector in screen space (y down)
uniform float detail;        // 0-1 octave roll-off
uniform float softness;      // 0-1 threshold width
uniform float falloff;       // 0-1 vertical density bias
uniform float seed;
uniform float4 tint;         // straight (unpremultiplied) RGBA
" + NoisePreamble + @"
float fbm(float2 p, float rough) {
    float sum = 0.0;
    float amp = 0.5;
    float norm = 0.0;
    for (int i = 0; i < 5; i++) {
        sum += amp * valueNoise(p);
        norm += amp;
        p *= 2.03;
        amp *= rough;
    }
    return sum / norm;
}

half4 main(float2 coord) {
    if (amount <= 0.0 || tint.a <= 0.0 || resolution.y <= 0.0) {
        return half4(0.0);
    }
    // Square units keyed to the frame height, so the look is aspect-correct and resolution-independent.
    float2 uv = coord / resolution.y;
    // Ground bias also *stratifies*: squashing the noise vertically and stretching it horizontally turns
    // billows into the full-width layers real ground fog forms. Without this a high falloff just pools the
    // same blobs in a corner, which reads as thin smoke rather than fog.
    float strat = clamp(falloff, 0.0, 1.0);
    float2 p = float2(uv.x * (1.0 - strat * 0.55), uv.y * (1.0 + strat * 2.5)) * scale
             + float2(seed * 17.13, seed * 9.71)
             - flowDir * (time * speed * 0.06 * scale);

    float rough = mix(0.25, 0.7, clamp(detail, 0.0, 1.0));
    // Warp the sample point by a coarser noise field that evolves on its own clock: the clouds churn in
    // place as well as drift, which is the difference between smoke and a moving wallpaper.
    float w = fbm(p * 0.7 + float2(0.0, time * speed * 0.05), 0.5) - 0.5;
    float d = fbm(p + float2(w, -w) * 1.1, rough);

    // Ground bias: at falloff 1 the density ramps from nothing at the top of frame to full at the bottom.
    float vy = clamp(coord.y / resolution.y, 0.0, 1.0);
    d *= mix(1.0, smoothstep(0.1, 1.0, vy), strat);

    // Amount does two things, the way a smoke overlay's opacity does in practice: it slides the coverage
    // threshold (more of the frame has smoke in it) *and* caps the opacity (the smoke is thinner). Without
    // the cap a mid setting blows out to a solid white sheet that obliterates the plate underneath.
    float e = mix(0.03, 0.40, clamp(softness, 0.0, 1.0));
    float mid = mix(0.78, 0.32, amount);
    float a = clamp(smoothstep(mid - e, mid + e, d) * amount * tint.a, 0.0, 1.0);
    return half4(half3(tint.rgb * a), half(a));
}";

    // Particles (Dust / Embers / Sparks) — one hashed particle per cell of a square lattice, sampled over the
    // 3x3 neighbourhood so a sprite straddling a cell edge still draws. The whole lattice is carried along the
    // flow direction by shifting the sample point, so nothing accumulates state and nothing pops; per-particle
    // life comes from the sinusoidal cross-flow wander and the brightness flicker, both hashed per cell.
    // Two layers - a near field and a smaller, faster far field - give the parallax a single flat field lacks.
    private const string ParticlesSksl = @"
uniform float2 resolution;
uniform float time;
uniform float amount;
uniform float count;         // cells across the frame height
uniform float radius;        // particle radius as a fraction of the frame height
uniform float speed;
uniform float2 flowDir;      // unit vector in screen space (y down)
uniform float spread;        // cross-flow wander and per-particle angle jitter, 0-1
uniform float flicker;       // 0-1
uniform float streak;        // 0-1 elongation behind the particle
uniform float falloff;       // 0-1 source bias: thins the field the further it has travelled
uniform float seed;
uniform float4 tint;
" + NoisePreamble + @"
// One layer of the lattice. `cells` scales the grid, `rate` the flow, `gain` the layer's contribution.
// Returns (density, hot-core weight): the core is what gives a particle a white-hot centre inside its
// tinted falloff, which is the difference between an ember and an orange dot.
float2 layerSample(float2 uv, float2 jitter, float cells, float sizeScale, float rate, float gain) {
    // `jitter` decorrelates one layer's lattice from the other's. It offsets the lattice, not the frame, so
    // the source bias below still reads the particle's true position on screen.
    float2 shift = flowDir * (time * speed * rate * cells);
    float2 q = uv * cells + jitter - shift;
    float2 base = floor(q);
    float r = max(radius * cells * sizeScale, 0.0008);

    float acc = 0.0;
    float core = 0.0;
    for (int dy = -1; dy <= 1; dy++) {
        for (int dx = -1; dx <= 1; dx++) {
            float2 c = base + float2(float(dx), float(dy));
            float h1 = hash21(c + seed * 3.7);
            float h2 = hash21(c + float2(31.7, 13.1) + seed * 3.7);
            float h3 = hash21(c + float2(71.3, 57.9) + seed * 3.7);
            float h4 = hash21(c + float2(13.9, 91.7) + seed * 3.7);
            float h5 = hash21(c + float2(5.1, 47.3) + seed * 3.7);

            // Particle at a hashed spot in its cell.
            float2 pos = c + float2(h1, h2);

            // Source bias, applied per particle rather than per pixel: how far this particle has travelled
            // across the frame along the flow, 0 at the edge it comes from. Biasing the *spawn* is what makes
            // the gradient survive - scaling the final alpha instead does nothing wherever the accumulated
            // field already saturates, which is most of a dense one.
            // Back out of the moving lattice into screen coordinates: the source edge is fixed to the
            // frame, and the particles flow through the gradient rather than carrying it along with them.
            float2 pn = ((pos + shift - jitter) / cells) * (resolution.y / resolution);
            float travelled = clamp(0.5 + dot(pn - 0.5, flowDir), 0.0, 1.0);
            float bias = mix(1.0, 1.0 - smoothstep(-0.1, 1.0, travelled), clamp(falloff, 0.0, 1.0));

            // Not every cell spawns. A particle per cell is what makes a lattice read as a lattice; leaving
            // a third of them empty is what turns an even dither into drifting motes that clump and gap.
            float alive = step(0.34 + (1.0 - bias) * 0.62, h4);

            // Each particle flies at its own angle off the nominal direction. Spread does this as well as the
            // sideways wander: one global velocity vector is what makes a particle field read as driven rain.
            float ang = (h2 - 0.5) * spread * 2.2;
            float ca = cos(ang);
            float sa = sin(ang);
            float2 dir = float2(flowDir.x * ca - flowDir.y * sa, flowDir.x * sa + flowDir.y * ca);
            float2 perp = float2(-dir.y, dir.x);

            // ...wandering across its own heading as it goes.
            pos += perp * (sin(time * speed * (0.6 + h1 * 1.8) + h2 * 6.2832) * spread * 0.45);

            // Radius varies widely per particle, so the field has big near motes and small far ones rather
            // than one stamp repeated. A separate hash from the spawn gate, or the survivors would all be
            // drawn from the same narrow slice.
            float pr = r * (0.3 + 2.4 * h5);
            float stretch = 1.0 + streak * 8.0 * (0.15 + 2.2 * h1);

            // The streak trails *behind* the particle only (a spark has a head and a tail, not a symmetric
            // smear), and the hot core is measured undistorted so the head stays a round, bright point.
            float2 d = q - pos;
            float alongD = dot(d, dir);
            float tail = mix(1.0, stretch, step(alongD, 0.0));
            float f = length(float2(alongD / tail, dot(d, perp))) / pr;
            float fcore = length(d) / pr;

            // Soft round falloff, the per-particle brightness pulse, and a wide spread of base brightness.
            float pulse = mix(1.0, 0.35 + 0.65 * (0.5 + 0.5 * sin(time * speed * (2.0 + h3 * 6.0) + h1 * 6.2832)),
                              clamp(flicker, 0.0, 1.0));
            float bright = (0.25 + 0.95 * h3) * pulse * alive * (0.35 + 0.65 * bias);
            acc += exp(-f * f * 2.5) * bright;
            core += exp(-fcore * fcore * 10.0) * bright;
        }
    }
    return float2(acc, core) * gain;
}

half4 main(float2 coord) {
    if (amount <= 0.0 || tint.a <= 0.0 || count <= 0.0 || resolution.y <= 0.0) {
        return half4(0.0);
    }
    float2 uv = coord / resolution.y;
    // The second layer is a genuine far field - smaller particles moving faster - rather than the same
    // field at half brightness, which the eye reads as two discrete sizes instead of depth.
    float2 s = layerSample(uv, float2(0.0), count, 1.0, 0.12, 1.0)
             + layerSample(uv, float2(4.31, 2.17), count * 1.7, 0.5, 0.19, 0.75);

    float a = clamp(s.x, 0.0, 1.0) * amount * tint.a;
    // Hot centres run toward white inside the tint, the way a real ember or spark is brightest at its core.
    float3 rgb = mix(tint.rgb, float3(1.0), clamp(s.y, 0.0, 1.0) * 0.7);
    return half4(half3(rgb * a), half(a));
}";

    // Light leak — a soft band through a point in the frame, rotated to `flowDir`, fading along its length and
    // breathing slowly. The noise modulation (dialled out by `softness`) is what keeps it from reading as a
    // clean gradient: real leaks have grain and unevenness along the edge.
    private const string LightLeakSksl = @"
uniform float2 resolution;
uniform float time;
uniform float amount;
uniform float2 center;       // leak centre in normalised frame coordinates (x across, y down)
uniform float2 flowDir;      // band axis, unit vector in screen space (y down)
uniform float width;         // half-width as a fraction of the frame height
uniform float softness;      // 0-1
uniform float speed;
uniform float seed;
uniform float4 tint;
" + NoisePreamble + @"
half4 main(float2 coord) {
    if (amount <= 0.0 || tint.a <= 0.0 || resolution.y <= 0.0) {
        return half4(0.0);
    }
    // Square units relative to the frame height so the band stays circular-correct on any aspect.
    float2 d = (coord - center * resolution) / resolution.y;
    float2 perp = float2(-flowDir.y, flowDir.x);
    float along = dot(d, flowDir);
    float across = dot(d, perp);

    float breathe = 0.85 + 0.15 * sin(time * speed * 1.7 + seed);
    float w = max(width, 0.005) * breathe;
    // The band plus a wider, dimmer halo: a leak bleeds past its own edge rather than stopping at a line.
    float band = exp(-(across * across) / (w * w));
    float halo = exp(-(across * across) / ((w * 2.6) * (w * 2.6))) * 0.35;
    float reach = w * 5.0 + 0.35;
    float run = exp(-(along * along) / (reach * reach));

    // Uneven edge. Softness smooths it but never fully flattens it — a perfectly even band reads as a
    // gradient fill, not as light bleeding in past a film gate.
    float n = valueNoise(float2(along * 2.5 + time * speed * 0.25, across * 4.0) + seed * 5.3);
    float uneven = mix(0.30 + 1.2 * n, 1.0, clamp(softness, 0.0, 1.0) * 0.65);

    float a = clamp((band + halo) * run * uneven * amount, 0.0, 1.0) * tint.a;
    // A hot, desaturating core down the middle of the band, as an over-exposed leak has.
    float3 rgb = mix(tint.rgb, float3(1.0), clamp(band * band * run, 0.0, 1.0) * 0.22);
    return half4(half3(rgb * a), half(a));
}";

    private static readonly SKRuntimeEffect Clouds = Compile(CloudsSksl, nameof(Clouds));
    private static readonly SKRuntimeEffect Particles = Compile(ParticlesSksl, nameof(Particles));
    private static readonly SKRuntimeEffect LightLeak = Compile(LightLeakSksl, nameof(LightLeak));

    private static SKRuntimeEffect Compile(string sksl, string name) =>
        SKRuntimeEffect.CreateShader(sksl, out string error)
            ?? throw new InvalidOperationException($"Atmosphere '{name}' SkSL failed to compile: {error}");

    /// <summary>
    /// Whether <paramref name="generatorTypeId"/> is one this renderer draws — the phase-2 atmospherics.
    /// </summary>
    public static bool Handles(string generatorTypeId) => GeneratorTypeIds.IsAtmosphere(generatorTypeId);

    /// <summary>
    /// Draws one atmospheric generator over the full <paramref name="width"/>×<paramref name="height"/>
    /// canvas. The caller has already cleared it to transparent; this paints premultiplied colour over it.
    /// A generator id this renderer does not handle draws nothing.
    /// </summary>
    /// <param name="canvas">The generator's own offscreen canvas, at the sequence resolution.</param>
    /// <param name="generator">The generator resolved at this frame's time.</param>
    /// <param name="width">Canvas width in pixels.</param>
    /// <param name="height">Canvas height in pixels.</param>
    public static void Draw(SKCanvas canvas, ResolvedGenerator generator, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(generator);
        if (width <= 0 || height <= 0)
            return;

        SKRuntimeEffect program;
        SKRuntimeEffectUniforms uniforms;
        string id = generator.GeneratorTypeId;

        if (GeneratorTypeIds.IsCloud(id))
        {
            program = Clouds;
            uniforms = new SKRuntimeEffectUniforms(program)
            {
                ["scale"] = (float)Math.Clamp(generator.Get(GeneratorParamNames.Scale, 3.0), 0.1, 40.0),
                ["detail"] = Unit(generator, GeneratorParamNames.Detail, 0.5),
                ["softness"] = Unit(generator, GeneratorParamNames.Softness, 0.5),
                ["falloff"] = Unit(generator, GeneratorParamNames.Falloff, 0.0),
            };
        }
        else if (GeneratorTypeIds.IsParticles(id))
        {
            program = Particles;
            uniforms = new SKRuntimeEffectUniforms(program)
            {
                ["count"] = (float)Math.Clamp(generator.Get(GeneratorParamNames.Count, 30.0), 1.0, 200.0),
                ["radius"] = (float)Math.Clamp(generator.Get(GeneratorParamNames.Size, 0.005), 0.0005, 0.2),
                ["spread"] = Unit(generator, GeneratorParamNames.Spread, 0.5),
                ["flicker"] = Unit(generator, GeneratorParamNames.Flicker, 0.0),
                ["streak"] = Unit(generator, GeneratorParamNames.Streak, 0.0),
                ["falloff"] = Unit(generator, GeneratorParamNames.Falloff, 0.0),
            };
        }
        else if (id == GeneratorTypeIds.LightLeak)
        {
            program = LightLeak;
            uniforms = new SKRuntimeEffectUniforms(program)
            {
                ["center"] = new[]
                {
                    (float)Math.Clamp(generator.Get(GeneratorParamNames.PositionX, 0.85), -1.0, 2.0),
                    (float)Math.Clamp(generator.Get(GeneratorParamNames.PositionY, 0.3), -1.0, 2.0),
                },
                ["width"] = (float)Math.Clamp(generator.Get(GeneratorParamNames.Size, 0.18), 0.005, 2.0),
                ["softness"] = Unit(generator, GeneratorParamNames.Softness, 0.6),
            };
        }
        else
        {
            return; // not an atmospheric generator
        }

        // Shared uniforms. The direction is the compass every NLE's wind control uses — degrees
        // counter-clockwise from screen right, 90° up the frame — converted here to a screen-space (y down)
        // unit vector so the shaders never deal in angles.
        double degrees = generator.Get(GeneratorParamNames.Direction, 90.0);
        double radians = degrees * Math.PI / 180.0;
        uniforms["resolution"] = new[] { (float)width, (float)height };
        uniforms["time"] = (float)generator.LocalSeconds;
        uniforms["amount"] = Unit(generator, GeneratorParamNames.Amount, 1.0);
        uniforms["speed"] = (float)Math.Clamp(generator.Get(GeneratorParamNames.Speed, 1.0), 0.0, 20.0);
        uniforms["flowDir"] = new[] { (float)Math.Cos(radians), (float)-Math.Sin(radians) };
        // Seeds are whole numbers in the UI; keep them whole here so a keyframed seed steps rather than smears.
        uniforms["seed"] = (float)Math.Round(Math.Clamp(generator.Get(GeneratorParamNames.Seed, 0.0), 0.0, 100000.0));
        uniforms["tint"] = Straight(TitleRenderer.ParseColor(
            generator.GetString(GeneratorParamNames.Color), SKColors.White));

        using SKShader shader = program.ToShader(uniforms);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(SKRect.Create(0, 0, width, height), paint);
    }

    /// <summary>Reads a 0–1 parameter, clamped.</summary>
    private static float Unit(ResolvedGenerator generator, string name, double fallback) =>
        (float)Math.Clamp(generator.Get(name, fallback), 0.0, 1.0);

    /// <summary>The colour as straight (unpremultiplied) RGBA floats — the shaders premultiply themselves.</summary>
    private static float[] Straight(SKColor color) =>
        [color.Red / 255f, color.Green / 255f, color.Blue / 255f, color.Alpha / 255f];
}
