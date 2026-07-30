using System.Buffers.Binary;

namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// Reads and writes SQL Server's spatial UDT serialization — the byte form a
/// <c>geography</c> / <c>geometry</c> value takes on disk, in
/// <c>CAST(… AS varbinary(max))</c>, and on the TDS wire. Despite the
/// resemblance this is <b>not</b> OGC WKB; that separate encoding lives in
/// <see cref="SpatialWkb"/>.
/// </summary>
/// <remarks>
/// <para>Layout: 4-byte SRID, 1-byte version, 1-byte properties bitfield, then
/// either a shortcut body or the full tables
/// <c>numPoints + points[] + z[] + m[] + numFigures + figures[] + numShapes +
/// shapes[]</c>. A single point and a two-point LineString take shortcut
/// bodies that omit the tables entirely.</para>
/// <para>Each figure is <c>[1-byte attribute][4-byte point offset]</c>; the
/// attribute is <c>0x01</c> for a point or line figure, <c>0x02</c> for a
/// polygon's exterior ring and <c>0x00</c> for its interior rings. Each shape
/// is <c>[4-byte parent offset][4-byte figure offset][1-byte type]</c>, laid
/// out depth-first, where a shape's figure offset is the index of the first
/// figure anywhere in its subtree and <c>-1</c> when its subtree has none
/// (which is how an empty instance is expressed). The root's parent offset is
/// <c>-1</c>.</para>
/// <para><b>Axis order</b>: geography stores <c>(latitude, longitude)</c>
/// while the model and WKT hold <c>(longitude, latitude)</c>, so the codec
/// swaps on the way in and out. Geometry stores <c>(x, y)</c> throughout.</para>
/// </remarks>
internal static class SpatialBinaryCodec
{
    private const byte HasZ = 0x01;
    private const byte HasM = 0x02;
    private const byte IsValid = 0x04;
    private const byte IsSinglePoint = 0x08;
    private const byte IsSingleLineSegment = 0x10;

    private const byte FigureStroke = 0x01;
    private const byte FigureExteriorRing = 0x02;
    private const byte FigureInteriorRing = 0x00;

