using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Decodes SQL Server's <c>geography</c> / <c>geometry</c> binary wire form
/// into WKT (Well-Known Text). Covers Point / LineString / Polygon /
/// MultiPoint / MultiLineString / MultiPolygon / GeometryCollection in 2D
/// (no Z or M coordinates). Z/M-bearing shapes, version &gt; 2, and
/// malformed payloads return <see langword="null"/> so the row loader falls
/// back to <c>SqlValue.Null</c> rather than failing the BCP file.
/// </summary>
/// <remarks>
/// <para>Microsoft's spatial UDT serialization (a.k.a. the "MS spatial
/// binary" format) prefixes each value with a 4-byte SRID + 1-byte version
/// + 1-byte properties bitfield. Two shortcut shapes (single point + single
/// LineString) skip the figure/shape tables; everything else uses the full
/// layout of <c>numPoints + points[] + numFigures + figures[] + numShapes
/// + shapes[]</c>.</para>
///
/// <para>Each figure record is <c>[1 byte attribute][4 byte pointOffset]</c>;
/// the attribute is ignored here because the SQL Server format makes the
/// figure's role (outer vs inner ring, stroke, etc.) deducible from the
/// owning shape's type and the figure's position within the shape. Each
/// shape record is <c>[4 byte parentOffset][4 byte figureOffset][1 byte
/// type]</c>. Collection shapes (Multi* / GeometryCollection) typically set
/// <c>figureOffset = -1</c> since their content is described by child
/// shapes whose <c>parentOffset</c> points back at the collection.</para>
///
/// <para><b>Axis order divergence</b>: geography stores <c>(lat, long)</c>
/// in binary but WKT prints <c>(long lat)</c> per OGC. Geometry uses
/// <c>(x, y)</c> throughout. The decoder honors the inversion.</para>
///
/// <para>WKT coordinate formatting uses the <c>"R"</c> round-trip specifier
/// under <see cref="CultureInfo.InvariantCulture"/>, preserving full IEEE
/// 754 precision on re-parse.</para>
/// </remarks>
internal static class SpatialWkbDecoder
{
    private const byte HasZ = 0x01;
    private const byte HasM = 0x02;
    private const byte IsSinglePoint = 0x08;
    private const byte IsSingleLineString = 0x10;

    private const byte ShapePoint = 0x01;
    private const byte ShapeLineString = 0x02;
    private const byte ShapePolygon = 0x03;
    private const byte ShapeMultiPoint = 0x04;
    private const byte ShapeMultiLineString = 0x05;
    private const byte ShapeMultiPolygon = 0x06;
    private const byte ShapeGeometryCollection = 0x07;

    /// <summary>
    /// Decodes a Microsoft spatial binary payload to its WKT representation.
    /// Returns <see langword="null"/> when the payload uses Z/M coordinates,
    /// is malformed, has an unknown version, or contains a shape type the
    /// decoder doesn't model — callers fall back to <c>SqlValue.Null</c>.
    /// </summary>
    public static string? TryDecode(ReadOnlySpan<byte> wkb, bool isGeography)
    {
        if (wkb.Length < 6)
            return null;
        var version = wkb[4];
        if (version is not (0x01 or 0x02))
            return null;
        var properties = wkb[5];
        // 2D shapes only — Z/M would change the point-record width.
        if ((properties & (HasZ | HasM)) != 0)
            return null;

        // Shortcut: single point (22 bytes total).
        if ((properties & IsSinglePoint) != 0)
        {
            if (wkb.Length != 22)
                return null;
            var first = BinaryPrimitives.ReadDoubleLittleEndian(wkb[6..14]);
            var second = BinaryPrimitives.ReadDoubleLittleEndian(wkb[14..22]);
            return "POINT " + FormatCoord(first, second, isGeography);
        }

        // Shortcut: single LineString — 4-byte numPoints then point pairs.
        if ((properties & IsSingleLineString) != 0)
        {
            if (wkb.Length < 10)
                return null;
            var n = BinaryPrimitives.ReadInt32LittleEndian(wkb[6..10]);
            if (n < 0 || wkb.Length != 10 + (n * 16))
                return null;
            var pts = ReadPointArray(wkb[10..], n);
            if (pts is null)
                return null;
            var sb = new StringBuilder("LINESTRING ");
            AppendPointList(sb, pts, 0, n, isGeography);
            return sb.ToString();
        }

        // Full shape layout.
        return TryDecodeFull(wkb, isGeography);
    }

