using System.Buffers.Binary;
using System.Globalization;

namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Mirror of <see cref="SpatialWkbDecoder"/>: encodes a WKT (Well-Known Text)
/// string into SQL Server's <c>geography</c> / <c>geometry</c> binary CLR-UDT
/// serialization (a.k.a. the "MS spatial binary" format), the byte form real
/// SQL Server accepts on <c>CAST(... AS varbinary(max))</c> and that DacFx
/// reads over the wire. Covers the same 2D shape subset the decoder handles:
/// Point / LineString / Polygon / MultiPoint / MultiLineString / MultiPolygon
/// / GeometryCollection (no Z/M coordinates, no EMPTY geometries).
/// </summary>
/// <remarks>
/// <para>Layout (probe-confirmed against SQL Server 2025, 2026-07-16): a
/// 4-byte SRID + 1-byte version (<c>0x01</c>) + 1-byte serialization-properties
/// bitfield, then either a shortcut body or the full
/// <c>numPoints + points[] + numFigures + figures[] + numShapes + shapes[]</c>
/// tables. Two shortcuts: a single <c>POINT</c> writes properties
/// <c>0x0C</c> (isValid | isSinglePoint) then one coordinate pair with no
/// tables; a <c>LINESTRING</c> of <b>exactly two</b> points writes properties
/// <c>0x14</c> (isValid | isSingleLineSegment) then the two pairs with no
/// tables and no count. Everything else writes properties <c>0x04</c>
/// (isValid) and the full tables.</para>
///
/// <para><b>isValid bit</b>: real sets bit <c>0x04</c> when the instance is
/// valid and clears it for a stored-but-invalid instance (only
/// <c>geometry</c>, and <c>geography</c> instances real accepts without
/// validating — e.g. WWI <c>Countries.Border</c>). The simulator stores WKT
/// and cannot revalidate, so it always sets isValid. Every valid WWI shape
/// round-trips byte-identically; a stored-invalid instance diverges only in
/// this one bit and real still <c>CAST</c>s the bytes back. See
/// <c>docs/claude/spatial.md</c>.</para>
///
/// <para><b>Axis order</b>: geography WKT is <c>(long lat)</c> but the binary
/// stores <c>(lat, long)</c>; geometry stores <c>(x, y)</c> throughout — the
/// inverse of the decoder's honoring of the same inversion.</para>
///
/// <para><b>Figure attributes (version 1)</b>: point and line figures carry
/// <c>0x01</c>; a polygon's exterior (first) ring carries <c>0x02</c> and each
/// interior ring <c>0x00</c>. Shapes are laid out depth-first (pre-order); a
/// shape's <c>figureOffset</c> is the index of the first figure in its
/// subtree, and a leaf shape's is its own first figure. The root shape's
/// parent offset is <c>-1</c>.</para>
/// </remarks>
internal static class SpatialWkbEncoder
{
    private const byte Version = 0x01;
    private const byte IsValid = 0x04;
    private const byte IsSinglePoint = 0x08;
    private const byte IsSingleLineSegment = 0x10;

    private const byte ShapePoint = 0x01;
    private const byte ShapeLineString = 0x02;
    private const byte ShapePolygon = 0x03;
    private const byte ShapeMultiPoint = 0x04;
    private const byte ShapeMultiLineString = 0x05;
    private const byte ShapeMultiPolygon = 0x06;
    private const byte ShapeGeometryCollection = 0x07;

