using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_updatestats / sp_autostats / sp_createstats. Real walks its own internal
// tables too (sys.plan_persist_*, the agent's job tables, the ledger's) and
// reports each; the simulator carries none of them, so the reports cover user
// tables only.
partial class Simulation
{
    private static readonly SystemProcedureParameter[] UpdateStatsParameters =
    [
        new("resample", SqlType.NVarchar, 8, SqlValue.FromNVarchar("NO")),
    ];

    private static readonly SystemProcedureParameter[] AutoStatsParameters =
    [
        new("tblname", SqlType.NVarchar, 776),
        new("flagc", SqlType.NVarchar, 10, SqlValue.Null(SqlType.NVarchar)),
        new("indname", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] CreateStatsParameters =
    [
        new("indexonly", SqlType.NVarchar, 9, SqlValue.FromNVarchar("NO")),
        new("fullscan", SqlType.NVarchar, 9, SqlValue.FromNVarchar("NO")),
        new("norecompute", SqlType.NVarchar, 12, SqlValue.FromNVarchar("NO")),
        new("incremental", SqlType.NVarchar, 12, SqlValue.FromNVarchar("NO")),
    ];

    /// <summary>
    /// A statistic a report lists: an index's, or a <c>CREATE STATISTICS</c>
    /// one, in <c>stats_id</c> order.
    /// </summary>
    private readonly struct ReportedStatistic(string name, int leadingOrdinal, Action<bool> setNoRecompute, bool noRecompute)
    {
        public readonly string Name = name;
        public readonly int LeadingOrdinal = leadingOrdinal;
        public readonly bool NoRecompute = noRecompute;
        public readonly Action<bool> SetNoRecompute = setNoRecompute;
    }

    private static List<ReportedStatistic> StatisticsOf(List<IndexIdentity> identities, List<UserStatistic>? userStatistics)
    {
        var list = new List<ReportedStatistic>();
        foreach (var identity in identities)
        {
            if (identity.IsHeap || identity.Name is not { } name || identity.Index is { IsColumnstore: true })
                continue;
            if (identity.Constraint is { } constraint)
                list.Add(new(name, constraint.FullOrdinals.Length > 0 ? constraint.FullOrdinals[0] : -1, value => constraint.StatisticsNoRecompute = value, constraint.StatisticsNoRecompute));
            else if (identity.Index is { } index)
                list.Add(new(name, index.KeyFullOrdinals.Length > 0 ? index.KeyFullOrdinals[0] : -1, value => index.StatisticsNoRecompute = value, index.StatisticsNoRecompute));
        }
        if (userStatistics is not null)
        {
            foreach (var statistic in userStatistics)
                list.Add(new(statistic.Name, statistic.ColumnFullOrdinals.Length > 0 ? statistic.ColumnFullOrdinals[0] : -1, value => statistic.NoRecompute = value, statistic.NoRecompute));
        }
        return list;
    }

    /// <summary>The user tables of the current database in the order real walks them: by object id.</summary>
    private static List<(HeapTable Table, string SchemaName)> UserTablesByObjectId(BatchContext batch)
    {
        var tables = new List<(HeapTable, string)>();
        foreach (var (_, schema) in batch.CurrentDatabase.Schemas)
        {
            foreach (var table in schema.HeapTables.EnumerateValues())
                tables.Add((table, schema.Name));
        }
        tables.Sort(static (a, b) => a.Item1.ObjectId.CompareTo(b.Item1.ObjectId));
        return tables;
    }

    private static SimulatedInfoOutcome ProcedureMessage(BatchContext batch, string calledAs, int line, int number, string text) =>
        new(SimulatedSqlException.SystemProcedureMessage(batch, calledAs, line, number, text));

    /// <summary>
    /// <c>sp_updatestats [@resample]</c> updates every statistic of every user
    /// table that has been written since it was last current, and says which.
    /// A statistic is current when built, and again after <c>UPDATE
    /// STATISTICS</c> or an earlier run; real counts modifications per column,
    /// so an <c>UPDATE</c> of a column no statistic leads with leaves them
    /// current there where the simulator, counting per table, doesn't.
    /// <c>@resample</c> makes no difference: there is no histogram to sample.
    /// Only a sysadmin runs it (probed 2026-09-30 against SQL Server 2025; a
    /// <c>db_owner</c> member is refused).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpUpdateStats(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_updatestats", calledAs, arguments, UpdateStatsParameters);
        var security = batch.Connection.Security;
        if (!(security.EffectiveIsDbo || batch.Connection.Simulation.Logins.IsEmptyLockFree() || batch.Connection.Simulation.IsLoginSysadmin(security.Effective.LoginName)))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserDoesNotHavePermission(), 15);
        var option = values[0].IsNull ? "NO" : values[0].AsString.TrimEnd(' ');
        if (!option.Equals("NO", StringComparison.OrdinalIgnoreCase) && !option.Equals("RESAMPLE", StringComparison.OrdinalIgnoreCase))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidUpdateStatsOption(option.PadRight(8)), 27);

        var collation = batch.CurrentDatabase.Collation;
        foreach (var (table, schemaName) in UserTablesByObjectId(batch))
        {
            yield return ProcedureMessage(batch, calledAs, 94, 15650, $"Updating {QuoteIdentifier(schemaName)}.{QuoteIdentifier(table.Name)}");
            var statistics = StatisticsOf(table.IndexIdentities(), table.UserStatistics);
            int updated = 0, current = 0;
            foreach (var statistic in statistics)
            {
                if (table.IsStatisticStale(statistic.Name, statistic.LeadingOrdinal))
                {
                    updated++;
                    table.MarkStatisticsFresh([statistic.Name], collation);
                    yield return ProcedureMessage(batch, calledAs, 173, 15652, $"    {QuoteIdentifier(statistic.Name)} has been updated...");
                }
                else
                {
                    current++;
                    yield return ProcedureMessage(batch, calledAs, 179, 15653, $"    {QuoteIdentifier(statistic.Name)}, update is not necessary...");
                }
            }
            yield return ProcedureMessage(batch, calledAs, statistics.Count == 0 ? 128 : 185, 15651,
                $"    {updated} index(es)/statistic(s) have been updated, {current} did not require update.");
            yield return new SimulatedInfoOutcome(SimulatedSqlException.HelpBlankLineMessage(batch, calledAs, 190));
        }
        yield return ProcedureMessage(batch, calledAs, 193, 15005, "Statistics for all tables have been updated.");
    }

    /// <summary>
    /// <c>sp_autostats @tblname [, @flagc [, @indname]]</c> reports a table's or
    /// indexed view's automatic-statistics setting per statistic, or sets it for
    /// all of them or the one named: OFF is <c>sys.stats.no_recompute</c> = 1.
    /// The report has no <c>Last Updated</c> date, since nothing here builds one.
    /// A <c>@flagc</c> that is neither ON nor OFF answers the usage message.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpAutoStats(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_autostats";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, AutoStatsParameters);
        bool? turnOn = null;
        if (!values[1].IsNull)
        {
            var flag = values[1].AsString.TrimEnd(' ');
            if (flag.Equals("ON", StringComparison.OrdinalIgnoreCase))
            {
                turnOn = true;
            }
            else if (flag.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                turnOn = false;
            }
            else
            {
                yield return ProcedureMessage(batch, calledAs, 19, 17000, "Usage: sp_autostats <table_name> [, {ON|OFF} [, <index_name>] ]");
                yield break;
            }
        }

        var written = values[0].IsNull ? "(null)" : values[0].AsString;
        var parts = values[0].IsNull ? null : SplitDottedName(written);
        var database = batch.CurrentDatabase;
        if (parts is { Length: >= 3 } && !database.Collation.Equals(parts[0], database.Name))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.QualifiedNameMustBeCurrentDatabase(), 32, 15387);
        var found = parts is null || parts.Length > 3 ? null : FindUserTable(batch, parts);
        View? view = null;
        if (found is null && parts is { Length: <= 3 })
        {
            var schemaName = parts.Length == 1 ? Database.DefaultSchemaName : parts[^2];
            if (database.Schemas.TryGetValue(schemaName, out var schema) && schema.Views.TryGetValue(parts[^1], out var candidate) && candidate.Indexes.Count > 0)
                view = candidate;
        }
        if (found is null && view is null)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NoTableOrIndexedViewMatching(written), 46, 15390);
        if (found is { } visible && !CanSeeTableMetadata(batch, visible.Table))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NoTableOrIndexedViewMatching(written), 46, 15390);

        var statistics = found is { } table
            ? StatisticsOf(table.Table.IndexIdentities(), table.Table.UserStatistics)
            : StatisticsOf(view!.IndexIdentities(), null);
        var indexName = values[2].IsNull ? null : values[2].AsString;
        if (indexName is not null)
        {
            statistics = statistics.FindAll(statistic => database.Collation.Equals(statistic.Name, indexName));
            if (statistics.Count == 0 && turnOn is not null)
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.SelectedIndexDoesNotExist(written), 119, 15323);
        }

        if (turnOn is not null)
        {
            foreach (var statistic in statistics)
                statistic.SetNoRecompute(!turnOn.Value);
            yield break;
        }

        var rows = new List<SqlValue[]>(statistics.Count);
        foreach (var statistic in statistics)
        {
            rows.Add([
                SqlValue.FromNVarchar(QuoteIdentifier(statistic.Name)),
                SqlValue.FromVarchar(statistic.NoRecompute ? "OFF" : "ON"),
                SqlValue.Null(SqlType.DateTime),
            ]);
        }
        var autoUpdate = database.Switches.HasFlag(DatabaseSwitches.AutoUpdateStatistics) ? "ON" : "OFF";
        var autoCreate = database.Switches.HasFlag(DatabaseSwitches.AutoCreateStatistics) ? "ON" : "OFF";
        yield return ProcedureMessage(batch, calledAs, 55, 0, $"Global statistics settings for [{database.Name}]:");
        yield return ProcedureMessage(batch, calledAs, 56, 0, $"  Automatic update statistics: {autoUpdate}");
        yield return ProcedureMessage(batch, calledAs, 57, 0, $"  Automatic create statistics: {autoCreate}");
        yield return new SimulatedInfoOutcome(SimulatedSqlException.HelpBlankLineMessage(batch, calledAs, 58));
        yield return ProcedureMessage(batch, calledAs, 62, 0, $"settings for table [{written}]");
        yield return new SimulatedInfoOutcome(SimulatedSqlException.HelpBlankLineMessage(batch, calledAs, 63));
        yield return new SimulatedSqlResultSet(AutoStatsSchema, AutoStatsColumnNames, rows);
    }

    private static readonly SqlType[] AutoStatsSchema = [SqlType.NVarchar, VarcharSqlType.Get(3, Collation.Baseline, Coercibility.CoercibleDefault), SqlType.DateTime];
    private static readonly string[] AutoStatsColumnNames = ["Index Name", "AUTOSTATS", "Last Updated"];

    /// <summary>
    /// Whether the session's principal can see <paramref name="table"/> in the
    /// catalog — the visibility real's name lookup honors, where a table it can
    /// see nothing of is reported as no table at all.
    /// </summary>
    private static bool CanSeeTableMetadata(BatchContext batch, HeapTable table)
    {
        var principalId = PermissionEnforcement.MetadataVisibilityPrincipal(batch, batch.CurrentDatabase);
        if (principalId is null)
            return true;
        var closure = PermissionChecker.BuildPrincipalClosure(batch.CurrentDatabase, principalId.Value);
        return PermissionChecker.CanViewMetadata(batch.CurrentDatabase, closure, table.ObjectId, table.SchemaId, ServerLoginRights.For(batch.Connection));
    }

    /// <summary>
    /// <c>sp_createstats [@indexonly [, @fullscan [, @norecompute [, @incremental]]]]</c>
    /// creates a single-column statistic on every column of every user table
    /// that no statistic leads with — only the columns an index carries, with
    /// <c>'indexonly'</c> — by <c>CREATE STATISTICS</c>, named for the column.
    /// <c>xml</c>, <c>geography</c>, <c>geometry</c> and <c>hierarchyid</c>
    /// columns are passed over; any other a statistic can't take (<c>json</c>,
    /// <c>vector</c>) fails the run at its <c>CREATE STATISTICS</c> as real's
    /// does. Each option is its own keyword or NO.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpCreateStats(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_createstats";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, CreateStatsParameters);
        string[] keywords = ["INDEXONLY", "FULLSCAN", "NORECOMPUTE", "INCREMENTAL"];
        var chosen = new bool[keywords.Length];
        for (var i = 0; i < keywords.Length; i++)
        {
            var text = values[i].IsNull ? "NO" : values[i].AsString.TrimEnd(' ').ToUpperInvariant();
            if (text != "NO" && text != keywords[i])
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.InvalidSystemProcedureOption(procedure), 37, 15600);
            chosen[i] = text != "NO";
        }
        var options = new List<string>();
        if (chosen[1])
            options.Add("fullscan");
        if (chosen[2])
            options.Add("norecompute");
        if (chosen[3])
            options.Add("incremental = on");
        var withClause = options.Count == 0 ? "" : " with " + string.Join(", ", options);

        var databaseName = batch.CurrentDatabase.Name;
        var collation = batch.CurrentDatabase.Collation;
        var created = 0;
        foreach (var (table, schemaName) in UserTablesByObjectId(batch))
        {
            var leading = new HashSet<int>();
            var indexed = new HashSet<int>();
            foreach (var constraint in table.KeyConstraints)
            {
                if (constraint.FullOrdinals.Length > 0)
                    _ = leading.Add(constraint.FullOrdinals[0]);
                foreach (var ordinal in constraint.FullOrdinals)
                    _ = indexed.Add(ordinal);
            }
            foreach (var index in table.Indexes)
            {
                if (index.IsColumnstore || index.KeyFullOrdinals.Length == 0)
                    continue;
                _ = leading.Add(index.KeyFullOrdinals[0]);
                foreach (var ordinal in index.KeyFullOrdinals)
                    _ = indexed.Add(ordinal);
            }
            foreach (var statistic in table.UserStatistics)
            {
                if (statistic.ColumnFullOrdinals.Length > 0)
                    _ = leading.Add(statistic.ColumnFullOrdinals[0]);
            }

            var eligible = new List<int>();
            for (var ordinal = 0; ordinal < table.Columns.Length; ordinal++)
            {
                var column = table.Columns[ordinal];
                if (column.IsHidden || leading.Contains(ordinal) || column.Type is XmlSqlType or GeographySqlType or GeometrySqlType or HierarchyIdSqlType)
                    continue;
                if (chosen[0] && !indexed.Contains(ordinal))
                    continue;
                eligible.Add(ordinal);
            }

            var qualified = $"{databaseName}.{schemaName}.{table.Name}";
            if (eligible.Count == 0)
            {
                yield return ProcedureMessage(batch, calledAs, 187, 15013, $"Table '{qualified}': No columns without statistics found.");
                continue;
            }
            yield return ProcedureMessage(batch, calledAs, 192, 15018, $"Table '{qualified}': Creating statistics for the following columns:");
            foreach (var ordinal in eligible)
            {
                var name = table.Columns[ordinal].Name;
                var statement = new StringBuilder("create statistics ").Append(QuoteIdentifier(name)).Append(" on ")
                    .Append(QuoteIdentifier(schemaName)).Append('.').Append(QuoteIdentifier(table.Name)).Append('(').Append(QuoteIdentifier(name)).Append(')')
                    .Append(withClause).ToString();
                foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
                    yield return outcome;
                created++;
                yield return ProcedureMessage(batch, calledAs, 225, 0, "     " + name);
            }
        }
        _ = collation;
        yield return new SimulatedInfoOutcome(SimulatedSqlException.HelpBlankLineMessage(batch, calledAs, 254));
        yield return ProcedureMessage(batch, calledAs, 255, 15020, $"Statistics have been created for the {created} listed columns of the above tables.");
    }
}
