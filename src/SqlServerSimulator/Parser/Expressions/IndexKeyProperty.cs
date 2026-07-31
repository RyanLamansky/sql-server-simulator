using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>INDEXKEY_PROPERTY(object_id, index_id, key_id, 'property')</c>:
/// per-key-column metadata on an index. Returns <c>int</c>; NULL on any
/// NULL arg, unknown object, unknown index, out-of-range key, INCLUDE
/// columns (only key columns are reachable), or unknown property.
/// Property name is case-insensitive.
/// </summary>
/// <remarks>
/// Probe-confirmed shipped properties (SQL Server 2025, 2026-05-23):
/// <list type="bullet">
/// <item><description><c>ColumnId</c> — 1-based <c>sys.columns.column_id</c>
/// of the key column.</description></item>
/// <item><description><c>IsDescending</c> — 1 if the key column was declared
/// <c>DESC</c>, 0 otherwise. PK / UQ constraints don't track per-column
/// direction (the simulator's <see cref="KeyConstraint"/> has no DESC
/// metadata), so they always report 0 — matches probe.</description></item>
/// </list>
/// </remarks>
internal sealed class IndexKeyProperty : Expression
{
    private readonly Expression objectIdArg;
    private readonly Expression indexIdArg;
    private readonly Expression keyIdArg;
    private readonly Expression propertyArg;

    public IndexKeyProperty(ParserContext context)
    {
        this.objectIdArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.indexIdArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.keyIdArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var objectIdValue = this.objectIdArg.Run(runtime);
        var indexIdValue = this.indexIdArg.Run(runtime);
        var keyIdValue = this.keyIdArg.Run(runtime);
        var propValue = this.propertyArg.Run(runtime);
        if (objectIdValue.IsNull || indexIdValue.IsNull || keyIdValue.IsNull || propValue.IsNull)
            return SqlValue.Null(SqlType.Int32);

        var objectId = ScalarArguments.CoerceToInt(objectIdValue);
        if (ObjectProperty.FindObject(runtime.Batch.CurrentDatabase, objectId) is not HeapTable table)
            return SqlValue.Null(SqlType.Int32);

        var indexId = ScalarArguments.CoerceToInt(indexIdValue);
        if (IndexLookup.ResolveByIndexId(table, indexId) is not { } resolved)
            return SqlValue.Null(SqlType.Int32);

        // Only the key ordinal is declared smallint here; the object and
        // index ids are int (probe-confirmed 2026-07-31 by the target type
        // each overflow names).
        var keyId = ScalarArguments.CoerceToSmallInt(keyIdValue);
        if (IndexLookup.GetKeyColumn(resolved.Constraint, resolved.Index, keyId) is not { } keyCol)
            return SqlValue.Null(SqlType.Int32);

        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;
        Span<char> upper = stackalloc char[prop.Length];
        return prop.AsSpan().ToUpperInvariant(upper) switch
        {
            8 => upper switch
            {
                "COLUMNID" => SqlValue.FromInt32(IndexLookup.StorageOrdinalToColumnId(table, keyCol.StorageOrdinal)),
                _ => SqlValue.Null(SqlType.Int32),
            },
            12 => upper switch
            {
                "ISDESCENDING" => SqlValue.FromInt32(keyCol.IsDescending ? 1 : 0),
                _ => SqlValue.Null(SqlType.Int32),
            },
            _ => SqlValue.Null(SqlType.Int32),
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() =>
        $"INDEXKEY_PROPERTY({this.objectIdArg.DebugDisplay()}, {this.indexIdArg.DebugDisplay()}, {this.keyIdArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