    /// <summary>
    /// Decodes a payload, returning null when it is malformed, carries an
    /// unsupported version, or holds a shape kind with no model — the BACPAC
    /// row loader relies on that so one unreadable value doesn't fail an
    /// import.
    /// </summary>
    public static SpatialGeometry? TryDecode(ReadOnlySpan<byte> bytes, bool isGeography)
    {
        try
        {
            return Decode(bytes, isGeography);
        }
        catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a payload, raising the 24xxx failure real raises for a version
    /// it doesn't accept. Used by the CAST-from-varbinary path, where real
    /// surfaces the failure rather than yielding NULL.
    /// </summary>
    public static SpatialGeometry Decode(ReadOnlySpan<byte> bytes, bool isGeography)
    {
        if (bytes.Length < 6)
            throw SimulatedSqlException.SpatialUnexpectedEndOfInput(isGeography);
        var srid = BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]);
        var version = bytes[4];
        if (version is not (1 or 2))
            throw SimulatedSqlException.SpatialUnexpectedVersion(isGeography, version);
        var properties = bytes[5];
        var hasZ = (properties & HasZ) != 0;
        var hasM = (properties & HasM) != 0;
        var ordinates = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);

        if ((properties & IsSinglePoint) != 0)
        {
            var inline = ReadInterleaved(bytes[6..], 1, ordinates, hasZ, hasM, isGeography);
            return new SpatialGeometry(srid, SpatialShape.Leaf(SpatialShapeType.Point, [inline]));
        }

        if ((properties & IsSingleLineSegment) != 0)
        {
            var inline = ReadInterleaved(bytes[6..], 2, ordinates, hasZ, hasM, isGeography);
            return new SpatialGeometry(srid, SpatialShape.Leaf(SpatialShapeType.LineString, [inline]));
        }

        return new SpatialGeometry(srid, DecodeFull(bytes, hasZ, hasM, isGeography));
    }

    /// <summary>
    /// Reads the shortcut bodies, whose ordinates are interleaved per point
    /// rather than split into per-ordinate arrays as the full layout does.
    /// </summary>
    private static SpatialCoordinate[] ReadInterleaved(ReadOnlySpan<byte> body, int count, int ordinates, bool hasZ, bool hasM, bool isGeography)
    {
        if (body.Length < count * ordinates * 8)
            throw SimulatedSqlException.SpatialUnexpectedEndOfInput(isGeography);
        var points = new SpatialCoordinate[count];
        for (var i = 0; i < count; i++)
        {
            var at = i * ordinates * 8;
            var first = BinaryPrimitives.ReadDoubleLittleEndian(body.Slice(at, 8));
            var second = BinaryPrimitives.ReadDoubleLittleEndian(body.Slice(at + 8, 8));
            var z = hasZ ? BinaryPrimitives.ReadDoubleLittleEndian(body.Slice(at + 16, 8)) : (double?)null;
            var m = hasM ? BinaryPrimitives.ReadDoubleLittleEndian(body.Slice(at + (hasZ ? 24 : 16), 8)) : (double?)null;
            points[i] = MakeCoordinate(first, second, z, m, isGeography);
        }
        return points;
    }

    private static SpatialCoordinate MakeCoordinate(double first, double second, double? z, double? m, bool isGeography) =>
        isGeography ? new SpatialCoordinate(second, first, Defined(z), Defined(m)) : new SpatialCoordinate(first, second, Defined(z), Defined(m));

    /// <summary>NaN is the format's "this point has no value here" marker within an otherwise Z- or M-bearing instance.</summary>
    private static double? Defined(double? value) => value is { } v && !double.IsNaN(v) ? v : null;

    private static SpatialShape DecodeFull(ReadOnlySpan<byte> bytes, bool hasZ, bool hasM, bool isGeography)
    {
        var at = 6;
        var pointCount = ReadInt32(bytes, ref at);
        if (pointCount < 0)
            throw SimulatedSqlException.SpatialUnexpectedEndOfInput(isGeography);
        var coordinates = new (double First, double Second)[pointCount];
        for (var i = 0; i < pointCount; i++)
        {
            coordinates[i].First = ReadDouble(bytes, ref at);
            coordinates[i].Second = ReadDouble(bytes, ref at);
        }
        var z = ReadOrdinateArray(bytes, ref at, pointCount, hasZ);
        var m = ReadOrdinateArray(bytes, ref at, pointCount, hasM);

        var points = new SpatialCoordinate[pointCount];
        for (var i = 0; i < pointCount; i++)
            points[i] = MakeCoordinate(coordinates[i].First, coordinates[i].Second, z?[i], m?[i], isGeography);

        var figureCount = ReadInt32(bytes, ref at);
        if (figureCount < 0)
            throw SimulatedSqlException.SpatialUnexpectedEndOfInput(isGeography);
        var figureStart = new int[figureCount];
        for (var i = 0; i < figureCount; i++)
        {
            at++; // figure attribute — the owning shape's kind and the figure's position already determine its role
            figureStart[i] = ReadInt32(bytes, ref at);
        }

        var shapeCount = ReadInt32(bytes, ref at);
        if (shapeCount <= 0)
            throw SimulatedSqlException.SpatialUnexpectedEndOfInput(isGeography);
        var shapes = new (int Parent, int Figure, byte Type)[shapeCount];
        for (var i = 0; i < shapeCount; i++)
        {
            shapes[i].Parent = ReadInt32(bytes, ref at);
            shapes[i].Figure = ReadInt32(bytes, ref at);
            shapes[i].Type = bytes[at++];
        }

        return BuildShape(0, shapes, figureStart, points);
    }

    private static double[]? ReadOrdinateArray(ReadOnlySpan<byte> bytes, ref int at, int count, bool present)
    {
        if (!present)
            return null;
        var values = new double[count];
        for (var i = 0; i < count; i++)
            values[i] = ReadDouble(bytes, ref at);
        return values;
    }

    private static SpatialShape BuildShape(
        int index,
        (int Parent, int Figure, byte Type)[] shapes,
        int[] figureStart,
        SpatialCoordinate[] points)
    {
        var (_, figure, rawType) = shapes[index];
        if (!Enum.IsDefined((SpatialShapeType)rawType))
            throw new NotSupportedException($"Spatial shape type {rawType} is not modeled.");
        var type = (SpatialShapeType)rawType;

        var children = new List<SpatialShape>();
        for (var i = index + 1; i < shapes.Length; i++)
        {
            if (shapes[i].Parent == index)
                children.Add(BuildShape(i, shapes, figureStart, points));
        }
        if (children.Count > 0)
            return SpatialShape.Collection(type, [.. children]);

        if (figure < 0)
            return SpatialShape.Empty(type);

        // Figure offsets are non-decreasing in depth-first order, so this
        // shape owns figures up to the next strictly larger offset.
        var end = figureStart.Length;
        for (var i = index + 1; i < shapes.Length; i++)
        {
            if (shapes[i].Figure > figure)
            {
                end = shapes[i].Figure;
                break;
            }
        }

        var figures = new SpatialCoordinate[end - figure][];
        for (var f = figure; f < end; f++)
        {
            var start = figureStart[f];
            var stop = f + 1 < figureStart.Length ? figureStart[f + 1] : points.Length;
            figures[f - figure] = points[start..stop];
        }
        return SpatialShape.Leaf(type, figures);
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, ref int at)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at, 4));
        at += 4;
        return value;
    }

    private static double ReadDouble(ReadOnlySpan<byte> bytes, ref int at)
    {
        var value = BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(at, 8));
        at += 8;
        return value;
    }

    /// <summary>
    /// Encodes an instance. The <c>isValid</c> property bit is always set: the
    /// simulator has no topological validator to clear it with, which is the
    /// one documented byte-level divergence from real (see
    /// <c>docs/claude/spatial.md</c>).
    /// </summary>
    public static byte[] Encode(SpatialGeometry geometry, bool isGeography)
    {
        var root = geometry.Root;
        if (root.Type >= SpatialShapeType.CircularString)
            throw new NotSupportedException($"Encoding the spatial shape {root.Type} is not modeled.");

        var hasZ = root.AnyHasZ;
        var hasM = root.AnyHasM;
        var properties = (byte)(IsValid | (hasZ ? HasZ : 0) | (hasM ? HasM : 0));

        if (root.SinglePoint is { } single)
            return EncodeShortcut(geometry.Srid, (byte)(properties | IsSinglePoint), [single], hasZ, hasM, isGeography);
        if (root.Type == SpatialShapeType.LineString && root.Figures.Length == 1 && root.Figures[0].Length == 2)
            return EncodeShortcut(geometry.Srid, (byte)(properties | IsSingleLineSegment), root.Figures[0], hasZ, hasM, isGeography);

        var points = new List<SpatialCoordinate>();
        var figures = new List<(byte Attribute, int Start)>();
        var shapes = new List<(int Parent, int Figure, byte Type)>();
        Flatten(root, parent: -1, points, figures, shapes);

        var writer = new SpatialByteWriter(geometry.Srid, properties);
        writer.WriteInt32(points.Count);
        foreach (var point in points)
            writer.WritePair(point, isGeography);
        if (hasZ)
        {
            foreach (var point in points)
                writer.WriteDouble(point.Z ?? double.NaN);
        }
        if (hasM)
        {
            foreach (var point in points)
                writer.WriteDouble(point.M ?? double.NaN);
        }
        writer.WriteInt32(figures.Count);
        foreach (var (attribute, start) in figures)
        {
            writer.WriteByte(attribute);
            writer.WriteInt32(start);
        }
        writer.WriteInt32(shapes.Count);
        foreach (var (parent, figure, type) in shapes)
        {
            writer.WriteInt32(parent);
            writer.WriteInt32(figure);
            writer.WriteByte(type);
        }
        return writer.ToArray();
    }

    private static byte[] EncodeShortcut(int srid, byte properties, SpatialCoordinate[] points, bool hasZ, bool hasM, bool isGeography)
    {
        var writer = new SpatialByteWriter(srid, properties);
        foreach (var point in points)
        {
            writer.WritePair(point, isGeography);
            if (hasZ)
                writer.WriteDouble(point.Z ?? double.NaN);
            if (hasM)
                writer.WriteDouble(point.M ?? double.NaN);
        }
        return writer.ToArray();
    }

    /// <summary>
    /// Appends one shape's figures and its subtree's shapes in depth-first
    /// order, returning the index it took. A shape whose whole subtree has no
    /// figures records figure offset <c>-1</c>.
    /// </summary>
    private static void Flatten(
        SpatialShape shape,
        int parent,
        List<SpatialCoordinate> points,
        List<(byte Attribute, int Start)> figures,
        List<(int Parent, int Figure, byte Type)> shapes)
    {
        var index = shapes.Count;
        var firstFigure = figures.Count;
        shapes.Add((parent, firstFigure, (byte)shape.Type));

        var isPolygon = shape.Type is SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon;
        for (var i = 0; i < shape.Figures.Length; i++)
        {
            figures.Add((isPolygon ? (i == 0 ? FigureExteriorRing : FigureInteriorRing) : FigureStroke, points.Count));
            points.AddRange(shape.Figures[i]);
        }

        foreach (var child in shape.Children)
            Flatten(child, index, points, figures, shapes);

        if (figures.Count == firstFigure)
            shapes[index] = (parent, -1, (byte)shape.Type);
    }

    /// <summary>Little-endian append-only writer seeded with the fixed header.</summary>
    private readonly struct SpatialByteWriter
    {
        private readonly List<byte> bytes;

        public SpatialByteWriter(int srid, byte properties)
        {
            this.bytes = new List<byte>(64);
            WriteInt32(srid);
            WriteByte(1);
            WriteByte(properties);
        }

        public readonly void WriteByte(byte value) => this.bytes.Add(value);

        public readonly void WriteInt32(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            this.bytes.AddRange(buffer);
        }

        public readonly void WriteDouble(double value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
            this.bytes.AddRange(buffer);
        }

        public readonly void WritePair(SpatialCoordinate point, bool isGeography)
        {
            WriteDouble(isGeography ? point.Y : point.X);
            WriteDouble(isGeography ? point.X : point.Y);
        }

        public readonly byte[] ToArray() => [.. this.bytes];
    }
}
