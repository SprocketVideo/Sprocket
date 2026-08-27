using System.Globalization;
using System.Text;

namespace Sprocket.Plugins.Lv2;

/// <summary>The kind of an RDF term produced by <see cref="TurtleReader"/>.</summary>
internal enum TurtleNodeKind
{
    /// <summary>An IRI (absolute after base/prefix resolution).</summary>
    Iri,

    /// <summary>A blank node, identified by a reader-assigned label unique within the graph.</summary>
    Blank,

    /// <summary>A literal — its lexical value plus an optional datatype IRI (language tags are dropped).</summary>
    Literal,
}

/// <summary>One RDF term.</summary>
internal readonly record struct TurtleNode(TurtleNodeKind Kind, string Value, string? Datatype = null)
{
    public static TurtleNode Iri(string iri) => new(TurtleNodeKind.Iri, iri);
    public static TurtleNode Blank(string label) => new(TurtleNodeKind.Blank, label);
    public static TurtleNode Literal(string value, string? datatype = null) => new(TurtleNodeKind.Literal, value, datatype);

    public bool IsIri => Kind == TurtleNodeKind.Iri;
    public bool IsLiteral => Kind == TurtleNodeKind.Literal;

    /// <summary>The literal parsed as an invariant-culture number, if it is one (any numeric datatype or none).</summary>
    public double? AsNumber() =>
        Kind == TurtleNodeKind.Literal
        && double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

    public override string ToString() => Kind switch
    {
        TurtleNodeKind.Iri => $"<{Value}>",
        TurtleNodeKind.Blank => $"_:{Value}",
        _ => $"\"{Value}\"",
    };
}

/// <summary>One RDF triple.</summary>
internal sealed record TurtleTriple(TurtleNode Subject, TurtleNode Predicate, TurtleNode Object);

/// <summary>
/// A small in-memory RDF graph (the triples of one or more Turtle files) with the lookups the LV2 bundle reader
/// needs: the objects of a (subject, predicate), the subjects typed as some class, and RDF collections.
/// </summary>
internal sealed class TurtleGraph
{
    private readonly List<TurtleTriple> _triples = [];
    private readonly Dictionary<TurtleNode, List<TurtleTriple>> _bySubject = [];

    /// <summary>Every triple, in document order.</summary>
    public IReadOnlyList<TurtleTriple> Triples => _triples;

    public void Add(TurtleTriple triple)
    {
        _triples.Add(triple);
        if (!_bySubject.TryGetValue(triple.Subject, out List<TurtleTriple>? list))
            _bySubject[triple.Subject] = list = [];
        list.Add(triple);
    }

    /// <summary>Merges another graph's triples into this one (blank-node labels are assumed disjoint — the reader
    /// prefixes them per document).</summary>
    public void AddRange(TurtleGraph other)
    {
        foreach (TurtleTriple t in other._triples)
            Add(t);
    }

    /// <summary>Every triple with <paramref name="subject"/> as its subject (indexed lookup).</summary>
    public IEnumerable<TurtleTriple> TriplesOf(TurtleNode subject) =>
        _bySubject.TryGetValue(subject, out List<TurtleTriple>? list) ? list : [];

    /// <summary>All objects of (<paramref name="subject"/>, <paramref name="predicateIri"/>), in order.</summary>
    public IEnumerable<TurtleNode> Objects(TurtleNode subject, string predicateIri)
    {
        if (!_bySubject.TryGetValue(subject, out List<TurtleTriple>? list))
            yield break;
        foreach (TurtleTriple t in list)
            if (t.Predicate.IsIri && t.Predicate.Value == predicateIri)
                yield return t.Object;
    }

    /// <summary>The first object of (<paramref name="subject"/>, <paramref name="predicateIri"/>), or null.</summary>
    public TurtleNode? FirstObject(TurtleNode subject, string predicateIri)
    {
        foreach (TurtleNode o in Objects(subject, predicateIri))
            return o;
        return null;
    }

    /// <summary>The first literal object's lexical value, or null.</summary>
    public string? FirstString(TurtleNode subject, string predicateIri)
    {
        foreach (TurtleNode o in Objects(subject, predicateIri))
            if (o.IsLiteral)
                return o.Value;
        return null;
    }

    /// <summary>The first numeric literal object, or null.</summary>
    public double? FirstNumber(TurtleNode subject, string predicateIri)
    {
        foreach (TurtleNode o in Objects(subject, predicateIri))
            if (o.AsNumber() is double d)
                return d;
        return null;
    }

