using Sprocket.Core.Model;
using Sprocket.Core.Rendering;

namespace Sprocket.Render.Effects;

/// <summary>
/// Mosaic (plan/features/toy-cassette-camera.md, phase 1): the After Effects / Premiere primitive — the layer
/// is divided into Horizontal × Vertical Blocks, and each block is filled with one colour: the block's average
/// (a small grid of taps), or with Sharp Colors on, the sample at its centre (AE semantics). Edge Softness is
/// our addition — a smooth blend across block borders for the soft-edged pixels of a low-res sensor shown on a
/// TV. A registry SkSL effect, so preview and export run the same program (§5).
/// </summary>
/// <remarks>
/// <para>The grid is laid over the reserved <c>sprocket_bounds</c> layer rect, so it follows the layer's
/// transform and crop rather than the canvas, and a preview at one resolution matches an export at another.</para>
/// <para>Each tap re-evaluates the upstream chain (§7), so the averaging is capped at 4×4 taps per block
/// (fewer when a block is smaller than 4 source pixels — a one-pixel block samples exactly its pixel centre, so
/// Mosaic at full resolution is a pass-through). With Edge Softness at 0 every pixel reads exactly one block;
/// only pixels inside a soft border blend (and so evaluate) up to the four blocks around them.</para>
/// <para>The border blend is a bilinear mix between neighbouring block colours whose weight is a smoothstep
/// centred on the border, with its width set by Edge Softness: 0 collapses it to a hard step (classic blocks),
/// 100% spreads it block centre to block centre (a smooth upscale of the block grid). Averaging premultiplied
/// colour keeps <c>rgb ≤ a</c>, so alpha edges composite correctly.</para>
/// </remarks>
public sealed class MosaicEffect : IVideoEffect
{
    private const string Sksl = @"
uniform shader src;
uniform float horizontalBlocks; // blocks across the layer rect, whole number >= 1
uniform float verticalBlocks;   // blocks down the layer rect, whole number >= 1
uniform float sharpColors;      // 1 = each block's centre sample, 0 = the block's averaged taps
uniform float edgeSoftness;     // [0, 1] width of the blend across block borders (0 = hard blocks)
uniform float4 sprocket_bounds; // reserved: layer rect (left, top, width, height) — auto-bound by the pipeline
" + SkslSnippets.BlockGrid + @"
// One block's colour (premultiplied): its centre sample, or the mean of an n×n tap grid (n <= 4 per axis,
// reduced for blocks under four pixels so every tap lands on its own pixel centre).
float4 blockColor(float2 cell, float2 blocks) {
    cell = gridClampCell(cell, blocks);
    if (sharpColors > 0.5) {
        return float4(src.eval(gridCellPoint(cell, float2(0.5), sprocket_bounds, blocks)));
    }
    float2 n = clamp(floor(gridBlockSize(sprocket_bounds, blocks)), float2(1.0), float2(4.0));
    float4 sum = float4(0.0);
    for (int j = 0; j < 4; j++) {
        for (int i = 0; i < 4; i++) {
            if (float(i) < n.x && float(j) < n.y) {
                float2 f = (float2(float(i), float(j)) + 0.5) / n;
                sum += float4(src.eval(gridCellPoint(cell, f, sprocket_bounds, blocks)));
            }
        }
    }
    return sum / (n.x * n.y);
}

half4 main(float2 coord) {
    if (sprocket_bounds.z <= 0.0 || sprocket_bounds.w <= 0.0) {
        return src.eval(coord);
    }
    float2 blocks = float2(horizontalBlocks, verticalBlocks);
    // Position relative to the block centres: c0 is the block up-left of the pixel, f in [0, 1) how far the
    // pixel sits toward the next block centre. The block border is at f = 0.5.
    float2 u = gridPosition(coord, sprocket_bounds, blocks) - 0.5;
    float2 c0 = floor(u);
    float2 f = u - c0;
    float2 w;
    if (edgeSoftness > 0.0) {
        w = smoothstep(float2(0.5 - 0.5 * edgeSoftness), float2(0.5 + 0.5 * edgeSoftness), f);
    } else {
        w = step(float2(0.5), f);
    }
    float w00 = (1.0 - w.x) * (1.0 - w.y);
    float w10 = w.x * (1.0 - w.y);
    float w01 = (1.0 - w.x) * w.y;
    float w11 = w.x * w.y;
    float4 sum = float4(0.0);
    if (w00 > 0.0) { sum += w00 * blockColor(c0, blocks); }
    if (w10 > 0.0) { sum += w10 * blockColor(c0 + float2(1.0, 0.0), blocks); }
    if (w01 > 0.0) { sum += w01 * blockColor(c0 + float2(0.0, 1.0), blocks); }
    if (w11 > 0.0) { sum += w11 * blockColor(c0 + float2(1.0, 1.0), blocks); }
    return half4(sum);
}";

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; } = EffectCatalog.Find(EffectTypeIds.Mosaic)
        ?? throw new InvalidOperationException($"'{EffectTypeIds.Mosaic}' is missing from EffectCatalog.BuiltIns.");

    /// <inheritdoc />
    public string SkslSource => Sksl;

    /// <inheritdoc />
    public void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms)
    {
        // Integer parameters are rounded here as well as snapped in the Inspector, so a keyframe eased between
        // two counts (or an MCP value like 12.4) still yields a whole-block grid.
        uniforms.Set("horizontalBlocks", (float)Math.Clamp(Math.Round(effect.Get(EffectParamNames.HorizontalBlocks, 10.0)), 1.0, 1920.0));
        uniforms.Set("verticalBlocks", (float)Math.Clamp(Math.Round(effect.Get(EffectParamNames.VerticalBlocks, 10.0)), 1.0, 1080.0));
        uniforms.Set("sharpColors", effect.Get(EffectParamNames.SharpColors, 0.0) > 0.5 ? 1f : 0f);
        uniforms.Set("edgeSoftness", (float)Math.Clamp(effect.Get(EffectParamNames.EdgeSoftness, 0.0), 0.0, 1.0));
        // sprocket_bounds is a reserved uniform auto-bound by SkiaEffectPipeline (§13) — not set here.
    }
}
