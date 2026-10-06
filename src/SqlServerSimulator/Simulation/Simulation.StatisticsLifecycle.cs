using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The statistics lifecycle: building a statistic's snapshot of the data,
// auto-creating and auto-updating statistics for the columns a query's
// predicates read, and the modification counts both judge staleness by — see
// docs/claude/indexes.md.
partial class Simulation
{
    /// <summary>
    /// One statistic of a table — an index's, a key constraint's, or a
    /// standalone one — with what building it needs: its key columns, the
    /// columns its density vector extends over, and its filter.
    /// </summary>
    internal readonly struct TableStatistic(string name, int statsId, int[] keyOrdinals, int[] densityOrdinals, BooleanExpression? filter, string? filterDefinition, StatisticsState state, bool noRecompute, UserStatistic? user)
    {
        /// <summary>The filter's <c>filter_definition</c> rendering, which the header's <c>Filter Expression</c> reports.</summary>
        public readonly string? FilterDefinition = filterDefinition;

        public readonly string Name = name;
        public readonly int StatsId = statsId;
        public readonly int[] KeyOrdinals = keyOrdinals;
        public readonly int[] DensityOrdinals = densityOrdinals;
        public readonly BooleanExpression? Filter = filter;
        public readonly StatisticsState State = state;
        public readonly bool NoRecompute = noRecompute;

        /// <summary>The standalone statistic this is, or null for an index's.</summary>
        public readonly UserStatistic? User = user;

        public int LeadingOrdinal => this.KeyOrdinals.Length > 0 ? this.KeyOrdinals[0] : -1;
    }

    /// <summary>
    /// Every statistic of <paramref name="table"/> in <c>stats_id</c> order —
    /// its rowstore indexes' and keys', then its standalone ones. A
    /// nonclustered index's density vector runs on over the clustered key,
    /// unique or not (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static List<TableStatistic> StatisticsOn(HeapTable table)
    {
        var identities = table.IndexIdentities();
        int[] clusteredKey = [];
        foreach (var identity in identities)
        {
            if (identity is { Constraint: { IsClustered: true } key })
                clusteredKey = key.FullOrdinals;
            else if (identity is { Index: { IsClustered: true, IsColumnstore: false } index })
                clusteredKey = index.KeyFullOrdinals;
        }

        var statistics = new List<TableStatistic>();
        foreach (var identity in identities)
        {
            switch (identity)
            {
                case { Constraint: { } key }:
                    statistics.Add(new(key.Name, identity.IndexId, key.FullOrdinals, key.IsClustered ? key.FullOrdinals : WithClusteredKey(key.FullOrdinals, clusteredKey),
                        null, null, key.Statistics, key.StatisticsNoRecompute, null));
                    break;
                case { Index: { IsColumnstore: false, IsHypothetical: false } index }:
                    statistics.Add(new(index.Name, identity.IndexId, index.KeyFullOrdinals,
                        index.IsClustered ? index.KeyFullOrdinals : WithClusteredKey(index.KeyFullOrdinals, clusteredKey),
                        index.Filter, index.FilterDefinition, index.Statistics, index.StatisticsNoRecompute, null));
                    break;
            }
        }
        foreach (var statistic in table.UserStatistics)
            statistics.Add(new(statistic.Name, statistic.StatsId, statistic.ColumnFullOrdinals, statistic.ColumnFullOrdinals, statistic.Filter, statistic.FilterDefinition, statistic.Statistics, statistic.NoRecompute, statistic));
        return statistics;
    }

    private static int[] WithClusteredKey(int[] key, int[] clusteredKey)
    {
        if (clusteredKey.Length == 0)
            return key;
        List<int> extended = [.. key];
        foreach (var ordinal in clusteredKey)
        {
            if (!extended.Contains(ordinal))
                extended.Add(ordinal);
        }
        return [.. extended];
    }

