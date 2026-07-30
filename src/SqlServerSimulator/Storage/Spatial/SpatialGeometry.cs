namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// Shape kinds an OGC instance can take. Values are the shape-type byte of
/// SQL Server's spatial UDT serialization, so the binary codec writes the
/// enum directly.
/// </summary>
internal enum SpatialShapeType : byte
{
    Point = 1,
    LineString = 2,
    Polygon = 3,
    MultiPoint = 4,
    MultiLineString = 5,
    MultiPolygon = 6,
    GeometryCollection = 7,

    /// <summary>Curved shape — recognized so the WKT label list and the
    /// binary reader name it, but no operation evaluates one.</summary>
    CircularString = 8,

    /// <inheritdoc cref="CircularString"/>
    CompoundCurve = 9,

    /// <inheritdoc cref="CircularString"/>
    CurvePolygon = 10,

    /// <summary>The whole-earth <c>geography</c> instance. Recognized like
    /// the curved shapes above; no operation evaluates one.</summary>
    FullGlobe = 11,
}

/// <summary>
/// One coordinate of a spatial instance. <see cref="X"/> / <see cref="Y"/>
/// are always in <b>WKT axis order</b> — for <c>geography</c> that is
/// (longitude, latitude), the reverse of the order the binary form stores.
/// Keeping one convention in the model means only the binary codec swaps.
/// </summary>
/// <remarks>
/// <see cref="Z"/> and <see cref="M"/> are per-coordinate because WKT admits
/// a literal <c>NULL</c> in either slot (<c>POINT(1 2 NULL 4)</c>), so a
/// Z-bearing instance can still carry a missing Z on an individual point.
/// </remarks>
internal readonly struct SpatialCoordinate(double x, double y, double? z = null, double? m = null) : IEquatable<SpatialCoordinate>
{
    public readonly double X = Normalize(x);

    public readonly double Y = Normalize(y);

    public readonly double? Z = z is { } zv ? Normalize(zv) : null;

    public readonly double? M = m is { } mv ? Normalize(mv) : null;

    /// <summary>
    /// Folds negative zero onto positive zero. Real does this on the way in —
    /// <c>POINT(-0 -0)</c> stores and prints as <c>POINT (0 0)</c> — so
    /// normalizing at construction keeps the text, binary and property reads
    /// consistent with it.
    /// </summary>
    private static double Normalize(double value) => value == 0 ? 0 : value;

    public bool Equals(SpatialCoordinate other) =>
        this.X.Equals(other.X) && this.Y.Equals(other.Y) && Nullable.Equals(this.Z, other.Z) && Nullable.Equals(this.M, other.M);

    public override bool Equals(object? obj) => obj is SpatialCoordinate other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.X, this.Y, this.Z, this.M);

    public static bool operator ==(SpatialCoordinate left, SpatialCoordinate right) => left.Equals(right);

    public static bool operator !=(SpatialCoordinate left, SpatialCoordinate right) => !left.Equals(right);
}

/// <summary>
/// One node of a spatial instance's shape tree.
/// </summary>
/// <remarks>
/// <para><see cref="Figures"/> holds the shape's own point runs: a Point has
/// one figure of one coordinate, a LineString one figure of its vertices, a
/// Polygon one figure per ring with the exterior ring first. An empty shape
/// has no figures.</para>
/// <para><see cref="Children"/> holds member shapes of MultiPoint /
/// MultiLineString / MultiPolygon / GeometryCollection; those kinds carry no
/// figures of their own. The split mirrors the figure and shape tables of the
/// binary form, so <see cref="SpatialBinaryCodec"/> walks the tree without an
/// intermediate representation.</para>
/// </remarks>
internal sealed class SpatialShape(SpatialShapeType type, SpatialCoordinate[][] figures, SpatialShape[] children)
{
    public static readonly SpatialCoordinate[][] NoFigures = [];

    public static readonly SpatialShape[] NoChildren = [];

    public readonly SpatialShapeType Type = type;

    public readonly SpatialCoordinate[][] Figures = figures;

    public readonly SpatialShape[] Children = children;

    public static SpatialShape Leaf(SpatialShapeType type, SpatialCoordinate[][] figures) => new(type, figures, NoChildren);

    public static SpatialShape Collection(SpatialShapeType type, SpatialShape[] children) => new(type, NoFigures, children);

    public static SpatialShape Empty(SpatialShapeType type) => new(type, NoFigures, NoChildren);

    /// <summary>Single coordinate of a non-empty Point, else null.</summary>
    public SpatialCoordinate? SinglePoint =>
        this.Type == SpatialShapeType.Point && this.Figures.Length == 1 && this.Figures[0].Length == 1
            ? this.Figures[0][0]
            : null;

