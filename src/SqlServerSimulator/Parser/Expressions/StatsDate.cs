using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>STATS_DATE(object_id, stats_id)</c>: returns the <c>datetime</c>
/// of the last statistics refresh for the given index / statistics object.
/// Real SQL Server returns NULL on a freshly-created index that hasn't
/// triggered an auto-stats run yet; the simulator returns the owning
/// table's <c>CreateDate</c> as a fake-but-realistic placeholder (the
/// simulator has no stats lifecycle, so claiming "stats were computed
/// when the table was created" is consistent with the no-update-stats-yet
/// reality).
/// </summary>
/// <remarks>
/// NULL on any NULL arg, unknown <c>object_id</c>, or unknown
/// <c>stats_id</c>. <c>stats_id</c> is the same as
/// <c>sys.indexes.index_id</c> (for index-backing stats); standalone
/// statistics objects (<c>CREATE STATISTICS</c>) aren't modeled.
/// </remarks>
internal sealed class StatsDate : Expression
{
    private readonly Expression objectIdArg;
    private readonly Expression statsIdArg;

    public StatsDate(ParserContext context)
    {
        this.objectIdArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.statsIdArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var objectIdValue = this.objectIdArg.Run(runtime);
        var statsIdValue = this.statsIdArg.Run(runtime);
        if (objectIdValue.IsNull || statsIdValue.IsNull)
            return SqlValue.Null(SqlType.DateTime);

        // Both ids convert before the object is looked up (probed 2026-09-26
        // against SQL Server 2025).
        var objectId = ScalarArguments.CoerceToInt(objectIdValue);
        var statsId = ScalarArguments.CoerceToInt(statsIdValue);
        if (ObjectProperty.FindObject(runtime.Batch.CurrentDatabase, objectId) is not HeapTable table)
            return SqlValue.Null(SqlType.DateTime);

        // Use the same resolver as INDEX_COL / INDEXKEY_PROPERTY so the
        // stats_id and sys.indexes.index_id agree. Unknown id → NULL.
        return IndexLookup.ResolveByIndexId(table, statsId) is null
            ? SqlValue.Null(SqlType.DateTime)
            : SqlValue.FromDateTime(table.CreateDate);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = AssignmentRules.ArgumentType(this.objectIdArg, SqlType.Int32, batch, resolveColumnType);
        _ = AssignmentRules.ArgumentType(this.statsIdArg, SqlType.Int32, batch, resolveColumnType);
        return SqlType.DateTime;
    }

    internal override string DebugDisplay() =>
        $"STATS_DATE({this.objectIdArg.DebugDisplay()}, {this.statsIdArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.objectIdArg).Child(this.statsIdArg);
}
