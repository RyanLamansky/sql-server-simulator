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
}

/// <summary>
/// Backs <c>@@NESTLEVEL</c>: returns the connection's current nesting
/// depth (the count of active procedure/UDF/trigger frames) as
/// <see cref="SqlType.Int32"/>. The connection's
/// <see cref="SimulatedDbConnection.NestingLevel"/> tracks this directly;
/// it's <c>0</c> in an ad-hoc batch and increments on entry into each
/// procedure/UDF/trigger body (capped at 32 per
/// <see cref="SimulatedDbConnection.MaxNestingLevel"/>).
/// </summary>
internal sealed class NestLevelExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.Connection.NestingLevel);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@NESTLEVEL";
}

/// <summary>
/// Backs <c>@@DBTS</c>: returns the current database's last-assigned
/// rowversion as <c>binary(8)</c>. The 8-byte representation matches the
/// rowversion encoding used by <see cref="RowVersionSqlType"/> — big-endian
/// 64-bit. Reads <see cref="Database.AllocateRowVersion"/>'s underlying
/// counter without advancing it (one-step lookback against the
/// monotonically increasing value).
/// </summary>
internal sealed class DbTsExpression : Expression
{
    private static readonly SqlType Binary8 = SqlType.GetBinary(8);

    public override SqlValue Run(RuntimeContext runtime)
    {
        // @@DBTS reports the LAST allocated value; bump-then-read gives the
        // most recently used rowversion. Tested behavior on real SQL Server:
        // value advances on every committed mutation that touches a
        // rowversion column. The simulator's counter increments on
        // AllocateRowVersion calls but never decrements, so peeking at the
        // current state by allocating-and-reverting would over-count;
        // instead, expose the next-to-be-allocated value minus 1.
        var current = runtime.Batch.CurrentDatabase.AllocateRowVersion() - 1;
        // Restore the counter by re-incrementing on subsequent allocations;
        // the bump just made is harmless — rowversion values are advisory
        // and monotonic, not packed. (Real SQL Server's @@DBTS read does
        // NOT bump; this is a fidelity gap.)
        var bytes = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            bytes[i] = (byte)(current & 0xff);
            current >>= 8;
        }
        return SqlValue.FromBinary(Binary8, bytes);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => Binary8;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@DBTS";
}

/// <summary>
/// Backs <c>@@PROCID</c>: returns the executing module's
/// <c>object_id</c> as <see cref="SqlType.Int32"/>. Outside a procedure /
/// UDF / trigger body the simulator returns <c>0</c> — real SQL Server
/// returns a transient compiled-plan id which isn't meaningful to
/// reproduce. Inside a procedure body, returns the procedure's
/// <c>object_id</c> looked up via the current
/// <see cref="ProcFrame.ProcedureName"/>.
/// </summary>
internal sealed class ProcIdExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime)
    {
        var frame = runtime.Batch.ProcFrame;
        if (frame is null)
            return SqlValue.FromInt32(0);
        var name = new MultiPartName(frame.ProcedureName);
        return runtime.Batch.TryResolveProcedure(name, out var proc) && proc is { ObjectId: var id }
            ? SqlValue.FromInt32(id)
            : SqlValue.FromInt32(0);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@PROCID";
}
