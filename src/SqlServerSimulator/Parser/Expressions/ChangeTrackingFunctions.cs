using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CHANGE_TRACKING_CURRENT_VERSION()</c>: the current database's last
/// committed change tracking version as <c>bigint</c>, <c>0</c> before any
/// tracked change and NULL while the database doesn't track.
/// </summary>
internal sealed class ChangeTrackingCurrentVersion : Expression
{
    public ChangeTrackingCurrentVersion(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("change_tracking_current_version", 0);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var database = runtime.Batch.CurrentDatabase;
        return database.ChangeTracking is null
            ? SqlValue.Null(SqlType.BigInt)
            : SqlValue.FromInt64(database.ChangeTrackingVersion);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.BigInt;

    internal override string DebugDisplay() => "CHANGE_TRACKING_CURRENT_VERSION()";

    internal override void Describe(NodeShape shape) { }
}

/// <summary>
/// SQL <c>CHANGE_TRACKING_MIN_VALID_VERSION(object_id)</c>: the oldest version
/// a client may synchronize from for the table the id names in the current
/// database, as <c>bigint</c>; NULL for any id
/// that isn't a tracked table. The argument must be an <c>int</c> — any
/// other type, a bare <c>NULL</c> included, is Msg 8116 (probed 2026-09-27
/// against SQL Server 2025).
/// </summary>
internal sealed class ChangeTrackingMinValidVersion : Expression
{
    private readonly Expression objectIdArg;

    public ChangeTrackingMinValidVersion(ParserContext context)
    {
        this.objectIdArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        ChangeTrackingIsColumnInMask.RejectNullLiteral(this.objectIdArg, "change_tracking_min_valid_version");
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var idValue = this.objectIdArg.Run(runtime);
        if (idValue.IsNull)
            return SqlValue.Null(SqlType.BigInt);
        var id = idValue.AsInt32;
        foreach (var schema in runtime.Batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.ObjectId == id)
                    return table.ChangeTracking is { } tracking ? SqlValue.FromInt64(tracking.MinValidVersion) : SqlValue.Null(SqlType.BigInt);
            }
        }
        return SqlValue.Null(SqlType.BigInt);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        ChangeTrackingIsColumnInMask.RequireInt(this.objectIdArg, batch, resolveColumnType, "change_tracking_min_valid_version");
        return SqlType.BigInt;
    }

    internal override string DebugDisplay() => $"CHANGE_TRACKING_MIN_VALID_VERSION({this.objectIdArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.objectIdArg);
}

/// <summary>
/// SQL <c>CHANGE_TRACKING_IS_COLUMN_IN_MASK(column_id, change_columns)</c>:
/// <c>1</c> when the <c>SYS_CHANGE_COLUMNS</c> mask names the column, else
/// <c>0</c>, as <c>int</c>. A NULL mask means every column changed and answers
/// <c>1</c>. The mask is a four-byte header and then one little-endian
/// <c>int</c> column id per changed column; one shorter than eight bytes is
/// Msg 22101, while trailing bytes past the last whole id are ignored and the
/// header is never read (probed 2026-09-27 against SQL Server 2025). The
/// column id must be an <c>int</c> (Msg 8116 otherwise).
/// </summary>
internal sealed class ChangeTrackingIsColumnInMask : Expression
{
    private readonly Expression columnIdArg;
    private readonly Expression maskArg;

    public ChangeTrackingIsColumnInMask(ParserContext context)
    {
        this.columnIdArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("change_tracking_is_column_in_mask", 2);
        this.maskArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        RejectNullLiteral(this.columnIdArg, "change_tracking_is_column_in_mask");
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var columnIdValue = this.columnIdArg.Run(runtime);
        var maskValue = this.maskArg.Run(runtime);
        if (maskValue.IsNull)
            return SqlValue.FromInt32(1);
        var mask = maskValue.CoerceTo(SqlType.Varbinary).AsBytes;
        if (mask.Length < 8)
            throw SimulatedSqlException.InvalidChangeColumnsMask();
        if (columnIdValue.IsNull)
            return SqlValue.FromInt32(0);
        var columnId = columnIdValue.AsInt32;
        for (var offset = 4; offset + 4 <= mask.Length; offset += 4)
        {
            if (BitConverter.ToInt32(mask, offset) == columnId)
                return SqlValue.FromInt32(1);
        }
        return SqlValue.FromInt32(0);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        RequireInt(this.columnIdArg, batch, resolveColumnType, "change_tracking_is_column_in_mask");
        var maskType = this.maskArg.GetSqlType(batch, resolveColumnType);
        if (!Expression.IsUntypedNullLiteral(this.maskArg) && maskType.PairClass is TypePairClass.AnsiString or TypePairClass.UnicodeString)
            throw SimulatedSqlException.ImplicitConversionNotAllowed(maskType.SqlServerName, "varbinary");
        return SqlType.Int32;
    }

    /// <summary>
    /// The change tracking scalars' id rule: an <c>int</c> and nothing else,
    /// where the narrowing a catalog-id scalar allows is Msg 8116 naming the
    /// type — <c>NULL</c> for a bare NULL.
    /// </summary>
    internal static void RequireInt(Expression argument, BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType, string functionName)
    {
        RejectNullLiteral(argument, functionName);
        var type = argument.GetSqlType(batch, resolveColumnType);
        if (type != SqlType.Int32)
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(type, argument), 1, functionName);
    }

    /// <summary>
    /// A bare <c>NULL</c> id is refused while the batch compiles, so it ends
    /// the batch before any statement runs, a branch never taken included.
    /// </summary>
    internal static void RejectNullLiteral(Expression argument, string functionName)
    {
        if (Expression.IsUntypedNullLiteral(argument))
            throw SimulatedSqlException.InvalidArgumentDataType("NULL", 1, functionName);
    }

    internal override string DebugDisplay() => $"CHANGE_TRACKING_IS_COLUMN_IN_MASK({this.columnIdArg.DebugDisplay()}, {this.maskArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.columnIdArg).Child(this.maskArg);
}