    /// <summary>
    /// Builds <paramref name="statistic"/>'s snapshot from the table's rows as
    /// they stand — a table with none leaves it without one, as real's is.
    /// </summary>
    internal static void BuildStatistics(BatchContext batch, HeapTable table, TableStatistic statistic) =>
        statistic.State.Snapshot = BuildStatisticsSnapshot(batch, table, statistic.KeyOrdinals, statistic.DensityOrdinals, statistic.Filter);

    /// <summary>Builds every statistic of <paramref name="table"/> whose name <paramref name="matches"/> accepts.</summary>
    internal static void BuildStatistics(BatchContext batch, HeapTable table, Func<TableStatistic, bool> matches)
    {
        foreach (var statistic in StatisticsOn(table))
        {
            if (matches(statistic))
                BuildStatistics(batch, table, statistic);
        }
    }

    /// <summary>
    /// Reads the rows a statistic over <paramref name="keyOrdinals"/> describes
    /// — those <paramref name="filter"/> admits — into a histogram of the
    /// leading column, the density of each prefix of
    /// <paramref name="densityOrdinals"/>, and the header's figures. Null when
    /// the table has no rows. One step per distinct value up to 200, a
    /// leading <c>NULL</c> step counting the rows whose leading column is
    /// NULL; past 200 values, 200 boundaries spread evenly, MIN and MAX
    /// always among them. Real merges steps of similar spread even under 200
    /// values, which isn't modeled.
    /// </summary>
    internal static StatisticsSnapshot? BuildStatisticsSnapshot(BatchContext batch, HeapTable table, int[] keyOrdinals, int[] densityOrdinals, BooleanExpression? filter)
    {
        if (table.Heap.RowCount == 0 || keyOrdinals.Length == 0)
            return null;

        var leading = keyOrdinals[0];
        var prefixCount = densityOrdinals.Length;
        var prefixSets = new HashSet<SqlValueKey>[prefixCount];
        var prefixLengths = new double[prefixCount];
        for (var p = 0; p < prefixCount; p++)
            prefixSets[p] = [];
        double keyLength = 0;
        var counts = new Dictionary<SqlValueKey, (SqlValue Value, long Count)>();
        long nulls = 0, rows = 0, unfiltered = 0;
        SqlValue[]? fullRow = null;
        foreach (var rowBytes in table.Heap.EnumerateRows())
        {
            unfiltered++;
            fullRow = DecodeFullRowWithComputed(table, rowBytes, batch, ref fullRow);
            if (filter is not null && EvaluateIndexFilter(filter, table, fullRow, batch) != true)
                continue;
            rows++;
            double length = 0;
            for (var p = 0; p < prefixCount; p++)
            {
                var value = fullRow[densityOrdinals[p]];
                length += value.IsNull ? 0 : Parser.Expressions.DataLength.ByteCount(value);
                prefixLengths[p] += length;
                var tuple = new SqlValue[p + 1];
                for (var k = 0; k <= p; k++)
                    tuple[k] = fullRow[densityOrdinals[k]];
                _ = prefixSets[p].Add(new SqlValueKey(tuple));
            }
            foreach (var ordinal in keyOrdinals)
                keyLength += fullRow[ordinal] is { IsNull: false } part ? Parser.Expressions.DataLength.ByteCount(part) : 0;
            var lead = fullRow[leading];
            if (lead.IsNull)
            {
                nulls++;
                continue;
            }
            var key = new SqlValueKey([lead]);
            counts[key] = counts.TryGetValue(key, out var existing) ? (existing.Value, existing.Count + 1) : (lead, 1);
        }

        var histogram = BuildHistogramSteps(table.Columns[leading].Type, counts, nulls);
        double rangeRows = 0, distinctRange = 0;
        foreach (var step in histogram)
        {
            rangeRows += step.RangeRows;
            distinctRange += step.DistinctRangeRows;
        }
        var densityVector = new DensityVectorRow[prefixCount];
        var names = new List<string>(prefixCount);
        for (var p = 0; p < prefixCount; p++)
        {
            names.Add(table.Columns[densityOrdinals[p]].Name);
            densityVector[p] = new DensityVectorRow(
                rows == 0 ? 0 : 1f / prefixSets[p].Count,
                rows == 0 ? 0 : (float)(prefixLengths[p] / rows),
                string.Join(", ", names));
        }
        return new StatisticsSnapshot(
            batch.CurrentStatement.UtcNow,
            rows,
            unfiltered,
            histogram,
            densityVector,
            distinctRange == 0 ? 0 : (float)(rangeRows / distinctRange),
            rows == 0 ? 0 : (float)(keyLength / rows),
            table.Columns[leading].Type.Category == SqlTypeCategory.String,
            table.Columns[leading].Type,
            table.ModificationCount(leading));
    }

