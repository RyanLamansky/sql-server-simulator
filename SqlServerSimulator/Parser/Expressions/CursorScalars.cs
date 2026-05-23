using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@FETCH_STATUS</c>: the status of the most recent <c>FETCH</c> on
/// this connection (0 success, -1 past end / no row, -2 keyset member deleted),
/// as <see cref="SqlType.Int32"/>. Session-global across all cursors.
/// </summary>
internal sealed class FetchStatusExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.Connection.LastFetchStatus);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "@@FETCH_STATUS";
}

/// <summary>
/// Backs <c>@@CURSOR_ROWS</c>: row count of the most recently OPENed cursor —
/// the count for STATIC / KEYSET, <c>-1</c> for DYNAMIC.
/// </summary>
internal sealed class CursorRowsExpression : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt32(runtime.Batch.Connection.LastCursorRows);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => "@@CURSOR_ROWS";
}

/// <summary>
/// SQL <c>CURSOR_STATUS(scope, name)</c>: reports a named cursor's state as a
/// <see cref="SqlType.SmallInt"/> — <c>1</c> open (with rows, or any open DYNAMIC
/// cursor), <c>0</c> open but empty, <c>-1</c> closed, <c>-3</c> the cursor
/// doesn't exist. The scope argument (<c>'global'</c> / <c>'local'</c>) is
/// accepted and ignored (the simulator keeps one per-connection cursor map).
/// </summary>
internal sealed class CursorStatusFunction : Expression
{
    private readonly Expression scopeArg;
    private readonly Expression nameArg;

    public CursorStatusFunction(ParserContext context)
    {
        this.scopeArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.FunctionRequiresNArguments("cursor_status", 2);
        this.nameArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        _ = this.scopeArg.Run(runtime); // scope ignored; evaluated for side-effect parity
        var nameValue = this.nameArg.Run(runtime);
        if (nameValue.IsNull)
            return SqlValue.Null(SqlType.SmallInt);
        var name = nameValue.AsString;
        return SqlValue.FromInt16((short)(runtime.Batch.Connection.Cursors.TryGetValue(name, out var cursor)
            ? cursor.StatusValue
            : -3));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override string DebugDisplay() => $"CURSOR_STATUS({this.scopeArg.DebugDisplay()}, {this.nameArg.DebugDisplay()})";
}
