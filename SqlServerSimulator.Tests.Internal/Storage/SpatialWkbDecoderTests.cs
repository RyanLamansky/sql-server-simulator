using System.Buffers.Binary;
using SqlServerSimulator.Storage.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Decoder fidelity tests for <see cref="SpatialWkbDecoder"/>. Covers the
/// simple-point shortcut, the single-LineString shortcut, the full-form
/// shapes (Polygon, MultiPolygon, GeometryCollection), and the bailout
/// paths (Z/M, truncated, unknown version, unsupported shape type).
/// </summary>
[TestClass]
public sealed class SpatialWkbDecoderTests
{
    [TestMethod]
    public void SimplePoint_Geography_AxisInversion()
    {
        var wkb = BuildSimplePoint(srid: 4326, firstCoord: 47.6, secondCoord: -122.3);
        AreEqual("POINT (-122.3 47.6)", SpatialWkbDecoder.TryDecode(wkb, isGeography: true));
    }

    [TestMethod]
    public void SimplePoint_Geometry_NoAxisInversion()
    {
        var wkb = BuildSimplePoint(srid: 0, firstCoord: 1.5, secondCoord: 2.5);
        AreEqual("POINT (1.5 2.5)", SpatialWkbDecoder.TryDecode(wkb, isGeography: false));
    }

