using Sprocket.Plugins.Lv2;
using Xunit;

namespace Sprocket.Plugins.Tests.Lv2;

/// <summary>Reading a real on-disk LV2 bundle layout (manifest + plugin .ttl) into <see cref="Lv2PluginInfo"/> (PLAN.md step 59).</summary>
public sealed class Lv2BundleReaderTests : IDisposable
{
    private readonly List<string> _dirs = [];

    private string NewBundle(string name = "amp.lv2")
    {
        string dir = Path.Combine(Path.GetTempPath(), "sprocket-lv2-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(dir);
        _dirs.Add(Path.GetDirectoryName(dir)!);
        return dir;
    }

    public void Dispose()
    {
        foreach (string d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    internal const string Manifest = """
        @prefix lv2:  <http://lv2plug.in/ns/lv2core#> .
        @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
        <http://example.org/amp>
            a lv2:Plugin ;
            lv2:binary <amp.so> ;
            rdfs:seeAlso <amp.ttl> .
        """;

    internal static readonly string PluginTtl = """
        @prefix doap: <http://usefulinc.com/ns/doap#> .
        @prefix foaf: <http://xmlns.com/foaf/0.1/> .
        @prefix lv2:  <http://lv2plug.in/ns/lv2core#> .
        @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
        @prefix units: <http://lv2plug.in/ns/extensions/units#> .
        @prefix urid: <http://lv2plug.in/ns/ext/urid#> .
        @prefix pprops: <http://lv2plug.in/ns/ext/port-props#> .

        <http://example.org/amp>
            a lv2:Plugin, lv2:AmplifierPlugin ;
            doap:name "Simple Amplifier" ;
            doap:license <http://opensource.org/licenses/isc> ;
            doap:maintainer [ foaf:name "Sprocket Tests" ] ;
            rdfs:comment "A gain plugin.\nSecond line." ;
            lv2:minorVersion 2 ; lv2:microVersion 5 ;
            lv2:requiredFeature urid:map ;
            lv2:optionalFeature lv2:hardRTCapable ;
            lv2:port [
                a lv2:InputPort, lv2:ControlPort ;
                lv2:index 0 ; lv2:symbol "gain" ; lv2:name "Gain" ;
                lv2:default 0.0 ; lv2:minimum -90.0 ; lv2:maximum 24.0 ;
                units:unit units:db ;
                rdfs:comment "Gain in decibels."
            ] , [
                a lv2:InputPort, lv2:ControlPort ;
                lv2:index 3 ; lv2:symbol "mode" ; lv2:name "Mode" ;
                lv2:portProperty lv2:enumeration, lv2:integer ;
                lv2:default 1 ; lv2:minimum 0 ; lv2:maximum 2 ;
                lv2:scalePoint [ rdfs:label "Soft" ; rdf:value 0 ] ,
                               [ rdfs:label "Hard" ; rdf:value 1 ] ,
                               [ rdfs:label "Clip" ; rdf:value 2 ]
            ] , [
                a lv2:InputPort, lv2:AudioPort ;
                lv2:index 1 ; lv2:symbol "in" ; lv2:name "In"
            ] , [
                a lv2:OutputPort, lv2:AudioPort ;
                lv2:index 2 ; lv2:symbol "out" ; lv2:name "Out"
            ] .
        """.Replace("rdf:value", "<http://www.w3.org/1999/02/22-rdf-syntax-ns#value>");

    [Fact]
    public void Reads_identity_ports_features_and_versions_from_the_bundle()
    {
        string bundle = NewBundle();
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), Manifest);
        File.WriteAllText(Path.Combine(bundle, "amp.ttl"), PluginTtl);

        Lv2BundleInfo info = Lv2BundleReader.Read(bundle);

        Assert.Empty(info.Warnings);
        Lv2PluginInfo plugin = Assert.Single(info.Plugins);
        Assert.Equal("http://example.org/amp", plugin.Uri);
        Assert.Equal("Simple Amplifier", plugin.Name);
        Assert.Equal("Sprocket Tests", plugin.Maker);
        Assert.Equal("http://opensource.org/licenses/isc", plugin.License);
        Assert.Equal(Path.GetFullPath(Path.Combine(bundle, "amp.so")), plugin.BinaryPath);
        Assert.Equal(Path.GetFullPath(bundle), plugin.BundlePath);
        Assert.Equal("2.5", plugin.Version);
        Assert.Equal([Lv2Ns.UridMap], plugin.RequiredFeatures);

        Assert.Equal([0, 1, 2, 3], plugin.Ports.Select(p => p.Index)); // sorted by index, not document order
        Lv2PortInfo gain = plugin.Ports[0];
        Assert.True(gain.IsControlInput);
        Assert.Equal("gain", gain.Symbol);
        Assert.Equal(-90.0, gain.Minimum);
        Assert.Equal(24.0, gain.Maximum);
        Assert.Equal(0.0, gain.Default);
        Assert.Equal(Lv2Ns.Units + "db", gain.UnitIri);
        Assert.Equal("Gain in decibels.", gain.Comment);

        Lv2PortInfo mode = plugin.Ports[3];
        Assert.Contains(Lv2Ns.Enumeration, mode.Properties);
        Assert.Equal(["Soft", "Hard", "Clip"], mode.ScalePoints.Select(s => s.Label));

        Assert.True(plugin.Ports[1].IsAudioInput);
        Assert.True(plugin.Ports[2].IsAudioOutput);
        Assert.Equal(1, plugin.AudioInputCount);
        Assert.Equal(1, plugin.AudioOutputCount);
        Assert.Empty(plugin.BlockingPorts);
    }

    [Fact]
    public void Missing_manifest_throws_FileNotFound()
    {
        string bundle = NewBundle();
        Assert.Throws<FileNotFoundException>(() => Lv2BundleReader.Read(bundle));
    }

    [Fact]
    public void Missing_seeAlso_file_and_missing_binary_are_reported_not_thrown()
    {
        string bundle = NewBundle();
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), """
            @prefix lv2:  <http://lv2plug.in/ns/lv2core#> .
            @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
            <http://example.org/one> a lv2:Plugin ; rdfs:seeAlso <gone.ttl> .
            <http://example.org/two> a lv2:Plugin ; lv2:binary <two.so> .
            """);

