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
/// <para>The curved kinds read with real's own grammar and checks: a
/// <c>CIRCULARSTRING</c> reads its points in pairs after the first, a
/// <c>COMPOUNDCURVE</c> element is a bare line or a labelled
/// <c>CIRCULARSTRING</c> continuing from the previous element's end, and a
/// <c>CURVEPOLYGON</c> ring may be any of the three figure kinds.
/// <c>FULLGLOBE</c> is the whole-earth <c>geography</c> instance, with no
/// body; <c>geometry</c> refuses it with real's own 24303, and a collection
/// may not hold it (24150).</para>
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
        if (reader.position < text.Length)
            throw SimulatedSqlException.SpatialWktNotValid(isGeography);
        if (isGeography)
            SpatialGeodeticValidator.RejectAntipodalEdges(root);
        return new SpatialGeometry(srid, root);
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

    private SpatialShape ReadTaggedText(string? matchedLabel, bool inCollection = false)
    {
        SkipWhitespace();
        var type = matchedLabel is null ? ReadLabel() : LabelType(matchedLabel);

        // FULLGLOBE is a geography-only kind; real rejects it outright on the
        // planar type rather than reporting it as an unknown label.
        if (type == SpatialShapeType.FullGlobe && !this.isGeography)
            throw SimulatedSqlException.SpatialInvalidOpenGisType(this.isGeography, OpenGisName(type));
        if (type == SpatialShapeType.FullGlobe)
        {
            // The whole globe has no body, and no collection may hold it.
            return !inCollection ? SpatialShape.Empty(type) : throw SimulatedSqlException.SpatialFullGlobeInCollection();
        }

        SkipWhitespace();
        return TryConsumeKeyword("EMPTY") ? SpatialShape.Empty(type) : type switch
        {
            SpatialShapeType.Point => SpatialShape.Leaf(type, [ReadParenthesizedPoint()]),
            SpatialShapeType.LineString => SpatialShape.Leaf(type, [ReadLineStringBody()]),
            SpatialShapeType.Polygon => SpatialShape.Leaf(type, ReadPolygonBody()),
            SpatialShapeType.MultiPoint => SpatialShape.Collection(type, ReadMultiPointBody()),
            SpatialShapeType.MultiLineString => SpatialShape.Collection(type, ReadRepeated(static r => SpatialShape.Leaf(SpatialShapeType.LineString, [r.ReadLineStringBody()]))),
            SpatialShapeType.MultiPolygon => SpatialShape.Collection(type, ReadRepeated(static r => SpatialShape.Leaf(SpatialShapeType.Polygon, r.ReadPolygonBody()))),
            SpatialShapeType.CircularString => SpatialShape.Curve(type, [ReadCircularStringBody()], [SpatialFigureType.Arc], [null]),
            SpatialShapeType.CompoundCurve => ReadCompoundCurveBody(),
            SpatialShapeType.CurvePolygon => ReadCurvePolygonBody(),
            _ => SpatialShape.Collection(type, ReadRepeated(static r => r.ReadTaggedText(null, inCollection: true))),
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
        return CheckRing([.. points], interiorRingNumber);
    }

    /// <summary>
    /// A ring needs four points and must end where it starts. <c>geometry</c>
    /// names the exterior ring apart from a numbered interior one, while
    /// <c>geography</c> numbers every ring from 1 — except a <c>CURVEPOLYGON</c>
    /// ring written <c>EMPTY</c>, which real reports in <c>geometry</c>'s terms
    /// on both types.
    /// </summary>
    private SpatialCoordinate[] CheckRing(SpatialCoordinate[] points, int interiorRingNumber) =>
        points.Length < 4
            ? throw SimulatedSqlException.SpatialRingTooFewPoints(this.isGeography, interiorRingNumber, planarNumbering: points.Length == 0)
            : points[0].X.Equals(points[^1].X) && points[0].Y.Equals(points[^1].Y)
                ? points
                : throw SimulatedSqlException.SpatialRingNotClosed(this.isGeography, interiorRingNumber);

    /// <summary>
    /// A <c>CIRCULARSTRING</c>'s parenthesized points. After the first they
    /// come in pairs — each arc adds a point on it and its end — so an input
    /// stopping after an odd number is Msg 24142 expecting the <c>,</c> of the
    /// pair, and a lone point is 24212. Each arc's three points must agree on
    /// Z (24214).
    /// </summary>
    private SpatialCoordinate[] ReadCircularStringBody()
    {
        ExpectLiteral("(");
        var points = new List<SpatialCoordinate> { ReadCoordinate() };
        while (TryConsumeSeparator())
        {
            points.Add(ReadCoordinate());
            ExpectLiteral(",");
            points.Add(ReadCoordinate());
        }
        ExpectLiteral(")");
        if (points.Count < 3)
            throw SimulatedSqlException.SpatialCircularStringTooFewPoints(this.isGeography);
        for (var i = 0; i + 2 < points.Count; i += 2)
        {
            if (!Nullable.Equals(points[i].Z, points[i + 1].Z) || !Nullable.Equals(points[i].Z, points[i + 2].Z))
                throw SimulatedSqlException.SpatialArcZNotEqual(this.isGeography);
        }
        return [.. points];
    }

    /// <summary>
    /// A <c>COMPOUNDCURVE</c>'s elements: a bare <c>(…)</c> line, or — when
    /// the element opens with a <c>C</c> — a labelled <c>CIRCULARSTRING</c>.
    /// Each element must start exactly where the previous one ended (24134),
    /// and that shared point is stored once.
    /// </summary>
    private SpatialShape ReadCompoundCurveBody()
    {
        var (points, segments) = ReadCompoundElements();
        return SpatialShape.Curve(SpatialShapeType.CompoundCurve, [points], [SpatialFigureType.Composite], [segments]);
    }

    private (SpatialCoordinate[] Points, SpatialSegmentType[] Segments) ReadCompoundElements()
    {
        ExpectLiteral("(");
        var elements = new List<(bool IsArc, SpatialCoordinate[] Points)>();
        do
        {
            var isArc = NextStartsLabel();
            if (isArc)
                ExpectLiteral("CIRCULARSTRING");
            var element = isArc ? ReadCircularStringBody() : ReadLineStringBody();
            if (elements.Count > 0 && elements[^1].Points[^1] != element[0])
                throw SimulatedSqlException.SpatialCompoundCurveNotContinuous(this.isGeography);
            elements.Add((isArc, element));
        }
        while (TryConsumeSeparator());
        ExpectLiteral(")");
        return SpatialCurves.Compose(elements);
    }

    /// <summary>
    /// A <c>CURVEPOLYGON</c>'s rings, each a bare <c>(…)</c> line ring, a
    /// labelled <c>CIRCULARSTRING</c> or a labelled <c>COMPOUNDCURVE</c>,
    /// held to the same ring rules as a <c>POLYGON</c>'s.
    /// </summary>
    private SpatialShape ReadCurvePolygonBody()
    {
        ExpectLiteral("(");
        var rings = new List<SpatialCoordinate[]>();
        var types = new List<SpatialFigureType>();
        var segments = new List<SpatialSegmentType[]?>();
        do
        {
            SpatialCoordinate[] ring;
            SpatialFigureType type;
            SpatialSegmentType[]? run = null;
            SkipWhitespace();
            if (NextStartsLabel() && MatchesAt("COMPOUNDCURVE"))
            {
                this.position += "COMPOUNDCURVE".Length;
                SkipWhitespace();
                if (TryConsumeKeyword("EMPTY"))
                    throw SimulatedSqlException.SpatialCompoundCurveRingEmpty(this.isGeography);
                (ring, run) = ReadCompoundElements();
                type = SpatialFigureType.Composite;
            }
            else if (NextStartsLabel())
            {
                ExpectLiteral("CIRCULARSTRING");
                SkipWhitespace();
                if (TryConsumeKeyword("EMPTY"))
                    throw SimulatedSqlException.SpatialCircularStringRingEmpty(this.isGeography);
                ring = ReadCircularStringBody();
                type = SpatialFigureType.Arc;
            }
            else if (TryConsumeKeyword("EMPTY"))
            {
                ring = [];
                type = SpatialFigureType.Line;
            }
            else
            {
                ExpectLiteral("(");
                var points = new List<SpatialCoordinate> { ReadCoordinate() };
                while (TryConsumeSeparator())
                    points.Add(ReadCoordinate());
                ExpectLiteral(")");
                ring = [.. points];
                type = SpatialFigureType.Line;
            }
            rings.Add(CheckRing(ring, rings.Count));
            types.Add(type);
            segments.Add(run);
        }
        while (TryConsumeSeparator());
        ExpectLiteral(")");
        return SpatialShape.Curve(SpatialShapeType.CurvePolygon, [.. rings], [.. types], [.. segments]);
    }

    /// <summary>Whether the next non-whitespace character opens a label rather than a <c>(</c> — how real tells a labelled curve element from a bare line.</summary>
    private bool NextStartsLabel()
    {
        SkipWhitespace();
        return this.position < this.text.Length && this.text[this.position] is 'C' or 'c';
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