    /// <summary>
    /// Encodes <paramref name="wkt"/> into the MS spatial binary form.
    /// <paramref name="isGeography"/> selects the axis-order convention;
    /// <paramref name="srid"/> is the value's spatial reference id (the
    /// simulator's stored default — 4326 for geography, 0 for geometry — since
    /// it doesn't track per-value SRID). Throws
    /// <see cref="NotSupportedException"/> for EMPTY / Z / M / unrecognized
    /// shapes the decoder likewise skips.
    /// </summary>
    public static byte[] Encode(string wkt, bool isGeography, int srid)
    {
        var pos = 0;
        var root = ParseGeometry(wkt, ref pos);

        var bytes = new List<byte>(64);
        WriteInt(bytes, srid);
        bytes.Add(Version);

        if (root.Type == ShapePoint)
        {
            bytes.Add(IsValid | IsSinglePoint);
            var (a, b) = root.Figures![0][0];
            WritePoint(bytes, a, b, isGeography);
            return [.. bytes];
        }

        if (root.Type == ShapeLineString && root.Figures![0].Count == 2)
        {
            bytes.Add(IsValid | IsSingleLineSegment);
            foreach (var (a, b) in root.Figures[0])
                WritePoint(bytes, a, b, isGeography);
            return [.. bytes];
        }

        bytes.Add(IsValid);
        var points = new List<(double a, double b)>();
        var figures = new List<(byte attribute, int pointOffset)>();
        var shapes = new List<(int parent, int figureOffset, byte type)>();
        BuildTables(root, parent: -1, points, figures, shapes);

        WriteInt(bytes, points.Count);
        foreach (var (a, b) in points)
            WritePoint(bytes, a, b, isGeography);

        WriteInt(bytes, figures.Count);
        foreach (var (attribute, pointOffset) in figures)
        {
            bytes.Add(attribute);
            WriteInt(bytes, pointOffset);
        }

        WriteInt(bytes, shapes.Count);
        foreach (var (parent, figureOffset, type) in shapes)
        {
            WriteInt(bytes, parent);
            WriteInt(bytes, figureOffset);
            bytes.Add(type);
        }

        return [.. bytes];
    }

    /// <summary>
    /// Depth-first (pre-order) walk populating the point / figure / shape
    /// tables. A shape's <c>figureOffset</c> is captured as the current figure
    /// count before its own (or its subtree's) figures are appended.
    /// </summary>
    private static void BuildTables(
        Shape node,
        int parent,
        List<(double a, double b)> points,
        List<(byte attribute, int pointOffset)> figures,
        List<(int parent, int figureOffset, byte type)> shapes)
    {
        var shapeIndex = shapes.Count;
        var figureOffset = figures.Count;
        shapes.Add((parent, figureOffset, node.Type));

        if (node.Figures is { } nodeFigures)
        {
            for (var ring = 0; ring < nodeFigures.Count; ring++)
            {
                var attribute = node.Type == ShapePolygon
                    ? ring == 0 ? (byte)0x02 : (byte)0x00
                    : (byte)0x01;
                figures.Add((attribute, points.Count));
                points.AddRange(nodeFigures[ring]);
            }
        }
        else
        {
            foreach (var child in node.Children!)
                BuildTables(child, shapeIndex, points, figures, shapes);
        }
    }

    private static void WritePoint(List<byte> bytes, double first, double second, bool isGeography)
    {
        if (isGeography)
        {
            WriteDouble(bytes, second);
            WriteDouble(bytes, first);
        }
        else
        {
            WriteDouble(bytes, first);
            WriteDouble(bytes, second);
        }
    }

    private static void WriteInt(List<byte> bytes, int value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
        bytes.AddRange(tmp);
    }

    private static void WriteDouble(List<byte> bytes, double value)
    {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(tmp, value);
        bytes.AddRange(tmp);
    }

    private static Shape ParseGeometry(string s, ref int pos)
    {
        var tag = ReadTag(s, ref pos);
        switch (tag)
        {
            case "GEOMETRYCOLLECTION":
                {
                    var children = new List<Shape>();
                    Expect(s, ref pos, '(');
                    do
                        children.Add(ParseGeometry(s, ref pos));
                    while (TryConsumeComma(s, ref pos));
                    Expect(s, ref pos, ')');
                    return new Shape(ShapeGeometryCollection, children);
                }

            case "LINESTRING":
                return new Shape(ShapeLineString, [ParseParenCoordList(s, ref pos)]);
            case "MULTILINESTRING":
                {
                    var children = new List<Shape>();
                    Expect(s, ref pos, '(');
                    do
                        children.Add(new Shape(ShapeLineString, [ParseParenCoordList(s, ref pos)]));
                    while (TryConsumeComma(s, ref pos));
                    Expect(s, ref pos, ')');
                    return new Shape(ShapeMultiLineString, children);
                }

            case "MULTIPOINT":
                {
                    var children = new List<Shape>();
                    Expect(s, ref pos, '(');
                    do
                        children.Add(new Shape(ShapePoint, [[ParseMultiPointMember(s, ref pos)]]));
                    while (TryConsumeComma(s, ref pos));
                    Expect(s, ref pos, ')');
                    return new Shape(ShapeMultiPoint, children);
                }

            case "MULTIPOLYGON":
                {
                    var children = new List<Shape>();
                    Expect(s, ref pos, '(');
                    do
                        children.Add(new Shape(ShapePolygon, ParsePolygonRings(s, ref pos)));
                    while (TryConsumeComma(s, ref pos));
                    Expect(s, ref pos, ')');
                    return new Shape(ShapeMultiPolygon, children);
                }

            case "POINT":
                return new Shape(ShapePoint, [ParseParenCoordList(s, ref pos)]);
            case "POLYGON":
                return new Shape(ShapePolygon, ParsePolygonRings(s, ref pos));
            default:
                throw new NotSupportedException($"WKT shape '{tag}' is not supported by the spatial encoder.");
        }
    }

