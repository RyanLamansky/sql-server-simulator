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

    /// <summary>A run of circular arcs, each through three points, consecutive arcs sharing an endpoint.</summary>
    CircularString = 8,

    /// <summary>A chain of line and arc segments; its one figure is <see cref="SpatialFigureType.Composite"/>.</summary>
    CompoundCurve = 9,

    /// <summary>A polygon whose rings may be line, arc or composite figures.</summary>
    CurvePolygon = 10,

    /// <summary>The whole-earth <c>geography</c> instance. Recognized like
    /// the curved shapes above; no operation evaluates one.</summary>
    FullGlobe = 11,
}

/// <summary>
/// What one figure's points trace. Values are the figure-attribute byte of the
/// version 2 serialization; version 1 writes its own polygon-ring attributes
/// instead, which is why a plain instance never needs this.
/// </summary>
internal enum SpatialFigureType : byte
{
    /// <summary>Straight segments between consecutive points — also a point figure.</summary>
    Line = 1,

    /// <summary>Circular arcs, each through three points, consecutive arcs sharing an endpoint.</summary>
    Arc = 2,

    /// <summary>A mix of both, spelled out by the figure's <see cref="SpatialSegmentType"/> run.</summary>
    Composite = 3,
}

/// <summary>
/// One segment of a composite figure, as the version 2 serialization's segment
/// table writes it. A line segment consumes one further point and an arc two;
/// the <c>First</c> kinds open each run written as its own WKT element, which is
/// what lets a <c>COMPOUNDCURVE</c> print back as it was written.
/// </summary>
internal enum SpatialSegmentType : byte
{
    Line = 0,
    Arc = 1,
    FirstLine = 2,
    FirstArc = 3,
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
internal sealed class SpatialShape(
    SpatialShapeType type,
    SpatialCoordinate[][] figures,
    SpatialShape[] children,
    SpatialFigureType[]? figureTypes = null,
    SpatialSegmentType[]?[]? segments = null)
{
    public static readonly SpatialCoordinate[][] NoFigures = [];

    public static readonly SpatialShape[] NoChildren = [];

    public readonly SpatialShapeType Type = type;

    public readonly SpatialCoordinate[][] Figures = figures;

    public readonly SpatialShape[] Children = children;

    /// <summary>
    /// Each figure's <see cref="SpatialFigureType"/>, parallel to
    /// <see cref="Figures"/>; null when every figure is a plain
    /// <see cref="SpatialFigureType.Line"/>, which is every figure of the seven
    /// plain kinds.
    /// </summary>
    public readonly SpatialFigureType[]? FigureTypes = figureTypes;

    /// <summary>A <see cref="SpatialFigureType.Composite"/> figure's segment run, parallel to <see cref="Figures"/>; null elsewhere.</summary>
    public readonly SpatialSegmentType[]?[]? Segments = segments;

    public static SpatialShape Leaf(SpatialShapeType type, SpatialCoordinate[][] figures) => new(type, figures, NoChildren);

    /// <summary>A curved leaf: a <c>CIRCULARSTRING</c>, <c>COMPOUNDCURVE</c> or <c>CURVEPOLYGON</c> with its figures' types.</summary>
    public static SpatialShape Curve(SpatialShapeType type, SpatialCoordinate[][] figures, SpatialFigureType[] figureTypes, SpatialSegmentType[]?[] segments) =>
        new(type, figures, NoChildren, figureTypes, segments);

    /// <summary>The type of figure <paramref name="index"/>.</summary>
    public SpatialFigureType FigureType(int index) => this.FigureTypes?[index] ?? SpatialFigureType.Line;

    /// <summary>
    /// True when this shape or any member is one of the three curved kinds —
    /// what sends an operation through <see cref="SpatialCurves.Linearize"/>
    /// and the serialization to version 2, whether or not a figure holds an arc.
    /// </summary>
    public bool IsCurved
    {
        get
        {
            if (this.Type is SpatialShapeType.CircularString or SpatialShapeType.CompoundCurve or SpatialShapeType.CurvePolygon)
                return true;
            foreach (var child in this.Children)
            {
                if (child.IsCurved)
                    return true;
            }
            return false;
        }
    }

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
    private bool? planarValidity;
    private bool? geodeticValidity;

    /// <summary>
    /// Whether the instance satisfies the OGC validity rules real enforces —
    /// <c>STIsValid()</c>, and the gate most instance methods report Msg 24144
    /// from. Computed once per instance because a stored value is decoded once
    /// and read many times.
    /// </summary>
    public bool IsPlanarValid => this.planarValidity ??= this.Root.IsCurved ? SpatialCurves.IsPlanarValid(this.Root) : SpatialValidator.IsValid(this.Root);

    /// <summary>
    /// The same question on the round earth, where the edges are great elliptic
    /// arcs and a ring's written direction decides which side its interior is
    /// on. Cached for the same reason.
    /// </summary>
    public bool IsGeodeticValid => this.geodeticValidity ??= this.Root.Type == SpatialShapeType.FullGlobe
        || (this.Root.IsCurved ? SpatialCurves.IsGeodeticValid(this.Root) : SpatialGeodeticValidator.IsValid(this.Root));

    /// <summary>Validity in the terms of whichever spatial type owns the value.</summary>
    public bool IsValidFor(bool isGeography) => isGeography ? this.IsGeodeticValid : this.IsPlanarValid;

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

    /// <summary>
    /// Returns <paramref name="srid"/>, or raises real's refusal: Msg 24100
    /// for a <c>geometry</c> SRID outside 0..999999, and Msg 24204 for a
    /// <c>geography</c> SRID that isn't one of the reference systems
    /// <c>sys.spatial_reference_systems</c> lists (probed 2026-10-05 against
    /// SQL Server 2025, 0, 1, 999999 and -1 included).
    /// </summary>
    public static int ValidateSrid(int srid, bool isGeography) =>
        isGeography
            ? Array.BinarySearch(GeographySrids, srid) >= 0 ? srid : throw SimulatedSqlException.SpatialUnsupportedGeographySrid()
            : srid is >= 0 and <= MaxSrid ? srid : throw SimulatedSqlException.SpatialInvalidSrid(isGeography);

    /// <summary>
    /// The 393 SRIDs a stock SQL Server 2025 instance lists in
    /// <c>sys.spatial_reference_systems</c>, ascending — the ones a
    /// <c>geography</c> may carry (probed 2026-10-05).
    /// </summary>
    private static readonly int[] GeographySrids =
    [
        4120, 4121, 4122, 4123, 4124, 4127, 4128, 4129, 4130, 4131, 4132, 4133, 4134, 4135, 4136, 4137, 4138,
        4139, 4141, 4142, 4143, 4144, 4145, 4146, 4147, 4148, 4149, 4150, 4151, 4152, 4153, 4154, 4155, 4156,
        4157, 4158, 4159, 4160, 4161, 4162, 4163, 4164, 4165, 4166, 4167, 4168, 4169, 4170, 4171, 4173, 4174,
        4175, 4176, 4178, 4179, 4180, 4181, 4182, 4183, 4184, 4188, 4189, 4190, 4191, 4192, 4193, 4194, 4195,
        4196, 4197, 4198, 4199, 4200, 4201, 4202, 4203, 4204, 4205, 4206, 4207, 4208, 4209, 4210, 4211, 4212,
        4213, 4214, 4215, 4216, 4218, 4219, 4220, 4221, 4222, 4223, 4224, 4225, 4227, 4229, 4230, 4231, 4232,
        4236, 4237, 4238, 4239, 4240, 4241, 4242, 4243, 4244, 4245, 4246, 4247, 4248, 4249, 4250, 4251, 4252,
        4253, 4254, 4255, 4256, 4257, 4258, 4259, 4261, 4262, 4263, 4265, 4266, 4267, 4268, 4269, 4270, 4271,
        4272, 4273, 4274, 4275, 4276, 4277, 4278, 4279, 4280, 4281, 4282, 4283, 4284, 4285, 4286, 4288, 4289,
        4292, 4293, 4295, 4297, 4298, 4299, 4300, 4301, 4302, 4303, 4304, 4306, 4307, 4308, 4309, 4310, 4311,
        4312, 4313, 4314, 4315, 4316, 4317, 4318, 4319, 4322, 4324, 4326, 4600, 4601, 4602, 4603, 4604, 4605,
        4606, 4607, 4608, 4609, 4610, 4611, 4612, 4613, 4614, 4615, 4616, 4617, 4618, 4619, 4620, 4621, 4622,
        4623, 4624, 4625, 4626, 4627, 4628, 4629, 4630, 4632, 4633, 4636, 4637, 4638, 4639, 4640, 4641, 4642,
        4643, 4644, 4646, 4657, 4658, 4659, 4660, 4661, 4662, 4663, 4664, 4665, 4666, 4667, 4668, 4669, 4670,
        4671, 4672, 4673, 4674, 4675, 4676, 4677, 4678, 4679, 4680, 4682, 4683, 4684, 4686, 4687, 4688, 4689,
        4690, 4691, 4692, 4693, 4694, 4695, 4696, 4697, 4698, 4699, 4700, 4701, 4702, 4703, 4704, 4705, 4706,
        4707, 4708, 4709, 4710, 4711, 4712, 4713, 4714, 4715, 4716, 4717, 4718, 4719, 4720, 4721, 4722, 4723,
        4724, 4725, 4726, 4727, 4728, 4729, 4730, 4732, 4733, 4734, 4735, 4736, 4737, 4738, 4739, 4740, 4741,
        4742, 4743, 4744, 4745, 4746, 4747, 4748, 4749, 4750, 4751, 4752, 4753, 4754, 4755, 4756, 4757, 4758,
        4801, 4802, 4803, 4804, 4805, 4806, 4807, 4808, 4809, 4810, 4811, 4813, 4814, 4815, 4816, 4817, 4818,
        4820, 4821, 4895, 4898, 4900, 4901, 4902, 4903, 4904, 4907, 4909, 4921, 4923, 4925, 4927, 4929, 4931,
        4933, 4935, 4937, 4939, 4941, 4943, 4945, 4947, 4949, 4951, 4953, 4955, 4957, 4959, 4961, 4963, 4965,
        4967, 4971, 4973, 4975, 4977, 4979, 4981, 4983, 4985, 4987, 4989, 4991, 4993, 4995, 4997, 4999, 7843,
        7844, 104001,
    ];
}
