namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// A vertex on the integer grid the constructive operations compute on. The
/// coordinates are offsets from the grid's centre, so they run roughly
/// ±2^47 across the operands' extent.
/// </summary>
internal readonly struct GridPoint(long x, long y) : IEquatable<GridPoint>, IComparable<GridPoint>
{
    public readonly long X = x;

    public readonly long Y = y;

    public bool Equals(GridPoint other) => this.X == other.X && this.Y == other.Y;

    public override bool Equals(object? obj) => obj is GridPoint other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.X, this.Y);

    /// <summary>The sweep order real's output follows: by y, then by x.</summary>
    public int CompareTo(GridPoint other) => this.Y != other.Y ? this.Y.CompareTo(other.Y) : this.X.CompareTo(other.X);

    public static bool operator ==(GridPoint left, GridPoint right) => left.Equals(right);

    public static bool operator !=(GridPoint left, GridPoint right) => !left.Equals(right);

    /// <summary>Cross product of <c>b - a</c> and <c>c - a</c>, exact.</summary>
    public static Int128 Cross(GridPoint a, GridPoint b, GridPoint c) =>
        ((Int128)(b.X - a.X) * (c.Y - a.Y)) - ((Int128)(b.Y - a.Y) * (c.X - a.X));

    public static int Orientation(GridPoint a, GridPoint b, GridPoint c) => Int128.Sign(Cross(a, b, c));
}

/// <summary>
/// The precision model real's constructive operations run under: every
/// coordinate is snapped onto an integer grid of 2^48 steps per axis across
/// the operands' combined extent, the operation runs exactly on that grid,
/// and the result maps back.
/// </summary>
/// <remarks>
/// <para>Each axis carries its own scale <c>s = 2^48 / (max - min)</c> and an
/// integer offset <c>C = round(centre · s)</c>; a coordinate lands at
/// <c>trunc(x·s - C + 0.5)</c>. The truncation is real's, not a rounding: a
/// coordinate below the centre lands one step high whenever its scaled value
/// has a fraction, which is why a computed point below the centre drifts up by
/// one grid step (probe-derived, SQL Server 2025, 2026-09-28).</para>
/// <para>A result vertex that is one of the inputs' own vertices maps back to
/// that vertex's original coordinates exactly, which is what real writes; a
/// vertex the operation computed maps back through the scale and so carries
/// the grid's last-digit noise.</para>
/// </remarks>
internal sealed class SpatialPrecisionGrid
{
    private readonly double scaleX;
    private readonly double scaleY;
    private readonly long offsetX;
    private readonly long offsetY;
    private readonly Dictionary<GridPoint, SpatialCoordinate> originals = [];
    private readonly Dictionary<GridPoint, (double X, double Y)> computed = [];

    /// <summary>
    /// Whether an input vertex maps back to its own coordinates. Real's
    /// <c>MakeValid</c> doesn't restore them: every vertex it writes has been
    /// through the grid, so a vertex below the centre comes back one step
    /// high.
    /// </summary>
    public bool RestoreOriginals = true;

    private SpatialPrecisionGrid(double minX, double maxX, double minY, double maxY)
    {
        (this.scaleX, this.offsetX) = Axis(minX, maxX);
        (this.scaleY, this.offsetY) = Axis(minY, maxY);
    }

    private static (double Scale, long Offset) Axis(double min, double max)
    {
        var range = max - min;
        if (!(range > 0) || double.IsInfinity(range))
            range = Math.Max(Math.Abs(min), 1);
        var scale = 281474976710656.0 / range;
        return (scale, (long)Math.Round((min + max) / 2 * scale));
    }

    /// <summary>Builds the grid over the extent of every coordinate beneath the given shapes.</summary>
    public static SpatialPrecisionGrid Over(params SpatialShape[] shapes)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var shape in shapes)
        {
            foreach (var point in shape.Coordinates())
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
        }
        return minX > maxX ? new SpatialPrecisionGrid(0, 1, 0, 1) : new SpatialPrecisionGrid(minX, maxX, minY, maxY);
    }

    /// <summary>Builds the grid over an explicit extent, for operations whose result reaches past their input.</summary>
    public static SpatialPrecisionGrid Over(double minX, double maxX, double minY, double maxY) => new(minX, maxX, minY, maxY);

    /// <summary>Snaps an input vertex, remembering its original coordinates for the way back.</summary>
    public GridPoint Snap(SpatialCoordinate coordinate)
    {
        var point = new GridPoint(Round((coordinate.X * this.scaleX) - this.offsetX), Round((coordinate.Y * this.scaleY) - this.offsetY));
        _ = this.originals.TryAdd(point, new SpatialCoordinate(coordinate.X, coordinate.Y));
        return point;
    }

    private static long Round(double scaled) => (long)(scaled + 0.5);

    /// <summary>
    /// Remembers the unrounded grid-space location of a computed vertex, which
    /// is what real maps back — it keeps a crossing's fraction rather than
    /// rounding it onto the grid.
    /// </summary>
    public void RecordComputed(GridPoint point, (double X, double Y) location) => _ = this.computed.TryAdd(point, location);

    /// <summary>
    /// Maps a grid vertex back to the plane: an input vertex exactly, a
    /// computed one from its unrounded location through the scale, as
    /// <c>X / s + C / s</c> — the grouping real's last digits follow.
    /// </summary>
    public SpatialCoordinate ToCoordinate(GridPoint point)
    {
        if (this.RestoreOriginals && this.originals.TryGetValue(point, out var original))
            return original;
        var (x, y) = this.computed.TryGetValue(point, out var location) ? location : (point.X, point.Y);
        return new SpatialCoordinate((x / this.scaleX) + (this.offsetX / this.scaleX), (y / this.scaleY) + (this.offsetY / this.scaleY));
    }
}