        Lv2BundleInfo info = Lv2BundleReader.Read(bundle);

        Assert.Single(info.Plugins); // "two" loads (with no ports); "one" has no binary
        Assert.Equal("http://example.org/two", info.Plugins[0].Uri);
        Assert.Contains(info.Warnings, w => w.Contains("gone.ttl"));
        Assert.Contains(info.Warnings, w => w.Contains("lv2:binary"));
    }

    [Fact]
    public void References_outside_the_bundle_or_to_remote_hosts_are_refused()
    {
        string bundle = NewBundle();
        string outside = Path.Combine(Path.GetDirectoryName(bundle)!, "outside.ttl");
        File.WriteAllText(outside, "");
        string outsideIri = new Uri(outside).AbsoluteUri;
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), $$"""
            @prefix lv2:  <http://lv2plug.in/ns/lv2core#> .
            @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
            <http://example.org/a> a lv2:Plugin ; lv2:binary <{{outsideIri}}> .
            <http://example.org/b> a lv2:Plugin ; lv2:binary <file://evil.example/share/x.so> .
            <http://example.org/c> a lv2:Plugin ; lv2:binary <c.so> ; rdfs:seeAlso <{{outsideIri}}> , <file://evil.example/share/x.ttl> .
            """);

        Lv2BundleInfo info = Lv2BundleReader.Read(bundle);

        Lv2PluginInfo c = Assert.Single(info.Plugins); // a and b have no in-bundle binary
        Assert.Equal("http://example.org/c", c.Uri);
        Assert.Equal(2, info.Warnings.Count(w => w.StartsWith("http://example.org/c") && w.Contains("not a file inside the bundle")));
        Assert.Contains(info.Warnings, w => w.StartsWith("http://example.org/a"));
        Assert.Contains(info.Warnings, w => w.StartsWith("http://example.org/b"));

        Assert.Null(Lv2BundleReader.LocalPath("file://evil.example/share/x.so", bundle));
        Assert.Null(Lv2BundleReader.LocalPath(outsideIri, bundle));
        Assert.NotNull(Lv2BundleReader.LocalPath(new Uri(Path.Combine(bundle, "c.so")).AbsoluteUri, bundle));
    }

    [Fact]
    public void Non_dense_or_huge_port_indices_skip_the_plugin()
    {
        string bundle = NewBundle();
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), """
            @prefix lv2:  <http://lv2plug.in/ns/lv2core#> .
            <http://example.org/gap> a lv2:Plugin ; lv2:binary <a.so> ;
                lv2:port [ a lv2:InputPort, lv2:AudioPort ; lv2:index 0 ; lv2:symbol "in" ] ,
                         [ a lv2:OutputPort, lv2:AudioPort ; lv2:index 5 ; lv2:symbol "out" ] .
            <http://example.org/huge> a lv2:Plugin ; lv2:binary <a.so> ;
                lv2:port [ a lv2:InputPort, lv2:AudioPort ; lv2:index 2000000000 ; lv2:symbol "in" ] .
            <http://example.org/ok> a lv2:Plugin ; lv2:binary <a.so> ;
                lv2:port [ a lv2:InputPort, lv2:AudioPort ; lv2:index 1 ; lv2:symbol "in" ] ,
                         [ a lv2:OutputPort, lv2:AudioPort ; lv2:index 0 ; lv2:symbol "out" ] .
            """);

        Lv2BundleInfo info = Lv2BundleReader.Read(bundle);

        Lv2PluginInfo ok = Assert.Single(info.Plugins);
        Assert.Equal("http://example.org/ok", ok.Uri);
        Assert.Contains(info.Warnings, w => w.StartsWith("http://example.org/gap") && w.Contains("dense"));
        Assert.Contains(info.Warnings, w => w.StartsWith("http://example.org/huge"));
    }

    [Fact]
    public void Atom_port_without_connectionOptional_is_a_blocking_port_but_with_it_is_not()
    {
        var atomTypes = new HashSet<string> { Lv2Ns.InputPort, "http://lv2plug.in/ns/ext/atom#AtomPort" };
        var required = new Lv2PortInfo(4, "control", "Control", atomTypes, new HashSet<string>(), null, null, null, null, []);
        var optional = required with { Properties = new HashSet<string> { Lv2Ns.ConnectionOptional } };

        Assert.False(required.IsSupportedKind);
        Assert.False(required.IsConnectionOptional);
        Assert.True(optional.IsConnectionOptional);

        Lv2PluginInfo plugin = new("u", "n", "", "", "", "b.so", "/bundle", 0, 0, [], [required]);
        Assert.Single(plugin.BlockingPorts);
        Assert.Empty((plugin with { Ports = [optional] }).BlockingPorts);
    }
}