    [TestMethod]
    public void SimplePoint_RoundTripsBitForBit()
    {
        var original = -122.13469409942627;
        var wkb = BuildSimplePoint(srid: 4326, firstCoord: 47.642438, secondCoord: original);
        var wkt = SpatialWkbDecoder.TryDecode(wkb, isGeography: true);
        IsNotNull(wkt);
        var openParen = wkt.IndexOf('(');
        var space = wkt.IndexOf(' ', openParen);
        var firstStr = wkt[(openParen + 1)..space];
        AreEqual(original, double.Parse(firstStr, System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void SingleLineString_TwoPoints_Geography()
    {
        // Single-LineString shortcut: properties byte = 0x10, payload = 4-byte
        // numPoints + numPoints × 16 bytes (lat, long pairs in geography).
        // Build a 3-point line: (lat=0, long=0) → (lat=1, long=2) → (lat=3, long=4).
        var wkb = BuildShortcutLineString(srid: 4326,
            (0, 0),
            (1, 2),
            (3, 4));
        // Geography axis inversion: (lat, long) → (long lat).
        AreEqual("LINESTRING (0 0, 2 1, 4 3)", SpatialWkbDecoder.TryDecode(wkb, isGeography: true));
    }

    [TestMethod]
    public void Polygon_SingleRing_Geometry()
    {
        // Full-form Polygon: one outer ring of 4 points (closing point
        // repeats the first). Geometry: (x, y) prints as-is.
        var wkb = BuildFullShape(
            srid: 0,
            points: [(0, 0), (10, 0), (10, 10), (0, 0)],
            figurePointStarts: [0],
            shapes: [(parent: -1, figOffset: 0, type: 0x03)]);
        AreEqual("POLYGON ((0 0, 10 0, 10 10, 0 0))", SpatialWkbDecoder.TryDecode(wkb, isGeography: false));
    }

    [TestMethod]
    public void Polygon_WithInteriorRing_Geometry()
    {
        // Polygon with two figures (outer + 1 inner ring).
        var wkb = BuildFullShape(
            srid: 0,
            points: [
                (0, 0), (10, 0), (10, 10), (0, 10), (0, 0),     // outer
                (2, 2), (4, 2), (4, 4), (2, 4), (2, 2)],        // inner
            figurePointStarts: [0, 5],
            shapes: [(parent: -1, figOffset: 0, type: 0x03)]);
        AreEqual("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 4 2, 4 4, 2 4, 2 2))",
            SpatialWkbDecoder.TryDecode(wkb, isGeography: false));
    }

    [TestMethod]
    public void MultiPolygon_TwoPolygons_Geometry()
    {
        // MultiPolygon root (figOffset = -1) with two child Polygon shapes.
        // Both polygons have single rings (3-point triangles).
        var wkb = BuildFullShape(
            srid: 0,
            points: [
                (0, 0), (1, 0), (0, 1), (0, 0),
                (5, 5), (6, 5), (5, 6), (5, 5)],
            figurePointStarts: [0, 4],
            shapes: [
                (parent: -1, figOffset: -1, type: 0x06),   // root MultiPolygon
                (parent: 0, figOffset: 0, type: 0x03),     // child Polygon 1
                (parent: 0, figOffset: 1, type: 0x03)]);   // child Polygon 2
        AreEqual("MULTIPOLYGON (((0 0, 1 0, 0 1, 0 0)), ((5 5, 6 5, 5 6, 5 5)))",
            SpatialWkbDecoder.TryDecode(wkb, isGeography: false));
    }

    [TestMethod]
    public void GeometryCollection_PointAndLineString()
    {
        var wkb = BuildFullShape(
            srid: 0,
            points: [(1, 2), (10, 20), (30, 40)],
            figurePointStarts: [0, 1],
            shapes: [
                (parent: -1, figOffset: -1, type: 0x07),   // root GeometryCollection
                (parent: 0, figOffset: 0, type: 0x01),     // child Point
                (parent: 0, figOffset: 1, type: 0x02)]);   // child LineString
        AreEqual("GEOMETRYCOLLECTION (POINT (1 2), LINESTRING (10 20, 30 40))",
            SpatialWkbDecoder.TryDecode(wkb, isGeography: false));
    }

    [TestMethod]
    public void Truncated_Payload_ReturnsNull()
        => IsNull(SpatialWkbDecoder.TryDecode(new byte[3], isGeography: true));

    [TestMethod]
    public void ZorM_Bits_ReturnsNull()
    {
        var wkb = BuildSimplePoint(srid: 4326, firstCoord: 0, secondCoord: 0);
        wkb[5] = 0x08 | 0x01; // IsSinglePoint + HasZ — Z/M not supported.
        IsNull(SpatialWkbDecoder.TryDecode(wkb, isGeography: true));
    }

    [TestMethod]
    public void UnknownVersion_ReturnsNull()
    {
        var wkb = BuildSimplePoint(srid: 4326, firstCoord: 0, secondCoord: 0);
        wkb[4] = 0xFF;
        IsNull(SpatialWkbDecoder.TryDecode(wkb, isGeography: true));
    }

    [TestMethod]
    public void UnsupportedShapeType_ReturnsNull()
    {
        // Shape type 0x99 isn't one of Point/Line/Poly/Multi*/Collection.
        var wkb = BuildFullShape(
            srid: 0,
            points: [(0, 0)],
            figurePointStarts: [0],
            shapes: [(parent: -1, figOffset: 0, type: 0x99)]);
        IsNull(SpatialWkbDecoder.TryDecode(wkb, isGeography: false));
    }

    /// <summary>
    /// Builds a Microsoft-spatial-binary simple-point payload (22 bytes).
    /// </summary>
    private static byte[] BuildSimplePoint(int srid, double firstCoord, double secondCoord)
    {
        var buf = new byte[22];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), srid);
        buf[4] = 0x01; // version
        buf[5] = 0x08; // IsSinglePoint
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(6, 8), firstCoord);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(14, 8), secondCoord);
        return buf;
    }

    /// <summary>
    /// Builds a shortcut-form single LineString payload.
    /// </summary>
    private static byte[] BuildShortcutLineString(int srid, params (double first, double second)[] coords)
    {
        var buf = new byte[10 + (coords.Length * 16)];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), srid);
        buf[4] = 0x01;
        buf[5] = 0x10; // IsSingleLineString
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(6, 4), coords.Length);
        for (var i = 0; i < coords.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(10 + (i * 16), 8), coords[i].first);
            BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(10 + (i * 16) + 8, 8), coords[i].second);
        }
        return buf;
    }

    /// <summary>
    /// Builds a full-form MS spatial binary payload (no shortcut bits).
    /// Header is 6 bytes (SRID + version=1 + properties=0); the figure
    /// attribute byte is hard-coded to 0x01 (stroke) — the decoder ignores
    /// the attribute and infers role from shape type + position.
    /// </summary>
    private static byte[] BuildFullShape(
        int srid,
        (double a, double b)[] points,
        int[] figurePointStarts,
        (int parent, int figOffset, byte type)[] shapes)
    {
        var size = 6 + 4 + (points.Length * 16) + 4 + (figurePointStarts.Length * 5) + 4 + (shapes.Length * 9);
        var buf = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), srid);
        buf[4] = 0x01;
        buf[5] = 0x00;
        var pos = 6;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), points.Length);
        pos += 4;
        foreach (var (a, b) in points)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(pos, 8), a);
            BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(pos + 8, 8), b);
            pos += 16;
        }
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), figurePointStarts.Length);
        pos += 4;
        foreach (var fp in figurePointStarts)
        {
            buf[pos] = 0x01;
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos + 1, 4), fp);
            pos += 5;
        }
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), shapes.Length);
        pos += 4;
        foreach (var (parent, figOffset, type) in shapes)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos, 4), parent);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos + 4, 4), figOffset);
            buf[pos + 8] = type;
            pos += 9;
        }
        return buf;
    }
}