    private static string? TryDecodeFull(ReadOnlySpan<byte> wkb, bool isGeography)
    {
        var pos = 6;
        if (pos + 4 > wkb.Length)
            return null;
        var numPoints = BinaryPrimitives.ReadInt32LittleEndian(wkb.Slice(pos, 4));
        pos += 4;
        if (numPoints < 0 || pos + (numPoints * 16) > wkb.Length)
            return null;
        var points = ReadPointArray(wkb.Slice(pos, numPoints * 16), numPoints);
        if (points is null)
            return null;
        pos += numPoints * 16;

        if (pos + 4 > wkb.Length)
            return null;
        var numFigures = BinaryPrimitives.ReadInt32LittleEndian(wkb.Slice(pos, 4));
        pos += 4;
        if (numFigures < 0 || pos + (numFigures * 5) > wkb.Length)
            return null;
        var figurePointStart = new int[numFigures];
        for (var i = 0; i < numFigures; i++)
            figurePointStart[i] = BinaryPrimitives.ReadInt32LittleEndian(wkb.Slice(pos + 1 + (i * 5), 4));
        pos += numFigures * 5;

        if (pos + 4 > wkb.Length)
            return null;
        var numShapes = BinaryPrimitives.ReadInt32LittleEndian(wkb.Slice(pos, 4));
        pos += 4;
        if (numShapes <= 0 || pos + (numShapes * 9) > wkb.Length)
            return null;
        var shapes = new (int parent, int figOffset, byte type)[numShapes];
        for (var i = 0; i < numShapes; i++)
        {
            shapes[i] = (
                BinaryPrimitives.ReadInt32LittleEndian(wkb.Slice(pos, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(wkb.Slice(pos + 4, 4)),
                wkb[pos + 8]);
            pos += 9;
        }

        var sb = new StringBuilder();
        return AppendShape(sb, shapeIdx: 0, bare: false, shapes, figurePointStart, numPoints, points, isGeography)
            ? sb.ToString()
            : null;
    }

    /// <summary>
    /// Recursive writer. <paramref name="bare"/> = true skips the shape's
    /// type-name prefix (used when the shape is a child of a Multi*
    /// collection, since the parent already named the type and the children
    /// are written inline as parenthesized bodies).
    /// </summary>
    private static bool AppendShape(
        StringBuilder sb,
        int shapeIdx,
        bool bare,
        (int parent, int figOffset, byte type)[] shapes,
        int[] figurePointStart,
        int numPoints,
        (double a, double b)[] points,
        bool isGeography)
    {
        var (_, figOffset, type) = shapes[shapeIdx];
        var figEnd = FigureEnd(shapeIdx, shapes, figurePointStart.Length);

        switch (type)
        {
            case ShapePoint:
                if (figOffset < 0 || figOffset >= figurePointStart.Length)
                    return false;
                {
                    var pi = figurePointStart[figOffset];
                    if (pi < 0 || pi >= numPoints)
                        return false;
                    if (!bare) _ = sb.Append("POINT ");
                    AppendCoord(sb, points[pi], isGeography);
                    return true;
                }
            case ShapeLineString:
                if (figOffset < 0 || figEnd - figOffset != 1)
                    return false;
                if (!bare) _ = sb.Append("LINESTRING ");
                return AppendFigure(sb, figOffset, figurePointStart, numPoints, points, isGeography);
            case ShapePolygon:
                if (figOffset < 0 || figEnd <= figOffset)
                    return false;
                _ = sb.Append(bare ? "(" : "POLYGON (");
                for (var f = figOffset; f < figEnd; f++)
                {
                    if (f > figOffset) _ = sb.Append(", ");
                    if (!AppendFigure(sb, f, figurePointStart, numPoints, points, isGeography))
                        return false;
                }
                _ = sb.Append(')');
                return true;
            case ShapeMultiPoint:
                return AppendCollection(sb, "MULTIPOINT", shapeIdx, bareChildren: true, shapes, figurePointStart, numPoints, points, isGeography);
            case ShapeMultiLineString:
                return AppendCollection(sb, "MULTILINESTRING", shapeIdx, bareChildren: true, shapes, figurePointStart, numPoints, points, isGeography);
            case ShapeMultiPolygon:
                return AppendCollection(sb, "MULTIPOLYGON", shapeIdx, bareChildren: true, shapes, figurePointStart, numPoints, points, isGeography);
            case ShapeGeometryCollection:
                return AppendCollection(sb, "GEOMETRYCOLLECTION", shapeIdx, bareChildren: false, shapes, figurePointStart, numPoints, points, isGeography);
            default:
                return false;
        }
    }

    /// <summary>
    /// Writes a single figure as a parenthesized comma-separated point list.
    /// Figure point range is <c>[figurePointStart[f], figurePointStart[f+1])</c>
    /// — or <c>numPoints</c> for the last figure.
    /// </summary>
    private static bool AppendFigure(
        StringBuilder sb,
        int figIdx,
        int[] figurePointStart,
        int numPoints,
        (double a, double b)[] points,
        bool isGeography)
    {
        var start = figurePointStart[figIdx];
        var end = figIdx + 1 < figurePointStart.Length ? figurePointStart[figIdx + 1] : numPoints;
        if (start < 0 || end > numPoints || end < start)
            return false;
        AppendPointList(sb, points, start, end, isGeography);
        return true;
    }

    private static bool AppendCollection(
        StringBuilder sb,
        string typeName,
        int shapeIdx,
        bool bareChildren,
        (int parent, int figOffset, byte type)[] shapes,
        int[] figurePointStart,
        int numPoints,
        (double a, double b)[] points,
        bool isGeography)
    {
        _ = sb.Append(typeName).Append(" (");
        var first = true;
        for (var s = shapeIdx + 1; s < shapes.Length; s++)
        {
            if (shapes[s].parent != shapeIdx) continue;
            if (!first) _ = sb.Append(", ");
            first = false;
            if (!AppendShape(sb, s, bareChildren, shapes, figurePointStart, numPoints, points, isGeography))
                return false;
        }
        _ = sb.Append(')');
        return true;
    }

    /// <summary>
    /// Finds the figure-index range end for shape <paramref name="shapeIdx"/>:
    /// the next shape's figOffset (if non-negative). Collection shapes use
    /// figOffset = -1 — skip past them to find the next concrete figure
    /// owner. Falls back to numFigures at end-of-shapes.
    /// </summary>
    private static int FigureEnd(int shapeIdx, (int parent, int figOffset, byte type)[] shapes, int numFigures)
    {
        for (var s = shapeIdx + 1; s < shapes.Length; s++)
        {
            if (shapes[s].figOffset >= 0)
                return shapes[s].figOffset;
        }
        return numFigures;
    }

    private static (double a, double b)[]? ReadPointArray(ReadOnlySpan<byte> bytes, int n)
    {
        if (bytes.Length < n * 16)
            return null;
        var arr = new (double a, double b)[n];
        for (var i = 0; i < n; i++)
        {
            arr[i] = (
                BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(i * 16, 8)),
                BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice((i * 16) + 8, 8)));
        }
        return arr;
    }

    private static void AppendPointList(StringBuilder sb, (double a, double b)[] points, int start, int end, bool isGeography)
    {
        _ = sb.Append('(');
        for (var i = start; i < end; i++)
        {
            if (i > start) _ = sb.Append(", ");
            AppendCoordBare(sb, points[i], isGeography);
        }
        _ = sb.Append(')');
    }

    private static void AppendCoord(StringBuilder sb, (double a, double b) p, bool isGeography)
    {
        _ = sb.Append('(');
        AppendCoordBare(sb, p, isGeography);
        _ = sb.Append(')');
    }

    private static void AppendCoordBare(StringBuilder sb, (double a, double b) p, bool isGeography)
    {
        var (first, second) = isGeography ? (p.b, p.a) : (p.a, p.b);
        _ = sb.Append(Format(first)).Append(' ').Append(Format(second));
    }

    private static string FormatCoord(double first, double second, bool isGeography)
    {
        var (wktFirst, wktSecond) = isGeography ? (second, first) : (first, second);
        return $"({Format(wktFirst)} {Format(wktSecond)})";
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
