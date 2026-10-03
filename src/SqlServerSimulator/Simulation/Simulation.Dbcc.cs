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
    /// Column names of the <c>DBCC SHOW_STATISTICS … WITH HISTOGRAM</c> result
    /// set, probe-confirmed against SQL Server 2025. <c>RANGE_HI_KEY</c> is typed
    /// dynamically as the statistic's leading key column type; the trailing four
    /// are <c>real</c> / <c>real</c> / <c>bigint</c> / <c>real</c>.
    /// </summary>
    private static readonly string[] HistogramColumnNames =
        ["RANGE_HI_KEY", "RANGE_ROWS", "EQ_ROWS", "DISTINCT_RANGE_ROWS", "AVG_RANGE_ROWS"];

    /// <summary>
    /// Parses and executes the SHRINK family — <c>DBCC SHRINKDATABASE</c> /
    /// <c>DBCC SHRINKFILE</c> — peeking past the <c>DBCC</c> keyword. On any
    /// other subcommand it restores the cursor to <c>DBCC</c> (so
    /// <see cref="ParseDbccCommand"/> handles it) and returns false.
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
    /// result set but Msg 5201 for each file, as real reports a file with no
    /// free space to give back; SHRINKFILE yields the documented per-file row
    /// with sizes synthesized from the heap page totals, since the simulator
    /// models a flat page list rather than physical database files. Msg 2528
    /// closes both, and <c>WITH NO_INFOMSGS</c> silences everything, the row
    /// included (probed 2026-09-28 against SQL Server 2025).
    /// </remarks>
    private static bool TryParseShrink(ParserContext context, BatchContext batch, out List<SimulatedStatementOutcome> outcomes)
    {
        outcomes = [];
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

        // Optional `WITH NO_INFOMSGS` — the lone WITH option for SHRINK —
        // silences SHRINKFILE's row as well as the messages.
        var informational = true;
        var afterParen = context.SaveCheckpoint();
        context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            _ = context.GetNextRequired<Name>();
            informational = false;
        }
        else
        {
            context.RestoreCheckpoint(afterParen);
        }

        if (batch.IsSkipping)
            return true;

        var simulation = context.Connection.Simulation;
        var target = isFile
            ? context.Connection.CurrentDatabase
            : ResolveShrinkDatabase(simulation, context.Connection.CurrentDatabase, firstArg);

        ShrinkDatabaseStorage(context.Connection.Simulation, target);

        if (!informational)
            return true;
        if (!isFile)
        {
            // The files' sizes are fixed, so neither ever has free space to give back.
            var databaseId = SmallDatabaseId(simulation, target);
            batch.AppendInfoError(@class: 0, state: 1, number: 5201, message: SimulatedSqlException.ShrinkSkippedFileText(1, databaseId));
            batch.AppendInfoError(@class: 0, state: 2, number: 5201, message: SimulatedSqlException.ShrinkSkippedFileText(2, databaseId));
            batch.AppendInfoError(@class: 0, state: 1, number: 2528, message: "DBCC execution completed. If DBCC printed error messages, contact your system administrator.");
        }
        else
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
            outcomes.Add(new SimulatedSqlResultSet(ShrinkFileSchema, ShrinkFileColumnNames, [RowEncoder.EncodeRow(ShrinkFileSchema, row)]));
            AfterDbccRows(batch, outcomes, SimulatedSqlException.DbccExecutionCompletedMessage(batch));
        }

        return true;
    }

    /// <summary>
    /// Parses and executes <c>DBCC SHOW_STATISTICS(&lt;table&gt;, &lt;stat&gt;) WITH
    /// HISTOGRAM</c> — DacFx's bacpac-export chunking probe — peeking past the
    /// <c>DBCC</c> keyword and restoring the cursor (returning false) on any
    /// other subcommand. Both argument forms real accepts are handled: a
    /// <c>N'...'</c> string literal whose content is a 1- / 2-part bracketed name
    /// (DacFx's form) and a bare dotted identifier. Only <c>WITH HISTOGRAM</c>
    /// (Msg 2528 after the rows) and <c>WITH HISTOGRAM, NO_INFOMSGS</c> are
    /// modeled; the no-WITH three-result-set form and every other WITH option
    /// (STAT_HEADER / DENSITY_VECTOR / STATS_STREAM) raise
    /// <see cref="NotSupportedException"/> naming the unmodeled option.
    /// </summary>
    /// <remarks>
    /// The named statistic is matched against the table's index-backed stats via
    /// the canonical <see cref="HeapTable.IndexIdentities"/> allocator; a heap
    /// identity (no backing index) can't match and a miss raises Msg 2767. The
    /// histogram is generated honestly from live heap data but as a single bucket
    /// (real emits up to ~200 steps): <c>RANGE_HI_KEY</c> = MAX of the leading key
    /// column, <c>EQ_ROWS</c> = rows equal to that max, <c>RANGE_ROWS</c> = the
    /// remaining non-null rows, <c>DISTINCT_RANGE_ROWS</c> = distinct non-null
    /// values minus one, <c>AVG_RANGE_ROWS</c> = <c>RANGE_ROWS / DISTINCT_RANGE_ROWS</c>
    /// (1 when there are no range rows, matching real's single-row convention).
    /// An empty table yields an empty (0-row) result set.
    /// </remarks>
    private static bool TryParseShowStatistics(ParserContext context, BatchContext batch, out List<SimulatedStatementOutcome> outcomes)
    {
        outcomes = [];
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        if ((context.Token as UnquotedString)?.ContextualKeyword != ContextualKeyword.Show_Statistics)
        {
            context.RestoreCheckpoint(checkpoint);
            return false;
        }

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var (tableName, tableText) = ParseShowStatisticsName(context, parameterNumber: 1);

        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var (statName, _) = ParseShowStatisticsName(context, parameterNumber: 2);

        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.With })
            throw new NotSupportedException("DBCC SHOW_STATISTICS without WITH HISTOGRAM (the STAT_HEADER / DENSITY_VECTOR / HISTOGRAM three-result-set form) isn't modeled.");

        var option = context.GetNextRequired<Name>();
        if (!BuiltInToken.Equals(option.Value, "HISTOGRAM"))
            throw new NotSupportedException($"DBCC SHOW_STATISTICS WITH {option.Value} isn't modeled; only WITH HISTOGRAM ships.");

        var informational = true;
        var afterOption = context.SaveCheckpoint();
        if (context.MoveNext() && context.Token is Operator { Character: ',' })
        {
            if (context.GetNextRequired<Name>() is not { Value: var second } || !BuiltInToken.Equals(second, "NO_INFOMSGS"))
                throw new NotSupportedException("DBCC SHOW_STATISTICS WITH HISTOGRAM combined with options other than NO_INFOMSGS isn't modeled.");
            informational = false;
        }
        else
        {
            context.RestoreCheckpoint(afterOption);
        }

        if (batch.IsSkipping)
            return true;

        if (!batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindTableOrObject(tableText);

        outcomes.Add(BuildHistogram(batch, table, statName.Leaf));
        if (informational)
            AfterDbccRows(batch, outcomes, SimulatedSqlException.DbccExecutionCompletedMessage(batch));
        return true;
    }

    /// <summary>
    /// Reads one <c>DBCC SHOW_STATISTICS</c> argument as an object name: a
    /// <c>N'[schema].[table]'</c> string literal (parsed with the same seam
    /// <c>OBJECT_ID</c> uses) or a bare dotted / bracketed identifier. Returns the
    /// parsed <see cref="MultiPartName"/> plus the raw text real echoes in its
    /// Msg 2501. A NULL or unparseable argument raises Msg 2560.
    /// </summary>
    private static (MultiPartName Name, string Display) ParseShowStatisticsName(ParserContext context, int parameterNumber)
    {
        if (context.Token is Literal literal)
        {
            if (literal.Value.IsNull)
                throw SimulatedSqlException.DbccParameterIsIncorrect(parameterNumber);
            var text = literal.Value.CoerceTo(SqlType.NVarchar).AsString;
            return ObjectId.TryParseObjectName(text, out var parsed)
                ? (parsed, text)
                : throw SimulatedSqlException.DbccParameterIsIncorrect(parameterNumber);
        }
        var name = BatchContext.ParseObjectName(context);
        return (name, name.ToString());
    }

    /// <summary>
    /// Resolves <paramref name="statisticsName"/> to an index-backed statistic on
    /// <paramref name="table"/> and builds its single-bucket histogram result set.
    /// The synthetic heap identity (no backing index) can't match; a miss raises
    /// Msg 2767.
    /// </summary>
    private static SimulatedSqlResultSet BuildHistogram(BatchContext batch, HeapTable table, string statisticsName)
    {
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.IsHeap || !BuiltInToken.Equals(identity.Name, statisticsName))
                continue;

            var leadingOrdinal = identity.Constraint is { } key
                ? key.StorageOrdinals[0]
                : identity.Index!.KeyColumns[0].StorageOrdinal;
            var storedColumns = table.StoredColumns;
            var lobStore = table.Heap;
            return ComputeHistogram(table.Schema[leadingOrdinal], table, rowBytes => RowDecoder.DecodeColumn(storedColumns, rowBytes, leadingOrdinal, lobStore));
        }

        // A CREATE STATISTICS object describes its leading column over the rows
        // its filter admits, the filter judged as a filtered index's is.
        var collation = batch.CurrentDatabase.Collation;
        if (table.UserStatistics.Find(statistic => collation.Equals(statistic.Name, statisticsName)) is { } user)
        {
            var leadingFull = user.ColumnFullOrdinals[0];
            SqlValue[]? fullRow = null;
            return ComputeHistogram(table.Columns[leadingFull].Type, table, rowBytes =>
            {
                fullRow = DecodeFullRowWithComputed(table, rowBytes, batch, ref fullRow);
                return user.Filter is { } filter && EvaluateIndexFilter(filter, table, fullRow, batch) != true ? null : fullRow[leadingFull];
            });
        }
        throw SimulatedSqlException.CouldNotLocateStatistics(statisticsName);
    }

    /// <summary>
    /// Scans <paramref name="table"/>'s live rows once, reading the leading key
    /// column through <paramref name="readLeading"/> (null for a row the
    /// statistic's filter leaves out), and folds the values into a multi-step histogram: one step
    /// per distinct leading-key value up to 200 steps, else 200 boundary steps
    /// evenly spaced over the sorted distinct values. The first step is always
    /// the MIN value and the last the MAX, matching real SQL Server's
    /// histogram envelope — DacFx's bacpac-export chunking interpolates
    /// between adjacent RANGE_HI_KEY steps, and a histogram without the MIN
    /// anchor overflows its boundary arithmetic client-side.
    /// <c>RANGE_HI_KEY</c> carries the leading key column's own type so it
    /// round-trips over the wire through the standard codecs. An empty table
    /// yields a 0-row result set.
    /// </summary>
    private static SimulatedSqlResultSet ComputeHistogram(SqlType keyType, HeapTable table, Func<byte[], SqlValue?> readLeading)
    {
        SqlType[] schema = [keyType, SqlType.Real, SqlType.Real, SqlType.BigInt, SqlType.Real];

        var counts = new Dictionary<SqlValueKey, (SqlValue Value, long Count)>();
        foreach (var rowBytes in table.Heap.EnumerateRows())
        {
            if (readLeading(rowBytes) is not { IsNull: false } value)
                continue;
            var key = new SqlValueKey([value]);
            counts[key] = counts.TryGetValue(key, out var existing)
                ? (existing.Value, existing.Count + 1)
                : (value, 1);
        }

        if (counts.Count == 0)
            return new SimulatedSqlResultSet(schema, HistogramColumnNames, Array.Empty<byte[]>());

        var sorted = new (SqlValue Value, long Count)[counts.Count];
        var n = 0;
        foreach (var entry in counts.Values)
            sorted[n++] = entry;
        Array.Sort(sorted, static (a, b) => a.Value.CompareTo(b.Value));

        // Boundary indices into the sorted distinct array: every distinct
        // value when they fit in 200 steps, else 200 evenly-spaced indices.
        // Index 0 (MIN) and index n-1 (MAX) are always present.
        const int maxSteps = 200;
        var stepIndexes = new List<int>(Math.Min(maxSteps, sorted.Length));
        if (sorted.Length <= maxSteps)
        {
            for (var i = 0; i < sorted.Length; i++)
                stepIndexes.Add(i);
        }
        else
        {
            var previous = -1;
            for (var k = 0; k < maxSteps; k++)
            {
                var index = (int)((long)k * (sorted.Length - 1) / (maxSteps - 1));
                if (index == previous)
                    continue;
                stepIndexes.Add(index);
                previous = index;
            }
        }

        var rows = new byte[stepIndexes.Count][];
        var lowerExclusive = -1;
        for (var step = 0; step < stepIndexes.Count; step++)
        {
            var boundary = stepIndexes[step];
            long rangeRows = 0;
            for (var i = lowerExclusive + 1; i < boundary; i++)
                rangeRows += sorted[i].Count;
            var distinctRangeRows = Math.Max(0, boundary - lowerExclusive - 1);
            var avgRangeRows = distinctRangeRows == 0 ? 1f : (float)rangeRows / distinctRangeRows;
            SqlValue[] row =
            [
                sorted[boundary].Value,
                SqlValue.FromSingle(rangeRows),
                SqlValue.FromSingle(sorted[boundary].Count),
                SqlValue.FromInt64(distinctRangeRows),
                SqlValue.FromSingle(avgRangeRows),
            ];
            rows[step] = RowEncoder.EncodeRow(schema, row);
            lowerExclusive = boundary;
        }

        return new SimulatedSqlResultSet(schema, HistogramColumnNames, rows);
    }

    /// <summary>
    /// Resolves the <c>DBCC SHRINKDATABASE</c> first argument to a database: a
    /// bare / bracketed name routes through <see cref="Databases"/>
    /// (Msg 2520 on miss), a numeric database-id through the same
    /// <c>master</c>-is-1 / user-databases-from-5 convention
    /// <see cref="DbId"/> uses, 0 naming the current database.
    /// </summary>
    private static Database ResolveShrinkDatabase(Simulation simulation, Database current, Token? firstArg)
    {
        switch (firstArg)
        {
            case Name name:
                return simulation.Databases.TryGetValue(name.Value, out var byName)
                    ? byName
                    : throw SimulatedSqlException.CouldNotFindDatabase(name.Value);
            case Numeric { Value: { IsNull: false } idValue } when idValue.AsInt32 == 0:
                return current;
            case Numeric { Value: { IsNull: false } idValue }:
                var id = idValue.AsInt32;
                foreach (var (db, pos) in DbId.DatabasesWithIds(simulation))
                {
                    if (pos == id)
                        return db;
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
    private static void ShrinkDatabaseStorage(Simulation simulation, Database database)
    {
        VersionStore.RunGarbageCollection(simulation, database);
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
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
        foreach (var ((versionPage, _), _) in table.Heap.RowVersions)
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
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, table) in schema.HeapTables)
                total += table.Heap.Pages.Count + table.Heap.LobPages.Count;
        }
        return total;
    }

    /// <summary>The database id (matching <see cref="DbId"/>: master = 1, user databases from 5) as a smallint.</summary>
    private static short SmallDatabaseId(Simulation simulation, Database target)
    {
        foreach (var (db, id) in DbId.DatabasesWithIds(simulation))
        {
            if (ReferenceEquals(db, target))
                return id;
        }
        return 1;
    }

    private static readonly string[] InputBufferColumnNames = ["EventType", "Parameters", "EventInfo"];


    /// <summary>
    /// Parses and executes <c>DBCC INPUTBUFFER(spid [, request_id]) [WITH NO_INFOMSGS]</c>:
    /// the row <c>sys.dm_exec_input_buffer</c> reports, under the legacy
    /// column names, then Msg 2528. A session may read its own buffer; any
    /// other takes <c>VIEW SERVER STATE</c> (Msg 2571). A session id no
    /// session holds is Msg 7955, a request id other than 0 Msg 7960, and
    /// a non-integer id Msg 2560 — each probed 2026-09-25 against SQL Server 2025.
    /// </summary>
    private static bool TryParseInputBuffer(ParserContext context, BatchContext batch, out SimulatedStatementOutcome? outcome, out SimulatedError? completion)
    {
        outcome = null;
        completion = null;
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextRequired() is not UnquotedString subcommand || !BuiltInToken.Equals(subcommand.ToString(), "INPUTBUFFER"))
        {
            context.RestoreCheckpoint(checkpoint);
            return false;
        }

        var arguments = new List<Expression>(2);
        var afterName = context.SaveCheckpoint();
        context.MoveNextOptional();
        if (context.Token is Operator { Character: '(' })
        {
            do
            {
                context.MoveNextRequired();
                arguments.Add(Expression.Parse(context));
            }
            while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            afterName = context.SaveCheckpoint();
            context.MoveNextOptional();
        }

        var informational = true;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            var option = context.GetNextRequired<Name>();
            if (!BuiltInToken.Equals(option.ToString(), "NO_INFOMSGS"))
                throw SimulatedSqlException.DbccWithOptionNotValid();
            informational = false;
        }
        else
        {
            context.RestoreCheckpoint(afterName);
        }

        if (arguments.Count is 0 or > 2)
            throw SimulatedSqlException.DbccWrongParameterCount();
        if (batch.IsSkipping)
            return true;

        var runtime = new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), batch);
        var spid = DbccIntegerArgument(arguments[0].Run(runtime), 1);
        var requestId = arguments.Count == 2 ? DbccIntegerArgument(arguments[1].Run(runtime), 2) : 0;

        var connection = batch.Connection;
        var security = connection.Security;
        if (spid != connection.Spid && !security.EffectiveIsDbo
            && !connection.Simulation.HoldsServerPermission(security.Effective.LoginName, Permission.ViewServerState))
        {
            throw SimulatedSqlException.DbccPermissionDenied(security.Effective.DatabasePrincipalName, "inputbuffer");
        }
        if (Selection.InputBufferOf(connection.Simulation, spid) is not { } row)
            throw SimulatedSqlException.InvalidSpidSpecified(spid);
        if (requestId != 0)
            throw SimulatedSqlException.InvalidSpidOrBatchId(spid, requestId);

        // Each string column is as wide as its value, as real sizes them.
        var eventType = row[0].AsString;
        var eventInfo = row[2].IsNull ? null : row[2].AsString.Length > 4000 ? row[2].AsString[..4000] : row[2].AsString;
        SqlType[] schema =
        [
            NVarcharSqlType.Get(Math.Max(1, eventType.Length), Collation.Baseline, Coercibility.Implicit),
            SqlType.SmallInt,
            NVarcharSqlType.Get(Math.Max(1, eventInfo?.Length ?? 1), Collation.Baseline, Coercibility.Implicit),
        ];
        outcome = new SimulatedSqlResultSet(schema, InputBufferColumnNames, [RowEncoder.EncodeRow(schema, [
            SqlValue.FromNVarchar(eventType), row[1], eventInfo is null ? SqlValue.Null(schema[2]) : SqlValue.FromNVarchar(eventInfo),
        ])]);
        if (informational)
            completion = batch.InfoMessage(@class: 0, state: 1, number: 2528, message: "DBCC execution completed. If DBCC printed error messages, contact your system administrator.");
        return true;

        static int DbccIntegerArgument(SqlValue value, int position) =>
            !value.IsNull && SqlType.IsIntegerCategory(value.Type) ? value.CoerceTo(SqlType.Int32).AsInt32 : throw SimulatedSqlException.DbccParameterIsIncorrect(position);
    }
}
