using System.Globalization;

namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// Parses OGC Well-Known Text into a <see cref="SpatialGeometry"/>, raising
/// the same 24xxx failures real SQL Server's spatial library raises — the
/// invalid-label, number-expected, expected-token, truncated-input,
/// trailing-content, ring-shape and latitude-domain checks all fire where
/// real fires them.
/// </summary>
/// <remarks>
/// <para>Labels are matched case-insensitively as a prefix rather than as a
/// greedy word, which is what makes <c>POINTX(1 2)</c> report a missing
/// <c>(</c> (Msg 24142) instead of an unknown label — real behaves the same
/// way.</para>
/// <para>The curved kinds (<c>CIRCULARSTRING</c> / <c>COMPOUNDCURVE</c> /
/// <c>CURVEPOLYGON</c>) and <c>FULLGLOBE</c> are recognized labels: real
/// accepts them, so treating them as unknown would be the wrong error. They
/// raise <see cref="NotSupportedException"/> instead, except
/// <c>FULLGLOBE</c> on <c>geometry</c>, which real itself rejects with
/// 24303.</para>
/// </remarks>
internal sealed class SpatialWktReader
{
    private readonly string text;
    private readonly bool isGeography;
    private int position;

    private SpatialWktReader(string text, bool isGeography)
    {
        this.text = text;
        this.isGeography = isGeography;
    }

    /// <summary>
    /// Reads a complete WKT instance.
    /// </summary>
    /// <param name="text">The well-known text.</param>
    /// <param name="srid">Spatial reference id to stamp on the result.</param>
    /// <param name="isGeography">True to apply geography's latitude-domain check and treat <c>FULLGLOBE</c> as legal.</param>
    /// <param name="requiredLabel">
    /// When non-null, the single label this call accepts — the
    /// <c>ST<i>Kind</i>FromText</c> constructors pass their own kind and real
    /// reports Msg 24142 for anything else.
    /// </param>
    public static SpatialGeometry Read(string text, int srid, bool isGeography, string? requiredLabel = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new SpatialWktReader(text, isGeography);
        reader.SkipWhitespace();
        if (reader.position >= text.Length)
            throw SimulatedSqlException.SpatialWktEmpty(isGeography);
        if (requiredLabel is not null)
            reader.ExpectLiteral(requiredLabel);
        var root = reader.ReadTaggedText(requiredLabel);
        reader.SkipWhitespace();
        return reader.position < text.Length
            ? throw SimulatedSqlException.SpatialWktNotValid(isGeography)
            : new SpatialGeometry(srid, root);
    }

    /// <summary>Label spellings, longest-first so a prefix match never stops short of the real label.</summary>
    private static readonly (string Label, SpatialShapeType Type)[] Labels =
    [
        ("GEOMETRYCOLLECTION", SpatialShapeType.GeometryCollection),
        ("MULTILINESTRING", SpatialShapeType.MultiLineString),
        ("CIRCULARSTRING", SpatialShapeType.CircularString),
        ("COMPOUNDCURVE", SpatialShapeType.CompoundCurve),
        ("CURVEPOLYGON", SpatialShapeType.CurvePolygon),
        ("MULTIPOLYGON", SpatialShapeType.MultiPolygon),
        ("MULTIPOINT", SpatialShapeType.MultiPoint),
        ("LINESTRING", SpatialShapeType.LineString),
        ("FULLGLOBE", SpatialShapeType.FullGlobe),
        ("POLYGON", SpatialShapeType.Polygon),
        ("POINT", SpatialShapeType.Point),
    ];

    /// <summary>Label spelling used in the invalid-OpenGis-type error, which is Pascal-cased where the WKT label is upper.</summary>
    private static string OpenGisName(SpatialShapeType type) => type switch
    {
        SpatialShapeType.CircularString => "CircularString",
        SpatialShapeType.CompoundCurve => "CompoundCurve",
        SpatialShapeType.CurvePolygon => "CurvePolygon",
        _ => "FullGlobe",
    };