    private static HistogramStep[] BuildHistogramSteps(SqlType keyType, Dictionary<SqlValueKey, (SqlValue Value, long Count)> counts, long nulls)
    {
        var steps = new List<HistogramStep>();
        if (nulls > 0)
            steps.Add(new HistogramStep(SqlValue.Null(keyType), 0, nulls, 0, 1));
        if (counts.Count == 0)
            return [.. steps];

        var sorted = new (SqlValue Value, long Count)[counts.Count];
        var n = 0;
        foreach (var (_, entry) in counts)
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

        var lowerExclusive = -1;
        foreach (var boundary in stepIndexes)
        {
            long rangeRows = 0;
            for (var i = lowerExclusive + 1; i < boundary; i++)
                rangeRows += sorted[i].Count;
            var distinctRangeRows = Math.Max(0, boundary - lowerExclusive - 1);
            steps.Add(new HistogramStep(sorted[boundary].Value, rangeRows, sorted[boundary].Count, distinctRangeRows,
                distinctRangeRows == 0 ? 1f : (float)rangeRows / distinctRangeRows));
            lowerExclusive = boundary;
        }
        return [.. steps];
    }

    /// <summary>
    /// What compiling a query does for each base-table column its predicates
    /// read — the WHERE, a join's ON, the HAVING, the GROUP BY and a DISTINCT
    /// list: the optimizer loads the column's statistics, creating one when
    /// none leads with it (an <c>_WA_Sys_&lt;column id&gt;_&lt;object id&gt;</c>
    /// statistic, under <c>AUTO_CREATE_STATISTICS</c>, over a table with rows)
    /// and rebuilding every one leading with it that is stale — built over an
    /// empty table that has rows now, or modified past its threshold since,
    /// under <c>AUTO_UPDATE_STATISTICS</c> and short of <c>NORECOMPUTE</c>
    /// (probed 2026-10-05 against SQL Server 2025). An <c>ORDER BY</c> or a
    /// projection loads nothing.
    /// </summary>
    internal static void LoadQueryStatistics(BatchContext batch, HeapTable table, int columnOrdinal)
    {
        if (table.IsTableVariable || table.IsTypeTable || table.IsMemoryOptimized || (uint)columnOrdinal >= (uint)table.Columns.Length)
            return;
        var database = table.OwningDatabase ?? batch.Connection.Simulation.Databases[TempdbDatabaseName];
        if (database.IsReadOnly)
            return;

        // Every query with a predicate passes through here as it parses, so
        // the walk over the table's statistics allocates nothing unless one
        // of them is stale; StatisticsOn's full descriptions are built only
        // then.
        var autoUpdate = database.Switches.HasFlag(DatabaseSwitches.AutoUpdateStatistics);
        var led = false;
        var stale = false;
        foreach (var key in table.KeyConstraints)
        {
            if (key.FullOrdinals is [var leading, ..] && leading == columnOrdinal)
            {
                led = true;
                stale |= autoUpdate && !key.StatisticsNoRecompute && IsStale(table, key.Statistics, columnOrdinal);
            }
        }
        foreach (var index in table.Indexes)
        {
            if (!index.IsColumnstore && !index.IsHypothetical && index.KeyFullOrdinals is [var leading, ..] && leading == columnOrdinal)
            {
                led = true;
                stale |= autoUpdate && !index.StatisticsNoRecompute && IsStale(table, index.Statistics, columnOrdinal);
            }
        }
        foreach (var user in table.UserStatistics)
        {
            if (user.ColumnFullOrdinals is [var leading, ..] && leading == columnOrdinal)
            {
                led = true;
                stale |= autoUpdate && !user.NoRecompute && IsStale(table, user.Statistics, columnOrdinal);
            }
        }
        if (stale)
        {
            foreach (var statistic in StatisticsOn(table))
            {
                if (statistic.LeadingOrdinal == columnOrdinal && !statistic.NoRecompute && IsStale(table, statistic.State, columnOrdinal))
                    BuildStatistics(batch, table, statistic);
            }
        }
        if (led || !database.Switches.HasFlag(DatabaseSwitches.AutoCreateStatistics) || table.Heap.RowCount == 0)
            return;

        var column = table.Columns[columnOrdinal];
        // A predicate on a computed column real doesn't store reads the
        // columns its expression does, which is where the statistics go
        // (probed 2026-10-06 against SQL Server 2025).
        if (column.Computed is { } expression && !column.IsPersisted && !column.IsHidden)
        {
            expression.VisitColumnReferences(name =>
            {
                var read = Array.FindIndex(table.Columns, candidate => database.Collation.Equals(candidate.Name, name.Leaf));
                if (read >= 0 && read != columnOrdinal && table.Columns[read].Computed is null)
                    LoadQueryStatistics(batch, table, read);
            });
            return;
        }
        if (column.IsHidden || (column.Computed is not null && !column.IsPersisted)
            || column.Type is XmlSqlType or SpatialSqlType or VectorSqlType or JsonSqlType or ClrUdtSqlType || column.Type.IsLegacyLob)
        {
            return;
        }
        lock (table)
        {
            if (table.UserStatistics.Exists(statistic => statistic.ColumnFullOrdinals.Length > 0 && statistic.ColumnFullOrdinals[0] == columnOrdinal))
                return;
            var created = new UserStatistic(
                $"_WA_Sys_{column.ColumnId:X8}_{table.ObjectId:X8}",
                table.NextFreeIndexId(),
                [columnOrdinal],
                noRecompute: false,
                batch.CurrentStatement.UtcNow,
                autoCreated: true);
            created.Statistics.AutoDrop = true;
            created.Statistics.Snapshot = BuildStatisticsSnapshot(batch, table, [columnOrdinal], [columnOrdinal], null);
            // A new list rather than an append, so a reader enumerating the
            // old one meanwhile isn't disturbed.
            table.UserStatistics = [.. table.UserStatistics, created];
        }
        batch.Connection.Simulation.CatalogRows.Invalidate();
    }

