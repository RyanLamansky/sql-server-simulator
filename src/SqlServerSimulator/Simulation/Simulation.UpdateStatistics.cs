using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>UPDATE STATISTICS table [ name | ( name [, …] ) ] [WITH option [, …]]</c>.
    /// Entered with the cursor on the <c>STATISTICS</c> keyword.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no histogram to rebuild, so the statement is its validation plus
    /// one catalog effect: each user-created statistic it reaches takes
    /// <c>sys.stats.no_recompute</c> from whether <c>NORECOMPUTE</c> was written,
    /// which also clears a flag an earlier update set.
    /// </para>
    /// <para>
    /// Real's checks and their order, probed 2026-09-25 against SQL Server 2025:
    /// the option list is settled while parsing — an unknown option is Msg 155,
    /// a repeated one Msg 1039, two of <c>SAMPLE</c> / <c>RESAMPLE</c> /
    /// <c>FULLSCAN</c> or of <c>ALL</c> / <c>COLUMNS</c> / <c>INDEX</c> Msg 1052,
    /// and a percent past 100 Msg 1031 — then a missing table (or a view with no
    /// index) is Msg 2706, a read-only database Msg 3906 at state 13, a caller
    /// without <c>ALTER</c> Msg 1088, and a name that is neither an index nor a
    /// statistic on the table Msg 2727.
    /// </para>
    /// </remarks>
    internal static bool TryParseUpdateStatistics(ParserContext context)
    {
        context.MoveNextRequired();
        var tableName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();

        List<string>? targets = null;
        if (context.Token is Operator { Character: '(' })
        {
            targets = [];
            do
            {
                if (context.GetNextRequired() is not Name target)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                targets.Add(target.Value);
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        else if (context.Token is Name single)
        {
            targets = [single.Value];
            context.MoveNextOptional();
        }

        var options = context.Token is ReservedKeyword { Keyword: Keyword.With }
            ? ParseUpdateStatisticsOptions(context)
            : default;
        var (noRecompute, onPartitions, incremental) = (options.NoRecompute, options.OnPartitions, options.Incremental);
        // A statistics stream loads one named statistic (Msg 1092, probed
        // 2026-10-05 against SQL Server 2025).
        if (options.StatsStream && (targets?.Count ?? 0) != 1)
            throw SimulatedSqlException.StatsStreamNeedsOneStatistic(targets?.Count ?? 0);

        if (context.Batch.IsSkipping)
            return true;

        List<IndexIdentity> indexes;
        HeapTable? table = null;
        if (context.Batch.TryResolveTable(tableName, out var resolved))
        {
            table = resolved;
            indexes = table.IndexIdentities();
            table.OwningDatabase?.RejectWriteWhenReadOnly(state: 13);
            if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
                throw SimulatedSqlException.CannotFindObjectForCreateIndex(tableName.ToString());
        }
        else if (context.Batch.TryResolveView(tableName, out var view))
        {
            indexes = view.IndexIdentities();
            if (indexes.Count == 0)
                throw SimulatedSqlException.TableDoesNotExist(tableName.Leaf, state: 7);
            context.CurrentDatabase.RejectWriteWhenReadOnly(state: 13);
        }
        else
        {
            throw SimulatedSqlException.TableDoesNotExist(tableName.Leaf, state: 6);
        }

        // Nothing here is partitioned, so no statistic is or can be incremental.
        if (incremental)
            throw SimulatedSqlException.StatisticsCannotBeIncremental();
        if (onPartitions)
            throw SimulatedSqlException.OnPartitionsNeedsIncrementalStatistics();

        var collation = context.CurrentDatabase.Collation;
        foreach (var target in targets ?? [])
        {
            if (!indexes.Exists(index => index.Name is { } name && collation.Equals(name, target))
                && table?.UserStatistics.Exists(statistic => collation.Equals(statistic.Name, target)) != true)
            {
                throw SimulatedSqlException.CannotFindIndex(target);
            }
        }

        if (table is not null)
        {
            foreach (var statistic in table.UserStatistics)
            {
                if (targets is null || targets.Exists(target => collation.Equals(statistic.Name, target)))
                    statistic.NoRecompute = noRecompute;
            }
        }
        // The statistics an index backs take it too (probed 2026-09-30 against
        // SQL Server 2025), and the update is what a table's stale-statistics
        // check reads (see HeapTable.StatisticsFreshness).
        foreach (var identity in indexes)
        {
            if (identity.IsHeap || identity.Name is not { } indexName
                || (targets is not null && !targets.Exists(target => collation.Equals(indexName, target))))
            {
                continue;
            }
            if (identity.Constraint is { } constraint)
                constraint.StatisticsNoRecompute = noRecompute;
            else if (identity.Index is { } index)
                index.StatisticsNoRecompute = noRecompute;
        }
        table?.MarkStatisticsFresh(targets, collation);
        // The update rebuilds what it reached — standalone statistics under
        // COLUMNS, index statistics under INDEX, both otherwise — and records
        // the sampling and drop options it ran under on each.
        if (table is not null)
        {
            foreach (var statistic in StatisticsOn(table))
            {
                if ((targets is not null && !targets.Exists(target => collation.Equals(statistic.Name, target)))
                    || (options.Scope == "COLUMNS" && statistic.User is null)
                    || (options.Scope == "INDEX" && statistic.User is not null))
                {
                    continue;
                }
                BuildStatistics(context.Batch, table, statistic);
                if (options.PersistSample is { } persist)
                    statistic.State.HasPersistedSample = persist;
                if (options.AutoDrop is { } autoDrop)
                    statistic.State.AutoDrop = autoDrop;
            }
        }
        // The event names the table as its target and no statistic of its
        // own (probed 2026-10-04 against SQL Server 2025).
        RecordDdlEvent(context, "UPDATE_STATISTICS", EventSchemaName(tableName), null, "STATISTICS", tableName.Leaf, table is null ? "VIEW" : "TABLE");
        return true;
    }

    /// <summary>What an <c>UPDATE STATISTICS</c> option list wrote; the nullable options are null when unwritten.</summary>
    private readonly struct UpdateStatisticsOptions(bool noRecompute, bool onPartitions, bool incremental, bool? persistSample, bool? autoDrop, string? scope, bool statsStream)
    {
        public readonly bool NoRecompute = noRecompute;
        public readonly bool OnPartitions = onPartitions;
        public readonly bool Incremental = incremental;
        public readonly bool? PersistSample = persistSample;
        public readonly bool? AutoDrop = autoDrop;

        /// <summary><c>ALL</c>, <c>COLUMNS</c> or <c>INDEX</c>, or null.</summary>
        public readonly string? Scope = scope;

        public readonly bool StatsStream = statsStream;
    }

    /// <summary>
    /// Parses the <c>WITH</c> list of <c>UPDATE STATISTICS</c>, raising real's
    /// option errors, and answers whether <c>NORECOMPUTE</c>,
    /// <c>RESAMPLE ON PARTITIONS</c> and <c>INCREMENTAL = ON</c> were among them.
    /// Entered with the cursor on <c>WITH</c>.
    /// </summary>
    private static UpdateStatisticsOptions ParseUpdateStatisticsOptions(ParserContext context)
    {
        bool? persistSample = null, autoDrop = null;
        var statsStream = false;
        var noRecompute = false;
        var onPartitions = false;
        var incremental = false;
        var seen = new List<string>();
        string? sampling = null;
        string? scope = null;
        do
        {
            var token = context.GetNextRequired();
            string option;
            switch (token)
            {
                case ReservedKeyword { Keyword: Keyword.All }:
                    option = "ALL";
                    scope = ConflictingOption(scope, option, ScopeOrder);
                    break;
                case ReservedKeyword { Keyword: Keyword.Index }:
                    option = "INDEX";
                    scope = ConflictingOption(scope, option, ScopeOrder);
                    break;
                case Name name when BuiltInToken.Equals(name.Value, "COLUMNS"):
                    option = "COLUMNS";
                    scope = ConflictingOption(scope, option, ScopeOrder);
                    break;
                case Name name when BuiltInToken.Equals(name.Value, "FULLSCAN"):
                    option = "FULLSCAN";
                    sampling = ConflictingOption(sampling, option, SamplingOrder);
                    break;
                case Name name when BuiltInToken.Equals(name.Value, "RESAMPLE"):
                    sampling = ConflictingOption(sampling, "RESAMPLE", SamplingOrder);
                    RecordOption(seen, "RESAMPLE");
                    context.MoveNextOptional();
                    if (context.Token is ReservedKeyword { Keyword: Keyword.On })
                    {
                        // ON PARTITIONS (n [TO m] [, …]) narrows an incremental
                        // statistic's refresh; nothing here is partitioned.
                        if (context.GetNextRequired() is not Name partitions || !BuiltInToken.Equals(partitions.Value, "PARTITIONS")
                            || context.GetNextRequired() is not Operator { Character: '(' })
                        {
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        }
                        while (context.GetNextRequired() is not Operator { Character: ')' })
                        {
                        }
                        onPartitions = true;
                        context.MoveNextOptional();
                    }
                    continue;
                case Name name when BuiltInToken.Equals(name.Value, "SAMPLE"):
                    option = "SAMPLE";
                    if (context.GetNextRequired() is not Numeric amount)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    var unit = context.GetNextRequired() switch
                    {
                        ReservedKeyword { Keyword: Keyword.Percent } => "PERCENT",
                        UnquotedString { ContextualKeyword: ContextualKeyword.Rows } => "ROWS",
                        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                    };
                    if (unit == "PERCENT" && amount.Value.CoerceTo(SqlType.Float).AsDouble > 100)
                        throw SimulatedSqlException.TopPercentOutOfRange();
                    sampling = ConflictingOption(sampling, unit, SamplingOrder);
                    break;
                case Name name when BuiltInToken.Equals(name.Value, "NORECOMPUTE"):
                    option = "NORECOMPUTE";
                    noRecompute = true;
                    break;
                case Name name when BuiltInToken.Equals(name.Value, "MAXDOP"):
                    option = "MAXDOP";
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextRequired() is not Numeric)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Name name when BuiltInToken.Equals(name.Value, "INCREMENTAL"):
                    option = "INCREMENTAL";
                    incremental = ParseOnOffValue(context);
                    break;
                case Name name when BuiltInToken.Equals(name.Value, "PERSIST_SAMPLE_PERCENT"):
                    option = "PERSIST_SAMPLE_PERCENT";
                    persistSample = ParseOnOffValue(context);
                    break;
                case Name name when BuiltInToken.Equals(name.Value, "AUTO_DROP"):
                    option = "AUTO_DROP";
                    autoDrop = ParseOnOffValue(context);
                    break;
                // The undocumented page and row count overrides parse and are
                // discarded (probed 2026-10-05 against SQL Server 2025).
                case ReservedKeyword { Keyword: Keyword.RowCount }:
                case Name name when BuiltInToken.Equals(name.Value, "PAGECOUNT"):
                    option = token.Source.ToString().ToUpperInvariant();
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    if (context.GetNextRequired() is not Numeric)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Name name when BuiltInToken.Equals(name.Value, "STATS_STREAM"):
                    option = "STATS_STREAM";
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                    statsStream = true;
                    break;
                case Name name:
                    throw SimulatedSqlException.UnrecognizedUpdateStatisticsOption(name.Value);
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            RecordOption(seen, option);
            context.MoveNextOptional();
        } while (context.Token is Operator { Character: ',' });
        // Persisting a sample needs one to persist (Msg 153, probed 2026-10-05).
        if (persistSample == true && sampling is null)
            throw SimulatedSqlException.InvalidUsageOfIndexOption("PERSIST_SAMPLE_PERCENT", "UPDATE STATISTICS");
        return new UpdateStatisticsOptions(noRecompute, onPartitions, incremental, persistSample, autoDrop, scope, statsStream);
    }

    /// <summary>The sampling options in the order real names a conflicting pair.</summary>
    private static readonly string[] SamplingOrder = ["PERCENT", "ROWS", "RESAMPLE", "FULLSCAN"];

    /// <summary>The scope options in the order real names a conflicting pair.</summary>
    private static readonly string[] ScopeOrder = ["ALL", "INDEX", "COLUMNS"];

    /// <summary>
    /// Takes <paramref name="option"/> as its group's choice, or raises Msg 1052
    /// naming the group's two choices in real's fixed order (probed 2026-09-25:
    /// <c>FULLSCAN, SAMPLE 10 PERCENT</c> reads "PERCENT" and "FULLSCAN").
    /// </summary>
    private static string ConflictingOption(string? chosen, string option, string[] order)
    {
        if (chosen is null || chosen == option)
            return option;
        return Array.IndexOf(order, chosen) < Array.IndexOf(order, option)
            ? throw SimulatedSqlException.ConflictingUpdateStatisticsOptions(chosen, option)
            : throw SimulatedSqlException.ConflictingUpdateStatisticsOptions(option, chosen);
    }

    /// <summary>Consumes an option's <c>= ON</c> / <c>= OFF</c>, leaving the cursor on the value, and answers whether it was ON.</summary>
    private static bool ParseOnOffValue(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return context.GetNextRequired() is ReservedKeyword { Keyword: var value and (Keyword.On or Keyword.Off) }
            ? value == Keyword.On
            : throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    private static void RecordOption(List<string> seen, string option)
    {
        if (seen.Contains(option))
            throw SimulatedSqlException.OptionSpecifiedMoreThanOnce(option);
        seen.Add(option);
    }
}