    /// <summary>The subjects that have (<paramref name="predicateIri"/>, <paramref name="obj"/>), in order, deduplicated.</summary>
    public IEnumerable<TurtleNode> SubjectsWith(string predicateIri, TurtleNode obj)
    {
        var seen = new HashSet<TurtleNode>();
        foreach (TurtleTriple t in _triples)
            if (t.Predicate.IsIri && t.Predicate.Value == predicateIri && t.Object == obj && seen.Add(t.Subject))
                yield return t.Subject;
    }

    /// <summary>Walks an RDF collection (<c>rdf:first</c>/<c>rdf:rest</c> chain) starting at <paramref name="head"/>.</summary>
    public IEnumerable<TurtleNode> Collection(TurtleNode head)
    {
        var visited = new HashSet<TurtleNode>();
        TurtleNode current = head;
        while (!(current.IsIri && current.Value == TurtleReader.RdfNil) && visited.Add(current))
        {
            if (FirstObject(current, TurtleReader.RdfFirst) is TurtleNode first)
                yield return first;
            if (FirstObject(current, TurtleReader.RdfRest) is not TurtleNode rest)
                yield break;
            current = rest;
        }
    }
}

/// <summary>
/// A minimal managed Turtle (RDF 1.1) reader — enough of the grammar to read LV2 bundle metadata
/// (<c>manifest.ttl</c> + plugin <c>.ttl</c>): prefix/base directives (<c>@prefix</c>/<c>PREFIX</c>), IRIs,
/// prefixed names, the <c>a</c> keyword, blank nodes (<c>_:label</c> and <c>[ … ]</c> property lists),
/// collections (<c>( … )</c>, expanded to <c>rdf:first</c>/<c>rdf:rest</c>), predicate/object lists with
/// <c>;</c> and <c>,</c>, and string/numeric/boolean literals with datatypes and language tags. Chosen over a
/// bundled <c>lilv</c> per the step-59 plan ("prefer managed if the subset stays small"): no native dependency,
/// and it is fully unit-testable. Malformed input throws <see cref="FormatException"/> with a line number.
/// </summary>
internal sealed class TurtleReader
{
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string RdfType = Rdf + "type";
    public const string RdfFirst = Rdf + "first";
    public const string RdfRest = Rdf + "rest";
    public const string RdfNil = Rdf + "nil";
    public const string XsdBoolean = "http://www.w3.org/2001/XMLSchema#boolean";
    public const string XsdInteger = "http://www.w3.org/2001/XMLSchema#integer";
    public const string XsdDecimal = "http://www.w3.org/2001/XMLSchema#decimal";
    public const string XsdDouble = "http://www.w3.org/2001/XMLSchema#double";

    private readonly string _text;
    private readonly string _blankPrefix;
    private readonly Dictionary<string, string> _prefixes = new(StringComparer.Ordinal);
    private readonly TurtleGraph _graph = new();
    private string _base;
    private int _pos;
    private int _line = 1;
    private int _blankCounter;
    private int _depth;

    /// <summary>The maximum nesting of <c>[ … ]</c> / <c>( … )</c>. Real LV2 metadata nests two or three levels; a
    /// hostile file of unclosed brackets would otherwise recurse the parser into an uncatchable stack overflow
    /// during startup discovery.</summary>
    private const int MaxDepth = 64;

    private TurtleReader(string text, string baseIri, string blankPrefix)
    {
        _text = text;
        _base = baseIri;
        _blankPrefix = blankPrefix;
    }

