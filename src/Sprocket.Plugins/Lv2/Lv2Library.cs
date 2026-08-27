using System.Runtime.InteropServices;

namespace Sprocket.Plugins.Lv2;

/// <summary>
/// One loaded LV2 bundle (PLAN.md step 59): its metadata (from <see cref="Lv2BundleReader"/>) joined to the
/// <c>LV2_Descriptor</c>s found in its binaries by walking <c>lv2_descriptor(0, 1, …)</c> and matching plugin
/// URIs. Each plugin that is hostable in the core subset — required features all supported, no blocking
/// (non-optional, non-audio/control) ports, ≥1 audio in and out — becomes an <see cref="Lv2EffectProvider"/>.
/// </summary>
/// <remarks>
/// As with LADSPA, <b>binaries are never unmapped within a session</b>: a live <see cref="Lv2Effect"/> may be
/// mid-<c>run()</c> on the audio thread through the descriptor's function pointers, and there is no safe
/// quiescence barrier. Disabling a bundle only unregisters its catalog descriptors.
/// </remarks>
internal sealed unsafe class Lv2Library
{
    private const int MaxDescriptors = 100_000; // runaway guard for a malformed lv2_descriptor()

    private Lv2Library(Lv2BundleInfo bundle, IReadOnlyList<Lv2EffectProvider> providers, IReadOnlyList<string> skipped)
    {
        Bundle = bundle;
        Providers = providers;
        Skipped = skipped;
    }

    /// <summary>The bundle directory path.</summary>
    public string Path => Bundle.Path;

    public Lv2BundleInfo Bundle { get; }

    /// <summary>The hostable effect providers, in manifest order.</summary>
    public IReadOnlyList<Lv2EffectProvider> Providers { get; }

    /// <summary>Notes for plugins found but not hosted (unsupported feature, blocking port, no audio I/O, missing
    /// descriptor) plus the bundle reader's own warnings.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>
    /// Reads and loads the bundle at <paramref name="bundleDirectory"/>. Throws if the manifest is missing or
    /// unparseable, or if a binary cannot be loaded as a native module (<see cref="DllNotFoundException"/> /
    /// <see cref="BadImageFormatException"/>) or lacks <c>lv2_descriptor</c> (<see cref="EntryPointNotFoundException"/>).
    /// </summary>
    public static Lv2Library Load(string bundleDirectory)
    {
        Lv2BundleInfo bundle = Lv2BundleReader.Read(bundleDirectory);
        var skipped = new List<string>(bundle.Warnings);
        var providers = new List<Lv2EffectProvider>();

        // Descriptors per binary, keyed by plugin URI (a bundle may hold several plugins in one binary).
        var descriptorsByBinary = new Dictionary<string, Dictionary<string, nint>>(StringComparer.Ordinal);

        foreach (Lv2PluginInfo info in bundle.Plugins)
        {
            string label = info.Name.Length > 0 ? info.Name : info.Uri;

            IReadOnlyList<string> unsupported = Lv2Features.Unsupported(info.RequiredFeatures);
            if (unsupported.Count > 0)
            {
                skipped.Add($"{label} — requires unsupported feature(s): {string.Join(", ", unsupported)}.");
                continue;
            }
            Lv2PortInfo[] blocking = info.BlockingPorts.ToArray();
            if (blocking.Length > 0)
            {
                skipped.Add($"{label} — uses port type(s) outside the core subset: {string.Join(", ", blocking.Select(p => p.Symbol))}.");
                continue;
            }
            if (info.AudioInputCount < 1 || info.AudioOutputCount < 1)
            {
                skipped.Add($"{label} — not a hostable effect ({info.AudioInputCount} audio in, {info.AudioOutputCount} audio out).");
                continue;
            }

            if (!descriptorsByBinary.TryGetValue(info.BinaryPath, out Dictionary<string, nint>? descriptors))
                descriptorsByBinary[info.BinaryPath] = descriptors = EnumerateDescriptors(info.BinaryPath, info.BundlePath);

            if (!descriptors.TryGetValue(info.Uri, out nint descriptorPtr))
            {
                skipped.Add($"{label} — binary '{System.IO.Path.GetFileName(info.BinaryPath)}' exports no descriptor for its URI.");
                continue;
            }

            providers.Add(new Lv2EffectProvider(descriptorPtr, info));
        }

        return new Lv2Library(bundle, providers, skipped);
    }

    /// <summary>Loads a binary (kept mapped for the session) and collects its descriptors by plugin URI, through
    /// <c>lv2_descriptor</c> or, for a binary that only exports the newer entry point, <c>lv2_lib_descriptor</c>.</summary>
    internal static Dictionary<string, nint> EnumerateDescriptors(string binaryPath, string bundlePath)
    {
        nint module = NativeLibrary.Load(binaryPath);
        delegate* unmanaged<uint, nint> descriptorAt = null;
        Lv2LibDescriptor* lib = null;
        if (NativeLibrary.TryGetExport(module, Lv2Abi.DescriptorFunction, out nint export))
        {
            descriptorAt = (delegate* unmanaged<uint, nint>)export;
        }
        else
        {
            nint libExport = NativeLibrary.GetExport(module, Lv2Abi.LibDescriptorFunction); // throws EntryPointNotFound: not LV2
            string bundle = bundlePath.EndsWith(System.IO.Path.DirectorySeparatorChar) ? bundlePath : bundlePath + System.IO.Path.DirectorySeparatorChar;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(bundle + "\0");
            fixed (byte* b = bytes)
                lib = (Lv2LibDescriptor*)((delegate* unmanaged<byte*, nint, nint>)libExport)(b, Lv2Features.FeaturesArray);
            if (lib is null || lib->GetPlugin == nint.Zero)
                throw new EntryPointNotFoundException("lv2_lib_descriptor returned no usable library descriptor.");
        }

        var result = new Dictionary<string, nint>(StringComparer.Ordinal);
        for (uint index = 0; index < MaxDescriptors; index++)
        {
            nint descriptorPtr = descriptorAt != null
                ? descriptorAt(index)
                : ((delegate* unmanaged<nint, uint, nint>)lib->GetPlugin)(lib->Handle, index);
            if (descriptorPtr == nint.Zero)
                break;
            var d = (Lv2Descriptor*)descriptorPtr;
            string? uri = d->URI == nint.Zero ? null : Marshal.PtrToStringUTF8(d->URI);
            if (uri is not null)
                result.TryAdd(uri, descriptorPtr);
        }
        return result;
    }
}
