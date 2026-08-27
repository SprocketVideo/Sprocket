namespace Sprocket.Plugins.Lv2;

/// <summary>One <c>lv2:scalePoint</c> on a control port: a labelled notable value (an enumeration entry).</summary>
public sealed record Lv2ScalePoint(string Label, double Value);

/// <summary>
/// One LV2 port as described by the bundle's Turtle metadata. Pure managed data (no native pointers), so the
/// parameter mapping and the tests work against it without a plugin binary.
/// </summary>
/// <param name="Index">The port index (<c>lv2:index</c>) passed to <c>connect_port</c>.</param>
/// <param name="Symbol">The port's stable machine name (<c>lv2:symbol</c>) — the parameter key.</param>
/// <param name="Name">The port's human-readable name (<c>lv2:name</c>).</param>
/// <param name="Types">The port's <c>rdf:type</c> IRIs (direction + kind classes).</param>
/// <param name="Properties">The port's <c>lv2:portProperty</c> IRIs (toggled, integer, enumeration, …).</param>
/// <param name="Default">The <c>lv2:default</c> value, if declared.</param>
/// <param name="Minimum">The <c>lv2:minimum</c>, if declared.</param>
/// <param name="Maximum">The <c>lv2:maximum</c>, if declared.</param>
/// <param name="UnitIri">The <c>units:unit</c> IRI, if declared.</param>
/// <param name="ScalePoints">The port's scale points, in declaration order.</param>
/// <param name="Comment">The port's <c>rdfs:comment</c>, if any (shown as the parameter tooltip).</param>
public sealed record Lv2PortInfo(
    int Index,
    string Symbol,
    string Name,
    IReadOnlySet<string> Types,
    IReadOnlySet<string> Properties,
    double? Default,
    double? Minimum,
    double? Maximum,
    string? UnitIri,
    IReadOnlyList<Lv2ScalePoint> ScalePoints,
    string? Comment = null)
{
    public bool IsInput => Types.Contains(Lv2Ns.InputPort);
    public bool IsOutput => Types.Contains(Lv2Ns.OutputPort);
    public bool IsAudio => Types.Contains(Lv2Ns.AudioPort);
    public bool IsControl => Types.Contains(Lv2Ns.ControlPort);

    public bool IsAudioInput => IsAudio && IsInput;
    public bool IsAudioOutput => IsAudio && IsOutput;
    public bool IsControlInput => IsControl && IsInput;
    public bool IsControlOutput => IsControl && IsOutput;

    /// <summary>True when the host may leave this port unconnected (<c>lv2:connectionOptional</c>).</summary>
    public bool IsConnectionOptional => Properties.Contains(Lv2Ns.ConnectionOptional);

    /// <summary>True for a port type the core-subset host can drive: audio or control, in or out.</summary>
    public bool IsSupportedKind => (IsAudio || IsControl) && (IsInput || IsOutput) && !Types.Contains(Lv2Ns.CvPort);
}

