using SqlServerSimulator.Parser;
using System.Globalization;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Column schema of the <c>DBCC SHRINKFILE</c> report row, probe-confirmed
    /// against SQL Server 2025: <c>DbId</c> is smallint, the rest int.
    /// </summary>
    private static readonly SqlType[] ShrinkFileSchema =
        [SqlType.SmallInt, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32];

    private static readonly string[] ShrinkFileColumnNames =
        ["DbId", "FileId", "CurrentSize", "MinimumSize", "UsedPages", "EstimatedPages"];

    /// <summary>
    /// Parses and executes the SHRINK family — <c>DBCC SHRINKDATABASE</c> /
    /// <c>DBCC SHRINKFILE</c> — peeking past the <c>DBCC</c> keyword. On any
    /// other subcommand it restores the cursor to <c>DBCC</c> (so
    /// <see cref="TryParseDbcc"/> handles TRACEON / TRACEOFF) and returns false.
    /// </summary>
    /// <remarks>
    /// Both forms reclaim memory by trimming fully-dead pages and freed LOB
    /// pages from the tail of every base table's storage
    /// (<see cref="Heap.TrimTrailingDeadPages"/> /
    /// <see cref="Heap.TrimTrailingFreeLobPages"/>). Because a <c>(page, slot)</c>
    /// address is depended on by cursors, version Rids, and forward pointers,
    /// only the trailing run can be dropped — this lowers the high-water mark
    /// but doesn't compact to the live-row count, and so leaves interior dead
    /// pages reusable in place. A version-store GC pass runs first to release
    /// any history that no live snapshot still pins. SHRINKDATABASE produces no
    /// result set (matching the real server, which reports nothing when there's
    /// no file movement to describe); SHRINKFILE yields the documented per-file
    /// row with sizes synthesized from the heap page totals, since the simulator
    /// models a flat page list rather than physical database files.
    /// </remarks>
    private static bool TryParseShrink(ParserContext context, BatchContext batch, out SimulatedStatementOutcome? outcome)
    {
        outcome = null;
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        var subcommand = (context.Token as UnquotedString)?.ContextualKeyword;
        if (subcommand is not (ContextualKeyword.ShrinkDatabase or ContextualKeyword.ShrinkFile))
        {
            context.RestoreCheckpoint(checkpoint);
            return false;
        }
        var isFile = subcommand == ContextualKeyword.ShrinkFile;

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var firstArg = context.Token;
        var fileId = isFile && firstArg is Numeric { Value: { IsNull: false } fid } ? fid.AsInt32 : 1;

        // Consume any remaining comma-separated arguments (target percent / size,
        // NOTRUNCATE / TRUNCATEONLY / EMPTYFILE) — all parse-and-discard.
        context.MoveNextRequired();
        while (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            context.MoveNextRequired();
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Optional `WITH NO_INFOMSGS` — the lone WITH option for SHRINK; the
        // simulator emits no info messages, so it's consumed and discarded.
        var afterParen = context.SaveCheckpoint();
        context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
            _ = context.GetNextRequired<Name>();
        else
            context.RestoreCheckpoint(afterParen);

        if (batch.IsSkipping)
            return true;

        var simulation = context.Connection.Simulation;
        var target = isFile
            ? context.Connection.CurrentDatabase
            : ResolveShrinkDatabase(simulation, firstArg);

        ShrinkDatabaseStorage(target);

        if (isFile)
        {
            var totalPages = TotalHeapPages(target);
            SqlValue[] row =
            [
                SqlValue.FromInt16(SmallDatabaseId(simulation, target)),
                SqlValue.FromInt32(fileId),
                SqlValue.FromInt32(totalPages),
                SqlValue.FromInt32(totalPages),
                SqlValue.FromInt32(totalPages),
                SqlValue.FromInt32(totalPages),
            ];
            outcome = new SimulatedSqlResultSet(ShrinkFileSchema, ShrinkFileColumnNames, [RowEncoder.EncodeRow(ShrinkFileSchema, row)]);
        }

        return true;
    }

    /// <summary>
    /// Resolves the <c>DBCC SHRINKDATABASE</c> first argument to a database: a
    /// bare / bracketed name routes through <see cref="Databases"/>
    /// (Msg 2520 on miss), a numeric database-id through the same 1-based
    /// name-ordered id convention <see cref="DbId"/> uses.
    /// </summary>
    private static Database ResolveShrinkDatabase(Simulation simulation, Token? firstArg)
    {
        switch (firstArg)
        {
            case Name name:
                return simulation.Databases.TryGetValue(name.Value, out var byName)
                    ? byName
                    : throw SimulatedSqlException.CouldNotFindDatabase(name.Value);
            case Numeric { Value: { IsNull: false } idValue }:
                var id = idValue.AsInt32;
                short pos = 1;
                foreach (var db in DbId.OrderedDatabases(simulation))
                {
                    if (pos == id)
                        return db;
                    pos++;
                }
                throw SimulatedSqlException.CouldNotFindDatabase(id.ToString(CultureInfo.InvariantCulture));
            default:
                throw SimulatedSqlException.CouldNotFindDatabase(firstArg?.ToString() ?? "");
        }
    }

    /// <summary>
    /// Trims every base table in <paramref name="database"/> down to its
    /// trailing-live storage. Runs version-store GC first so unpinned history
    /// stops holding pages, then drops fully-dead trailing data pages (gated by
    /// a no-version-entry / no-held-lock check) and freed trailing LOB pages.
    /// </summary>
    private static void ShrinkDatabaseStorage(Database database)
    {
        VersionStore.RunGarbageCollection(database);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                _ = table.Heap.TrimTrailingDeadPages(p => PageIsPinned(table, p));
                _ = table.Heap.TrimTrailingFreeLobPages();
            }
        }
    }

    /// <summary>
    /// True when a historical version or a held row lock is keyed on
    /// <paramref name="pageIndex"/> — either keeps a <c>(page, slot)</c> address
    /// reachable, so the page can't be dropped even when its rows are all dead.
    /// </summary>
    private static bool PageIsPinned(HeapTable table, int pageIndex)
    {
        foreach (var (versionPage, _) in table.RowVersions.Keys)
        {
            if (versionPage == pageIndex)
                return true;
        }
        foreach (var (rid, resource) in table.RowLocks)
        {
            if (rid.PageIndex == pageIndex && resource.Holders.Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>Total data + LOB pages held across every base table in the database.</summary>
    private static int TotalHeapPages(Database database)
    {
        var total = 0;
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
                total += table.Heap.Pages.Count + table.Heap.LobPages.Count;
        }
        return total;
    }

    /// <summary>The 1-based, name-ordered database id (matching <see cref="DbId"/>) as a smallint.</summary>
    private static short SmallDatabaseId(Simulation simulation, Database target)
    {
        short id = 1;
        foreach (var db in DbId.OrderedDatabases(simulation))
        {
            if (ReferenceEquals(db, target))
                return id;
            id++;
        }
        return 1;
    }

    /// <summary>
    /// Parses <c>DBCC TRACEON(N)</c> / <c>DBCC TRACEOFF(N)</c>. The optional
    /// <c>, -1</c> suffix that promotes the flag to global scope isn't modeled
    /// — flags scope to <see cref="SimulatedDbConnection.TraceFlags"/> on the
    /// executing connection, so concurrent connections don't share state.
    /// </summary>
    private static bool TryParseDbcc(ParserContext context)
    {
        context.MoveNextRequired();
        bool turningOn;
        switch ((context.Token as UnquotedString)?.ContextualKeyword)
        {
            case ContextualKeyword.TraceOn: turningOn = true; break;
            case ContextualKeyword.TraceOff: turningOn = false; break;
            default: return false;
        }

        if (context.GetNextRequired() is not Operator { Character: '(' })
            return false;

        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numericValue })
            return false;

        if (context.GetNextRequired() is not Operator { Character: ')' })
            return false;

        if (context.Batch.IsSkipping)
            return true;
        var flag = numericValue.AsInt32;
        var flags = context.Connection.TraceFlags;
        _ = turningOn ? flags.Add(flag) : flags.Remove(flag);
        return true;
    }
}
