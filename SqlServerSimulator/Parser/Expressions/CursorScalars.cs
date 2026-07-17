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
/// SQL <c>CURSOR_STATUS(scope, name)</c>: reports a cursor's state as a
/// <see cref="SqlType.SmallInt"/>. The scope argument selects the namespace and
/// is honored (probe-confirmed): <c>'global'</c> / <c>'local'</c> look in the
/// connection-global / batch-local named-cursor maps, <c>'variable'</c> looks up
/// the cursor variable <c>@name</c>. Return codes: <c>1</c> open (with rows, or
/// any open DYNAMIC cursor), <c>0</c> open but empty, <c>-1</c> closed /
/// allocated-not-open, <c>-2</c> a cursor variable declared with no cursor
/// allocated, <c>-3</c> no cursor of that name in the named scope.
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
        var scopeValue = this.scopeArg.Run(runtime);
        var nameValue = this.nameArg.Run(runtime);
        if (nameValue.IsNull)
            return SqlValue.Null(SqlType.SmallInt);
        var batch = runtime.Batch;
        var scope = scopeValue.IsNull ? "" : scopeValue.AsString;
        var name = nameValue.AsString;

        // 'variable' scope: @name is a cursor variable. -2 = declared but no
        // cursor allocated; -3 = not a declared cursor variable at all.
        if (string.Equals(scope, "variable", StringComparison.OrdinalIgnoreCase))
        {
            var varName = name.StartsWith('@') ? name[1..] : name;
            return SqlValue.FromInt16((short)(batch.CursorVariables.TryGetValue(varName, out var bound)
                ? bound?.StatusValue ?? -2
                : -3));
        }

        // 'local' / 'global' scope: the respective named-cursor map only.
        var map = string.Equals(scope, "local", StringComparison.OrdinalIgnoreCase)
            ? batch.LocalCursors
            : batch.Connection.Cursors;
        return SqlValue.FromInt16((short)(map.TryGetValue(name, out var cursor)
            ? cursor.StatusValue
            : -3));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override string DebugDisplay() => $"CURSOR_STATUS({this.scopeArg.DebugDisplay()}, {this.nameArg.DebugDisplay()})";
}
