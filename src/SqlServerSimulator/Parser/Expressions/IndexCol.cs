using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>INDEX_COL('table', index_id, key_id)</c>: returns the
/// <c>sysname</c> name of the key column at the <c>key_id</c>
/// position (1-based) of the index identified by <c>index_id</c>.
/// Returns NULL when the index doesn't exist, <c>key_id</c> is out of
/// range, the <c>key_id</c> refers to an INCLUDE column (only key columns
/// are reachable through this function), or any arg is NULL.
/// Probe-confirmed wording / shape against SQL Server 2025 (2026-05-23).
/// </summary>
/// <remarks>
/// The table-name argument follows the same dotted-string convention as
/// <c>OBJECT_ID</c> — 1- to 4-part with optional bracket quoting,
/// case-insensitive (reuses the same parser as <c>OBJECT_ID</c>).
/// <c>index_id</c> resolution mirrors the
/// <c>sys.indexes</c> emission order via <see cref="IndexLookup.ResolveByIndexId"/>.
/// </remarks>
internal sealed class IndexCol : Expression
{
    private readonly Expression tableArg;
    private readonly Expression indexIdArg;
    private readonly Expression keyIdArg;

    public IndexCol(ParserContext context)
    {
        this.tableArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.indexIdArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.keyIdArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var tableValue = this.tableArg.Run(runtime);
        var indexIdValue = this.indexIdArg.Run(runtime);
        var keyIdValue = this.keyIdArg.Run(runtime);
        if (tableValue.IsNull || indexIdValue.IsNull || keyIdValue.IsNull)
            return SqlValue.Null(SqlType.SystemName);

        var tableName = tableValue.CoerceTo(SqlType.NVarchar).AsString;
        if (!ObjectId.TryParseObjectName(tableName, out var parsed)
            || !runtime.Batch.TryResolveTable(parsed, out var table))
        {
            return SqlValue.Null(SqlType.SystemName);
        }

        var indexId = indexIdValue.CoerceTo(SqlType.Int32).AsInt32;
        if (IndexLookup.ResolveByIndexId(table, indexId) is not { } resolved)
            return SqlValue.Null(SqlType.SystemName);

        var keyId = keyIdValue.CoerceTo(SqlType.Int32).AsInt32;
        if (IndexLookup.GetKeyColumn(resolved.Constraint, resolved.Index, keyId) is not { } keyCol)
            return SqlValue.Null(SqlType.SystemName);

        var columnId = IndexLookup.StorageOrdinalToColumnId(table, keyCol.StorageOrdinal);
        return columnId < 1 || columnId > table.Columns.Length
            ? SqlValue.Null(SqlType.SystemName)
            : SqlValue.FromSystemName(table.Columns[columnId - 1].Name);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() =>
        $"INDEX_COL({this.tableArg.DebugDisplay()}, {this.indexIdArg.DebugDisplay()}, {this.keyIdArg.DebugDisplay()})";
}