    private SpatialShape ReadTaggedText(string? matchedLabel)
    {
        SkipWhitespace();
        var type = matchedLabel is null ? ReadLabel() : LabelType(matchedLabel);

        // FULLGLOBE is a geography-only kind; real rejects it outright on the
        // planar type rather than reporting it as an unknown label.
        if (type == SpatialShapeType.FullGlobe && !this.isGeography)
            throw SimulatedSqlException.SpatialInvalidOpenGisType(this.isGeography, OpenGisName(type));
        if (type >= SpatialShapeType.CircularString)
            throw new NotSupportedException($"The spatial shape {OpenGisName(type)} is not modeled.");

        SkipWhitespace();
        return TryConsumeKeyword("EMPTY") ? SpatialShape.Empty(type) : type switch
        {
            SpatialShapeType.Point => SpatialShape.Leaf(type, [ReadParenthesizedPoint()]),
            SpatialShapeType.LineString => SpatialShape.Leaf(type, [ReadLineStringBody()]),
            SpatialShapeType.Polygon => SpatialShape.Leaf(type, ReadPolygonBody()),
            SpatialShapeType.MultiPoint => SpatialShape.Collection(type, ReadMultiPointBody()),
            SpatialShapeType.MultiLineString => SpatialShape.Collection(type, ReadRepeated(static r => SpatialShape.Leaf(SpatialShapeType.LineString, [r.ReadLineStringBody()]))),
            SpatialShapeType.MultiPolygon => SpatialShape.Collection(type, ReadRepeated(static r => SpatialShape.Leaf(SpatialShapeType.Polygon, r.ReadPolygonBody()))),
            _ => SpatialShape.Collection(type, ReadRepeated(static r => r.ReadTaggedText(null))),
        };
    }

    private SpatialShapeType LabelType(string label)
    {
        foreach (var (name, type) in Labels)
        {
            if (name.Equals(label, StringComparison.OrdinalIgnoreCase))
                return type;
        }
        throw SimulatedSqlException.SpatialInvalidLabel(this.isGeography, label);
    }

    private SpatialShapeType ReadLabel()
    {
        foreach (var (name, type) in Labels)
        {
            if (MatchesAt(name))
            {
                this.position += name.Length;
                return type;
            }
        }
        throw SimulatedSqlException.SpatialInvalidLabel(this.isGeography, this.text[this.position..].TrimEnd());
    }

