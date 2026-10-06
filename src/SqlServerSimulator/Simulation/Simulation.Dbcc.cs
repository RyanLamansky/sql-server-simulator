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
        // A file id may carry a sign, which only finds no file.
        var negativeFirst = false;
        if (context.Token is Operator { Character: '-' })
        {
            negativeFirst = true;
            context.MoveNextRequired();
        }
        var firstArg = context.Token;

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
        // Shrinking takes db_owner (Msg 7983, probed 2026-10-04 against SQL
        // Server 2025), and SHRINKFILE's file must be one of the database's.
        RequireDbccDatabaseOwner(batch, target, isFile ? "shrinkfile" : "shrinkdatabase");
        var fileId = 1;
        if (isFile)
            fileId = ResolveShrinkFile(target, firstArg, negativeFirst);

        ShrinkDatabaseStorage(context.Connection.Simulation, target);

        if (!informational)
            return true;
        if (!isFile)
        {
            // The files' sizes are fixed, so neither ever has free space to give back.
            var databaseId = SmallDatabaseId(simulation, target);
            batch.AppendInfoError(@class: 0, state: 1, number: 5201, message: SimulatedSqlException.ShrinkSkippedFileText(1, databaseId));
            batch.AppendInfoError(@class: 0, state: 2, number: 5201, message: SimulatedSqlException.ShrinkSkippedFileText(2, databaseId));
            batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.DbccExecutionCompletedMessage(batch));
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
    /// Parses and executes <c>DBCC SHOW_STATISTICS(&lt;table&gt;, &lt;stat&gt;)
    /// [WITH option [, …]]</c> — among others DacFx's bacpac-export chunking
    /// probe — peeking past the <c>DBCC</c> keyword and restoring the cursor
    /// (returning false) on any other subcommand. Each argument is a
    /// <c>N'...'</c> string literal holding a 1- / 2-part name (DacFx's form),
    /// a bare dotted identifier, or a variable holding the name.
    /// </summary>
    /// <remarks>
    /// The report reads the statistic's snapshot — the rows as they stood when
    /// it was last built (see <see cref="StatisticsSnapshot"/>) — in up to
    /// three result sets, in real's fixed order: the <c>STAT_HEADER</c> row,
    /// the <c>DENSITY_VECTOR</c> and the <c>HISTOGRAM</c>, all three when no
    /// option picks among them. A statistic built over an empty table reports
    /// a header of NULLs and no density or histogram rows. <c>STATS_STREAM</c>
    /// (the serialized statistic) raises <see cref="NotSupportedException"/>.
    /// Errors mirror real: an unknown option is Msg 195 while compiling, an
    /// unresolvable table Msg 2501, an unknown statistic Msg 2767, a NULL,
    /// numeric or unparseable argument Msg 2560 (probed 2026-10-05 against SQL
    /// Server 2025).
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
        var tableArgument = ParseShowStatisticsArgument(context, parameterNumber: 1);

        // One argument is Msg 2583, Msg 2528 following it (probed 2026-10-04
        // against SQL Server 2025).
        if (context.GetNextRequired() is Operator { Character: ')' })
        {
            if (batch.IsSkipping)
                return true;
            throw SimulatedSqlException.FollowedByDbccCompleted(SimulatedSqlException.DbccWrongParameterCount(state: 5), batch.Connection.Language);
        }
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var statisticArgument = ParseShowStatisticsArgument(context, parameterNumber: 2);

        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        bool header = false, density = false, histogram = false, statsStream = false;
        var informational = true;
        var afterParen = context.SaveCheckpoint();
        if (context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            while (true)
            {
                var option = context.GetNextRequired<Name>();
                if (BuiltInToken.Equals(option.Value, "HISTOGRAM"))
                    histogram = true;
                else if (BuiltInToken.Equals(option.Value, "STAT_HEADER"))
                    header = true;
                else if (BuiltInToken.Equals(option.Value, "DENSITY_VECTOR"))
                    density = true;
                else if (BuiltInToken.Equals(option.Value, "NO_INFOMSGS"))
                    informational = false;
                else if (BuiltInToken.Equals(option.Value, "STATS_STREAM"))
                    statsStream = true;
                else
                    throw SimulatedSqlException.WithOptionNotRecognized(option.Value);
                var afterOption = context.SaveCheckpoint();
                if (context.MoveNext() && context.Token is Operator { Character: ',' })
                    continue;
                context.RestoreCheckpoint(afterOption);
                break;
            }
        }
        else
        {
            context.RestoreCheckpoint(afterParen);
        }
        if (!header && !density && !histogram)
            header = density = histogram = true;

        if (batch.IsSkipping)
            return true;

        var (tableName, tableText) = ResolveShowStatisticsArgument(batch, tableArgument, parameterNumber: 1);
        var (statName, _) = ResolveShowStatisticsArgument(batch, statisticArgument, parameterNumber: 2);

        if (!batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindTableOrObject(tableText);
        // Reading statistics takes SELECT on the table: Msg 229 then Msg 2557
        // without it (probed 2026-10-04 against SQL Server 2025).
        var database = batch.DatabaseFor(table);
        if (!table.IsTableVariable && table.Name is not ['#', ..]
            && !PermissionEnforcement.HoldsPermission(batch, database, Permission.Select, PermissionChecker.ClassObject, table.ObjectId, table.SchemaId))
        {
            throw SimulatedSqlException.FollowedByDbccCompleted(SimulatedSqlException.ShowStatisticsPermissionDenied(
                batch.Connection.Security.Effective.DatabasePrincipalName, table.Name, database.Name, SchemaNameOf(database, table), tableText), batch.Connection.Language);
        }

        var collation = batch.CurrentDatabase.Collation;
        TableStatistic? found = null;
        foreach (var candidate in StatisticsOn(table))
        {
            if (collation.Equals(candidate.Name, statName.Leaf))
            {
                found = candidate;
                break;
            }
        }
        if (found is not { } statistic)
        {
            var missing = SimulatedSqlException.CouldNotLocateStatistics(statName.Leaf);
            throw informational ? SimulatedSqlException.FollowedByDbccCompleted(missing, batch.Connection.Language) : missing;
        }
        if (statsStream)
            throw new NotSupportedException("DBCC SHOW_STATISTICS WITH STATS_STREAM (the serialized statistic) isn't modeled.");

        var snapshot = statistic.State.Snapshot;
        if (header)
            outcomes.Add(StatisticsHeader(statistic, snapshot));
        if (density)
            outcomes.Add(StatisticsDensityVector(snapshot));
        if (histogram)
            outcomes.Add(StatisticsHistogram(table.Columns[statistic.LeadingOrdinal].Type, snapshot));
        if (informational)
            AfterDbccRows(batch, outcomes, SimulatedSqlException.DbccExecutionCompletedMessage(batch));
        return true;
    }

    private static readonly SqlType[] StatisticsHeaderSchema =
    [
        SqlType.SystemName,
        NVarcharSqlType.Get(20, Collation.Baseline, Coercibility.CoercibleDefault),
        SqlType.BigInt,
        SqlType.BigInt,
        SqlType.SmallInt,
        SqlType.Real,
        SqlType.Real,
        NCharSqlType.Get(3, Collation.Baseline, Coercibility.CoercibleDefault),
        NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault),
        SqlType.BigInt,
        SqlType.Float,
    ];

    private static readonly string[] StatisticsHeaderColumnNames =
        ["Name", "Updated", "Rows", "Rows Sampled", "Steps", "Density", "Average key length", "String Index", "Filter Expression", "Unfiltered Rows", "Persisted Sample Percent"];

    private static readonly SqlType[] DensityVectorSchema =
        [SqlType.Real, SqlType.Real, NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.CoercibleDefault)];

    private static readonly string[] DensityVectorColumnNames = ["All density", "Average Length", "Columns"];

    /// <summary>The <c>STAT_HEADER</c> row: every value NULL but the name for a statistic without a snapshot.</summary>
    private static SimulatedSqlResultSet StatisticsHeader(TableStatistic statistic, StatisticsSnapshot? snapshot)
    {
        SqlValue[] row = snapshot is null
            ? [SqlValue.FromSystemName(statistic.Name), .. StatisticsHeaderSchema[1..].Select(static type => SqlValue.Null(type))]
            :
            [
                SqlValue.FromSystemName(statistic.Name),
                SqlValue.FromString(StatisticsHeaderSchema[1], SqlValue.FromDateTime(snapshot.Updated).CoerceTo(SqlType.NVarchar).AsString),
                SqlValue.FromInt64(snapshot.Rows),
                SqlValue.FromInt64(snapshot.Rows),
                SqlValue.FromInt16((short)snapshot.Histogram.Length),
                SqlValue.FromSingle(snapshot.HeaderDensity),
                SqlValue.FromSingle(snapshot.AverageKeyLength),
                SqlValue.FromString(StatisticsHeaderSchema[7], snapshot.StringIndex ? "YES" : "NO "),
                statistic.FilterDefinition is null ? SqlValue.Null(StatisticsHeaderSchema[8]) : SqlValue.FromString(StatisticsHeaderSchema[8], statistic.FilterDefinition),
                SqlValue.FromInt64(snapshot.UnfilteredRows),
                SqlValue.FromDouble(0),
            ];
        return new SimulatedSqlResultSet(StatisticsHeaderSchema, StatisticsHeaderColumnNames, [RowEncoder.EncodeRow(StatisticsHeaderSchema, row)]);
    }

    private static SimulatedSqlResultSet StatisticsDensityVector(StatisticsSnapshot? snapshot)
    {
        var rows = new List<byte[]>();
        foreach (var entry in snapshot?.DensityVector ?? [])
        {
            rows.Add(RowEncoder.EncodeRow(DensityVectorSchema,
                [SqlValue.FromSingle(entry.AllDensity), SqlValue.FromSingle(entry.AverageLength), SqlValue.FromString(DensityVectorSchema[2], entry.Columns)]));
        }
        return new SimulatedSqlResultSet(DensityVectorSchema, DensityVectorColumnNames, [.. rows]);
    }

    /// <summary>
    /// The <c>HISTOGRAM</c> rows. <c>RANGE_HI_KEY</c> carries the leading key
    /// column's own type, so it round-trips over the wire through the standard
    /// codecs; the first non-NULL step is always the MIN value and the last the
    /// MAX — load-bearing for DacFx, whose bacpac-export chunker interpolates
    /// between adjacent steps and overflows client-side without the MIN anchor.
    /// </summary>
    private static SimulatedSqlResultSet StatisticsHistogram(SqlType keyType, StatisticsSnapshot? snapshot)
    {
        SqlType[] schema = [keyType, SqlType.Real, SqlType.Real, SqlType.BigInt, SqlType.Real];
        var rows = new List<byte[]>();
        foreach (var step in snapshot?.Histogram ?? [])
        {
            rows.Add(RowEncoder.EncodeRow(schema,
            [
                step.RangeHighKey.IsNull ? SqlValue.Null(keyType) : step.RangeHighKey,
                SqlValue.FromSingle(step.RangeRows),
                SqlValue.FromSingle(step.EqualRows),
                SqlValue.FromInt64(step.DistinctRangeRows),
                SqlValue.FromSingle(step.AverageRangeRows),
            ]));
        }
        return new SimulatedSqlResultSet(schema, HistogramColumnNames, [.. rows]);
    }

    /// <summary>
    /// Reads one <c>DBCC SHOW_STATISTICS</c> argument: a string literal, a
    /// bare dotted / bracketed identifier, or a variable, resolved to a name
    /// when the statement runs. A NULL or numeric argument is Msg 2560.
    /// </summary>
    private static object ParseShowStatisticsArgument(ParserContext context, int parameterNumber) => context.Token switch
    {
        Literal literal => literal.Value.IsNull ? throw SimulatedSqlException.DbccParameterIsIncorrect(parameterNumber) : literal.Value,
        Numeric => throw SimulatedSqlException.DbccParameterIsIncorrect(parameterNumber),
        AtPrefixedString variable => variable.Span.ToString(),
        _ => BatchContext.ParseObjectName(context),
    };

    /// <summary>
    /// Resolves a parsed <c>DBCC SHOW_STATISTICS</c> argument to the name it
    /// holds and the text real echoes in its Msg 2501; a variable is read now.
    /// </summary>
    private static (MultiPartName Name, string Display) ResolveShowStatisticsArgument(BatchContext batch, object argument, int parameterNumber)
    {
        if (argument is MultiPartName name)
            return (name, name.ToString());
        var value = argument switch
        {
            SqlValue literal => literal,
            string variable => batch.Variables.TryGetValue(variable, out var slot) ? slot.Value : throw SimulatedSqlException.MustDeclareScalarVariable(variable),
            _ => throw SimulatedSqlException.DbccParameterIsIncorrect(parameterNumber),
        };
        if (value.IsNull)
            throw SimulatedSqlException.DbccParameterIsIncorrect(parameterNumber);
        var text = value.CoerceTo(SqlType.NVarchar).AsString;
        return ObjectId.TryParseObjectName(text, out var parsed)
            ? (parsed, text)
            : throw SimulatedSqlException.DbccParameterIsIncorrect(parameterNumber);
    }

    /// <summary>
    /// The file a <c>DBCC SHRINKFILE</c> names, by id or by name; one the
    /// database doesn't have is Msg 8985, state 2 for an id and 1 for a name
    /// (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static int ResolveShrinkFile(Database database, Token? firstArg, bool negative)
    {
        switch (firstArg)
        {
            case Numeric { Value: { IsNull: false } idValue } when SqlType.IsIntegerCategory(idValue.Type):
                var id = negative ? -idValue.CoerceTo(SqlType.BigInt).AsInt64 : idValue.CoerceTo(SqlType.BigInt).AsInt64;
                return database.Files.Exists(file => file.FileId == id && !file.IsContainer)
                    ? (int)id
                    : throw SimulatedSqlException.ShrinkFileNotFound(id.ToString(CultureInfo.InvariantCulture), database.Name, 2);
            case Literal { Value: { IsNull: false } text } when SqlType.IsStringCategory(text.Type):
                return database.FindFile(text.AsString.TrimEnd(' ')) is { } byLiteral
                    ? byLiteral.FileId
                    : throw SimulatedSqlException.ShrinkFileNotFound(text.AsString, database.Name, 1);
            case Name name:
                return database.FindFile(name.Value) is { } byName
                    ? byName.FileId
                    : throw SimulatedSqlException.ShrinkFileNotFound(name.Value, database.Name, 1);
            default:
                return 1;
        }
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
            case Literal { Value: { IsNull: false } text } when SqlType.IsStringCategory(text.Type):
                return simulation.Databases.TryGetValue(text.AsString, out var byLiteral)
                    ? byLiteral
                    : throw SimulatedSqlException.CouldNotFindDatabase(text.AsString);
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
        // A session id past smallint is no session id at all (probed
        // 2026-10-04 against SQL Server 2025).
        if (spid > short.MaxValue)
            throw SimulatedSqlException.DbccParameterIsIncorrect(1);
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
            completion = SimulatedSqlException.DbccExecutionCompletedMessage(batch);
        return true;

        static int DbccIntegerArgument(SqlValue value, int position) =>
            !value.IsNull && SqlType.IsIntegerCategory(value.Type) ? value.CoerceTo(SqlType.Int32).AsInt32 : throw SimulatedSqlException.DbccParameterIsIncorrect(position);
    }
}
