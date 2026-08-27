using Sprocket.Core.Audio;
using Sprocket.Core.Model;

namespace Sprocket.Plugins.Lv2;

/// <summary>One hosted LV2 plugin on the existing <see cref="IAudioEffectProvider"/> seam (PLAN.md step 59): its
/// catalog descriptor plus a factory for fresh <see cref="Lv2Effect"/> DSP instances. No mixer changes needed.</summary>
internal sealed class Lv2EffectProvider : IAudioEffectProvider
{
    private readonly nint _descriptorPtr;

    public Lv2EffectProvider(nint descriptorPtr, Lv2PluginInfo info)
    {
        _descriptorPtr = descriptorPtr;
        Info = info;
        Descriptor = Lv2ParameterMapping.ToEffectDescriptor(info);
    }

    /// <inheritdoc />
    public EffectDescriptor Descriptor { get; }

    /// <summary>The plugin's bundle metadata.</summary>
    public Lv2PluginInfo Info { get; }

    /// <inheritdoc />
    public IAudioEffect CreateEffect() => new Lv2Effect(_descriptorPtr, Info);
}