    /// <summary>
    /// Parses <paramref name="text"/>. Relative IRIs resolve against <paramref name="baseIri"/> (an absolute IRI,
    /// e.g. the bundle directory as a <c>file:</c> URI with a trailing slash). <paramref name="blankPrefix"/> keeps
    /// blank-node labels unique when several documents are merged into one graph.
    /// </summary>
    public static TurtleGraph Parse(string text, string baseIri, string blankPrefix = "b")
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new TurtleReader(text, baseIri, blankPrefix);
        reader.ParseDocument();
        return reader._graph;
    }

    // ── grammar ──────────────────────────────────────────────────────────────────────────────────────

    private void ParseDocument()
    {
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _text.Length)
                return;

            if (Peek() == '@')
            {
                ParseAtDirective();
                continue;
            }
            if (MatchKeywordIgnoreCase("PREFIX"))
            {
                ParsePrefixBody(requireDot: false);
                continue;
            }
            if (MatchKeywordIgnoreCase("BASE"))
            {
                ParseBaseBody(requireDot: false);
                continue;
            }

            ParseTriples();
        }
    }

    private void ParseAtDirective()
    {
        Expect('@');
        string word = ReadWord();
        switch (word)
        {
            case "prefix":
                ParsePrefixBody(requireDot: true);
                break;
            case "base":
                ParseBaseBody(requireDot: true);
                break;
            default:
                throw Error($"Unknown directive '@{word}'.");
        }
    }

    private void ParsePrefixBody(bool requireDot)
    {
        SkipWhitespaceAndComments();
        string prefix = ReadPrefixLabel(); // "foo:" → "foo", ":" → ""
        SkipWhitespaceAndComments();
        string iri = ReadIriRef();
        _prefixes[prefix] = iri;
        if (requireDot)
        {
            SkipWhitespaceAndComments();
            Expect('.');
        }
    }

    private void ParseBaseBody(bool requireDot)
    {
        SkipWhitespaceAndComments();
        _base = ReadIriRef();
        if (requireDot)
        {
            SkipWhitespaceAndComments();
            Expect('.');
        }
    }

    private void ParseTriples()
    {
        TurtleNode subject;
        bool anonymousWithBody = false;
        if (Peek() == '[')
        {
            // "[ p o ; … ] ." (a subject that is itself a blank node property list) or a bare "[]".
            subject = ParseBlankNodePropertyList();
            anonymousWithBody = true;
        }
        else
        {
            subject = ParseSubject();
        }

        SkipWhitespaceAndComments();
        if (anonymousWithBody && Peek() == '.')
        {
            Advance();
            return;
        }

        ParsePredicateObjectList(subject);
        SkipWhitespaceAndComments();
        Expect('.');
    }

    private TurtleNode ParseSubject()
    {
        SkipWhitespaceAndComments();
        char c = Peek();
        if (c == '<') return TurtleNode.Iri(ResolveIri(ReadIriRef()));
        if (c == '(') return ParseCollection();
        if (c == '_' && PeekAt(1) == ':') return ReadBlankNodeLabel();
        return ParsePrefixedName();
    }

    private void ParsePredicateObjectList(TurtleNode subject)
    {
        while (true)
        {
            SkipWhitespaceAndComments();
            if (Peek() is '.' or ']')
                return; // trailing ';' before the terminator is legal
            TurtleNode predicate = ParsePredicate();
            ParseObjectList(subject, predicate);
            SkipWhitespaceAndComments();
            if (Peek() != ';')
                return;
            while (Peek() == ';') // "; ;" sequences are legal
            {
                Advance();
                SkipWhitespaceAndComments();
            }
        }
    }

    private TurtleNode ParsePredicate()
    {
        SkipWhitespaceAndComments();
        if (Peek() == '<')
            return TurtleNode.Iri(ResolveIri(ReadIriRef()));
        if (Peek() == 'a' && IsDelimiter(PeekAt(1)))
        {
            Advance();
            return TurtleNode.Iri(RdfType);
        }
        return ParsePrefixedName();
    }

    private void ParseObjectList(TurtleNode subject, TurtleNode predicate)
    {
        while (true)
        {
            TurtleNode obj = ParseObject();
            _graph.Add(new TurtleTriple(subject, predicate, obj));
            SkipWhitespaceAndComments();
            if (Peek() != ',')
                return;
            Advance();
        }
    }

    private TurtleNode ParseObject()
    {
        SkipWhitespaceAndComments();
        if (_pos >= _text.Length)
            throw Error("Unexpected end of input; expected an object.");

        char c = Peek();
        switch (c)
        {
            case '<':
                return TurtleNode.Iri(ResolveIri(ReadIriRef()));
            case '[':
                return ParseBlankNodePropertyList();
            case '(':
                return ParseCollection();
            case '"':
            case '\'':
                return ParseStringLiteral();
            case '_' when PeekAt(1) == ':':
                return ReadBlankNodeLabel();
        }

        if (c is '+' or '-' or '.' || char.IsAsciiDigit(c))
            return ParseNumericLiteral();

        if (MatchKeyword("true"))
            return TurtleNode.Literal("true", XsdBoolean);
        if (MatchKeyword("false"))
            return TurtleNode.Literal("false", XsdBoolean);

        return ParsePrefixedName();
    }

    private TurtleNode ParseBlankNodePropertyList()
    {
        Expect('[');
        EnterNesting();
        TurtleNode node = NewBlank();
        SkipWhitespaceAndComments();
        if (Peek() == ']')
        {
            Advance();
            _depth--;
            return node;
        }
        ParsePredicateObjectList(node);
        SkipWhitespaceAndComments();
        Expect(']');
        _depth--;
        return node;
    }

    private void EnterNesting()
    {
        if (++_depth > MaxDepth)
            throw Error($"Nesting deeper than {MaxDepth} levels.");
    }

    private TurtleNode ParseCollection()
    {
        Expect('(');
        EnterNesting();
        var items = new List<TurtleNode>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (Peek() == ')')
            {
                Advance();
                break;
            }
            items.Add(ParseObject());
        }
        _depth--;

        if (items.Count == 0)
            return TurtleNode.Iri(RdfNil);

        TurtleNode head = NewBlank();
        TurtleNode current = head;
        for (int i = 0; i < items.Count; i++)
        {
            _graph.Add(new TurtleTriple(current, TurtleNode.Iri(RdfFirst), items[i]));
            TurtleNode rest = i == items.Count - 1 ? TurtleNode.Iri(RdfNil) : NewBlank();
            _graph.Add(new TurtleTriple(current, TurtleNode.Iri(RdfRest), rest));
            current = rest;
        }
        return head;
    }

    private TurtleNode ParseStringLiteral()
    {
        char quote = Peek();
        string value;
        if (PeekAt(1) == quote && PeekAt(2) == quote)
        {
            _pos += 3;
            value = ReadUntilTripleQuote(quote);
        }
        else
        {
            Advance();
            value = ReadUntilQuote(quote);
        }

        // Optional language tag or datatype.
        if (Peek() == '@')
        {
            Advance();
            while (_pos < _text.Length && (char.IsAsciiLetterOrDigit(Peek()) || Peek() == '-'))
                Advance();
            return TurtleNode.Literal(value);
        }
        if (Peek() == '^' && PeekAt(1) == '^')
        {
            _pos += 2;
            TurtleNode datatype = Peek() == '<' ? TurtleNode.Iri(ResolveIri(ReadIriRef())) : ParsePrefixedName();
            return TurtleNode.Literal(value, datatype.Value);
        }
        return TurtleNode.Literal(value);
    }

    private TurtleNode ParseNumericLiteral()
    {
        int start = _pos;
        if (Peek() is '+' or '-')
            Advance();
        while (char.IsAsciiDigit(Peek()))
            Advance();
        bool isDecimal = false, isDouble = false;
        if (Peek() == '.' && char.IsAsciiDigit(PeekAt(1)))
        {
            isDecimal = true;
            Advance();
            while (char.IsAsciiDigit(Peek()))
                Advance();
        }
        if (Peek() is 'e' or 'E')
        {
            isDouble = true;
            Advance();
            if (Peek() is '+' or '-')
                Advance();
            while (char.IsAsciiDigit(Peek()))
                Advance();
        }
        string lexical = _text[start.._pos];
        if (lexical.Length == 0 || lexical is "+" or "-" or ".")
            throw Error("Malformed numeric literal.");
        string datatype = isDouble ? XsdDouble : isDecimal ? XsdDecimal : XsdInteger;
        return TurtleNode.Literal(lexical, datatype);
    }

    private TurtleNode ParsePrefixedName()
    {
        int start = _pos;
        while (_pos < _text.Length && IsPrefixChar(Peek()))
            Advance();
        int colon = _text.IndexOf(':', start, _pos - start);
        if (colon < 0)
            throw Error($"Expected a prefixed name or IRI at '{Snippet(start)}'.");

        // The local part: everything up to a delimiter; a trailing '.' belongs to the statement, not the name.
        while (_pos < _text.Length && IsLocalNameChar(Peek()))
            Advance();
        while (_pos > colon + 1 && _text[_pos - 1] == '.')
            _pos--;

        string prefix = _text[start..colon];
        string local = _text[(colon + 1).._pos];
        if (!_prefixes.TryGetValue(prefix, out string? ns))
            throw Error($"Undefined prefix '{prefix}:'.");
        return TurtleNode.Iri(ns + local);
    }

    // ── lexical helpers ──────────────────────────────────────────────────────────────────────────────

    private string ReadIriRef()
    {
        Expect('<');
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length)
                throw Error("Unterminated IRI.");
            char c = _text[_pos++];
            if (c == '>')
                break;
            if (c == '\\')
                sb.Append(ReadEscape());
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private string ReadPrefixLabel()
    {
        int start = _pos;
        while (_pos < _text.Length && Peek() != ':' && !char.IsWhiteSpace(Peek()))
            Advance();
        Expect(':');
        return _text[start..(_pos - 1)];
    }

    private TurtleNode ReadBlankNodeLabel()
    {
        _pos += 2; // "_:"
        int start = _pos;
        while (_pos < _text.Length && IsLocalNameChar(Peek()))
            Advance();
        while (_pos > start && _text[_pos - 1] == '.')
            _pos--;
        if (_pos == start)
            throw Error("Empty blank node label.");
        return TurtleNode.Blank(_blankPrefix + ":" + _text[start.._pos]);
    }

    private string ReadUntilQuote(char quote)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length)
                throw Error("Unterminated string literal.");
            char c = _text[_pos++];
            if (c == quote)
                return sb.ToString();
            if (c == '\n')
                throw Error("Newline in single-quoted string literal.");
            sb.Append(c == '\\' ? ReadEscape() : c);
        }
    }

    private string ReadUntilTripleQuote(char quote)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length)
                throw Error("Unterminated long string literal.");
            if (_text[_pos] == quote && PeekAt(1) == quote && PeekAt(2) == quote)
            {
                _pos += 3;
                return sb.ToString();
            }
            char c = _text[_pos++];
            if (c == '\n')
                _line++;
            sb.Append(c == '\\' ? ReadEscape() : c);
        }
    }

    private string ReadEscape()
    {
        if (_pos >= _text.Length)
            throw Error("Dangling escape.");
        char e = _text[_pos++];
        switch (e)
        {
            case 't': return "\t";
            case 'n': return "\n";
            case 'r': return "\r";
            case 'b': return "\b";
            case 'f': return "\f";
            case '"': return "\"";
            case '\'': return "'";
            case '\\': return "\\";
            case 'u': return ReadUnicodeEscape(4);
            case 'U': return ReadUnicodeEscape(8);
            default: throw Error($"Unknown escape '\\{e}'.");
        }
    }

    private string ReadUnicodeEscape(int digits)
    {
        if (_pos + digits > _text.Length)
            throw Error("Truncated unicode escape.");
        string hex = _text.Substring(_pos, digits);
        _pos += digits;
        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp)
            || cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
            throw Error("Malformed unicode escape.");
        return char.ConvertFromUtf32(cp);
    }

    private string ReadWord()
    {
        int start = _pos;
        while (_pos < _text.Length && char.IsAsciiLetter(Peek()))
            Advance();
        return _text[start.._pos];
    }

    private bool MatchKeyword(string keyword)
    {
        if (string.CompareOrdinal(_text, _pos, keyword, 0, keyword.Length) != 0 || !IsDelimiter(PeekAt(keyword.Length)))
            return false;
        _pos += keyword.Length;
        return true;
    }

    private bool MatchKeywordIgnoreCase(string keyword)
    {
        if (_pos + keyword.Length > _text.Length
            || string.Compare(_text, _pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0
            || !char.IsWhiteSpace(PeekAt(keyword.Length)))
            return false;
        _pos += keyword.Length;
        return true;
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (c == '\n')
            {
                _line++;
                _pos++;
            }
            else if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '#')
            {
                while (_pos < _text.Length && _text[_pos] != '\n')
                    _pos++;
            }
            else
            {
                return;
            }
        }
    }

    private void Expect(char c)
    {
        if (_pos >= _text.Length || _text[_pos] != c)
            throw Error($"Expected '{c}' but found '{(_pos < _text.Length ? _text[_pos].ToString() : "end of input")}'.");
        _pos++;
    }

    private string ResolveIri(string iri)
    {
        if (iri.Length == 0)
            return _base;
        if (IsAbsoluteIri(iri))
            return iri;
        if (Uri.TryCreate(_base, UriKind.Absolute, out Uri? baseUri) && Uri.TryCreate(baseUri, iri, out Uri? resolved))
            return resolved.ToString();
        return _base + iri; // a non-URI base (unit tests / odd bundles): plain concatenation
    }

    private static bool IsAbsoluteIri(string iri)
    {
        int colon = iri.IndexOf(':');
        if (colon <= 0)
            return false;
        for (int i = 0; i < colon; i++)
        {
            char c = iri[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
                return false;
        }
        return char.IsAsciiLetter(iri[0]);
    }

    private TurtleNode NewBlank() => TurtleNode.Blank($"{_blankPrefix}:anon{_blankCounter++}");

    private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';
    private char PeekAt(int offset) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';
    private void Advance() => _pos++;

    private static bool IsDelimiter(char c) => c == '\0' || char.IsWhiteSpace(c) || c is '<' or '[' or '(' or '"' or '\'' or ';' or ',' or '.' or ']' or ')' or '#';
    private static bool IsPrefixChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' || c == ':';
    private static bool IsLocalNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '%' or '·' || c > '';

    private string Snippet(int start) => _text.Substring(start, Math.Min(20, _text.Length - start));
    private FormatException Error(string message) => new($"Turtle parse error (line {_line}): {message}");
}