/// <summary>
/// One LV2 plugin's bundle metadata as pure managed data: identity, versions, required features, binary, and
/// ports. Produced by <see cref="Lv2BundleReader"/>; consumed by <see cref="Lv2ParameterMapping"/> (catalog
/// descriptor) and <see cref="Lv2Effect"/> (port layout).
/// </summary>
/// <param name="Uri">The plugin URI — its globally unique, location-independent identity.</param>
/// <param name="Name">The <c>doap:name</c> (may be empty).</param>
/// <param name="Maker">The maintainer's <c>foaf:name</c> (may be empty).</param>
/// <param name="License">The <c>doap:license</c> IRI (may be empty).</param>
/// <param name="Comment">The plugin's <c>rdfs:comment</c> (may be empty).</param>
/// <param name="BinaryPath">Full local path of the shared library (<c>lv2:binary</c>).</param>
/// <param name="BundlePath">Full local path of the bundle directory (passed to <c>instantiate</c>).</param>
/// <param name="MinorVersion">The <c>lv2:minorVersion</c> (0 if absent).</param>
/// <param name="MicroVersion">The <c>lv2:microVersion</c> (0 if absent).</param>
/// <param name="RequiredFeatures">Feature IRIs the plugin cannot run without.</param>
/// <param name="Ports">Every port, ordered by index.</param>
public sealed record Lv2PluginInfo(
    string Uri,
    string Name,
    string Maker,
    string License,
    string Comment,
    string BinaryPath,
    string BundlePath,
    int MinorVersion,
    int MicroVersion,
    IReadOnlyList<string> RequiredFeatures,
    IReadOnlyList<Lv2PortInfo> Ports)
{
    /// <summary>The control input ports, in index order — the plugin's parameters.</summary>
    public IEnumerable<Lv2PortInfo> ControlInputs => Ports.Where(p => p.IsControlInput);

    public int AudioInputCount => Ports.Count(p => p.IsAudioInput);
    public int AudioOutputCount => Ports.Count(p => p.IsAudioOutput);

    /// <summary>Ports of a kind the core-subset host cannot drive and that are <em>not</em> connection-optional —
    /// any such port makes the plugin unhostable (LV2 requires every port be connected before <c>run</c>).</summary>
    public IEnumerable<Lv2PortInfo> BlockingPorts => Ports.Where(p => !p.IsSupportedKind && !p.IsConnectionOptional);

    /// <summary>"minor.micro" for the Plugin Manager row.</summary>
    public string Version => $"{MinorVersion}.{MicroVersion}";
}

