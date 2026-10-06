using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@CONNECTIONS</c>: returns the number of sessions the
/// <see cref="Simulation"/> has allocated as <see cref="SqlType.Int32"/>
/// (real SQL Server's @@CONNECTIONS is <c>int</c> — probe-confirmed). Reads
/// <see cref="Simulation.ConnectionsAllocated"/>, a live count derived from
/// the SPID allocator; on real SQL Server this is cumulative login attempts
/// since server start, which the session-allocation count proxies without
/// separate instrumentation.
/// </summary>
internal sealed class ConnectionsExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.Connection.Simulation.ConnectionsAllocated);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@CONNECTIONS";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// Backs <c>@@NESTLEVEL</c>: returns the connection's current nesting
/// depth (the count of active procedure/UDF/trigger frames) as
/// <see cref="SqlType.Int32"/>. The connection's
/// <see cref="SimulatedDbConnection.NestingLevel"/> tracks this directly;
/// it's <c>0</c> in an ad-hoc batch and increments on entry into each
/// procedure/UDF/trigger body (capped at 32 per
/// <see cref="SimulatedDbConnection.MaxNestingLevel"/>), a view or inline
/// function body leaving it as its caller's
/// (<see cref="SimulatedDbConnection.InlinedBodyDepth"/>).
/// </summary>
internal sealed class NestLevelExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.Connection.NestingLevel - runtime.Batch.Connection.InlinedBodyDepth);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@NESTLEVEL";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// Backs <c>@@DBTS</c>: returns the current database's last-assigned
/// rowversion as <c>varbinary(8)</c>, the type real describes it with (probed
/// 2026-10-02 against SQL Server 2025). The 8-byte representation matches the
/// rowversion encoding used by <see cref="RowVersionSqlType"/> — big-endian
/// 64-bit. Reads <see cref="Database.LastRowVersion"/>, so a read doesn't
/// advance the counter.
/// </summary>
internal sealed class DbTsExpression : Expression
{
    private static readonly VarbinarySqlType Varbinary8 = VarbinarySqlType.Get(8);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var current = runtime.Batch.CurrentDatabase.LastRowVersion;
        var bytes = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            bytes[i] = (byte)(current & 0xff);
            current >>= 8;
        }
        return SqlValue.FromVarbinary(Varbinary8, bytes);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => Varbinary8;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@DBTS";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// Backs <c>@@PROCID</c>: the executing module's <c>object_id</c>
/// (<see cref="BatchContext.ModuleObjectId"/>) as <see cref="SqlType.Int32"/>.
/// Outside a procedure / function / trigger body it is the batch's ad hoc
/// object id, a hash of the batch's text — <c>EXEC('…')</c>'s or
/// <c>sp_executesql</c>'s own for a dynamic batch (see
/// <see cref="BuiltInResources.AdHocObjectIdOf"/>).
/// </summary>
internal sealed class ProcIdExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.ModuleObjectId is var module and not 0 ? module : BuiltInResources.AdHocObjectIdOf(runtime.Batch.Parser.Command.CommandText));

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@PROCID";

    internal override void Describe(NodeShape shape) { }
}
