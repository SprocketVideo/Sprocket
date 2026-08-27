using Sprocket.Plugins.Lv2;
using Xunit;

namespace Sprocket.Plugins.Tests.Lv2;

/// <summary>The managed Turtle reader (PLAN.md step 59) against the constructs LV2 bundles actually use.</summary>
public sealed class TurtleReaderTests
{
    private const string Base = "file:///bundle/";

    [Fact]
    public void Parses_prefixes_the_a_keyword_and_predicate_object_lists()
    {
        const string ttl = """
            @prefix lv2: <http://lv2plug.in/ns/lv2core#> .
            @prefix doap: <http://usefulinc.com/ns/doap#> .
            # a comment
            <http://example.org/amp>
                a lv2:Plugin, lv2:AmplifierPlugin ;
                doap:name "Simple Amp" ;
                lv2:binary <amp.so> .
            """;

        TurtleGraph g = TurtleReader.Parse(ttl, Base);
        TurtleNode amp = TurtleNode.Iri("http://example.org/amp");

        Assert.Equal(2, g.Objects(amp, TurtleReader.RdfType).Count());
        Assert.Contains(TurtleNode.Iri(Lv2Ns.Plugin), g.Objects(amp, TurtleReader.RdfType));
        Assert.Equal("Simple Amp", g.FirstString(amp, Lv2Ns.DoapName));
        Assert.Equal("file:///bundle/amp.so", g.FirstObject(amp, Lv2Ns.Binary)?.Value); // relative IRI resolved against the base
    }

    [Fact]
    public void Parses_blank_node_property_lists_and_numeric_literals()
    {
        const string ttl = """
            @prefix lv2: <http://lv2plug.in/ns/lv2core#> .
            <http://example.org/amp> lv2:port [
                a lv2:InputPort, lv2:ControlPort ;
                lv2:index 0 ;
                lv2:symbol "gain" ;
                lv2:default 0.0 ; lv2:minimum -90.0 ; lv2:maximum 24.0 ;
            ] , [
                a lv2:InputPort, lv2:AudioPort ;
                lv2:index 1 ;
                lv2:symbol "in" ;
            ] .
            """;

        TurtleGraph g = TurtleReader.Parse(ttl, Base);
        TurtleNode amp = TurtleNode.Iri("http://example.org/amp");
        TurtleNode[] ports = g.Objects(amp, Lv2Ns.Port).ToArray();

        Assert.Equal(2, ports.Length);
        Assert.All(ports, p => Assert.Equal(TurtleNodeKind.Blank, p.Kind));
        Assert.Equal(0, g.FirstNumber(ports[0], Lv2Ns.Index));
        Assert.Equal(-90.0, g.FirstNumber(ports[0], Lv2Ns.Minimum));
        Assert.Equal(24.0, g.FirstNumber(ports[0], Lv2Ns.Maximum));
        Assert.Equal("in", g.FirstString(ports[1], Lv2Ns.Symbol));
    }

    [Fact]
    public void Parses_sparql_style_prefix_base_and_typed_or_tagged_literals()
    {
        const string ttl = """
            PREFIX ex: <http://example.org/>
            BASE <http://base.example/>
            ex:thing ex:label "hello"@en ;
                     ex:value "1.5"^^<http://www.w3.org/2001/XMLSchema#float> ;
                     ex:flag true ;
                     ex:exp 1e3 ;
                     ex:rel <relative> .
            """;

        TurtleGraph g = TurtleReader.Parse(ttl, Base);
        TurtleNode thing = TurtleNode.Iri("http://example.org/thing");

        Assert.Equal("hello", g.FirstString(thing, "http://example.org/label"));
        Assert.Equal(1.5, g.FirstNumber(thing, "http://example.org/value"));
        Assert.Equal("true", g.FirstString(thing, "http://example.org/flag"));
        Assert.Equal(1000.0, g.FirstNumber(thing, "http://example.org/exp"));
        Assert.Equal("http://base.example/relative", g.FirstObject(thing, "http://example.org/rel")?.Value);
    }

