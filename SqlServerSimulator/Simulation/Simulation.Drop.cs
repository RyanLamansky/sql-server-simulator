using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>DROP TABLE [IF EXISTS] name[, name...]</c>. Routes <c>#foo</c>
    /// names to the connection's <see cref="SimulatedDbConnection.TempTables"/>
    /// dict; everything else to the named schema's heap-table dict (or the
    /// default <c>dbo</c> schema for an unqualified reference). Missing
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
        var targetKind = context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.Table } => DropTargetKind.Table,
            ReservedKeyword { Keyword: Keyword.Function } => DropTargetKind.Function,
            ReservedKeyword { Keyword: Keyword.View } => DropTargetKind.View,
            ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc } => DropTargetKind.Procedure,
            _ => DropTargetKind.None,
        };
        if (targetKind == DropTargetKind.None)
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
            var name = BatchContext.ParseObjectName(context);
            switch (targetKind)
            {
                case DropTargetKind.Function:
                    DropOneFunction(context, name, ifExists);
                    break;
                case DropTargetKind.View:
                    DropOneView(context, name, ifExists);
                    break;
                case DropTargetKind.Procedure:
                    DropOneProcedure(context, name, ifExists);
                    break;
                default:
                    DropOneTable(context, name, ifExists);
                    break;
            }

            // ParseObjectName leaves the cursor on the last name segment;
            // peek for the comma list separator without permanently advancing
            // past statement-boundary tokens.
            context.MoveNextOptional();
            if (context.Token is not Operator { Character: ',' })
                break;
            context.MoveNextRequired();
        }
        return true;
    }

    private enum DropTargetKind { None, Table, Function, View, Procedure }

    /// <summary>
    /// Removes one entry from the target schema's <see cref="Schema.Procedures"/>
    /// dict. Missing procedure → Msg 3701 (procedure variant) unless
    /// <paramref name="ifExists"/> is set. Same shape as
    /// <see cref="DropOneFunction"/> / <see cref="DropOneView"/> —
    /// procedures don't participate in the undo log either.
    /// </summary>
    private static void DropOneProcedure(ParserContext context, MultiPartName name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is null || !schema.Procedures.TryRemove(name.Leaf, out _))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropProcedureDoesNotExist(name.ToString());
        }
    }

    /// <summary>
    /// Removes one entry from the target schema's <see cref="Schema.Views"/>
    /// dict. Missing view → Msg 3701 (view variant) unless
    /// <paramref name="ifExists"/> is set.
    /// </summary>
    private static void DropOneView(ParserContext context, MultiPartName name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is null || !schema.Views.TryRemove(name.Leaf, out _))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropViewDoesNotExist(name.ToString());
        }
    }

    /// <summary>
    /// Removes one entry from the target schema's <see cref="Schema.Functions"/>
    /// dict. Routing mirrors <see cref="DropOneTable"/>'s regular-table branch:
    /// resolve the schema, lookup the leaf, raise Msg 3701 (function variant)
    /// on miss unless <paramref name="ifExists"/> is set. Functions don't
    /// participate in the undo log — same asymmetry as regular CREATE TABLE /
    /// DROP TABLE (only temp-table DDL is transactional).
    /// </summary>
    private static void DropOneFunction(ParserContext context, MultiPartName name, bool ifExists)
    {
        if (context.Batch.IsSkipping)
            return;
        var schema = context.Batch.TryResolveSchema(name, out var resolved) ? resolved : null;
        if (schema is null || !schema.Functions.TryRemove(name.Leaf, out _))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropFunctionDoesNotExist(name.ToString());
        }
    }

    private static void DropOneTable(ParserContext context, MultiPartName name, bool ifExists)
    {
        // In a skipped IF branch, gate both the existence check (Msg 3701)
        // and the dict removal: `IF OBJECT_ID('foo','U') IS NOT NULL DROP
        // TABLE foo` when foo doesn't exist should silently skip the un-taken
        // branch rather than raise.
        if (context.Batch.IsSkipping)
            return;
        var isTempTable = BatchContext.IsLocalTempName(name.Leaf);
        // For temp tables a qualifier is cosmetic (real SQL Server ignores it
        // — `tempdb..#foo` and `tempdb.dbo.#foo` both resolve to the same
        // session-local table). For regular tables the schema is looked up
        // through CurrentDatabase.Schemas; a missing schema or db-mismatched
        // 3-part name surfaces the standard Msg 3701 below.
        var destination = isTempTable
            ? context.Batch.Connection.TempTables
            : context.Batch.TryResolveSchema(name, out var schema) ? schema.HeapTables : null;
        if (destination is null || !destination.TryRemove(name.Leaf, out var removedTable))
        {
            if (ifExists)
                return;
            throw SimulatedSqlException.CannotDropTableDoesNotExist(name.ToString());
        }
        // Temp-table DDL participates in transaction rollback (matching real
        // SQL Server). Regular DROP TABLE isn't logged — same asymmetry
        // documented for CREATE TABLE.
        if (isTempTable && context.Connection.CurrentTransaction is { } tx)
            tx.UndoLog.RecordTempTableRemoval(context.Batch.Connection.TempTables, name.Leaf, removedTable);
    }
}