/// <summary>The result of reading one LV2 bundle directory: its plugins plus per-plugin notes for anything malformed.</summary>
public sealed record Lv2BundleInfo(string Path, IReadOnlyList<Lv2PluginInfo> Plugins, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads an LV2 bundle (a <c>*.lv2</c> directory) into <see cref="Lv2BundleInfo"/>: parses <c>manifest.ttl</c>,
/// finds every <c>lv2:Plugin</c>, follows each plugin's <c>rdfs:seeAlso</c> files, and gathers its metadata and
/// port descriptions through the managed <see cref="TurtleReader"/>. Pure file/text work — no binary is loaded.
/// </summary>
internal static class Lv2BundleReader
{
    /// <summary>The manifest every bundle must carry.</summary>
    public const string ManifestFileName = "manifest.ttl";

    /// <summary>Largest Turtle file the reader will open (real bundle metadata is a few KB; this stops a
    /// multi-GB file from exhausting memory during startup discovery).</summary>
    public const long MaxTurtleFileBytes = 16L * 1024 * 1024;

    /// <summary>Most <c>rdfs:seeAlso</c> files followed per plugin.</summary>
    public const int MaxSeeAlsoFiles = 32;

    /// <summary>Upper bound on a plugin's port count (mirrors the LADSPA reader's guard).</summary>
    public const int MaxPorts = 4096;

    /// <summary>Reads the bundle at <paramref name="bundleDirectory"/>. Throws <see cref="FileNotFoundException"/>
    /// if it has no manifest, or <see cref="FormatException"/> if the manifest itself is unparseable; problems in a
    /// single plugin's own files are recorded as warnings and that plugin is skipped.</summary>
    public static Lv2BundleInfo Read(string bundleDirectory)
    {
        string bundle = Path.GetFullPath(bundleDirectory);
        string manifestPath = Path.Combine(bundle, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"LV2 bundle has no {ManifestFileName}.", manifestPath);

        string baseIri = BundleBaseIri(bundle);
        TurtleGraph manifest = TurtleReader.Parse(ReadTurtleFile(manifestPath), baseIri, "manifest");

        var plugins = new List<Lv2PluginInfo>();
        var warnings = new List<string>();
        foreach (TurtleNode subject in manifest.SubjectsWith(TurtleReader.RdfType, TurtleNode.Iri(Lv2Ns.Plugin)))
        {
            if (!subject.IsIri)
            {
                warnings.Add("A plugin in the manifest has no URI — skipped.");
                continue;
            }

            try
            {
                plugins.Add(ReadPlugin(manifest, subject, bundle, baseIri, warnings));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{subject.Value}: {ex.Message}");
            }
        }

        return new Lv2BundleInfo(bundle, plugins, warnings);
    }

    private static Lv2PluginInfo ReadPlugin(TurtleGraph manifest, TurtleNode plugin, string bundle, string baseIri, List<string> warnings)
    {
        // The plugin's own description files plus just its own manifest triples (an indexed copy, so a manifest
        // declaring thousands of plugins doesn't cost O(N²)).
        var graph = new TurtleGraph();
        CopyClosure(manifest, plugin, graph);
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        int fileIndex = 0;
        foreach (TurtleNode seeAlso in manifest.Objects(plugin, Lv2Ns.RdfsSeeAlso))
        {
            if (!seeAlso.IsIri)
                continue;
            if (LocalPath(seeAlso.Value, bundle) is not { } file)
            {
                warnings.Add($"{plugin.Value}: rdfs:seeAlso '{seeAlso.Value}' is not a file inside the bundle — ignored.");
                continue;
            }
            if (!loaded.Add(file))
                continue;
            if (loaded.Count > MaxSeeAlsoFiles)
                throw new FormatException($"more than {MaxSeeAlsoFiles} rdfs:seeAlso files.");
            if (!File.Exists(file))
            {
                warnings.Add($"{plugin.Value}: rdfs:seeAlso file '{file}' is missing.");
                continue;
            }
            graph.AddRange(TurtleReader.Parse(ReadTurtleFile(file), baseIri, $"f{fileIndex++}"));
        }

        string? binary = graph.FirstObject(plugin, Lv2Ns.Binary) is { IsIri: true } b ? LocalPath(b.Value, bundle) : null;
        if (binary is null)
            throw new FormatException("plugin declares no lv2:binary inside the bundle.");

        string maker = "";
        if (graph.FirstObject(plugin, Lv2Ns.DoapMaintainer) is TurtleNode maintainer)
            maker = graph.FirstString(maintainer, Lv2Ns.FoafName) ?? "";

        var ports = new List<Lv2PortInfo>();
        foreach (TurtleNode portNode in graph.Objects(plugin, Lv2Ns.Port))
        {
            Lv2PortInfo? port = ReadPort(graph, portNode, warnings, plugin.Value);
            if (port is not null)
                ports.Add(port);
        }
        ports.Sort((x, y) => x.Index.CompareTo(y.Index));
        // LV2 requires port indices to be exactly 0 … N-1. The host passes them straight to connect_port, and
        // plugins index their port tables with them unchecked (the spec lets them), so a hostile .ttl paired with
        // a benign binary must not be able to drive it out of bounds.
        if (ports.Count > MaxPorts)
            throw new FormatException($"declares {ports.Count} ports (max {MaxPorts}).");
        for (int i = 0; i < ports.Count; i++)
            if (ports[i].Index != i)
                throw new FormatException("port indices are not the dense sequence 0 … N-1.");

        return new Lv2PluginInfo(
            Uri: plugin.Value,
            Name: graph.FirstString(plugin, Lv2Ns.DoapName) ?? "",
            Maker: maker,
            License: graph.FirstObject(plugin, Lv2Ns.DoapLicense) is { IsIri: true } lic ? lic.Value : "",
            Comment: graph.FirstString(plugin, Lv2Ns.RdfsComment) ?? "",
            BinaryPath: binary,
            BundlePath: bundle,
            MinorVersion: (int)(graph.FirstNumber(plugin, Lv2Ns.MinorVersion) ?? 0),
            MicroVersion: (int)(graph.FirstNumber(plugin, Lv2Ns.MicroVersion) ?? 0),
            RequiredFeatures: graph.Objects(plugin, Lv2Ns.RequiredFeature).Where(f => f.IsIri).Select(f => f.Value).Distinct().ToArray(),
            Ports: ports);
    }

    private static Lv2PortInfo? ReadPort(TurtleGraph graph, TurtleNode port, List<string> warnings, string pluginUri)
    {
        if (graph.FirstNumber(port, Lv2Ns.Index) is not double indexValue || indexValue < 0 || indexValue >= MaxPorts || indexValue != Math.Floor(indexValue))
            throw new FormatException("a port has no valid lv2:index."); // the plugin's port table can't be trusted

        var types = new HashSet<string>(graph.Objects(port, TurtleReader.RdfType).Where(t => t.IsIri).Select(t => t.Value), StringComparer.Ordinal);
        var props = new HashSet<string>(graph.Objects(port, Lv2Ns.PortProperty).Where(t => t.IsIri).Select(t => t.Value), StringComparer.Ordinal);

        var scalePoints = new List<Lv2ScalePoint>();
        foreach (TurtleNode sp in graph.Objects(port, Lv2Ns.ScalePoint))
        {
            if (graph.FirstNumber(sp, Lv2Ns.RdfValue) is double value)
                scalePoints.Add(new Lv2ScalePoint(graph.FirstString(sp, Lv2Ns.RdfsLabel) ?? value.ToString(System.Globalization.CultureInfo.InvariantCulture), value));
        }

        int index = (int)indexValue;
        return new Lv2PortInfo(
            Index: index,
            Symbol: graph.FirstString(port, Lv2Ns.Symbol) ?? $"port{index}",
            Name: graph.FirstString(port, Lv2Ns.Name) ?? "",
            Types: types,
            Properties: props,
            Default: graph.FirstNumber(port, Lv2Ns.Default),
            Minimum: graph.FirstNumber(port, Lv2Ns.Minimum),
            Maximum: graph.FirstNumber(port, Lv2Ns.Maximum),
            UnitIri: graph.FirstObject(port, Lv2Ns.UnitsUnit) is { IsIri: true } u ? u.Value : null,
            ScalePoints: scalePoints,
            Comment: graph.FirstString(port, Lv2Ns.RdfsComment));
    }

    /// <summary>The bundle directory as the <c>file:</c> base IRI relative bundle IRIs resolve against.</summary>
    internal static string BundleBaseIri(string bundleDirectory)
    {
        string dir = bundleDirectory.EndsWith(Path.DirectorySeparatorChar) ? bundleDirectory : bundleDirectory + Path.DirectorySeparatorChar;
        return new Uri(dir).AbsoluteUri;
    }

    /// <summary>Copies <paramref name="subject"/>'s triples from <paramref name="source"/> into <paramref name="target"/>,
    /// following blank-node objects transitively (a plugin's ports and scale points declared inline in the manifest
    /// are blank nodes). Indexed lookups, so the cost is the plugin's own description, not the whole manifest.</summary>
    private static void CopyClosure(TurtleGraph source, TurtleNode subject, TurtleGraph target)
    {
        var visited = new HashSet<TurtleNode>();
        var pending = new Stack<TurtleNode>();
        pending.Push(subject);
        while (pending.Count > 0)
        {
            TurtleNode node = pending.Pop();
            if (!visited.Add(node))
                continue;
            foreach (TurtleTriple t in source.TriplesOf(node))
            {
                target.Add(t);
                if (t.Object.Kind == TurtleNodeKind.Blank)
                    pending.Push(t.Object);
            }
        }
    }

    /// <summary>Reads a Turtle file, refusing one over <see cref="MaxTurtleFileBytes"/>.</summary>
    private static string ReadTurtleFile(string path)
    {
        if (new FileInfo(path).Length > MaxTurtleFileBytes)
            throw new FormatException($"'{Path.GetFileName(path)}' is larger than {MaxTurtleFileBytes / (1024 * 1024)} MB.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Converts a resolved <c>file:</c> IRI back to a local path <b>inside <paramref name="bundleDirectory"/></b>;
    /// null for a non-file IRI, a UNC/remote host (a <c>file://host/share</c> reference would open an SMB session
    /// with the user's credentials on every startup scan), or a path outside the bundle. Deliberately stricter
    /// than lilv (which honours any absolute IRI): a bundle has no legitimate reason to load files it doesn't ship.
    /// </summary>
    internal static string? LocalPath(string iri, string bundleDirectory)
    {
        if (!Uri.TryCreate(iri, UriKind.Absolute, out Uri? uri) || !uri.IsFile || uri.IsUnc || !string.IsNullOrEmpty(uri.Host))
            return null;
        try
        {
            string full = Path.GetFullPath(uri.LocalPath);
            string root = Path.GetFullPath(bundleDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            StringComparison cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return full.StartsWith(root, cmp) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
