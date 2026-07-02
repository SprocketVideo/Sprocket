using Sprocket.Core.Model;

namespace Sprocket.Core.Rendering;

/// <summary>
/// Receives an effect's uniform values for one frame. The Render layer implements this over its shader
/// runtime's uniform block; Core (and plugins) stay GPU-agnostic — they only name uniforms and values.
/// </summary>
public interface IUniformWriter
{
    /// <summary>Sets a scalar <c>float</c> uniform.</summary>
    void Set(string name, float value);

    /// <summary>Sets a vector/array uniform (e.g. a <c>float3</c> colour or a packed matrix).</summary>
    void Set(string name, float[] values);
}

/// <summary>
/// A shader-backed video effect (ARCHITECTURE.md §13): the contract both built-in shader effects and
/// plugin-contributed effects implement. It is <b>declarative</b> — the effect supplies its catalog
/// <see cref="Descriptor"/> (id, display name, typed parameters — the Inspector builds its editing UI from
/// this), its SkSL program, and a per-frame uniform binding; the Render layer owns compilation and
/// execution. That keeps GPU/Skia types out of Core and out of plugin assemblies (§2), and the effect runs
/// identically in preview and export because it is just another chained shader stage (§5, §7).
/// </summary>
/// <remarks>
/// SkSL contract: the program must declare <c>uniform shader src;</c> (the previous chain stage — sample it
/// with <c>src.eval(coord)</c>) and a <c>half4 main(float2 coord)</c> entry point, plus one uniform per value
/// bound in <see cref="BindUniforms"/>. Colour is <b>premultiplied</b> RGBA: keep <c>rgb</c> within
/// <c>[0, a]</c> so the effect composes correctly over lower layers (see the built-ins in
/// <c>SkiaEffectPipeline</c> for the idiom). Implementations must be stateless and thread-agnostic — the
/// same instance serves every render pipeline (preview, export, thumbnails).
/// </remarks>
public interface IVideoEffect
{
    /// <summary>
    /// The effect's catalog entry: its stable id (plugins use namespaced ids like <c>"plugin.acme.glow"</c> —
    /// the <c>builtin.</c> prefix is reserved), display name, category, and typed parameter descriptors.
    /// </summary>
    EffectDescriptor Descriptor { get; }

    /// <summary>The SkSL fragment program (see the SkSL contract in the type remarks).</summary>
    string SkslSource { get; }

    /// <summary>
    /// Binds the frame's uniform values from the resolved (keyframe-evaluated) parameters. Called once per
    /// frame per effect instance in the chain; must not allocate beyond the writer calls or touch shared state.
    /// </summary>
    void BindUniforms(ResolvedEffect effect, IUniformWriter uniforms);
}