    /// <summary>Parses <c>( (ring), (ring), … )</c> into a ring list.</summary>
    private static List<List<(double a, double b)>> ParsePolygonRings(string s, ref int pos)
    {
        var rings = new List<List<(double a, double b)>>();
        Expect(s, ref pos, '(');
        do
            rings.Add(ParseParenCoordList(s, ref pos));
        while (TryConsumeComma(s, ref pos));
        Expect(s, ref pos, ')');
        return rings;
    }

    /// <summary>Parses <c>( x y, x y, … )</c> into a coordinate list.</summary>
    private static List<(double a, double b)> ParseParenCoordList(string s, ref int pos)
    {
        var coords = new List<(double a, double b)>();
        Expect(s, ref pos, '(');
        do
            coords.Add(ParseCoord(s, ref pos));
        while (TryConsumeComma(s, ref pos));
        Expect(s, ref pos, ')');
        return coords;
    }

    /// <summary>
    /// A <c>MULTIPOINT</c> member is either a parenthesized single point
    /// <c>(x y)</c> or a bare pair <c>x y</c> — SQL Server accepts both forms.
    /// </summary>
    private static (double a, double b) ParseMultiPointMember(string s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        return pos < s.Length && s[pos] == '('
            ? ParseParenCoordList(s, ref pos)[0]
            : ParseCoord(s, ref pos);
    }

    private static (double a, double b) ParseCoord(string s, ref int pos)
    {
        var first = ReadNumber(s, ref pos);
        var second = ReadNumber(s, ref pos);
        // Discard any trailing Z / M ordinates — the 2D subset the decoder mirrors.
        while (TryReadNumber(s, ref pos, out _))
        {
        }

        return (first, second);
    }

    private static string ReadTag(string s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        var start = pos;
        while (pos < s.Length && char.IsLetter(s[pos]))
            pos++;
        return pos == start
            ? throw new NotSupportedException($"Malformed WKT near offset {pos}: expected a shape keyword.")
            : s[start..pos].ToUpperInvariant();
    }

    private static double ReadNumber(string s, ref int pos) =>
        TryReadNumber(s, ref pos, out var value)
            ? value
            : throw new NotSupportedException($"Malformed WKT near offset {pos}: expected a number.");

    private static bool TryReadNumber(string s, ref int pos, out double value)
    {
        SkipWhitespace(s, ref pos);
        var start = pos;
        while (pos < s.Length && IsNumberChar(s[pos]))
            pos++;
        if (pos == start)
        {
            value = 0;
            return false;
        }

        value = double.Parse(s.AsSpan(start, pos - start), CultureInfo.InvariantCulture);
        return true;
    }

    private static bool IsNumberChar(char c) =>
        c is (>= '0' and <= '9') or '-' or '+' or '.' or 'e' or 'E';

    private static bool TryConsumeComma(string s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        if (pos < s.Length && s[pos] == ',')
        {
            pos++;
            return true;
        }

        return false;
    }

    private static void Expect(string s, ref int pos, char expected)
    {
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length || s[pos] != expected)
            throw new NotSupportedException($"Malformed WKT near offset {pos}: expected '{expected}'.");
        pos++;
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
            pos++;
    }

    /// <summary>
    /// A parsed WKT node: either a leaf carrying figure rings (Point = one
    /// figure of one coordinate, LineString = one figure, Polygon = exterior
    /// ring then interior rings) or a collection carrying child shapes.
    /// </summary>
    private sealed class Shape
    {
        public readonly byte Type;
        public readonly List<List<(double a, double b)>>? Figures;
        public readonly List<Shape>? Children;

        public Shape(byte type, List<List<(double a, double b)>> figures)
        {
            this.Type = type;
            this.Figures = figures;
        }

        public Shape(byte type, List<Shape> children)
        {
            this.Type = type;
            this.Children = children;
        }
    }
}
