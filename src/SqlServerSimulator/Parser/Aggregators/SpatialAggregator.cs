using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Spatial;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>The four aggregates the spatial types expose as static methods.</summary>
internal enum SpatialAggregateMethod
{
    Union,
    Envelope,
    Collection,
    ConvexHull,
}

/// <summary>
/// One group's state for <c>UnionAggregate</c>, <c>EnvelopeAggregate</c>,
/// <c>CollectionAggregate</c> or <c>ConvexHullAggregate</c>.
/// </summary>
/// <remarks>
/// <para>Real's aggregates skip NULL, answer NULL for a group with no non-NULL
/// input or whose inputs don't share one SRID, take a string operand as
/// well-known text, and refuse an invalid instance with Msg 24144 named for
/// the aggregate's own class (probed 2026-09-28 against SQL Server 2025).</para>
/// <para><c>UnionAggregate</c> folds <c>STUnion</c> over the rows in the order
/// they arrive, so a lone row still comes back normalized as a union writes
/// it; <c>CollectionAggregate</c> gathers the rows as members, flattening a
/// collection input by one level; the envelope and hull read every row's
/// vertices at once.</para>
/// </remarks>
internal sealed class SpatialAggregator(SpatialAggregateMethod method, SpatialSqlType type) : Aggregator
{
    private readonly List<SpatialShape> members = [];
    private SpatialShape? union;
    private (SpatialVector Center, double Radius)? cap;
    private bool globe;
    private int? srid;
    private bool mixedSrid;

    public override void Add(SqlValue value)
    {
        if (value.IsNull)
            return;
        var isGeography = type.IsGeography;
        var instance = value.Type is SpatialSqlType
            ? value.AsSpatial
            : SpatialWktReader.Read(value.AsString, SpatialGeometry.DefaultSridFor(isGeography), isGeography);
        if (!instance.IsValidFor(isGeography))
            throw SimulatedSqlException.SpatialAggregateInstanceNotValid(this.ClassName);
        if (this.srid is { } existing && existing != instance.Srid)
            this.mixedSrid = true;
        this.srid ??= instance.Srid;
        if (this.mixedSrid)
            return;
        switch (method)
        {
            case SpatialAggregateMethod.Union:
                var sofar = this.union ?? SpatialShape.Empty(SpatialShapeType.GeometryCollection);
                this.union = isGeography
                    ? SpatialGeodeticConstructive.Overlay(SpatialOverlayOperation.Union, sofar, SpatialCurves.ForOperations(instance.Root, isGeography))
                    : SpatialConstructive.Overlay(SpatialOverlayOperation.Union, sofar, SpatialCurves.ForOperations(instance.Root, isGeography));
                break;
            case SpatialAggregateMethod.Envelope when isGeography:
                if (!this.globe && !instance.Root.IsEmpty)
                {
                    var merged = SpatialEnvelope.MergeCap(this.cap, SpatialCurves.ForOperations(instance.Root, isGeography));
                    this.globe = merged is null;
                    this.cap = merged;
                }
                break;
            case SpatialAggregateMethod.Collection:
                if (instance.Root.Type == SpatialShapeType.GeometryCollection)
                    this.members.AddRange(instance.Root.Children);
                else
                    this.members.Add(instance.Root);
                break;
            default:
                this.members.Add(SpatialCurves.ForOperations(instance.Root, isGeography));
                break;
        }
    }

    private string ClassName => (type.IsGeography ? "Geography" : "Geometry") + method switch
    {
        SpatialAggregateMethod.Collection => "CollectionAggregate",
        SpatialAggregateMethod.ConvexHull => "ConvexHullAggregate",
        SpatialAggregateMethod.Envelope => "EnvelopeAggregate",
        _ => "UnionAggregate",
    };

    public override SqlValue Result()
    {
        if (this.srid is not { } resultSrid || this.mixedSrid)
            return SqlValue.Null(type);
        var shape = method switch
        {
            SpatialAggregateMethod.Union => this.union!,
            SpatialAggregateMethod.Collection => SpatialShape.Collection(SpatialShapeType.GeometryCollection, [.. this.members]),
            SpatialAggregateMethod.Envelope when type.IsGeography => this.GeographyEnvelope(),
            SpatialAggregateMethod.Envelope => SpatialConstructive.Envelope(All()),
            _ => type.IsGeography ? SpatialGeodeticConstructive.ConvexHull(All()) : SpatialConstructive.ConvexHull(All()),
        };
        return SqlValue.FromSpatial(new SpatialGeometry(resultSrid, shape), type.IsGeography);
    }

    /// <summary>
    /// <c>geography::EnvelopeAggregate</c>: a curve polygon of two half
    /// circles through four control points about the merged cap's centre, at
    /// the cap's angle scaled by the polar radius of curvature <c>a²/b</c> as a
    /// great elliptic distance — <c>BufferWithCurves</c>'s construction — with
    /// a floor of √3·10⁻⁹ radians, which is what a lone point answers.
    /// A cap reaching a hemisphere answers <c>FULLGLOBE</c> (fitted against
    /// SQL Server 2025, 2026-09-29).
    /// </summary>
    private SpatialShape GeographyEnvelope()
    {
        if (this.globe)
            return SpatialShape.Empty(SpatialShapeType.FullGlobe);
        if (this.cap is not { } merged)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var radius = Math.Max(merged.Radius, MinimumCapRadius);
        return SpatialGeodeticBuffer.CurveCircle(SpatialEnvelope.ToCoordinate(merged.Center), radius * PolarCurvatureRadius);
    }

    private static readonly double MinimumCapRadius = Math.Sqrt(3) * 1e-9;

    private static double PolarCurvatureRadius => SpatialEllipsoid.SemiMajor * SpatialEllipsoid.SemiMajor / SpatialEllipsoid.SemiMinor;

    private SpatialShape All() => SpatialShape.Collection(SpatialShapeType.GeometryCollection, [.. this.members]);
}