    [Fact]
    public void Parses_collections_into_rdf_lists_and_long_strings()
    {
        string ttl = "@prefix ex: <http://example.org/> .\n"
            + "ex:s ex:list ( 1 2 3 ) ;\n"
            + "     ex:doc \"\"\"multi\nline\"\"\" .\n";

        TurtleGraph g = TurtleReader.Parse(ttl, Base);
        TurtleNode s = TurtleNode.Iri("http://example.org/s");
        TurtleNode head = g.FirstObject(s, "http://example.org/list")!.Value;

        double[] items = g.Collection(head).Select(n => n.AsNumber()!.Value).ToArray();
        Assert.Equal([1.0, 2.0, 3.0], items);
        Assert.Equal("multi\nline", g.FirstString(s, "http://example.org/doc"));
    }

    [Fact]
    public void Blank_node_labels_and_escapes_round_trip()
    {
        const string ttl = """
            @prefix ex: <http://example.org/> .
            _:x ex:name "tab\there \"quoted\" é" .
            ex:s ex:ref _:x .
            """;

        TurtleGraph g = TurtleReader.Parse(ttl, Base, "doc");
        TurtleNode x = g.FirstObject(TurtleNode.Iri("http://example.org/s"), "http://example.org/ref")!.Value;

        Assert.Equal(TurtleNodeKind.Blank, x.Kind);
        Assert.Equal("tab\there \"quoted\" é", g.FirstString(x, "http://example.org/name"));
    }

    [Fact]
    public void SubjectsWith_finds_every_typed_subject_once()
    {
        const string ttl = """
            @prefix lv2: <http://lv2plug.in/ns/lv2core#> .
            <http://example.org/a> a lv2:Plugin . <http://example.org/b> a lv2:Plugin .
            <http://example.org/a> a lv2:Plugin .
            """;

        TurtleGraph g = TurtleReader.Parse(ttl, Base);
        string[] subjects = g.SubjectsWith(TurtleReader.RdfType, TurtleNode.Iri(Lv2Ns.Plugin)).Select(n => n.Value).ToArray();

        Assert.Equal(["http://example.org/a", "http://example.org/b"], subjects);
    }

    [Theory]
    [InlineData("<http://example.org/s> <http://example.org/p> .")]        // missing object
    [InlineData("@prefix ex: <http://example.org/> . ex:s undefined:p 1 .")] // undefined prefix
    [InlineData("<http://example.org/s> <http://example.org/p> \"open .")]  // unterminated string
    [InlineData("@bogus <x> .")]                                            // unknown directive
    public void Malformed_input_throws_a_FormatException_with_a_line_number(string ttl)
    {
        var ex = Assert.Throws<FormatException>(() => TurtleReader.Parse(ttl, Base));
        Assert.Contains("line", ex.Message);
    }

    [Fact]
    public void Deeply_nested_blank_nodes_and_collections_are_rejected_not_stack_overflowed()
    {
        string brackets = "<http://example.org/s> <http://example.org/p> " + new string('[', 10_000);
        Assert.Throws<FormatException>(() => TurtleReader.Parse(brackets, Base));
        string parens = "<http://example.org/s> <http://example.org/p> " + new string('(', 10_000);
        Assert.Throws<FormatException>(() => TurtleReader.Parse(parens, Base));
    }

    [Theory]
    [InlineData("\\uD800")]     // lone surrogate
    [InlineData("\\U7FFFFFFF")] // beyond U+10FFFF
    [InlineData("\\u 12 ")]     // whitespace is not hex
    public void Invalid_unicode_escapes_are_FormatExceptions(string escape)
    {
        string ttl = "<http://example.org/s> <http://example.org/p> \"" + escape + "\" .";
        Assert.Throws<FormatException>(() => TurtleReader.Parse(ttl, Base));
    }

    [Fact]
    public void Empty_document_yields_an_empty_graph() =>
        Assert.Empty(TurtleReader.Parse("# nothing here\n", Base).Triples);
}
