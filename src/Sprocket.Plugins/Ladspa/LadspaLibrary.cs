using System.Runtime.InteropServices;

namespace Sprocket.Plugins.Ladspa;

/// <summary>
/// One loaded LADSPA library file (a <c>.so</c> / <c>.dll</c> / <c>.dylib</c>) and the effect providers it
/// contributes (PLAN.md step 59). A LADSPA library exports a single <c>ladspa_descriptor(index)</c> which the
/// host walks (0, 1, 2 … until it returns null) to enumerate the plugins inside; each one that is hostable as
/// an in-place effect (≥1 audio-in and ≥1 audio-out port) becomes a <see cref="LadspaEffectProvider"/>.
/// </summary>
/// <remarks>
/// <b>The native module is intentionally never unmapped within a session.</b> Its <c>const</c> descriptors hand
/// out the function pointers a live <see cref="LadspaEffect"/> executes on the audio thread; calling
/// <c>NativeLibrary.Free</c> while a DSP instance might be mid-<c>run()</c> would be a use-after-free with no
/// safe quiescence barrier. Disabling a LADSPA plugin therefore only unregisters its catalog descriptors (so the
/// mixer drops its instances on the next buffer); the small module stays resident until the process exits — the
/// same "keep the binary resident for the session" behaviour DAWs use for audio plugins.
/// </remarks>
internal sealed unsafe class LadspaLibrary
{
    private const int MaxDescriptors = 100_000; // runaway guard for a malformed ladspa_descriptor()

    private LadspaLibrary(string path, IReadOnlyList<LadspaEffectProvider> providers, IReadOnlyList<string> skipped)
    {
        Path = path;
        Providers = providers;
        Skipped = skipped;
    }

    /// <summary>Full path of the library file.</summary>
    public string Path { get; }

    /// <summary>The hostable effect providers discovered in the library, in descriptor order.</summary>
    public IReadOnlyList<LadspaEffectProvider> Providers { get; }

    /// <summary>Human-readable notes for plugins that were found but not hostable (e.g. a source/synth with no
    /// audio input) — surfaced as a per-file warning by the Plugin Manager.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>
    /// Loads a LADSPA library and enumerates its plugins. Throws <see cref="DllNotFoundException"/> /
    /// <see cref="BadImageFormatException"/> if the file cannot be loaded as a native module, or
    /// <see cref="EntryPointNotFoundException"/> if it is not a LADSPA library (no <c>ladspa_descriptor</c>).
    /// A library that loads but yields no hostable plugins returns with an empty <see cref="Providers"/> list.
    /// </summary>
    public static LadspaLibrary Load(string path)
    {
        nint module = NativeLibrary.Load(path);
        // Deliberately not freed on failure/never: the module stays mapped for the session (see the class remarks).
        nint export = NativeLibrary.GetExport(module, LadspaAbi.DescriptorFunction);
        var descriptorAt = (delegate* unmanaged<CULong, nint>)export;

        var providers = new List<LadspaEffectProvider>();
        var skipped = new List<string>();
        for (int index = 0; index < MaxDescriptors; index++)
        {
            nint descriptorPtr = descriptorAt(new CULong((nuint)index));
            if (descriptorPtr == nint.Zero)
                break;

            LadspaPluginInfo info = LadspaDescriptorReader.Read(descriptorPtr);
            if (info.AudioInputCount >= 1 && info.AudioOutputCount >= 1)
                providers.Add(new LadspaEffectProvider(descriptorPtr, info));
            else
                skipped.Add($"{Describe(info)} — not a hostable effect ({info.AudioInputCount} audio in, {info.AudioOutputCount} audio out).");
        }

        return new LadspaLibrary(path, providers, skipped);
    }

    private static string Describe(LadspaPluginInfo info) =>
        !string.IsNullOrWhiteSpace(info.Name) ? info.Name : (!string.IsNullOrWhiteSpace(info.Label) ? info.Label : "unnamed plugin");
}
