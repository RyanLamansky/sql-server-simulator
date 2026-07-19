using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>COL_NAME(table_id, col_id)</c>: returns the name of the
/// column at the given 1-based position in the table identified by
/// <c>table_id</c>. NULL on either argument or unknown id returns NULL.
/// Sibling of <see cref="ObjectName"/> for column-level metadata.
/// </summary>
internal sealed class ColName : Expression
{
    private readonly Expression tableIdArg;
    private readonly Expression colIdArg;

    public ColName(ParserContext context)
    {
        this.tableIdArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.colIdArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var tableIdValue = this.tableIdArg.Run(runtime);
        var colIdValue = this.colIdArg.Run(runtime);
        if (tableIdValue.IsNull || colIdValue.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var tableId = tableIdValue.CoerceTo(SqlType.Int32).AsInt32;
        var colId = colIdValue.CoerceTo(SqlType.Int32).AsInt32;
        foreach (var schema in runtime.Batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.ObjectId == tableId)
                {
                    return colId >= 1 && colId <= table.Columns.Length
                        ? SqlValue.FromString(SqlType.SystemName, table.Columns[colId - 1].Name)
                        : SqlValue.Null(SqlType.SystemName);
                }
            }
        }
        return SqlValue.Null(SqlType.SystemName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => $"COL_NAME({this.tableIdArg.DebugDisplay()}, {this.colIdArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>COL_LENGTH(table, col)</c>: returns the declared
/// storage length (in bytes) of the named column. Routes through
/// <see cref="ObjectId"/>-style name resolution (1- or 2-part dotted
/// form). NULL on either argument or unknown column returns NULL.
/// Result type is <see cref="SqlType.SmallInt"/>.
/// </summary>
internal sealed class ColLength : Expression
{
    private readonly Expression tableNameArg;
    private readonly Expression colNameArg;

    public ColLength(ParserContext context)
    {
        this.tableNameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.colNameArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var tn = this.tableNameArg.Run(runtime);
        var cn = this.colNameArg.Run(runtime);
        if (tn.IsNull || cn.IsNull)
            return SqlValue.Null(SqlType.SmallInt);
        var tableNameStr = tn.CoerceTo(SqlType.NVarchar).AsString;
        var colNameStr = cn.CoerceTo(SqlType.NVarchar).AsString;
        var parts = tableNameStr.Split('.');
        var multiPart = new MultiPartName(parts[0]);
        for (var i = 1; i < parts.Length; i++)
            multiPart = multiPart.WithAddedPart(parts[i]);
        if (!runtime.Batch.TryResolveTable(multiPart, out var table))
            return SqlValue.Null(SqlType.SmallInt);
        foreach (var col in table.Columns)
        {
            if (BuiltInToken.Comparer.Equals(col.Name, colNameStr))
                return SqlValue.FromInt16((short)EstimateColumnLength(col));
        }
        return SqlValue.Null(SqlType.SmallInt);
    }

    private static int EstimateColumnLength(HeapColumn col) => col.Type switch
    {
        // sys.columns.max_length conventions: fixed-length types report their
        // byte width; variable-length types report the declared max; MAX
        // types report -1. Mirrors the catalog-view computation.
        var t when t == SqlType.TinyInt || t == SqlType.Bit => 1,
        var t when t == SqlType.SmallInt => 2,
        var t when t == SqlType.Int32 || t == SqlType.Real || t == SqlType.SmallMoney
                || t == SqlType.SmallDateTime || t == SqlType.Date => 4,
        var t when t == SqlType.BigInt || t == SqlType.Float || t == SqlType.Money
                || t == SqlType.DateTime || t == SqlType.RowVersion => 8,
        var t when t == SqlType.UniqueIdentifier => 16,
        CharSqlType c => c.length,
        NCharSqlType nc => nc.length * 2,
        BinarySqlType bn => bn.length,
        VarcharSqlType vc => vc.length == 0 ? (col.MaxLength ?? 1) : (vc.length == -1 ? -1 : vc.length),
        NVarcharSqlType nv => nv.length == 0 ? (col.MaxLength ?? 1) * 2 : (nv.length == -1 ? -1 : nv.length * 2),
        VarbinarySqlType vb => vb.length == 0 ? (col.MaxLength ?? 1) : (vb.length == -1 ? -1 : vb.length),
        DecimalSqlType d => d.precision <= 9 ? 5 : d.precision <= 19 ? 9 : d.precision <= 28 ? 13 : 17,
        _ => -1,
    };

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override string DebugDisplay() => $"COL_LENGTH({this.tableNameArg.DebugDisplay()}, {this.colNameArg.DebugDisplay()})";
}
