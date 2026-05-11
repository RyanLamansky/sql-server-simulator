using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>DROP TABLE [IF EXISTS] name[, name...]</c>. Routes <c>#foo</c>
    /// names to the connection's <see cref="SimulatedDbConnection.TempTables"/>
    /// dict; everything else to <see cref="Database.HeapTables"/>. Missing
    /// table without <c>IF EXISTS</c> raises Msg 3701 St 5 (probe-confirmed
    /// against SQL Server 2025 for both regular and temp targets); with
    /// <c>IF EXISTS</c> the miss is silent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comma-list form (<c>DROP TABLE a, b, c</c>) is real SQL Server
    /// syntax — modeled here because it composes naturally with the single-
    /// table case and the parser's lookahead doesn't have to backtrack.
    /// </para>
    /// <para>
    /// Three-part qualifiers on a <c>#</c>-prefixed leaf are accepted and
    /// ignored (probe-confirmed: <c>tempdb..#foo</c>, <c>tempdb.dbo.#foo</c>,
    /// and even <c>claude..#foo</c> all resolve to the same session-local
    /// table). Qualified non-<c>#</c> names are rejected today — the
    /// simulator has a single database and no <c>USE</c>, so a database
    /// qualifier other than the current one would have no resolution target.
    /// </para>
    /// </remarks>
    private static bool TryParseDrop(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Table })
            return false;

        context.MoveNextRequired();
        var ifExists = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                return false;
            ifExists = true;
            context.MoveNextRequired();
        }

        while (true)
        {
            var name = ParseDropTargetName(context);
            DropOneTable(context, name, ifExists);

            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        return true;
    }

    /// <summary>
    /// Reads one (optionally qualified) table name. For <c>#</c>-prefixed
    /// leaves the qualifier is dropped (real SQL Server ignores it). For
    /// regular names a multi-part qualifier collapses to the leaf; the
    /// simulator has a single database and no <c>USE</c>, so a qualifier
    /// is cosmetic and routes to the same dict the leaf would. The leaf is
    /// the rightmost non-empty segment; empty segments between dots (the
    /// <c>tempdb..#foo</c> shape, schema omitted) are tolerated.
    /// </summary>
    private static string ParseDropTargetName(ParserContext context)
    {
        if (context.Token is not Name first)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var leaf = first.Value;

        // State-machine read of `.<segment>` suffixes. `cursorAlreadyAtNext`
        // is true when the previous iteration's empty-segment branch left
        // the cursor on the next `.` and we shouldn't MoveNext at the top.
        var cursorAlreadyAtNext = false;
        while (true)
        {
            if (!cursorAlreadyAtNext && !context.MoveNext())
                return leaf;
            cursorAlreadyAtNext = false;
            if (context.Token is not Operator { Character: '.' })
                return leaf;
            if (!context.MoveNext())
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.Token is Name next)
            {
                leaf = next.Value;
                continue;
            }
            if (context.Token is Operator { Character: '.' })
            {
                // Empty segment (e.g. between db and omitted schema). The
                // cursor is already on the next `.`; signal the next iteration
                // to skip its leading MoveNext so it consumes this `.`.
                cursorAlreadyAtNext = true;
                continue;
            }
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    private static void DropOneTable(ParserContext context, string name, bool ifExists)
    {
        var isTempTable = BatchContext.IsLocalTempName(name);
        var destination = isTempTable
            ? context.Batch.Connection.TempTables
            : context.CurrentDatabase.HeapTables;
        if (!destination.TryRemove(name, out var removedTable))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropTableDoesNotExist(name);
        }
        // Temp-table DDL participates in transaction rollback (matching real
        // SQL Server). Regular DROP TABLE isn't logged — same asymmetry
        // documented for CREATE TABLE.
        if (isTempTable && context.Connection.CurrentTransaction is { } tx)
            tx.UndoLog.RecordTempTableRemoval(context.Batch.Connection.TempTables, name, removedTable);
    }
}
