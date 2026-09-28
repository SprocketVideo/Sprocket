namespace Sprocket.Render.Effects;

/// <summary>
/// Small reusable SkSL fragments shared by the toy-cassette-camera family of registry effects
/// (plan/features/toy-cassette-camera.md): Mosaic, the Toy Cassette Camera stage and Echo. Each constant is a
/// self-contained block of SkSL declarations that an effect concatenates ahead of its own program text, so
/// the shared maths is written — and fixed — once.
/// </summary>
/// <remarks>
/// SkSL has no <c>#include</c>, hence plain string composition. The fragments declare functions and
/// constants only (no uniforms or children), with distinctive names so they cannot collide with an effect's
/// own identifiers. Effects that predate this class keep their inline copies — a deliberate no-churn choice,
/// not an invitation to diverge.
/// </remarks>
internal static class SkslSnippets
{
    /// <summary>
    /// <c>float cellHash(float2 p)</c> — a value hash in [0, 1]: cheap, deterministic, no texture (the same
    /// construction the B&amp;W grain and Flicker use). Hash a cell coordinate plus a seed to get noise that is
    /// identical for a given (cell, seed) — the property that makes preview and export match (§5).
    /// </summary>
    public const string CellHash = @"
float cellHash(float2 p) {
    float3 q = fract(float3(p.xyx) * float3(443.897, 441.423, 437.195));
    q += dot(q, q.yzx + 19.19);
    return fract((q.x + q.y) * q.z);
}
";

    /// <summary>
    /// <c>REC709_LUMA</c> and <c>float rec709Luma(float3 c)</c> — Rec.709 luma weights, the same ones the
    /// grading toolset uses.
    /// </summary>
    public const string Rec709Luma = @"
const float3 REC709_LUMA = float3(0.2126, 0.7152, 0.0722);

float rec709Luma(float3 c) {
    return dot(c, REC709_LUMA);
}
";

    /// <summary>
    /// A block grid of <c>blocks</c> cells (per axis) laid over a rect <c>(left, top, width, height)</c> — pass
    /// <c>sprocket_bounds</c> so the grid tracks the layer rather than the canvas:
    /// <list type="bullet">
    /// <item><c>float2 gridBlockSize(float4 rect, float2 blocks)</c> — one cell's size in canvas units.</item>
    /// <item><c>float2 gridPosition(float2 coord, float4 rect, float2 blocks)</c> — <c>coord</c> in cell units
    /// (integer part = cell index, fractional part = position inside the cell).</item>
    /// <item><c>float2 gridClampCell(float2 cell, float2 blocks)</c> — a cell index clamped onto the grid, so
    /// neighbour look-ups at the rect's edges reuse the edge cell.</item>
    /// <item><c>float2 gridCellPoint(float2 cell, float2 f, float4 rect, float2 blocks)</c> — the canvas point
    /// at fraction <c>f</c> ([0, 1]²) inside <c>cell</c>; <c>f = 0.5</c> is the cell centre.</item>
    /// </list>
    /// </summary>
    public const string BlockGrid = @"
float2 gridBlockSize(float4 rect, float2 blocks) {
    return rect.zw / blocks;
}

float2 gridPosition(float2 coord, float4 rect, float2 blocks) {
    return (coord - rect.xy) / gridBlockSize(rect, blocks);
}

float2 gridClampCell(float2 cell, float2 blocks) {
    return clamp(cell, float2(0.0), blocks - 1.0);
}

float2 gridCellPoint(float2 cell, float2 f, float4 rect, float2 blocks) {
    return rect.xy + (cell + f) * gridBlockSize(rect, blocks);
}
";
}
