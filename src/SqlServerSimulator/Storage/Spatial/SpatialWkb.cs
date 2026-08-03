using System.Buffers.Binary;

namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// OGC Well-Known Binary — the encoding <c>STAsBinary()</c> / <c>AsBinaryZM()</c>
/// produce and <c>ST<i>Kind</i>FromWKB</c> consume. Distinct from the UDT
/// serialization in <see cref="SpatialBinaryCodec"/>, which is what the value
/// actually stores.
/// </summary>
/// <remarks>
/// <para>Every geometry record is <c>[1-byte byte order][4-byte type]</c> plus
/// a body; the simulator writes little-endian (<c>0x01</c>) and reads either.
/// Collection members carry their own header, so a MultiPolygon nests full
/// records rather than bare bodies.</para>
/// <para>Z and M ride the ISO type codes — <c>+1000</c> for Z, <c>+2000</c>
/// for M, <c>+3000</c> for both — which is what <c>AsBinaryZM()</c> emits.
/// <c>STAsBinary()</c> drops them and writes the plain 2D codes.</para>
/// <para>Coordinates are in WKT axis order for both spatial types, so a
/// geography point writes (longitude, latitude) here even though it stores
/// the reverse.</para>
/// </remarks>
internal static class SpatialWkb
{
    private const uint ZOffset = 1000;
    private const uint MOffset = 2000;

    public static byte[] Write(SpatialGeometry geometry, bool includeZM)
    {
        var root = geometry.Root;
        if (root.Type >= SpatialShapeType.CircularString)
            throw new NotSupportedException($"Encoding the spatial shape {root.Type} as well-known binary is not modeled.");
        var hasZ = includeZM && root.AnyHasZ;
        var hasM = includeZM && root.AnyHasM;
        var bytes = new List<byte>(64);
        WriteShape(bytes, root, hasZ, hasM);
        return [.. bytes];
    }

    private static void WriteShape(List<byte> bytes, SpatialShape shape, bool hasZ, bool hasM)
    {
        bytes.Add(0x01);
        WriteUInt32(bytes, (uint)shape.Type + (hasZ ? ZOffset : 0) + (hasM ? MOffset : 0));

        switch (shape.Type)
        {
            case SpatialShapeType.Point:
                WritePoint(bytes, shape.Figures.Length == 1 && shape.Figures[0].Length == 1 ? shape.Figures[0][0] : EmptyPoint, hasZ, hasM);
                return;
            case SpatialShapeType.LineString:
                WriteFigure(bytes, shape.Figures.Length == 1 ? shape.Figures[0] : [], hasZ, hasM);
                return;
            case SpatialShapeType.Polygon:
                WriteUInt32(bytes, (uint)shape.Figures.Length);
                foreach (var ring in shape.Figures)
                    WriteFigure(bytes, ring, hasZ, hasM);
                return;
            default:
                WriteUInt32(bytes, (uint)shape.Children.Length);
                foreach (var child in shape.Children)
                    WriteShape(bytes, child, hasZ, hasM);
                return;
        }
    }

    /// <summary>OGC has no empty-point record; real writes NaN ordinates, which is what round-trips.</summary>
    private static readonly SpatialCoordinate EmptyPoint = new(double.NaN, double.NaN);

    private static void WriteFigure(List<byte> bytes, SpatialCoordinate[] points, bool hasZ, bool hasM)
    {
        WriteUInt32(bytes, (uint)points.Length);
        foreach (var point in points)
            WritePoint(bytes, point, hasZ, hasM);
    }

    private static void WritePoint(List<byte> bytes, SpatialCoordinate point, bool hasZ, bool hasM)
    {
        WriteDouble(bytes, point.X);
        WriteDouble(bytes, point.Y);
        if (hasZ)
            WriteDouble(bytes, point.Z ?? double.NaN);
        if (hasM)
            WriteDouble(bytes, point.M ?? double.NaN);
    }

