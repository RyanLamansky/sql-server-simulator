using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Spatial;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The value a spatial column holds after an <c>UPDATE</c>'s <c>SET loc.STSrid = …</c>:
/// the receiver re-stamped with the assigned SRID, which the statement then
/// stores as it would any assigned value. <c>STSrid</c> is the one settable
/// member either spatial type has.
/// </summary>
/// <remarks>
/// Probed 2026-10-06 against SQL Server 2025: a NULL receiver is Msg 5302
/// naming the column as written, a NULL SRID the bare .NET argument failure,
/// and an SRID outside the type's domain Msg 24100 / 24204 — the same answers
/// <c>SET @g.STSrid = …</c> gives a variable.
/// </remarks>
internal sealed class SpatialSridMutation(Expression receiver, string receiverName, SpatialSqlType type, Expression srid) : Expression
{
    private readonly Expression receiver = receiver;
    private readonly string receiverName = receiverName;
    private readonly SpatialSqlType type = type;
    private readonly Expression srid = srid;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.type;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var current = this.receiver.Run(runtime);
        if (current.IsNull)
            throw SimulatedSqlException.ClrMutatorOnNull("STSrid", this.receiverName);
        var assigned = this.srid.Run(runtime);
        if (assigned.IsNull)
            throw SimulatedSqlException.SpatialSridCannotBeNull(this.type.IsGeography);
        var validated = SpatialGeometry.ValidateSrid(ScalarArguments.CoerceToInt(assigned), this.type.IsGeography);
        return SqlValue.FromSpatial(current.AsSpatial.WithSrid(validated), this.type.IsGeography);
    }

    internal override string DebugDisplay() => $"{this.receiver.DebugDisplay()}.STSrid<-({this.srid.DebugDisplay()})";

    internal override void Describe(NodeShape shape) =>
        shape.Local(this.type.IsGeography).Local(this.receiverName).Child(this.receiver).Child(this.srid);
}