    /// <summary>
    /// Whether a statistic whose state is <paramref name="state"/> and whose
    /// leading column is <paramref name="leadingOrdinal"/> needs rebuilding before a query
    /// reads it: it has no snapshot but the table has rows, or the leading
    /// column has been modified past real's threshold for the rows it was
    /// built over — 500 modifications up to 500 rows (6 below 6 rows in a
    /// temporary table), else the lesser of 500 + 20% and √(1000 n).
    /// </summary>
    private static bool IsStale(HeapTable table, StatisticsState state, int leadingOrdinal)
    {
        if (state.Snapshot is not { } snapshot)
            return table.Heap.RowCount > 0;
        var modifications = table.ModificationCount(leadingOrdinal) - snapshot.ModificationBase;
        var n = snapshot.UnfilteredRows;
        var threshold = n <= 500
            ? (table.Name.StartsWith('#') && n < 6 ? 6.0 : 500.0)
            : Math.Min(500 + (0.2 * n), Math.Sqrt(1000.0 * n));
        return modifications >= threshold;
    }

    /// <summary>
    /// Loads statistics for every base-table column the predicates of one query
    /// block read (see <see cref="LoadQueryStatistics"/>). Names resolve the
    /// way the query's own do — qualified to a source, else to the one source
    /// carrying the column — a view's column loading its base table's when it
    /// passes one through, and anything else (an outer reference, a derived
    /// table's column, a view column computed from base columns) loads nothing.
    /// </summary>
    internal static void LoadPredicateStatistics(BatchContext batch, List<FromSource> sources, List<Expression> predicateOperands)
    {
        if (batch.IsSkipping || batch.CreateTimeBinding)
            return;
        var anyTable = false;
        foreach (var source in sources)
            anyTable |= source.BackingTable is { IsTableVariable: false } || source.BackingView is { BaseTable: not null };
        if (!anyTable)
            return;
        var collation = batch.CurrentDatabase.Collation;
        foreach (var operand in predicateOperands)
        {
            operand.VisitColumnReferences(name =>
            {
                if (ResolvePredicateColumn(collation, sources, name) is var (table, ordinal))
                    LoadQueryStatistics(batch, table, ordinal);
            });
        }
    }