    private static void WriteUInt32(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void WriteDouble(List<byte> bytes, double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    /// <summary>
    /// Reads a well-known binary instance.
    /// </summary>
    /// <param name="bytes">The encoded geometry.</param>
    /// <param name="srid">Spatial reference id to stamp on the result — WKB carries none.</param>
    /// <param name="isGeography">True to apply geography's latitude-domain check.</param>
    /// <param name="requiredType">When set, the only shape kind this call accepts.</param>
    public static SpatialGeometry Read(ReadOnlySpan<byte> bytes, int srid, bool isGeography, SpatialShapeType? requiredType = null)
    {
        var at = 0;
        var shape = ReadShape(bytes, ref at, isGeography);
        if (requiredType is { } required && shape.Type != required)
            throw SimulatedSqlException.SpatialInvalidOpenGisType(isGeography, shape.Type.ToString());
        if (isGeography)
            SpatialGeodeticValidator.RejectAntipodalEdges(shape);
        return new SpatialGeometry(srid, shape);
    }

    private static SpatialShape ReadShape(ReadOnlySpan<byte> bytes, ref int at, bool isGeography)
    {
        if (at + 5 > bytes.Length)
            throw SimulatedSqlException.SpatialUnexpectedEndOfInput(isGeography);
        var littleEndian = bytes[at++] == 0x01;
        var code = ReadUInt32(bytes, ref at, littleEndian, isGeography);
        var hasZ = code / ZOffset % 2 == 1;
        var hasM = code >= MOffset && code / MOffset % 2 == 1;
        var kind = code % ZOffset;
        if (kind is < 1 or > 7)
            throw new NotSupportedException($"Well-known binary shape type {code} is not modeled.");
        var type = (SpatialShapeType)kind;

        switch (type)
        {
            case SpatialShapeType.Point:
                var point = ReadPoint(bytes, ref at, littleEndian, hasZ, hasM, isGeography);
                return double.IsNaN(point.X) && double.IsNaN(point.Y)
                    ? SpatialShape.Empty(type)
                    : SpatialShape.Leaf(type, [[point]]);
            case SpatialShapeType.LineString:
                return SpatialShape.Leaf(type, [ReadFigure(bytes, ref at, littleEndian, hasZ, hasM, isGeography)]);
            case SpatialShapeType.Polygon:
                var ringCount = (int)ReadUInt32(bytes, ref at, littleEndian, isGeography);
                var rings = new SpatialCoordinate[ringCount][];
                for (var i = 0; i < ringCount; i++)
                    rings[i] = ReadFigure(bytes, ref at, littleEndian, hasZ, hasM, isGeography);
                return SpatialShape.Leaf(type, rings);
            default:
                var childCount = (int)ReadUInt32(bytes, ref at, littleEndian, isGeography);
                var children = new SpatialShape[childCount];
                for (var i = 0; i < childCount; i++)
                    children[i] = ReadShape(bytes, ref at, isGeography);
                return SpatialShape.Collection(type, children);
        }
    }

    private static SpatialCoordinate[] ReadFigure(ReadOnlySpan<byte> bytes, ref int at, bool littleEndian, bool hasZ, bool hasM, bool isGeography)
    {
        var count = (int)ReadUInt32(bytes, ref at, littleEndian, isGeography);
        var points = new SpatialCoordinate[count];
        for (var i = 0; i < count; i++)
            points[i] = ReadPoint(bytes, ref at, littleEndian, hasZ, hasM, isGeography);
        return points;
    }

    private static SpatialCoordinate ReadPoint(ReadOnlySpan<byte> bytes, ref int at, bool littleEndian, bool hasZ, bool hasM, bool isGeography)
    {
        var x = ReadDouble(bytes, ref at, littleEndian, isGeography);
        var y = ReadDouble(bytes, ref at, littleEndian, isGeography);
        if (isGeography && !double.IsNaN(y) && (y < -90 || y > 90))
            throw SimulatedSqlException.SpatialLatitudeOutOfRange();
        var z = hasZ ? ReadDouble(bytes, ref at, littleEndian, isGeography) : (double?)null;
        var m = hasM ? ReadDouble(bytes, ref at, littleEndian, isGeography) : (double?)null;
        return new SpatialCoordinate(x, y, z is { } zv && !double.IsNaN(zv) ? zv : null, m is { } mv && !double.IsNaN(mv) ? mv : null);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int at, bool littleEndian, bool isGeography)
    {
        if (at + 4 > bytes.Length)
            throw SimulatedSqlException.SpatialUnexpectedEndOfInput(isGeography);
        var slice = bytes.Slice(at, 4);
        at += 4;
        return littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(slice) : BinaryPrimitives.ReadUInt32BigEndian(slice);
    }

    private static double ReadDouble(ReadOnlySpan<byte> bytes, ref int at, bool littleEndian, bool isGeography)
    {
        if (at + 8 > bytes.Length)
            throw SimulatedSqlException.SpatialUnexpectedEndOfInput(isGeography);
        var slice = bytes.Slice(at, 8);
        at += 8;
        return littleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(slice) : BinaryPrimitives.ReadDoubleBigEndian(slice);
    }
}
