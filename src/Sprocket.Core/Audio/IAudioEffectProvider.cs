using Sprocket.Core.Model;

namespace Sprocket.Core.Audio;

/// <summary>
/// A plugin-contributed audio effect type (ARCHITECTURE.md §13, §19): pairs the catalog
/// <see cref="Descriptor"/> (id, display name, typed parameters — must have
/// <see cref="EffectCategory.Audio"/> so the render graph routes it to the mixer's DSP chain) with a
/// factory for fresh stateful <see cref="IAudioEffect"/> DSP instances. The mixer creates one instance per
/// chain slot (per clip/track/bus occurrence) and keeps it alive across buffers so filter/envelope state
/// persists; parameter changes flow through <see cref="IAudioEffect.Process"/>'s resolved parameters.
/// </summary>
public interface IAudioEffectProvider
{
    /// <summary>
    /// The effect's catalog entry. <see cref="EffectDescriptor.Category"/> must be
    /// <see cref="EffectCategory.Audio"/>; plugins use namespaced ids like <c>"plugin.acme.deesser"</c>
    /// (the <c>builtin.</c> prefix is reserved).
    /// </summary>
    EffectDescriptor Descriptor { get; }

    /// <summary>Creates a fresh, independent DSP instance (one per chain slot; the mixer owns its lifetime).</summary>
    IAudioEffect CreateEffect();
}