    /// <summary>Loads statistics for the columns of <paramref name="table"/> a single-table write's WHERE reads.</summary>
    internal static void LoadPredicateStatistics(BatchContext batch, HeapTable? table, BooleanExpression? where)
    {
        if (where is null || table is null || batch.IsSkipping || batch.CreateTimeBinding || table.IsTableVariable)
            return;
        var collation = batch.CurrentDatabase.Collation;
        where.VisitOperandExpressions(operand => operand.VisitColumnReferences(name =>
        {
            var ordinal = Array.FindIndex(table.Columns, column => collation.Equals(column.Name, name.Leaf));
            if (ordinal >= 0)
                LoadQueryStatistics(batch, table, ordinal);
        }));
    }

    /// <summary>Loads statistics for the columns a joined write's ON and WHERE clauses read.</summary>
    internal static void LoadJoinedPredicateStatistics(BatchContext batch, FromSource[] sources, JoinSpec[] joins, BooleanExpression? where)
    {
        if (batch.IsSkipping)
            return;
        var operands = new List<Expression>();
        where?.VisitOperandExpressions(operands.Add);
        foreach (var join in joins)
            join.OnPredicate?.VisitOperandExpressions(operands.Add);
        LoadPredicateStatistics(batch, [.. sources], operands);
    }

    private static (HeapTable Table, int Ordinal)? ResolvePredicateColumn(Collation collation, List<FromSource> sources, MultiPartName name)
    {
        (HeapTable, int)? found = null;
        foreach (var source in sources)
        {
            if (name.Count > 1 && !(source.Qualifier is { } qualifier && collation.Equals(qualifier, name.ImmediateQualifier)))
                continue;
            HeapTable table;
            int ordinal;
            if (source.BackingTable is { } backing)
            {
                table = backing;
                ordinal = Array.FindIndex(table.Columns, column => collation.Equals(column.Name, name.Leaf));
                if (ordinal < 0)
                    continue;
            }
            else if (source.BackingView is { BaseTable: { } viewBase } view)
            {
                // A view column passing a base column through reads that
                // column (probed 2026-10-06 against SQL Server 2025).
                var viewOrdinal = Array.FindIndex(source.ColumnNames, column => collation.Equals(column, name.Leaf));
                if (viewOrdinal < 0)
                    continue;
                if (viewOrdinal >= view.BaseColumnOrdinals.Length || view.BaseColumnOrdinals[viewOrdinal] < 0)
                    return null;
                (table, ordinal) = (viewBase, view.BaseColumnOrdinals[viewOrdinal]);
            }
            else
            {
                if (name.Count > 1)
                    return null;
                continue;
            }
            if (found is not null)
                return null;
            found = (table, ordinal);
        }
        return found;
    }
}
