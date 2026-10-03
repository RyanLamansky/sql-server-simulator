using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// ALTER TABLE … SWITCH and TRUNCATE TABLE … WITH (PARTITIONS …). The checks,
// their order and the constraint reasoning were probed 2026-09-27 against SQL
// Server 2025; see docs/claude/partitioning.md.
partial class Simulation
{
    /// <summary>
    /// <c>ALTER TABLE source SWITCH [PARTITION n] TO target [PARTITION m]
    /// [WITH (WAIT_AT_LOW_PRIORITY (…))]</c>, entered on <c>SWITCH</c>. Moves
    /// the source's rows — or one partition's — into the empty target, after
    /// real's checks in real's order: the partition numbers, the target's
    /// emptiness, the column shapes, the indexes, the foreign keys, and last
    /// whether what the source's partition range and trusted CHECK
    /// constraints admit fits the target's partition and constraints. The
    /// rows move as ordinary deletes and inserts in the statement's undo log,
    /// so a rollback restores both tables; no trigger fires, identity values
    /// travel as they are, and <c>@@ROWCOUNT</c> reads 0.
    /// </summary>
    private static bool TryParseAlterTableSwitch(ParserContext context, MultiPartName sourceName)
    {
        context.MoveNextRequired();
        Expression? sourcePartition = null;
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Partition })
        {
            context.MoveNextRequired();
            sourcePartition = Expression.Parse(context);
        }
        if (context.Token is not ReservedKeyword { Keyword: Keyword.To })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var targetName = BatchContext.ParseObjectName(context);
        context.MoveNextOptional();
        Expression? targetPartition = null;
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Partition })
        {
            context.MoveNextRequired();
            targetPartition = Expression.Parse(context);
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            SkipBalancedParens(context);
            context.MoveNextOptional();
        }
        if (context.Batch.IsSkipping)
            return true;

        var batch = context.Batch;
        if (!batch.TryResolveTable(sourceName, out var source))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(sourceName.ToString());
        if (!batch.TryResolveTable(targetName, out var target))
            throw SimulatedSqlException.SwitchTargetNotFound(targetName.Leaf);
        RejectOnMemoryOptimized(source, "The operation 'ALTER TABLE SWITCH'", 125);
        RejectOnMemoryOptimized(target, "The operation 'ALTER TABLE SWITCH'", 125);
        if (ReferenceEquals(source, target))
            throw SimulatedSqlException.SwitchSameTable(source.Name, target.Name);
        target.OwningDatabase?.RejectWriteWhenReadOnly();
        batch.AcquireTableRedefinitionLock(source);
        batch.AcquireTableRedefinitionLock(target);

        var sourceText = SwitchTableName(batch, source);
        var targetText = SwitchTableName(batch, target);
        // A system-versioned source can't switch at all, nor can a source
        // without a period switch into a target with one.
        if (source.SystemVersioning is not null)
            throw SimulatedSqlException.SwitchSystemVersionedSource(sourceText);
        if (target.PeriodColumns is not null && source.PeriodColumns is null)
            throw SimulatedSqlException.SwitchTargetHasPeriod(sourceText);
        var sourceNumber = ReadSwitchPartition(batch, sourcePartition, source, sourceText, state: 1);
        var targetNumber = ReadSwitchPartition(batch, targetPartition, target, targetText, state: 2);

        // The target must be empty, or its partition — checked ahead of
        // every shape difference.
        if (target.Partitioning is { } targetPlacement)
        {
            if (targetPlacement.Census(target).Rows[targetNumber - 1] != 0)
                throw SimulatedSqlException.SwitchTargetPartitionNotEmpty(targetNumber, targetText);
        }
        // The slot count keeps a deleted row's tombstone, so only a live row
        // makes the target non-empty.
        else if (target.Heap.RowCount != 0 && target.Heap.EnumerateRows().Any())
        {
            throw SimulatedSqlException.SwitchTargetNotEmpty(targetText);
        }

        RequireSwitchShapesMatch(batch.CurrentDatabase.Collation, source, sourceText, target, targetText);
        RequireSwitchConstraintsFit(batch, source, sourceText, sourceNumber, target, targetText, targetNumber);

        VersionStore.NoteDefinitionChange(batch, source);
        VersionStore.NoteDefinitionChange(batch, target);
        VersionStore.SetAsideVersions(batch, source);
        VersionStore.SetAsideVersions(batch, target);

        // Move the rows.
        var undoLog = context.Connection.CurrentTransaction?.UndoLog;
        var moving = new List<(int Page, int Slot, SqlValue[] Values)>();
        var storageOrdinal = source.Partitioning?.StorageOrdinal(source) ?? -1;
        foreach (var (page, slot, bytes) in source.Heap.EnumerateRowsWithAddress())
        {
            if (source.Partitioning is { } placement && placement.PartitionOfRow(source, storageOrdinal, bytes) != sourceNumber)
                continue;
            moving.Add((page, slot, RowDecoder.DecodeRow(source.StoredColumns, bytes, source.Heap)));
        }
        foreach (var (page, slot, values) in moving)
        {
            _ = target.Heap.Insert(RowEncoder.EncodeRow(target.StoredColumns, values, target.Heap), undoLog);
            source.Heap.DeleteAt(page, slot, undoLog, ReclaimSuperseded(source, context));
        }
        return true;
    }

    /// <summary>A switched table as the SWITCH errors name it: database, schema and table.</summary>
    private static string SwitchTableName(BatchContext batch, HeapTable table)
    {
        var database = batch.DatabaseFor(table);
        var schema = database.Schemas.EnumerateValues().FirstOrDefault(candidate => candidate.SchemaId == table.SchemaId)?.Name ?? Database.DefaultSchemaName;
        return $"{database.Name}.{schema}.{table.Name}";
    }

    /// <summary>
    /// The partition a SWITCH side names, 1 for an unpartitioned table. A
    /// partitioned side must name one (Msg 4911) that exists (Msg 4950); a
    /// number on an unpartitioned side is ignored with the class-0 Msg 4903.
    /// </summary>
    private static int ReadSwitchPartition(BatchContext batch, Expression? written, HeapTable table, string tableText, byte state)
    {
        long? number = written is null ? null : EvaluatePartitionNumber(batch, written);
        if (table.Partitioning is not { } placement)
        {
            if (number is { } ignored)
                batch.AppendInfoError(0, state, 4903, SimulatedSqlException.SwitchPartitionIgnoredMessage(ignored, tableText));
            return 1;
        }
        if (number is not { } given)
            throw SimulatedSqlException.SwitchPartitionNumberRequired(tableText, state);
        return given < 1 || given > placement.Fanout
            ? throw SimulatedSqlException.SwitchPartitionNotFound(given, tableText)
            : (int)given;
    }

    /// <summary>
    /// A partition-number expression's value as an integer, 0 for NULL or a
    /// value that won't convert (where real reports a meaningless number).
    /// </summary>
    private static long EvaluatePartitionNumber(BatchContext batch, Expression written)
    {
        var value = written.Run(new RuntimeContext(NoColumnResolver, batch));
        if (value.IsNull)
            return 0;
        try
        {
            return value.CoerceTo(SqlType.BigInt).AsInt64;
        }
        catch (Exception error) when (error is SimulatedSqlException or OverflowException or FormatException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Refuses a SWITCH between tables whose columns or indexes differ: the
    /// column count (Msg 4943), then per column its name (4942), type (4944),
    /// collation (4945), nullability (4985) and computed definition (4966);
    /// then a clustered index on one side only (4913), a target index without
    /// an identical source index (4947), and a target foreign key the source
    /// lacks (4968).
    /// </summary>
    private static void RequireSwitchShapesMatch(Collation collation, HeapTable source, string sourceText, HeapTable target, string targetText)
    {
        if (source.Columns.Length != target.Columns.Length)
            throw SimulatedSqlException.SwitchColumnCountMismatch(sourceText, source.Columns.Length, targetText, target.Columns.Length);
        for (var i = 0; i < source.Columns.Length; i++)
        {
            var left = source.Columns[i];
            var right = target.Columns[i];
            if (!collation.Equals(left.Name, right.Name))
                throw SimulatedSqlException.SwitchColumnNameMismatch(left.Name, i + 1, sourceText, right.Name, targetText);
            if (left.Type.SystemTypeId != right.Type.SystemTypeId
                || BuiltInResources.GetSysColumnMetadata(left) != BuiltInResources.GetSysColumnMetadata(right))
            {
                throw SimulatedSqlException.SwitchColumnTypeMismatch(left.Name, PartitionTypeText(left.Type), sourceText, PartitionTypeText(right.Type), targetText);
            }
            if (SqlType.IsCollatedString(left.Type) && !string.Equals(left.Type.Collation?.Name, right.Type.Collation?.Name, StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.SwitchColumnCollationMismatch(left.Name, sourceText, targetText);
            if (left.Nullable != right.Nullable)
                throw SimulatedSqlException.SwitchColumnNullabilityMismatch(left.Name, sourceText, targetText);
            if (left.ComputedDefinition != right.ComputedDefinition)
                throw SimulatedSqlException.SwitchComputedColumnMismatch(left.Name, left.ComputedDefinition ?? "", sourceText, right.ComputedDefinition ?? "", targetText);
        }

        var sourceIndexes = source.IndexIdentities().FindAll(static identity => !identity.IsHeap);
        var targetIndexes = target.IndexIdentities().FindAll(static identity => !identity.IsHeap);
        var sourceClustered = sourceIndexes.Find(static identity => identity.IndexId == 1);
        var targetClustered = targetIndexes.Find(static identity => identity.IndexId == 1);
        if (sourceClustered.Name is not null && targetClustered.Name is null)
            throw SimulatedSqlException.SwitchClusteredMismatch(sourceText, sourceClustered.Name, targetText, state: 1);
        if (targetClustered.Name is not null && sourceClustered.Name is null)
            throw SimulatedSqlException.SwitchClusteredMismatch(targetText, targetClustered.Name, sourceText, state: 2);
        foreach (var wanted in targetIndexes)
        {
            var shape = SwitchIndexShape(collation, target, wanted);
            if (!sourceIndexes.Exists(candidate => SwitchIndexShape(collation, source, candidate) == shape))
                throw SimulatedSqlException.SwitchNoIdenticalIndex(sourceText, wanted.Name!, targetText);
        }

        if (source.IncomingForeignKeys.Find(key => !ReferenceEquals(key.ChildTable, source)) is { } referencing)
            throw SimulatedSqlException.SwitchSourceReferenced(sourceText, referencing.Name);
        foreach (var key in target.OutgoingForeignKeys)
        {
            var corresponding = source.OutgoingForeignKeys.Exists(candidate =>
                ReferenceEquals(candidate.ReferencedTable, key.ReferencedTable)
                && candidate.ChildColumnOrdinals.AsSpan().SequenceEqual(key.ChildColumnOrdinals)
                && candidate.ReferencedColumnOrdinals.AsSpan().SequenceEqual(key.ReferencedColumnOrdinals));
            if (!corresponding)
                throw SimulatedSqlException.SwitchTargetForeignKey(targetText, key.Name, sourceText);
        }
    }

    /// <summary>What makes two indexes identical for SWITCH: clustering, uniqueness, and the key and included columns by name, order and direction.</summary>
    private static string SwitchIndexShape(Collation collation, HeapTable table, IndexIdentity identity)
    {
        string ColumnName(int fullOrdinal) => collation.Name + ":" + table.Columns[fullOrdinal].Name.ToUpperInvariant();
        if (identity.Constraint is { } key)
        {
            var keys = key.FullOrdinals.Select((ordinal, i) => ColumnName(ordinal) + (key.IsDescending(i) ? "-" : "+"));
            return $"{identity.IndexId == 1}|True|{string.Join(",", keys)}|";
        }
        var index = identity.Index!;
        return $"{identity.IndexId == 1}|{index.IsUnique}|{string.Join(",", index.KeyColumns.Select(column => ColumnName(column.ColumnOrdinal) + (column.IsDescending ? "-" : "+")))}|{string.Join(",", index.IncludedColumnOrdinals.Select(ColumnName).Order())}|{index.FilterDefinition}";
    }

    /// <summary>
    /// Refuses a SWITCH whose source may hold rows the target can't. What the
    /// source admits for a column — its partition's range when it is the
    /// partition column, narrowed by its enabled, trusted CHECK constraints
    /// over that column alone — must first satisfy each of the target's
    /// enabled CHECK constraints: one over a single column the source
    /// partitions on or constrains by that reasoning (Msg 4972), any other by
    /// a source constraint of the same definition (Msg 4971). Then what it admits for the partition column must fit the
    /// target partition's range (Msg 4973 from a partition; Msg 4982 from a
    /// table no CHECK over that column constrains at all, else 4972).
    /// </summary>
    private static void RequireSwitchConstraintsFit(
        BatchContext batch, HeapTable source, string sourceText, int sourceNumber, HeapTable target, string targetText, int targetNumber)
    {
        var collation = batch.CurrentDatabase.Collation;
        ValueDomain Admitted(HeapColumn sourceColumn)
        {
            var admitted = source.Partitioning is { } sourcePlacement && collation.Equals(sourcePlacement.Column.Name, sourceColumn.Name)
                ? ValueDomain.OfPartition(sourcePlacement, sourceNumber, sourceColumn)
                : ValueDomain.All(sourceColumn);
            foreach (var check in source.CheckConstraints)
            {
                if (!check.IsDisabled && !check.IsNotTrusted && SoleColumn(batch, check.Predicate, source) == sourceColumn)
                    admitted = admitted.Intersect(ValueDomain.OfPredicate(batch, check.Predicate, sourceColumn));
            }
            return admitted;
        }

        foreach (var check in target.CheckConstraints)
        {
            if (check.IsDisabled)
                continue;
            if (SoleColumn(batch, check.Predicate, target) is { } targetColumn
                && Array.Find(source.Columns, candidate => collation.Equals(candidate.Name, targetColumn.Name)) is { } sourceColumn
                && (ReferenceEquals(source.Partitioning?.Column, sourceColumn)
                    || source.CheckConstraints.Exists(candidate => SoleColumn(batch, candidate.Predicate, source) == sourceColumn)))
            {
                if (!Admitted(sourceColumn).IsWithin(ValueDomain.OfPredicate(batch, check.Predicate, targetColumn)))
                    throw SimulatedSqlException.SwitchSourceAllowsMore(sourceText, targetText);
                continue;
            }
            var corresponding = source.CheckConstraints.Exists(candidate =>
                !candidate.IsDisabled && string.Equals(candidate.Definition, check.Definition, StringComparison.OrdinalIgnoreCase));
            if (!corresponding)
                throw SimulatedSqlException.SwitchTargetCheckConstraint(targetText, check.Name, sourceText);
        }

        if (target.Partitioning is not { } targetPlacement
            || Array.Find(source.Columns, candidate => collation.Equals(candidate.Name, targetPlacement.Column.Name)) is not { } partitionColumn)
        {
            return;
        }
        if (Admitted(partitionColumn).IsWithin(ValueDomain.OfPartition(targetPlacement, targetNumber, partitionColumn)))
            return;
        if (source.Partitioning is not null)
            throw SimulatedSqlException.SwitchRangeNotSubset(sourceNumber, sourceText, targetNumber, targetText);
        throw source.CheckConstraints.Exists(check => SoleColumn(batch, check.Predicate, source) == partitionColumn)
            ? SimulatedSqlException.SwitchSourceAllowsMore(sourceText, targetText)
            : SimulatedSqlException.SwitchSourceCheckOutsideRange(sourceText, targetNumber, targetText);
    }

    /// <summary>The one column of <paramref name="table"/> that <paramref name="predicate"/> reads, or null when it reads none or several.</summary>
    private static HeapColumn? SoleColumn(BatchContext batch, BooleanExpression predicate, HeapTable table)
    {
        var collation = batch.CurrentDatabase.Collation;
        HeapColumn? sole = null;
        var several = false;
        predicate.VisitOperandExpressions(operand => operand.VisitColumnReferences(name =>
        {
            var column = Array.Find(table.Columns, candidate => collation.Equals(candidate.Name, name.Leaf));
            if (sole is null)
                sole = column;
            else if (!ReferenceEquals(sole, column))
                several = true;
        }));
        return several ? null : sole;
    }

    /// <summary>
    /// <c>TRUNCATE TABLE … WITH ( PARTITIONS ( n | n TO m [, …] ) )</c>: the
    /// partition list, parsed with the cursor on <c>WITH</c> and left on its
    /// closing parenthesis. Each bound is any expression, converted to an
    /// integer.
    /// </summary>
    private static List<(Expression Low, Expression? High)> ParseTruncatePartitions(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not UnquotedString { Value: var word } || !BuiltInToken.Equals(word, "PARTITIONS"))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var ranges = new List<(Expression Low, Expression? High)>();
        do
        {
            context.MoveNextRequired();
            if (context.Token is Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var low = Expression.Parse(context);
            Expression? high = null;
            if (context.Token is ReservedKeyword { Keyword: Keyword.To })
            {
                context.MoveNextRequired();
                high = Expression.Parse(context);
            }
            ranges.Add((low, high));
        } while (context.Token is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return ranges;
    }

    /// <summary>
    /// Truncates the listed partitions of <paramref name="table"/>: Msg 7729
    /// state 3 when it isn't partitioned, 7722 for a number past its
    /// partitions, 7728 for a reversed range, 7711 for a partition listed
    /// twice, and 3756 when an index isn't aligned with it. The rows go as
    /// ordinary deletes, so the identity high-water mark stays where it was.
    /// </summary>
    private static void TruncatePartitions(BatchContext batch, HeapTable table, List<(Expression Low, Expression? High)> ranges)
    {
        if (table.Partitioning is not { } placement)
            throw SimulatedSqlException.TruncatePartitionOnUnpartitioned(table.Name);
        var fanout = placement.Fanout;
        var chosen = new bool[fanout];
        foreach (var (lowExpression, highExpression) in ranges)
        {
            var low = EvaluatePartitionNumber(batch, lowExpression);
            var high = highExpression is null ? low : EvaluatePartitionNumber(batch, highExpression);
            if (low < 1 || low > fanout)
                throw SimulatedSqlException.InvalidPartitionNumber(low, table.Name, fanout);
            if (high < 1 || high > fanout)
                throw SimulatedSqlException.InvalidPartitionNumber(high, table.Name, fanout);
            if (low > high)
                throw SimulatedSqlException.InvalidPartitionRange(low, high);
            for (var number = low; number <= high; number++)
            {
                if (chosen[number - 1])
                    throw SimulatedSqlException.PartitionsOptionRepeated();
                chosen[number - 1] = true;
            }
        }
        foreach (var identity in table.IndexIdentities())
        {
            if (!ReferenceEquals(PlacementOf(table, identity)?.Scheme.Function, placement.Scheme.Function))
                throw SimulatedSqlException.TruncatePartitionUnalignedIndex(identity.Name ?? table.Name, table.Name, placement.Scheme.Function.Name);
        }

        var storageOrdinal = placement.StorageOrdinal(table);
        var doomed = new List<(int Page, int Slot)>();
        foreach (var (page, slot, bytes) in table.Heap.EnumerateRowsWithAddress())
        {
            if (chosen[placement.PartitionOfRow(table, storageOrdinal, bytes) - 1])
                doomed.Add((page, slot));
        }
        var undoLog = batch.Connection.CurrentTransaction?.UndoLog;
        var reclaim = !VersionStore.WillCaptureVersions(batch.DatabaseFor(table), table);
        foreach (var (page, slot) in doomed)
            table.Heap.DeleteAt(page, slot, undoLog, reclaim);
    }
}

/// <summary>
/// The non-NULL values, and whether NULL, one column may hold as far as a
/// partition's range and CHECK constraints tell — the reasoning real's
/// <c>ALTER TABLE … SWITCH</c> does statically. Values are a set of
/// intervals; an integer column's bounds read in <c>bigint</c> and close
/// over the next integer, so <c>a &gt;= 11</c> and <c>a &gt; 10</c> agree.
/// What the reasoning can't read — a <c>NOT</c>, a function, a constant that
/// won't convert — admits every value, which is conservative.
/// </summary>
internal sealed class ValueDomain(List<ValueInterval> intervals, bool admitsNull)
{
    public readonly List<ValueInterval> Intervals = intervals;

    public readonly bool AdmitsNull = admitsNull;

    /// <summary>Every value <paramref name="column"/>'s type holds, and NULL when it's nullable.</summary>
    public static ValueDomain All(HeapColumn column) => new([new ValueInterval(null, false, null, false)], column.Nullable);

    /// <summary>The values partition <paramref name="number"/> of <paramref name="placement"/> holds.</summary>
    public static ValueDomain OfPartition(PartitionPlacement placement, int number, HeapColumn column)
    {
        var function = placement.Scheme.Function;
        var boundaries = function.Boundaries;
        var lower = number >= 2 ? boundaries[number - 2] : (SqlValue?)null;
        var upper = number <= boundaries.Length ? boundaries[number - 1] : (SqlValue?)null;
        var admitsNull = column.Nullable && function.PartitionOf(SqlValue.Null(function.ParameterType)) == number;
        // A NULL upper boundary holds no value below it; a NULL lower one
        // bounds nothing, NULL ordering below every value.
        if (upper is { IsNull: true })
            return new ValueDomain([], admitsNull);
        var interval = new ValueInterval(
            lower is { IsNull: false } low ? DomainValue(low, column.Type) : null,
            function.BoundaryOnRight,
            upper is { } high ? DomainValue(high, column.Type) : null,
            !function.BoundaryOnRight);
        return new ValueDomain(Normalize([interval]), admitsNull);
    }

    /// <summary>
    /// The values for which <paramref name="predicate"/> isn't FALSE — what a
    /// CHECK constraint lets through — and whether NULL gets through. With
    /// <paramref name="asPartitionedView"/> it reads as real's partitioned-view
    /// analysis does (probed 2026-10-01 against SQL Server 2025), where
    /// <c>ALTER TABLE … SWITCH</c>'s admits every value: a <c>NOT</c>,
    /// <c>&lt;&gt;</c>, <c>NOT BETWEEN</c> or <c>NOT IN</c> over a shape the
    /// reasoning reads exactly is the complement, and an integer column's
    /// range bound by a fraction (<c>k &lt; 10.5</c>) rounds to the integers
    /// it admits.
    /// </summary>
    public static ValueDomain OfPredicate(BatchContext batch, BooleanExpression predicate, HeapColumn column, bool asPartitionedView = false)
    {
        var (values, nullOutcome, _) = Analyze(batch, predicate, column, asPartitionedView);
        return new ValueDomain(Normalize(values), column.Nullable && nullOutcome != false);
    }

    public ValueDomain Intersect(ValueDomain other) =>
        new(Normalize(IntersectIntervals(this.Intervals, other.Intervals)), this.AdmitsNull && other.AdmitsNull);

    /// <summary>Whether no value — NULL included — is admitted by both this and <paramref name="other"/>.</summary>
    public bool IsDisjointFrom(ValueDomain other) =>
        !(this.AdmitsNull && other.AdmitsNull) && IntersectIntervals(this.Intervals, other.Intervals).Count == 0;

    /// <summary>
    /// Whether <paramref name="value"/>, a value of <paramref name="columnType"/>,
    /// lies in this domain; NULL lies in it when it admits NULL.
    /// </summary>
    public bool Admits(SqlValue value, SqlType columnType)
    {
        if (value.IsNull)
            return this.AdmitsNull;
        var point = DomainValue(value, columnType);
        var probe = new ValueInterval(point, true, point, true);
        return this.Intervals.Exists(interval => Contains(interval, probe));
    }

    /// <summary>Whether every value this admits — NULL included — <paramref name="outer"/> admits too.</summary>
    public bool IsWithin(ValueDomain outer)
    {
        if (this.AdmitsNull && !outer.AdmitsNull)
            return false;
        foreach (var inner in this.Intervals)
        {
            if (!outer.Intervals.Exists(candidate => Contains(candidate, inner)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The values <paramref name="predicate"/> doesn't rule out, the verdict it
    /// gives NULL, and whether those values are exactly the ones it holds TRUE
    /// for rather than a conservative superset.
    /// </summary>
    private static (List<ValueInterval> Values, bool? NullOutcome, bool Exact) Analyze(BatchContext batch, BooleanExpression predicate, HeapColumn column, bool asPartitionedView)
    {
        var all = new List<ValueInterval> { new(null, false, null, false) };
        var conjuncts = new List<BooleanExpression>();
        predicate.CollectConjuncts(conjuncts);
        if (conjuncts.Count > 1)
        {
            var values = all;
            bool? outcome = true;
            var exact = true;
            foreach (var conjunct in conjuncts)
            {
                var (part, partNull, partExact) = Analyze(batch, conjunct, column, asPartitionedView);
                values = IntersectIntervals(values, part);
                outcome = outcome == false || partNull == false ? false : outcome == true && partNull == true ? true : null;
                exact &= partExact;
            }
            return (values, outcome, exact);
        }
        var disjuncts = new List<BooleanExpression>();
        predicate.CollectDisjuncts(disjuncts);
        if (disjuncts.Count > 1)
        {
            var values = new List<ValueInterval>();
            bool? outcome = false;
            var exact = true;
            foreach (var disjunct in disjuncts)
            {
                var (part, partNull, partExact) = Analyze(batch, disjunct, column, asPartitionedView);
                values.AddRange(part);
                outcome = outcome == true || partNull == true ? true : outcome == false && partNull == false ? false : null;
                exact &= partExact;
            }
            return (values, outcome, exact);
        }

        if (asPartitionedView && predicate.TryGetComplement(out var positive))
        {
            var (values, nullOutcome, exact) = Analyze(batch, positive, column, asPartitionedView);
            return exact ? (Complement(Normalize(values)), nullOutcome is { } verdict ? !verdict : null, true) : (all, null, false);
        }
        if (predicate.TryGetNullTest(out var tested, out var isNotNull) && IsColumn(batch, tested, column))
            return (isNotNull ? all : [], !isNotNull, true);
        if (predicate.TryGetEqualityOperands(out var left, out var right))
        {
            var point = IsColumn(batch, left, column) ? Constant(batch, right, column) : IsColumn(batch, right, column) ? Constant(batch, left, column) : null;
            return point is { } value ? ([new ValueInterval(value, true, value, true)], null, true) : (all, null, false);
        }
        if (predicate.TryGetRangeOperands(out var rangeLeft, out var op, out var rangeRight))
        {
            var columnLeft = IsColumn(batch, rangeLeft, column);
            if (!columnLeft && !IsColumn(batch, rangeRight, column))
                return (all, null, false);
            var bound = Constant(batch, columnLeft ? rangeRight : rangeLeft, column);
            if (!columnLeft)
            {
                op = op switch
                {
                    RangeComparison.Greater => RangeComparison.Less,
                    RangeComparison.GreaterOrEqual => RangeComparison.LessOrEqual,
                    RangeComparison.Less => RangeComparison.Greater,
                    _ => RangeComparison.GreaterOrEqual,
                };
            }
            if (bound is null && asPartitionedView && IntegerRangeBound(batch, columnLeft ? rangeRight : rangeLeft, column, op) is { } rounded)
                (bound, op) = rounded;
            if (bound is null)
                return (all, null, false);
            ValueInterval interval = op switch
            {
                RangeComparison.Greater => new(bound, false, null, false),
                RangeComparison.GreaterOrEqual => new(bound, true, null, false),
                RangeComparison.Less => new(null, false, bound, false),
                _ => new(null, false, bound, true),
            };
            return ([interval], null, true);
        }
        if (predicate.TryGetBetweenOperands(out var subject, out var lower, out var upper) && IsColumn(batch, subject, column))
        {
            return Constant(batch, lower, column) is { } low && Constant(batch, upper, column) is { } high
                ? ([new ValueInterval(low, true, high, true)], null, true)
                : (all, null, false);
        }
        if (predicate.TryGetEqualityFamily(out var pairs))
        {
            var points = new List<ValueInterval>();
            foreach (var (pairLeft, pairRight) in pairs)
            {
                var point = IsColumn(batch, pairLeft, column) ? Constant(batch, pairRight, column) : IsColumn(batch, pairRight, column) ? Constant(batch, pairLeft, column) : null;
                if (point is not { } value)
                    return (all, null, false);
                points.Add(new ValueInterval(value, true, value, true));
            }
            return (points, null, true);
        }
        return (all, null, false);
    }

    /// <summary>
    /// An integer column's bound from a fractional constant, rounded to the
    /// integers <paramref name="op"/> admits — <c>&lt; 10.5</c> is
    /// <c>&lt;= 10</c> and <c>&gt; 10.5</c> is <c>&gt;= 11</c> — or null when
    /// the column isn't an integer or the constant isn't a number.
    /// </summary>
    private static (SqlValue Bound, RangeComparison Op)? IntegerRangeBound(BatchContext batch, Expression expression, HeapColumn column, RangeComparison op)
    {
        if (!SqlType.IsIntegerCategory(column.Type))
            return null;
        var readsColumn = false;
        expression.VisitColumnReferences(_ => readsColumn = true);
        if (readsColumn)
            return null;
        try
        {
            var raw = expression.Run(new RuntimeContext(Simulation.NoColumnResolver, batch));
            if (raw.IsNull || raw.Type.Category is not (SqlTypeCategory.Decimal or SqlTypeCategory.Approximate or SqlTypeCategory.Money))
                return null;
            var value = raw.CoerceTo(SqlType.Float).AsDouble;
            return op is RangeComparison.Less or RangeComparison.LessOrEqual
                ? (SqlValue.FromInt64((long)Math.Floor(value) - (op == RangeComparison.Less && Math.Floor(value) == value ? 1 : 0)), RangeComparison.LessOrEqual)
                : (SqlValue.FromInt64((long)Math.Ceiling(value) + (op == RangeComparison.Greater && Math.Ceiling(value) == value ? 1 : 0)), RangeComparison.GreaterOrEqual);
        }
        catch (Exception error) when (error is SimulatedSqlException or OverflowException or FormatException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The values between and around <paramref name="normalized"/>'s sorted, merged intervals.</summary>
    private static List<ValueInterval> Complement(List<ValueInterval> normalized)
    {
        var gaps = new List<ValueInterval>(normalized.Count + 1);
        SqlValue? low = null;
        var lowInclusive = false;
        var unboundedBelow = true;
        foreach (var interval in normalized)
        {
            if (interval.Low is { } start)
                gaps.Add(new ValueInterval(unboundedBelow ? null : low, lowInclusive, start, !interval.LowInclusive));
            if (interval.High is not { } end)
                return gaps;
            low = end;
            lowInclusive = !interval.HighInclusive;
            unboundedBelow = false;
        }
        gaps.Add(new ValueInterval(unboundedBelow ? null : low, lowInclusive, null, false));
        return gaps;
    }

    private static bool IsColumn(BatchContext batch, Expression expression, HeapColumn column) =>
        expression is Reference reference && batch.CurrentDatabase.Collation.Equals(reference.ReferencedName.Leaf, column.Name);

    /// <summary>
    /// A constant operand's value in the column's domain type, or null when it
    /// isn't a constant, is NULL (a comparison with NULL rules nothing out),
    /// or doesn't convert exactly.
    /// </summary>
    private static SqlValue? Constant(BatchContext batch, Expression expression, HeapColumn column)
    {
        var readsColumn = false;
        expression.VisitColumnReferences(_ => readsColumn = true);
        if (readsColumn)
            return null;
        try
        {
            var raw = expression.Run(new RuntimeContext(Simulation.NoColumnResolver, batch));
            if (raw.IsNull)
                return null;
            var converted = raw.CoerceTo(column.Type);
            if (raw.Type != column.Type && raw.Type.Category != column.Type.Category && converted.CoerceTo(raw.Type).CompareTo(raw) != 0)
                return null;
            return DomainValue(converted, column.Type);
        }
        catch (Exception error) when (error is SimulatedSqlException or OverflowException or FormatException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A column value as the domain compares it: <c>bigint</c> for the integer types, else as it is.</summary>
    private static SqlValue DomainValue(SqlValue value, SqlType columnType) =>
        SqlType.IsIntegerCategory(columnType) ? value.CoerceTo(SqlType.BigInt) : value.Type == columnType ? value : value.CoerceTo(columnType);

    /// <summary>Drops empty intervals, closes integer bounds, sorts by lower bound and merges overlaps.</summary>
    private static List<ValueInterval> Normalize(List<ValueInterval> intervals)
    {
        var closed = new List<ValueInterval>(intervals.Count);
        foreach (var interval in intervals)
        {
            var current = interval;
            if (current.Low is { Type: BigIntSqlType } low && !current.LowInclusive)
                current = low.AsInt64 == long.MaxValue ? current : new ValueInterval(SqlValue.FromInt64(low.AsInt64 + 1), true, current.High, current.HighInclusive);
            if (current.High is { Type: BigIntSqlType } high && !current.HighInclusive)
                current = high.AsInt64 == long.MinValue ? current : new ValueInterval(current.Low, current.LowInclusive, SqlValue.FromInt64(high.AsInt64 - 1), true);
            if (!IsEmpty(current))
                closed.Add(current);
        }
        closed.Sort(static (a, b) => CompareLow(a, b));
        var merged = new List<ValueInterval>(closed.Count);
        foreach (var interval in closed)
        {
            if (merged.Count > 0 && Touches(merged[^1], interval))
            {
                var last = merged[^1];
                merged[^1] = CompareHigh(last, interval) >= 0 ? last : new ValueInterval(last.Low, last.LowInclusive, interval.High, interval.HighInclusive);
            }
            else
            {
                merged.Add(interval);
            }
        }
        return merged;
    }

    private static List<ValueInterval> IntersectIntervals(List<ValueInterval> left, List<ValueInterval> right)
    {
        var result = new List<ValueInterval>();
        foreach (var a in left)
        {
            foreach (var b in right)
            {
                var low = CompareLow(a, b) >= 0 ? a : b;
                var high = CompareHigh(a, b) <= 0 ? a : b;
                var candidate = new ValueInterval(low.Low, low.LowInclusive, high.High, high.HighInclusive);
                if (!IsEmpty(candidate))
                    result.Add(candidate);
            }
        }
        return result;
    }

    private static bool IsEmpty(ValueInterval interval)
    {
        if (interval.Low is not { } low || interval.High is not { } high)
            return false;
        var comparison = low.CompareTo(high);
        return comparison > 0 || (comparison == 0 && !(interval.LowInclusive && interval.HighInclusive));
    }

    /// <summary>Orders lower bounds: unbounded first, then by value, inclusive before exclusive.</summary>
    private static int CompareLow(ValueInterval a, ValueInterval b) =>
        a.Low is not { } left ? (b.Low is null ? 0 : -1)
        : b.Low is not { } right ? 1
        : left.CompareTo(right) is var comparison and not 0 ? comparison
        : a.LowInclusive == b.LowInclusive ? 0 : a.LowInclusive ? -1 : 1;

    /// <summary>Orders upper bounds: by value, exclusive before inclusive, unbounded last.</summary>
    private static int CompareHigh(ValueInterval a, ValueInterval b) =>
        a.High is not { } left ? (b.High is null ? 0 : 1)
        : b.High is not { } right ? -1
        : left.CompareTo(right) is var comparison and not 0 ? comparison
        : a.HighInclusive == b.HighInclusive ? 0 : a.HighInclusive ? 1 : -1;

    /// <summary>Whether <paramref name="next"/>, starting no lower, overlaps or abuts <paramref name="previous"/>.</summary>
    private static bool Touches(ValueInterval previous, ValueInterval next)
    {
        if (previous.High is not { } high || next.Low is not { } low)
            return true;
        var comparison = low.CompareTo(high);
        return comparison < 0
            || (comparison == 0 && (previous.HighInclusive || next.LowInclusive))
            || (low.Type is BigIntSqlType && previous.HighInclusive && next.LowInclusive && low.AsInt64 - 1 == high.AsInt64);
    }

    private static bool Contains(ValueInterval outer, ValueInterval inner) =>
        CompareLow(outer, inner) <= 0 && CompareHigh(outer, inner) >= 0;
}

/// <summary>One interval of a <see cref="ValueDomain"/>; a null bound is unbounded on that side.</summary>
internal readonly struct ValueInterval(SqlValue? low, bool lowInclusive, SqlValue? high, bool highInclusive)
{
    public readonly SqlValue? Low = low;
    public readonly bool LowInclusive = lowInclusive;
    public readonly SqlValue? High = high;
    public readonly bool HighInclusive = highInclusive;
}
