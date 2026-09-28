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
                    ? SpatialGeodeticConstructive.Overlay(SpatialOverlayOperation.Union, sofar, instance.Root)
                    : SpatialConstructive.Overlay(SpatialOverlayOperation.Union, sofar, instance.Root);
                break;
            case SpatialAggregateMethod.Collection:
                if (instance.Root.Type == SpatialShapeType.GeometryCollection)
                    this.members.AddRange(instance.Root.Children);
                else
                    this.members.Add(instance.Root);
                break;
            default:
                // geography's envelope is a curve polygon, which no operation models.
                if (isGeography && method == SpatialAggregateMethod.Envelope)
                    throw new NotSupportedException("geography::EnvelopeAggregate is not modeled.");
                this.members.Add(instance.Root);
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
            SpatialAggregateMethod.Envelope => SpatialConstructive.Envelope(All()),
            _ => type.IsGeography ? SpatialGeodeticConstructive.ConvexHull(All()) : SpatialConstructive.ConvexHull(All()),
        };
        return SqlValue.FromSpatial(new SpatialGeometry(resultSrid, shape), type.IsGeography);
    }

    private SpatialShape All() => SpatialShape.Collection(SpatialShapeType.GeometryCollection, [.. this.members]);
}