    /// <summary>
    /// True when the shape holds no coordinates anywhere beneath it. Matches
    /// <c>STIsEmpty()</c>: a collection whose members are all empty is itself
    /// empty.
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            foreach (var figure in this.Figures)
            {
                if (figure.Length > 0)
                    return false;
            }
            foreach (var child in this.Children)
            {
                if (!child.IsEmpty)
                    return false;
            }
            return this.Type != SpatialShapeType.FullGlobe;
        }
    }

    /// <summary>Total coordinate count beneath this shape — <c>STNumPoints()</c>.</summary>
    public int PointCount
    {
        get
        {
            var total = 0;
            foreach (var figure in this.Figures)
                total += figure.Length;
            foreach (var child in this.Children)
                total += child.PointCount;
            return total;
        }
    }

    /// <summary>
    /// Topological dimension — <c>STDimension()</c>. An empty instance of any
    /// kind reports -1; a collection reports the largest dimension among its
    /// non-empty members.
    /// </summary>
    public int Dimension
    {
        get
        {
            if (this.IsEmpty)
                return -1;
            switch (this.Type)
            {
                case SpatialShapeType.Point:
                case SpatialShapeType.MultiPoint:
                    return 0;
                case SpatialShapeType.LineString:
                case SpatialShapeType.MultiLineString:
                case SpatialShapeType.CircularString:
                case SpatialShapeType.CompoundCurve:
                    return 1;
                case SpatialShapeType.Polygon:
                case SpatialShapeType.MultiPolygon:
                case SpatialShapeType.CurvePolygon:
                case SpatialShapeType.FullGlobe:
                    return 2;
                default:
                    var best = -1;
                    foreach (var child in this.Children)
                        best = Math.Max(best, child.Dimension);
                    return best;
            }
        }
    }

    /// <summary>
    /// Walks every coordinate in figure order, descending into children —
    /// the order <c>STPointN()</c> indexes.
    /// </summary>
    public IEnumerable<SpatialCoordinate> Coordinates()
    {
        foreach (var figure in this.Figures)
        {
            foreach (var point in figure)
                yield return point;
        }
        foreach (var child in this.Children)
        {
            foreach (var point in child.Coordinates())
                yield return point;
        }
    }

    /// <summary>True when any coordinate beneath this shape carries a Z (or M) ordinate.</summary>
    public bool AnyHasZ => Any(static p => p.Z.HasValue);

    /// <inheritdoc cref="AnyHasZ"/>
    public bool AnyHasM => Any(static p => p.M.HasValue);

    private bool Any(Func<SpatialCoordinate, bool> predicate)
    {
        foreach (var point in Coordinates())
        {
            if (predicate(point))
                return true;
        }
        return false;
    }
}

/// <summary>
/// A parsed <c>geography</c> / <c>geometry</c> value: a spatial reference
/// identifier plus the shape tree. This is what a spatial
/// <see cref="SqlValue"/> carries in memory; the storage and wire forms are
/// produced by <see cref="SpatialBinaryCodec"/> on demand.
/// </summary>
/// <remarks>
/// The instance is axis-neutral — coordinates are held in WKT order for both
/// spatial types (see <see cref="SpatialCoordinate"/>), so only the binary
/// codec knows about geography's reversed storage order.
/// </remarks>
internal sealed class SpatialGeometry(int srid, SpatialShape root)
{
    /// <summary>Default SRID of a <c>geography</c> value — WGS 84.</summary>
    public const int DefaultGeographySrid = 4326;

    /// <summary>Default SRID of a <c>geometry</c> value — the undefined planar system.</summary>
    public const int DefaultGeometrySrid = 0;

    /// <summary>Largest SRID real accepts; anything outside 0..this raises Msg 24100.</summary>
    public const int MaxSrid = 999999;

    public readonly int Srid = srid;

    public readonly SpatialShape Root = root;

    private byte[]? encoded;
    private bool encodedIsGeography;

    /// <summary>
    /// The UDT serialization of this instance, cached because the row encoder
    /// asks for the byte count and the bytes in separate calls. An instance
    /// only ever belongs to one spatial type, so the flag guard is a
    /// correctness backstop rather than a real second cache slot.
    /// </summary>
    public byte[] Encoded(bool isGeography)
    {
        if (this.encoded is null || this.encodedIsGeography != isGeography)
        {
            this.encoded = SpatialBinaryCodec.Encode(this, isGeography);
            this.encodedIsGeography = isGeography;
        }
        return this.encoded;
    }

    /// <summary>Returns this instance re-stamped with a different SRID — the settable <c>STSrid</c> property.</summary>
    public SpatialGeometry WithSrid(int srid) => new(srid, this.Root);

    public static int DefaultSridFor(bool isGeography) => isGeography ? DefaultGeographySrid : DefaultGeometrySrid;

    /// <summary>Returns <paramref name="srid"/>, or raises Msg 24100 when it falls outside real's accepted domain.</summary>
    public static int ValidateSrid(int srid, bool isGeography) =>
        srid is >= 0 and <= MaxSrid ? srid : throw SimulatedSqlException.SpatialInvalidSrid(isGeography);
}
