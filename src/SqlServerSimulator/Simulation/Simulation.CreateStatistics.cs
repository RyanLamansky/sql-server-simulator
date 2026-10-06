using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE STATISTICS name ON table (column [, …]) [WITH option [, …]]</c>.
    /// Entered with the cursor on the <c>STATISTICS</c> keyword.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The statistic is recorded on the table and surfaces through
    /// <c>sys.stats</c> / <c>sys.stats_columns</c> with
    /// <c>user_created = 1</c>; nothing about query execution reads it — see
    /// <see cref="UserStatistic"/> for why the declaration alone is the
    /// modeled part.
    /// </para>
    /// <para>
    /// Diagnostics follow real's, probe-confirmed against SQL Server 2025:
    /// <strong>Msg 1088</strong> for a missing table (shared with CREATE
    /// INDEX, at its own state 12), <strong>Msg 1911</strong> for a missing
    /// column, and <strong>Msg 1927</strong> for a name the table already
    /// carries — which includes an <em>index</em>'s name, since statistics and
    /// indexes share one per-table name space.
    /// </para>
    /// <para>
    /// Of the WITH options only <c>NORECOMPUTE</c> has an observable effect
    /// (<c>sys.stats.no_recompute</c>); the sampling family (<c>FULLSCAN</c>,
    /// <c>SAMPLE n {PERCENT | ROWS}</c>, <c>PERSIST_SAMPLE_PERCENT</c>,
    /// <c>INCREMENTAL</c>, <c>MAXDOP</c>, <c>AUTO_DROP</c>) describes how real
    /// would scan the data to build a histogram there isn't one of here, so
    /// those parse and discard.
    /// </para>
    /// </remarks>
    internal static bool TryParseCreateStatistics(ParserContext context)
    {
        if (context.GetNextRequired() is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var statisticsName = nameToken.Value;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var targetTableName = BatchContext.ParseObjectName(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var columnNames = new List<string>();
        do
        {
            if (context.GetNextRequired() is not Name column)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            columnNames.Add(column.Value);
            context.MoveNextRequired();
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var optionsWritten = context.Token is ReservedKeyword { Keyword: Keyword.With };
        var options = ParseStatisticsOptions(context);

        // The filter follows the options; one written ahead of them is a
        // keyword real stops at (probed 2026-09-30 against SQL Server 2025).
        BooleanExpression? filter = null;
        string? filterDefinition = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where } whereKeyword)
        {
            if (optionsWritten)
                throw SimulatedSqlException.SyntaxErrorNearKeyword(whereKeyword);
            context.MoveNextRequired();
            RejectFilterPredicateKeywords(context);
            using (ParserScope.Enter(ref context.InFilterPredicate, true))
                filter = BooleanExpression.Parse(context);
            CheckFilterPredicate(filter, statistics: true, statisticsName, targetTableName.ToString());
            filterDefinition = filter.RenderFilterDefinition(context.Batch);
            options = ParseStatisticsOptions(context).Or(options);
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(targetTableName, out var table))
        {
            // A view takes statistics only once schema bound (Msg 1939, probed
            // 2026-10-05 against SQL Server 2025).
            if (context.Batch.TryResolveView(targetTableName, out var view))
            {
                if (!view.IsSchemaBound)
                    throw SimulatedSqlException.StatisticsOnViewNotSchemaBound(view.Name);
                if (!view.Indexes.Exists(static index => index is { IsUnique: true, IsClustered: true }))
                    throw SimulatedSqlException.ViewWithoutUniqueClusteredIndex(targetTableName.ToString(), statistics: true);
                throw new NotSupportedException("Statistics on an indexed view aren't modeled.");
            }
            throw filter is not null
                ? SimulatedSqlException.InvalidObjectName(targetTableName, state: 101)
                : SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());
        }

        table.OwningDatabase?.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.CannotFindObjectForCreateIndex(targetTableName.ToString());
        // Nothing here is partitioned, and a statistics stream comes from a
        // statistic real built (probed 2026-10-05 against SQL Server 2025).
        if (options.Incremental && table.Partitioning is null)
            throw SimulatedSqlException.StatisticsCannotBeIncremental(state: 1);
        if (options.StatsStream)
            throw SimulatedSqlException.StatisticsStreamCorrupt();

        var collation = context.Batch.CurrentDatabase.Collation;
        foreach (var existing in table.UserStatistics)
        {
            if (collation.Equals(existing.Name, statisticsName))
                throw SimulatedSqlException.StatisticsAlreadyExist(table.Name, statisticsName);
        }
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.Name is { } indexName && collation.Equals(indexName, statisticsName))
                throw SimulatedSqlException.StatisticsAlreadyExist(table.Name, statisticsName);
        }

        var filterOrdinals = filter is null ? [] : BindFilterColumns(context.Batch, table, filter);
        if (filter is not null)
            RejectComputedColumnInIndexFilter(context.Batch, table, statisticsName, targetTableName.ToString(), filter, forStatistics: true);

        var ordinals = new int[columnNames.Count];
        for (var i = 0; i < columnNames.Count; i++)
        {
            var ordinal = -1;
            for (var c = 0; c < table.Columns.Length; c++)
            {
                if (collation.Equals(table.Columns[c].Name, columnNames[i]))
                {
                    ordinal = c;
                    break;
                }
            }
            if (ordinal < 0)
                throw SimulatedSqlException.IndexColumnMissing(columnNames[i]);
            if (Array.IndexOf(ordinals, ordinal, 0, i) >= 0)
                throw SimulatedSqlException.DuplicateStatisticsColumn(table.Columns[ordinal].Name);
            // An xml or spatial column takes no statistic (probed 2026-10-05).
            if (table.Columns[ordinal].Type is XmlSqlType)
                throw SimulatedSqlException.StatisticsOnXmlColumn(statisticsName, targetTableName.ToString(), table.Columns[ordinal].Name);
            if (table.Columns[ordinal].Type is SpatialSqlType)
                throw SimulatedSqlException.VectorKeyColumnInvalid(table.Columns[ordinal].Name, targetTableName.ToString(), state: 1);
            // Statistics take the same determinism / precision gate an index
            // key does — real's Msg 2729 / 2799 both name "index or statistics".
            if (table.Columns[ordinal].Type is VectorSqlType or JsonSqlType or ClrUdtSqlType { Udt.IsByteOrdered: false })
                throw SimulatedSqlException.VectorKeyColumnInvalid(table.Columns[ordinal].Name, targetTableName.ToString(), table.Columns[ordinal].Type switch { JsonSqlType => 3, VectorSqlType => 4, _ => 1 });
            RejectComputedKeyColumnNotIndexable(context.CurrentDatabase, table.Columns, targetTableName.ToString(), table.Columns[ordinal], statisticsName, viaConstraint: false);
            ordinals[i] = ordinal;
        }

        var created = new UserStatistic(
            statisticsName,
            NextStatisticsId(table),
            ordinals,
            options.NoRecompute,
            context.Batch.CurrentStatement.UtcNow,
            filter,
            filterDefinition,
            filterOrdinals);
        created.Statistics.HasPersistedSample = options.PersistSample;
        created.Statistics.AutoDrop = options.AutoDrop;
        table.UserStatistics.Add(created);
        RecordDdlUndo(context, () => _ = table.UserStatistics.Remove(created));
        table.NoteStatisticsCreated(statisticsName, context.CurrentDatabase.Collation);

        // Building the statistic evaluates a computed column over every row
        // (probed 2026-10-05 against SQL Server 2025). A row the expression
        // fails on fails the statement but leaves the statistic, never built
        // (probed 2026-10-06).
        EvaluateStatisticsKey(context.Batch, table, ordinals, filter);
        created.Statistics.Snapshot = BuildStatisticsSnapshot(context.Batch, table, ordinals, ordinals, filter);
        RecordDdlEvent(context, "CREATE_STATISTICS", EventSchemaName(targetTableName), statisticsName, "STATISTICS", table.Name, "TABLE");
        return true;
    }

    /// <summary>
    /// Parses <c>DROP STATISTICS table.name [, …]</c>. Entered with the cursor
    /// on the <c>STATISTICS</c> keyword. Each entry is a dotted name whose leaf
    /// is the statistic and whose remaining segments address the table, so the
    /// form is 2-part (<c>t.s</c>), 3-part (<c>dbo.t.s</c>) or 4-part with the
    /// database. <strong>Msg 3701</strong> names the whole written form when
    /// nothing matches.
    /// </summary>
    internal static bool TryParseDropStatistics(ParserContext context)
    {
        var pending = new List<MultiPartName>();
        do
        {
            context.MoveNextRequired();
            pending.Add(BatchContext.ParseObjectName(context));
            context.MoveNextOptional();
            // The CREATE-style `name ON table` isn't DROP STATISTICS' grammar
            // (Msg 1053, probed 2026-10-05 against SQL Server 2025).
            if (context.Token is ReservedKeyword { Keyword: Keyword.On })
                throw SimulatedSqlException.DropStatisticsNeedsObjectDotName();
        } while (context.Token is Operator { Character: ',' });

        if (context.Batch.IsSkipping)
            return true;

        foreach (var written in pending)
        {
            if (written.Count < 2)
                throw SimulatedSqlException.CannotDropStatistics(written.ToString());
            var tableName = QualifierOf(written);
            if (!context.Batch.TryResolveTable(tableName, out var table))
                throw SimulatedSqlException.CannotDropStatistics(written.ToString());

            table.OwningDatabase?.RejectWriteWhenReadOnly();
            if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
                throw SimulatedSqlException.CannotDropStatistics(written.ToString());

            var collation = context.Batch.CurrentDatabase.Collation;
            var index = table.UserStatistics.FindIndex(s => collation.Equals(s.Name, written.Leaf));
            // An index's statistic goes only with its index (Msg 3739, probed
            // 2026-10-05 against SQL Server 2025).
            if (index < 0 && table.IndexIdentities().Exists(identity => identity.Name is { } name && collation.Equals(name, written.Leaf)))
                throw SimulatedSqlException.CannotDropIndexStatistics(written.ToString());
            if (index < 0)
                throw SimulatedSqlException.CannotDropStatistics(written.ToString());
            var dropped = table.UserStatistics[index];
            table.UserStatistics.RemoveAt(index);
            RecordDdlUndo(context, () => table.UserStatistics.Insert(Math.Min(index, table.UserStatistics.Count), dropped));
            RecordDdlEvent(context, "DROP_STATISTICS", EventSchemaName(tableName), written.Leaf, "STATISTICS", table.Name, "TABLE");
        }
        return true;
    }

    /// <summary>
    /// The written name minus its leaf — for <c>DROP STATISTICS</c>, where the
    /// leaf is the statistic and everything before it addresses the table.
    /// </summary>
    private static MultiPartName QualifierOf(MultiPartName written)
    {
        var qualifier = new MultiPartName(written[0]);
        for (var i = 1; i < written.Count - 1; i++)
            qualifier = qualifier.WithAddedPart(written[i]);
        return qualifier;
    }

    /// <summary>
    /// Evaluates the non-persisted computed columns of a statistic's key over
    /// the rows its filter admits, as building it does — so a row the
    /// expression fails on raises (Msg 8115 for an overflow) and ends only the
    /// statement, the batch and transaction going on (probed 2026-10-06
    /// against SQL Server 2025 for <c>CREATE</c> and <c>UPDATE STATISTICS</c>).
    /// </summary>
    private static void EvaluateStatisticsKey(BatchContext batch, HeapTable table, int[] ordinals, BooleanExpression? filter)
    {
        List<int> computed = [.. ordinals.Where(ordinal => table.Columns[ordinal] is { Computed: not null, IsPersisted: false })];
        if (computed.Count == 0)
            return;
        try
        {
            EvaluateComputedColumnsOverRows(table, computed, batch, endsColumnRewrite: false, filter);
        }
        catch (SimulatedSqlException failure)
        {
            failure.RaisedBuildingStatistics = true;
            throw;
        }
    }

    /// <summary>
    /// The next free per-table stats id: the lowest one no index or statistic
    /// holds, from 2 (id 1 is the clustered index's, a heap's included) — one
    /// pool, so an index dropped earlier leaves the gap a statistic fills.
    /// </summary>
    private static int NextStatisticsId(HeapTable table) => table.NextFreeIndexId();

    /// <summary>What a <c>CREATE STATISTICS</c> option list wrote.</summary>
    private readonly struct CreateStatisticsOptions(bool noRecompute, bool persistSample, bool autoDrop, bool incremental, bool statsStream)
    {
        public readonly bool NoRecompute = noRecompute;
        public readonly bool PersistSample = persistSample;
        public readonly bool AutoDrop = autoDrop;
        public readonly bool Incremental = incremental;
        public readonly bool StatsStream = statsStream;

        public CreateStatisticsOptions Or(CreateStatisticsOptions other) =>
            new(this.NoRecompute || other.NoRecompute, this.PersistSample || other.PersistSample, this.AutoDrop || other.AutoDrop,
                this.Incremental || other.Incremental, this.StatsStream || other.StatsStream);
    }

    /// <summary>The sampling options in the order real names a conflicting pair (probed 2026-10-05).</summary>
    private static readonly string[] CreateStatisticsSamplingOrder = ["STATS_STREAM", "ROWS", "PERCENT", "FULLSCAN"];

    /// <summary>
    /// Consumes the optional <c>WITH</c> option list of <c>CREATE
    /// STATISTICS</c>, raising real's option errors as it parses (probed
    /// 2026-10-05 against SQL Server 2025): an unknown name — or a value-less
    /// option given one — Msg 155, a repeated one Msg 1039, two sampling
    /// choices Msg 1052, a percent past 100 Msg 1031, a non-integer sample
    /// Msg 102, <c>PERSIST_SAMPLE_PERCENT = ON</c> without a sampling choice
    /// Msg 153 and a <c>MAXDOP</c> past 32767 Msg 304.
    /// </summary>
    private static CreateStatisticsOptions ParseStatisticsOptions(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return default;

        bool noRecompute = false, persistSample = false, autoDrop = false, incremental = false, statsStream = false;
        var seen = new List<string>();
        string? sampling = null;
        // An option name is an identifier, at most 128 characters.
        Span<char> buffer = stackalloc char[128];
        do
        {
            var token = context.GetNextRequired();
            var source = token.Source;
            var written = token is ReservedKeyword ? source.ToString().ToUpperInvariant() : source.ToString();
            var upper = buffer[..Math.Min(source.Length, buffer.Length)];
            _ = source[..upper.Length].ToUpperInvariant(upper);
            var valued = NextIsEquals(context);
            string recorded;
            switch (upper)
            {
                case "AUTO_DROP" when valued:
                    autoDrop = ParseOnOffValue(context);
                    recorded = "AUTO_DROP";
                    break;
                case "FULLSCAN" when !valued:
                    sampling = ConflictingCreateStatisticsOption(sampling, "FULLSCAN");
                    recorded = "FULLSCAN";
                    break;
                case "INCREMENTAL" when valued:
                    incremental = ParseOnOffValue(context);
                    recorded = "INCREMENTAL";
                    break;
                case "MAXDOP" when valued:
                    context.MoveNextRequired();
                    context.MoveNextRequired();
                    if (ReadIntegerOptionLiteral(context) > 32767)
                        throw SimulatedSqlException.IndexMaxDopOutOfRange(context.Token.Source.ToString());
                    recorded = "MAXDOP";
                    break;
                case "NORECOMPUTE" when !valued:
                    noRecompute = true;
                    recorded = "NORECOMPUTE";
                    break;
                case "PERSIST_SAMPLE_PERCENT" when valued:
                    persistSample = ParseOnOffValue(context);
                    recorded = "PERSIST_SAMPLE_PERCENT";
                    break;
                case "RESAMPLE":
                    throw SimulatedSqlException.SyntaxErrorNearText("RESAMPLE");
                case "SAMPLE" when !valued:
                    if (context.GetNextRequired() is not Numeric { Value: var amount } || amount.Type != SqlType.Int32)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    var unit = context.GetNextRequired() switch
                    {
                        ReservedKeyword { Keyword: Keyword.Percent } => "PERCENT",
                        UnquotedString { ContextualKeyword: ContextualKeyword.Rows } => "ROWS",
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                    if (unit == "PERCENT" && amount.AsInt32 > 100)
                        throw SimulatedSqlException.TopPercentOutOfRange();
                    sampling = ConflictingCreateStatisticsOption(sampling, unit);
                    recorded = "SAMPLE";
                    break;
                case "STATS_STREAM" when valued:
                    context.MoveNextRequired();
                    context.MoveNextRequired();
                    sampling = ConflictingCreateStatisticsOption(sampling, "STATS_STREAM");
                    statsStream = true;
                    recorded = "STATS_STREAM";
                    break;
                default:
                    throw SimulatedSqlException.UnrecognizedCreateStatisticsOption(written);
            }
            RecordOption(seen, recorded);
            context.MoveNextOptional();
        } while (context.Token is Operator { Character: ',' });

        if (persistSample && sampling is null)
            throw SimulatedSqlException.InvalidUsageOfIndexOption("PERSIST_SAMPLE_PERCENT", "CREATE STATISTICS");
        return new CreateStatisticsOptions(noRecompute, persistSample, autoDrop, incremental, statsStream);
    }

    /// <summary>Whether the token after the cursor is <c>=</c>, leaving the cursor where it is.</summary>
    private static bool NextIsEquals(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var equals = context.MoveNext() && context.Token is Operator { Character: '=' };
        context.RestoreCheckpoint(checkpoint);
        return equals;
    }

    /// <summary>Takes <paramref name="option"/> as the sampling choice, or raises Msg 1052 naming the two in real's order.</summary>
    private static string ConflictingCreateStatisticsOption(string? chosen, string option)
    {
        if (chosen is null || chosen == option)
            return option;
        return Array.IndexOf(CreateStatisticsSamplingOrder, chosen) < Array.IndexOf(CreateStatisticsSamplingOrder, option)
            ? throw SimulatedSqlException.ConflictingCreateStatisticsOptions(chosen, option)
            : throw SimulatedSqlException.ConflictingCreateStatisticsOptions(option, chosen);
    }
}
