using Sprocket.Core.Audio;
using Sprocket.Core.Model;

namespace Sprocket.Plugins.Ladspa;

/// <summary>
/// One hosted LADSPA plugin exposed on Sprocket's existing audio-plugin seam (PLAN.md step 59): it pairs the
/// catalog <see cref="EffectDescriptor"/> (built by <see cref="LadspaParameterMapping"/>) with a factory for
/// fresh <see cref="LadspaEffect"/> DSP instances. The mixer creates one instance per chain slot and keeps it
/// alive across buffers, exactly as for a built-in or a managed plugin — LADSPA needs no mixer change.
/// </summary>
internal sealed class LadspaEffectProvider : IAudioEffectProvider
{
    private readonly nint _descriptorPtr;
    private readonly LadspaPluginInfo _info;

    public LadspaEffectProvider(nint descriptorPtr, LadspaPluginInfo info)
    {
        _descriptorPtr = descriptorPtr;
        _info = info;
        Descriptor = LadspaParameterMapping.ToEffectDescriptor(info);
    }

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; }

    /// <summary>The plugin's parsed descriptor (identity + ports), for the Plugin Manager row and tests.</summary>
    public LadspaPluginInfo Info => _info;

    /// <inheritdoc />
    public IAudioEffect CreateEffect() => new LadspaEffect(_descriptorPtr, _info);
}