    private bool MatchesAt(string literal) =>
        this.position + literal.Length <= this.text.Length
        && this.text.AsSpan(this.position, literal.Length).Equals(literal, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Consumes a keyword only when it isn't glued to further word characters,
    /// so <c>EMPTYX</c> doesn't read as <c>EMPTY</c>.
    /// </summary>
    private bool TryConsumeKeyword(string keyword)
    {
        if (!MatchesAt(keyword))
            return false;
        var after = this.position + keyword.Length;
        if (after < this.text.Length && char.IsLetterOrDigit(this.text[after]))
            return false;
        this.position = after;
        return true;
    }

    /// <summary>
    /// Consumes a required literal, or raises Msg 24142.
    /// </summary>
    /// <remarks>
    /// The reported position and echoed text follow real's own idiosyncratic
    /// rule, probe-derived rather than reasoned: a single-character
    /// expectation reports the offset itself, while a label expectation
    /// reports one past it whenever the remaining input is longer than the
    /// label. The echo is the label's width of input when that much remains,
    /// and a single character when it doesn't.
    /// </remarks>
    private void ExpectLiteral(string expected)
    {
        SkipWhitespace();
        if (MatchesAt(expected))
        {
            this.position += expected.Length;
            return;
        }
        var remaining = this.text.Length - this.position;
        if (remaining <= 0)
            throw SimulatedSqlException.SpatialUnexpectedEndOfInput(this.isGeography);
        var echoLength = remaining >= expected.Length ? expected.Length : 1;
        var reported = expected.Length == 1 ? this.position : this.position + (remaining > expected.Length ? 1 : 0);
        throw SimulatedSqlException.SpatialTokenExpected(this.isGeography, expected, reported, this.text.Substring(this.position, echoLength));
    }

    private SpatialCoordinate[] ReadParenthesizedPoint()
    {
        ExpectLiteral("(");
        var point = ReadCoordinate();
        ExpectLiteral(")");
        return [point];
    }

    private SpatialCoordinate[] ReadLineStringBody()
    {
        ExpectLiteral("(");
        var points = new List<SpatialCoordinate> { ReadCoordinate() };
        while (TryConsumeSeparator())
            points.Add(ReadCoordinate());
        ExpectLiteral(")");
        return points.Count < 2 ? throw SimulatedSqlException.SpatialLineStringTooFewPoints(this.isGeography) : [.. points];
    }

    private SpatialCoordinate[][] ReadPolygonBody()
    {
        ExpectLiteral("(");
        var rings = new List<SpatialCoordinate[]> { ReadRing(0) };
        while (TryConsumeSeparator())
            rings.Add(ReadRing(rings.Count));
        ExpectLiteral(")");
        return [.. rings];
    }

    private SpatialCoordinate[] ReadRing(int interiorRingNumber)
    {
        ExpectLiteral("(");
        var points = new List<SpatialCoordinate> { ReadCoordinate() };
        while (TryConsumeSeparator())
            points.Add(ReadCoordinate());
        ExpectLiteral(")");
        return points.Count < 4
            ? throw SimulatedSqlException.SpatialRingTooFewPoints(this.isGeography, interiorRingNumber)
            : points[0].X.Equals(points[^1].X) && points[0].Y.Equals(points[^1].Y)
                ? [.. points]
                : throw SimulatedSqlException.SpatialRingNotClosed(this.isGeography, interiorRingNumber);
    }

    /// <summary>
    /// MULTIPOINT admits both <c>((0 0), (1 1))</c> and the bare
    /// <c>(0 0, 1 1)</c>. The first element fixes the form for the rest —
    /// real reports a missing <c>(</c> on a bare element that follows a
    /// parenthesized one.
    /// </summary>
    private SpatialShape[] ReadMultiPointBody()
    {
        ExpectLiteral("(");
        SkipWhitespace();
        var parenthesized = this.position < this.text.Length && this.text[this.position] == '(';
        var members = new List<SpatialShape> { ReadMultiPointMember(parenthesized) };
        while (TryConsumeSeparator())
            members.Add(ReadMultiPointMember(parenthesized));
        ExpectLiteral(")");
        return [.. members];
    }

    private SpatialShape ReadMultiPointMember(bool parenthesized) =>
        SpatialShape.Leaf(SpatialShapeType.Point, [parenthesized ? ReadParenthesizedPoint() : [ReadCoordinate()]]);

    private SpatialShape[] ReadRepeated(Func<SpatialWktReader, SpatialShape> readMember)
    {
        ExpectLiteral("(");
        var members = new List<SpatialShape> { readMember(this) };
        while (TryConsumeSeparator())
            members.Add(readMember(this));
        ExpectLiteral(")");
        return [.. members];
    }

    private bool TryConsumeSeparator()
    {
        SkipWhitespace();
        if (this.position >= this.text.Length || this.text[this.position] != ',')
            return false;
        this.position++;
        return true;
    }

    private SpatialCoordinate ReadCoordinate()
    {
        var x = ReadNumber() ?? throw SimulatedSqlException.SpatialUnexpectedEndOfInput(this.isGeography);
        var y = ReadNumber() ?? throw SimulatedSqlException.SpatialUnexpectedEndOfInput(this.isGeography);
        if (this.isGeography && (y < -90 || y > 90))
            throw SimulatedSqlException.SpatialLatitudeOutOfRange();
        var z = AtOrdinateBoundary() ? null : ReadNumber();
        var m = AtOrdinateBoundary() ? null : ReadNumber();
        return new SpatialCoordinate(x, y, z, m);
    }

    /// <summary>True when the next non-whitespace character ends the coordinate — no further ordinate follows.</summary>
    private bool AtOrdinateBoundary()
    {
        SkipWhitespace();
        return this.position >= this.text.Length || this.text[this.position] is ',' or ')' or '(';
    }

    /// <summary>
    /// Reads one ordinate. A literal <c>NULL</c> yields no value, which is how
    /// WKT expresses a missing Z alongside a present M
    /// (<c>POINT(1 2 NULL 4)</c>).
    /// </summary>
    private double? ReadNumber()
    {
        SkipWhitespace();
        var start = this.position;
        while (this.position < this.text.Length
            && this.text[this.position] is not ('(' or ')' or ',')
            && !char.IsWhiteSpace(this.text[this.position]))
        {
            this.position++;
        }

        if (this.position == start)
        {
            if (start >= this.text.Length)
                throw SimulatedSqlException.SpatialUnexpectedEndOfInput(this.isGeography);
            throw SimulatedSqlException.SpatialNumberExpected(this.isGeography, start, this.text.Substring(start, 1));
        }

        var token = this.text[start..this.position];
        return token.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null
            : double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value
            : throw SimulatedSqlException.SpatialNumberExpected(this.isGeography, this.position, token);
    }

    private void SkipWhitespace()
    {
        while (this.position < this.text.Length && char.IsWhiteSpace(this.text[this.position]))
            this.position++;
    }
}
