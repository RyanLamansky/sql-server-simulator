using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private const string IgnoreDupKeyOption = "IGNORE_DUP_KEY";

    /// <summary>
    /// <c>ALTER INDEX … SET</c> options taking <c>ON</c> / <c>OFF</c>.
    /// <c>STATISTICS_NORECOMPUTE</c> is recognized only so its name doesn't
    /// fall to Msg 155; the others are recorded.
    /// </summary>
    private static readonly FrozenSet<string> OnOffIndexOptions = new[]
    {
        "ALLOW_PAGE_LOCKS",
        "ALLOW_ROW_LOCKS",
        IgnoreDupKeyOption,
        "OPTIMIZE_FOR_SEQUENTIAL_KEY",
        "STATISTICS_NORECOMPUTE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>ALTER INDEX … SET</c> options taking a numeric value. Recognized and
    /// discarded — there is no B-tree for it to describe.
    /// </summary>
    private static readonly FrozenSet<string> NumericIndexOptions = new[]
    {
        "COMPRESSION_DELAY",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Index options <c>ALTER INDEX … SET</c> can't change, which real names
    /// "ALTER INDEX SET" options in its Msg 155 (probed 2026-09-26 against SQL
    /// Server 2025).
    /// </summary>
    private static readonly FrozenSet<string> NonSettableIndexOptions = new[]
    {
        "DATA_COMPRESSION",
        "DROP_EXISTING",
        "FILLFACTOR",
        "MAX_DURATION",
        "MAXDOP",
        "ONLINE",
        "PAD_INDEX",
        "RESUMABLE",
        "SORT_IN_TEMPDB",
        "STATISTICS_INCREMENTAL",
        "XML_COMPRESSION",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <c>ALTER INDEX { index_name | ALL } ON &lt;table&gt; SET ( option
    /// [, …] )</c>. Of the SET options only <c>IGNORE_DUP_KEY</c> carries a
    /// semantic (see <c>docs/claude/constraints.md</c>); <c>ALLOW_ROW_LOCKS</c>
    /// / <c>ALLOW_PAGE_LOCKS</c> / <c>OPTIMIZE_FOR_SEQUENTIAL_KEY</c> and a
    /// columnstore index's <c>COMPRESSION_DELAY</c> are recorded for the
    /// catalog, and <c>STATISTICS_NORECOMPUTE</c> moves the index's
    /// <c>sys.stats.no_recompute</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The option list is validated strictly: an unknown name raises
    /// <b>Msg 155</b>, and its <c>= value</c> must be <c>ON</c> / <c>OFF</c>
    /// (a numeric for <c>COMPRESSION_DELAY</c>) or the statement is a syntax
    /// error. The <c>WITH (…)</c> clause of CREATE INDEX and REBUILD skips a
    /// name it doesn't know, where real's refuses it with its own Msg 155.
    /// </para>
    /// <para>
    /// Setting <c>IGNORE_DUP_KEY</c> is narrower than declaring it, and each
    /// rejection was probe-confirmed against SQL Server 2025:
    /// a non-unique index raises <b>Msg 1915</b> (the CREATE path's equivalent is
    /// a different number, 1916, with different wording); a filtered index
    /// raises <b>Msg 10618</b> with the verb <c>alter</c> where CREATE says
    /// <c>create</c>; and the index backing a PRIMARY KEY / UNIQUE constraint
    /// raises <b>Msg 1979</b> — real accepts the option in such a constraint's
    /// own declaration but refuses to change it afterwards.
    /// <c>ALTER INDEX ALL</c> fans out over every index on the table and aborts
    /// on the first that refuses, so a table carrying a constraint-backed index
    /// can't have the option set table-wide.
    /// </para>
    /// <para>
    /// <c>REORGANIZE</c> has nothing to compact in a flat page list, so it
    /// validates and succeeds. Its own <c>WITH (…)</c> block takes
    /// <c>LOB_COMPACTION</c> and <c>COMPRESS_ALL_ROW_GROUPS</c> — real accepts
    /// the columnstore option on a rowstore index — and refuses anything else
    /// with a REORGANIZE-flavoured <b>Msg 155</b>, a non-<c>ON</c>/<c>OFF</c>
    /// value with <b>Msg 153</b>. A disabled index refuses REORGANIZE with
    /// <b>Msg 1973</b> where <c>ALL</c> skips over it, matching real.
    /// </para>
    /// <para>
    /// <c>RESUME</c> / <c>PAUSE</c> / <c>ABORT</c> address a paused resumable
    /// index build. The simulator never starts one — every index is built in
    /// place — so the whole model is real's own refusal: <b>Msg 10638</b> for a
    /// named index (State 1 for RESUME, 2 for PAUSE and ABORT) and
    /// <b>Msg 10680</b> at Level 11 for <c>ALL</c>, both raised after the table
    /// and index have resolved, and neither caring whether the index is
    /// disabled.
    /// </para>
    /// </remarks>
    private static bool TryParseAlterIndex(ParserContext context)
    {
        // ALL is a reserved keyword; a named index is an ordinary identifier.
        var alterAll = context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.All };
        string? indexName = null;
        if (!alterAll)
        {
            if (context.Token is not Name named)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            indexName = named.Value;
        }

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var tableName = BatchContext.ParseObjectName(context);
        var targetColumnstore = TargetsColumnstoreIndex(context, tableName, indexName);
        var targetsJson = TargetsJsonIndex(context, tableName, indexName);
        context.MoveNextRequired();

        var form = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Set } => AlterIndexForm.Set,
            UnquotedString { ContextualKeyword: ContextualKeyword.Disable } => AlterIndexForm.Disable,
            UnquotedString { ContextualKeyword: ContextualKeyword.Rebuild } => AlterIndexForm.Rebuild,
            UnquotedString { ContextualKeyword: ContextualKeyword.Reorganize } => AlterIndexForm.Reorganize,
            UnquotedString { ContextualKeyword: ContextualKeyword.Resume } => AlterIndexForm.Resume,
            UnquotedString { ContextualKeyword: ContextualKeyword.Pause } => AlterIndexForm.Pause,
            UnquotedString { ContextualKeyword: ContextualKeyword.Abort } => AlterIndexForm.Abort,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

        bool? ignoreDupKey = null;
        int? compressionDelay = null;
        var rebuildOptions = default(IndexOptions);
        Expression? partitionNumber = null;
        JsonRebuildRefusal? jsonRefusal = null;
        var compressAllRowGroups = false;
        switch (form)
        {
            case AlterIndexForm.Set:
                (ignoreDupKey, compressionDelay, rebuildOptions) = ParseAlterIndexSetOptions(context, targetColumnstore);
                break;
            case AlterIndexForm.Pause:
            case AlterIndexForm.Abort:
                // Neither takes a PARTITION clause or an option block; real
                // reports the trailing WITH as Msg 319.
                context.MoveNextOptional();
                break;
            case AlterIndexForm.Resume:
                // RESUME's WITH (…) carries the resumption controls
                // (MAX_DURATION / MAXDOP / WAIT_AT_LOW_PRIORITY). There is
                // nothing to resume, so the block is validated by name and
                // discarded.
                context.MoveNextOptional();
                ParseOptionalResumeWithClause(context);
                break;
            case AlterIndexForm.Reorganize:
                context.MoveNextOptional();
                partitionNumber = ParseOptionalIndexPartitionClause(context);
                compressAllRowGroups = ParseOptionalReorganizeWithClause(context);
                break;
            default:
                // REBUILD takes an optional PARTITION = ALL and its own WITH (…)
                // option block; neither describes anything a heap has.
                context.MoveNextOptional();
                var partitionAll = IsPartitionAll(context);
                partitionNumber = ParseOptionalIndexPartitionClause(context);
                if (partitionNumber is not null)
                    RejectSinglePartitionRebuildOptions(context);
                // An option list without its WITH is a syntax error at the
                // first option (probed 2026-10-05 against SQL Server 2025).
                if (context.Token is Operator { Character: '(' })
                {
                    context.MoveNextRequired();
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                jsonRefusal = targetsJson ? new JsonRebuildRefusal() : null;
                rebuildOptions = ParseOptionalIndexWithClause(
                    context,
                    targetsJson ? IndexOptionStatement.RebuildJsonIndex : targetColumnstore == true ? IndexOptionStatement.RebuildColumnstoreIndex : IndexOptionStatement.AlterIndexRebuild,
                    indexName,
                    jsonRefusal);
                // A single-partition rebuild may list partitions too (probed
                // 2026-10-05 against SQL Server 2025).
                if (rebuildOptions.CompressionOnPartitions && !partitionAll && partitionNumber is null)
                    throw SimulatedSqlException.CompressionPartitionsWithoutPartitionAll();
                break;
        }

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
        {
            if (context.Batch.TryResolveView(tableName, out var indexedView) && indexedView.Indexes.Count > 0)
            {
                AlterViewIndexes(context, indexedView, alterAll ? null : indexName, form, tableName);
                return true;
            }
            throw SimulatedSqlException.CannotFindObjectForAlterIndex(tableName.ToString());
        }
        RejectOnMemoryOptimized(table, "The operation 'ALTER INDEX'", 8);
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        RecordTableDdlUndo(context, table);
        // ALTER INDEX is gated on ALTER of the parent table — the same Msg 1088
        // state 9 a missing table earns (probe-confirmed).
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(table), table.ObjectId, table.SchemaId))
            throw SimulatedSqlException.CannotFindObjectForAlterIndex(tableName.ToString());

        // A named index has to resolve against the table's own indexes or its
        // key constraints — a constraint name is a legal ALTER INDEX target,
        // which is how Msg 1979 becomes reachable.
        var collation = context.Batch.CurrentDatabase.Collation;
        if (!alterAll)
        {
            foreach (var constraint in table.KeyConstraints)
            {
                if (collation.Equals(constraint.Name, indexName))
                {
                    var constraintPartition = RejectNamedIndexTarget(context.Batch, form, partitionNumber, constraint.IsClustered ? table.Partitioning : constraint.Partitioning, constraint.Name, table.Name, constraint.IsDisabled, tableName.ToString());
                    ApplyToConstraint(table, constraint, form, ignoreDupKey, rebuildOptions, context.Batch, tableName.ToString(), constraintPartition);
                    RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), indexName!, "INDEX", table.Name, "TABLE");
                    return true;
                }
            }

            foreach (var index in table.Indexes)
            {
                if (collation.Equals(index.Name, indexName))
                {
                    var indexPartition = RejectNamedIndexTarget(context.Batch, form, partitionNumber, index.IsClustered ? table.Partitioning : index.Partitioning, index.Name, table.Name, index.IsDisabled, tableName.ToString());
                    ApplyToIndex(context, table, index, form, ignoreDupKey, compressionDelay, rebuildOptions, tableName.ToString(), indexPartition);
                    RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), indexName!, "INDEX", table.Name, "TABLE");
                    return true;
                }
            }

            // A JSON index takes DISABLE, REBUILD and REORGANIZE (probed
            // 2026-09-27 and 2026-09-30 against SQL Server 2025) and refuses
            // SET and the resumable forms, a partition number, and the REBUILD
            // options that mean nothing to it.
            foreach (var jsonIndex in table.JsonIndexes)
            {
                if (collation.Equals(jsonIndex.Name, indexName))
                {
                    if (form == AlterIndexForm.Set && jsonIndex.IsDisabled)
                        throw SimulatedSqlException.OperationOnDisabledIndex(jsonIndex.Name, tableName.ToString());
                    if (form is AlterIndexForm.Set or AlterIndexForm.Resume or AlterIndexForm.Pause or AlterIndexForm.Abort)
                        throw SimulatedSqlException.JsonIndexAlterOptionsInvalid();
                    if (partitionNumber is not null)
                        throw SimulatedSqlException.PartitionNumberOnJsonIndex(jsonIndex.Name);
                    if (jsonRefusal is { State: not 0 })
                        throw SimulatedSqlException.InvalidJsonIndexRebuildOption(jsonRefusal.Name, jsonRefusal.State);
                    if (form == AlterIndexForm.Reorganize)
                    {
                        if (jsonIndex.IsDisabled)
                            throw SimulatedSqlException.OperationOnDisabledIndex(jsonIndex.Name, tableName.ToString());
                        if (compressAllRowGroups)
                            throw SimulatedSqlException.CompressAllRowGroupsNeedsColumnstore();
                    }
                    if (form is AlterIndexForm.Disable or AlterIndexForm.Rebuild)
                        jsonIndex.IsDisabled = form == AlterIndexForm.Disable;
                    RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), indexName!, "INDEX", table.Name, "TABLE");
                    return true;
                }
            }

            // A hypothetical index takes every form and nothing changes but
            // its disabled flag (probed 2026-10-02 and 2026-10-05 against SQL
            // Server 2025).
            if (table.HypotheticalIndexes.Find(hypothetical => collation.Equals(hypothetical.Name, indexName)) is { } hypotheticalIndex)
            {
                if (form is AlterIndexForm.Disable or AlterIndexForm.Rebuild)
                    hypotheticalIndex.IsDisabled = form == AlterIndexForm.Disable;
                return true;
            }

            // Real takes no ALTER INDEX form on a vector index (probed
            // 2026-09-29 against SQL Server 2025).
            if (table.VectorIndexes.Exists(vectorIndex => collation.Equals(vectorIndex.Name, indexName)))
                throw SimulatedSqlException.VectorIndexAlterUnsupported();

            throw SimulatedSqlException.CannotFindIndex(indexName!);
        }

        // ALL: the resumable forms never look at an individual index — real
        // raises its own ALL-flavoured refusal even for a table carrying no
        // index at all (probe-confirmed on a bare heap).
        if (form is AlterIndexForm.Resume or AlterIndexForm.Pause or AlterIndexForm.Abort)
            throw SimulatedSqlException.NoPendingResumableIndexOperationForAll(FormName(form), table.Name);

        // ALTER INDEX ALL reaches a table's vector index too, and refuses it.
        if (table.VectorIndexes.Count > 0)
            throw SimulatedSqlException.VectorIndexAlterUnsupported();
        long? allPartition = null;
        if (partitionNumber is not null)
        {
            // Real names the first index the statement would have touched —
            // index_id order, so a constraint's clustered index first — and
            // falls back to the table when there is none.
            var targets = table.IndexIdentities().FindAll(identity => !identity.IsHeap);
            var firstTarget = targets.Find(_ => true);
            if (firstTarget.Name is null || PlacementOf(table, firstTarget) is not { } firstPlacement)
                throw SimulatedSqlException.RebuildPartitionOnUnpartitioned(alterIndex: true, firstTarget.Name, table.Name);
            allPartition = ReadPartitionNumber(context.Batch, partitionNumber, "ALTER INDEX", "index", firstTarget.Name);
            RejectPartitionNumber(allPartition.Value, firstPlacement, firstTarget.Name, table.Name, form == AlterIndexForm.Reorganize);
            // A partition of every index needs every index partitioned
            // (probed 2026-10-05 against SQL Server 2025).
            if (targets.Find(identity => PlacementOf(table, identity) is null) is { Name: { } unaligned })
                throw SimulatedSqlException.AlterIndexAllUnalignedIndex(firstTarget.Name, unaligned);
        }

        // ALL: constraints first, matching real's abort-on-first-refusal — a
        // constraint-backed index is present in every table that has a key, so
        // an IGNORE_DUP_KEY set over ALL raises Msg 1979 before touching
        // anything. Nothing is mutated before every target has been accepted.
        foreach (var constraint in table.KeyConstraints)
        {
            // REORGANIZE over ALL steps past a disabled index where naming it
            // would be Msg 1973 (probe-confirmed).
            if (form == AlterIndexForm.Reorganize && constraint.IsDisabled)
                continue;
            ApplyToConstraint(table, constraint, form, ignoreDupKey, rebuildOptions, context.Batch, tableName.ToString(), allPartition);
        }
        foreach (var index in table.Indexes)
        {
            if (form == AlterIndexForm.Reorganize && index.IsDisabled)
                continue;
            ApplyToIndex(context, table, index, form, ignoreDupKey, compressionDelay, rebuildOptions, tableName.ToString(), allPartition);
        }
        // A table with no clustered index has the heap row index_id 0 stands for,
        // and ALL moves its locking options with the rest (probed 2026-09-30).
        if (form is AlterIndexForm.Set or AlterIndexForm.Rebuild && table.IndexIdentities().Exists(static identity => identity.IsHeap))
        {
            table.HeapAllowRowLocks = rebuildOptions.AllowRowLocks ?? table.HeapAllowRowLocks;
            table.HeapAllowPageLocks = rebuildOptions.AllowPageLocks ?? table.HeapAllowPageLocks;
        }
        // A JSON index takes no SET, and ALL reaches it after the relational ones.
        if (form == AlterIndexForm.Set && table.JsonIndexes.Count > 0)
            throw SimulatedSqlException.JsonIndexAlterOptionsInvalid();
        RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(tableName), table.Name, "INDEX", table.Name, "TABLE");
        return true;
    }

    /// <summary>
    /// Raises the refusals a named <c>ALTER INDEX</c> target earns once it has
    /// resolved: the partition clause on an unpartitioned index (Msg 7729) and
    /// the resumable forms' Msg 10638.
    /// </summary>
    private static long? RejectNamedIndexTarget(BatchContext batch, AlterIndexForm form, Expression? partitionNumber, Schemas.PartitionPlacement? placement, string indexName, string tableName, bool disabled, string writtenTableName)
    {
        long? number = null;
        if (partitionNumber is not null)
        {
            number = ReadPartitionNumber(batch, partitionNumber, "ALTER INDEX", "index", indexName);
            RejectPartitionNumber(number.Value, placement, indexName, tableName, form == AlterIndexForm.Reorganize);
            // Rebuilding one partition doesn't re-enable a disabled index
            // (probed 2026-10-05 against SQL Server 2025).
            if (disabled && form == AlterIndexForm.Rebuild)
                throw SimulatedSqlException.OperationOnDisabledIndex(indexName, writtenTableName);
        }
        if (form is AlterIndexForm.Resume or AlterIndexForm.Pause or AlterIndexForm.Abort)
            throw SimulatedSqlException.NoPendingResumableIndexOperation(FormName(form), indexName, tableName);
        return number;
    }

    /// <summary>
    /// Applies a rebuild's <c>DATA_COMPRESSION</c> to one rowset (probed
    /// 2026-10-05 against SQL Server 2025): a rebuild of one partition sets
    /// that partition's level — the whole-object level, else a clause listing
    /// it, else nothing — and a rebuild of every partition sets the level of
    /// all, or of the partitions its <c>ON PARTITIONS</c> clauses list.
    /// </summary>
    internal static void ApplyRebuildCompression(ref byte level, ref List<byte>? partitions, IndexOptions options, long? partition, Schemas.PartitionPlacement? placement, string name, string kind)
    {
        if (partition is { } number && placement is not null)
        {
            var chosen = options.DataCompression;
            foreach (var clause in options.PartitionCompressions ?? [])
            {
                if (clause.Ranges.Exists(range => range.Low <= number && number <= range.High))
                    chosen = clause.Level;
            }
            if (chosen is { } partitionLevel)
            {
                partitions = PartitionCompression.Apply(level, partitions, placement.Fanout, [], static _ => null!);
                partitions[(int)number - 1] = partitionLevel;
            }
            return;
        }
        if (options.DataCompression is { } whole)
        {
            level = whole;
            partitions = null;
        }
        if (options.PartitionCompressions is { } clauses && placement is not null)
        {
            var fanout = placement.Fanout;
            partitions = PartitionCompression.Apply(level, partitions, fanout, clauses,
                outOfRange => SimulatedSqlException.InvalidPartitionNumber(outOfRange, name, fanout, kind));
        }
    }

    private static string FormName(AlterIndexForm form) => form switch
    {
        AlterIndexForm.Abort => "ABORT",
        AlterIndexForm.Pause => "PAUSE",
        _ => "RESUME",
    };

    /// <summary>
    /// <c>ALTER INDEX</c> on an indexed view: <c>DISABLE</c> takes an index
    /// out of service — the clustered one every index on the view, so
    /// <c>NOEXPAND</c> no longer reads it and its uniqueness goes unchecked —
    /// and <c>REBUILD</c> puts it back; the other forms change nothing a read
    /// shows (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static void AlterViewIndexes(ParserContext context, Schemas.View view, string? indexName, AlterIndexForm form, MultiPartName writtenName)
    {
        if (!PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(view), view.ObjectId, view.SchemaId))
            throw SimulatedSqlException.CannotFindObjectForAlterIndex(writtenName.ToString());
        var targets = indexName is null
            ? view.Indexes
            : view.Indexes.FindAll(index => context.Batch.CurrentDatabase.Collation.Equals(index.Name, indexName));
        if (targets.Count == 0)
            throw SimulatedSqlException.CannotFindObjectForAlterIndex(writtenName.ToString());
        var before = view.Indexes.ConvertAll(static index => (index, index.IsDisabled));
        if (form == AlterIndexForm.Disable)
        {
            foreach (var index in targets.Exists(static index => index.IsClustered) ? view.Indexes : targets)
                index.IsDisabled = true;
        }
        else if (form == AlterIndexForm.Rebuild)
        {
            foreach (var index in targets)
                index.IsDisabled = false;
        }
        RecordDdlUndo(context, () =>
        {
            foreach (var (index, wasDisabled) in before)
                index.IsDisabled = wasDisabled;
        });
        RecordDdlEvent(context, "ALTER_INDEX", EventSchemaName(writtenName), indexName ?? "ALL", "INDEX", view.Name, "VIEW");
    }

    private enum AlterIndexForm
    {
        Set,
        Disable,
        Rebuild,
        Reorganize,
        Resume,
        Pause,
        Abort,
    }

    /// <summary>
    /// Applies the form to a PRIMARY KEY / UNIQUE constraint's backing index.
    /// DISABLE and REBUILD are allowed here — real permits taking a constraint's
    /// index out of service, and while it's out the constraint isn't enforced at
    /// all (probe-confirmed) — but changing <c>IGNORE_DUP_KEY</c> is not
    /// (Msg 1979). A SET that doesn't mention that option is a no-op rather than
    /// an error, so <c>ALTER INDEX ALL … SET (ALLOW_ROW_LOCKS = ON)</c> still
    /// succeeds on a table carrying a PRIMARY KEY.
    /// </summary>
    private static void ApplyToConstraint(
        HeapTable table, KeyConstraint constraint, AlterIndexForm form, bool? ignoreDupKey, IndexOptions rebuildOptions, BatchContext batch, string writtenTableName, long? partition = null)
    {
        switch (form)
        {
            case AlterIndexForm.Disable:
                constraint.IsDisabled = true;
                DisableForeignKeysOn(batch, table, constraint.IndexId, constraint.Name);
                if (constraint.IsClustered)
                    DisableNonclusteredWithClustered(batch, table);
                break;
            case AlterIndexForm.Rebuild:
                // A rebuild may not touch a key's IGNORE_DUP_KEY either
                // (probed 2026-10-05 against SQL Server 2025).
                if (rebuildOptions.IgnoreDupKeyWritten)
                    throw SimulatedSqlException.IgnoreDupKeyOnConstraintIndex(constraint.Name);
                if (constraint.IsDisabled)
                    ValidateExistingRowsForKeyConstraint(table, constraint, batch);
                constraint.IsDisabled = false;
                constraint.FillFactor = rebuildOptions.FillFactor ?? constraint.FillFactor;
                constraint.IsPadded = rebuildOptions.PadIndex ?? constraint.IsPadded;
                constraint.AllowRowLocks = rebuildOptions.AllowRowLocks ?? constraint.AllowRowLocks;
                constraint.AllowPageLocks = rebuildOptions.AllowPageLocks ?? constraint.AllowPageLocks;
                constraint.StatisticsNoRecompute = rebuildOptions.StatisticsNoRecompute ?? constraint.StatisticsNoRecompute;
                ApplyRebuildCompression(ref constraint.DataCompression, ref constraint.PartitionDataCompression, rebuildOptions, partition,
                    constraint.IsClustered ? table.Partitioning : constraint.Partitioning, constraint.Name, "index");
                constraint.XmlCompression = rebuildOptions.XmlCompression ?? constraint.XmlCompression;
                // A rebuild rebuilds the index's statistic from every row.
                BuildStatistics(batch, table, statistic => ReferenceEquals(statistic.State, constraint.Statistics));
                break;
            case AlterIndexForm.Reorganize:
                // Nothing to compact in a flat page list, but a disabled index
                // still refuses the operation.
                if (constraint.IsDisabled)
                    throw SimulatedSqlException.OperationOnDisabledIndex(constraint.Name, writtenTableName);
                break;
            default:
                if (constraint.IsDisabled)
                    throw SimulatedSqlException.OperationOnDisabledIndex(constraint.Name, writtenTableName);
                if (ignoreDupKey is not null)
                    throw SimulatedSqlException.IgnoreDupKeyOnConstraintIndex(constraint.Name);
                constraint.AllowRowLocks = rebuildOptions.AllowRowLocks ?? constraint.AllowRowLocks;
                constraint.AllowPageLocks = rebuildOptions.AllowPageLocks ?? constraint.AllowPageLocks;
                constraint.OptimizeForSequentialKey = rebuildOptions.OptimizeForSequentialKey ?? constraint.OptimizeForSequentialKey;
                constraint.StatisticsNoRecompute = rebuildOptions.StatisticsNoRecompute ?? constraint.StatisticsNoRecompute;
                break;
        }
    }

    /// <summary>
    /// Takes every FOREIGN KEY resting on index <paramref name="indexId"/> of
    /// <paramref name="table"/> out of service, as disabling the index does on
    /// real, with a Msg 1992 warning apiece. The key stays disabled and
    /// untrusted until re-enabled with <c>CHECK CONSTRAINT</c>, whatever later
    /// rebuilds the index (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static void DisableForeignKeysOn(BatchContext batch, HeapTable table, int indexId, string indexName)
    {
        foreach (var fk in table.IncomingForeignKeys)
        {
            if (fk.IsDisabled || BuiltInResources.ResolveForeignKeyIndexId(fk) != indexId)
                continue;
            fk.IsDisabled = true;
            fk.IsNotTrusted = true;
            if (!batch.IsSkipping)
                batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.ForeignKeyDisabledWithIndexMessage(batch, fk.Name, fk.ChildTable.Name, table.Name, indexName));
        }
    }

    /// <summary>
    /// Disables every nonclustered index and key of <paramref name="table"/>
    /// along with its clustered index, which holds the rows they point into,
    /// with a Msg 3750 apiece after the foreign keys' Msg 1992 — in index id
    /// order. Rebuilding the clustered index alone leaves them disabled
    /// (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    private static void DisableNonclusteredWithClustered(BatchContext batch, HeapTable table)
    {
        foreach (var identity in table.IndexIdentities())
        {
            switch (identity)
            {
                case { Constraint: { IsClustered: false, IsDisabled: false } key }:
                    key.IsDisabled = true;
                    DisableForeignKeysOn(batch, table, key.IndexId, key.Name);
                    break;
                case { Index: { IsClustered: false, IsDisabled: false, IsColumnstore: false } index }:
                    index.IsDisabled = true;
                    DisableForeignKeysOn(batch, table, index.IndexId, index.Name);
                    break;
                default:
                    continue;
            }
            if (!batch.IsSkipping)
                batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.IndexDisabledWithClusteredMessage(batch, identity.Name!, table.Name));
        }
    }

    private static void ApplyToIndex(
        ParserContext context, HeapTable table, Storage.Index index, AlterIndexForm form, bool? ignoreDupKey, int? compressionDelay, IndexOptions rebuildOptions, string writtenTableName, long? partition = null)
    {
        switch (form)
        {
            case AlterIndexForm.Disable:
                index.IsDisabled = true;
                table.SettleIndexIds();
                DisableForeignKeysOn(context.Batch, table, index.IndexId, index.Name);
                if (index.IsClustered)
                    DisableNonclusteredWithClustered(context.Batch, table);
                break;
            case AlterIndexForm.Rebuild:
                // A rebuild sets IGNORE_DUP_KEY as SET does, refusing it on a
                // non-unique index (probed 2026-10-05 against SQL Server 2025).
                if (rebuildOptions.IgnoreDupKeyWritten)
                {
                    if (!index.IsUnique)
                        throw SimulatedSqlException.IgnoreDupKeyOnNonUniqueIndexAlter(index.Name);
                    if (index.Filter is not null && rebuildOptions.IgnoreDupKey)
                        throw SimulatedSqlException.IgnoreDupKeyOnFilteredIndex("alter", index.Name, SchemaQualifyTableName(table, context.CurrentDatabase));
                    index.IgnoreDupKey = rebuildOptions.IgnoreDupKey;
                }
                // Rows that accumulated while the index was out of service are
                // re-validated on the way back in, exactly as a fresh CREATE
                // UNIQUE INDEX would be: Msg 1505 on a duplicate. A REBUILD of an
                // index that was never disabled is a no-op success.
                if (index.IsDisabled && index.IsUnique)
                {
                    ValidateExistingRowsForUniqueIndex(
                        table, index, context.Batch, QualifiedForViolation(table));
                }
                index.IsDisabled = false;
                // A rebuild keeps the FILLFACTOR / PAD_INDEX its WITH leaves
                // out (probed 2026-09-26 against SQL Server 2025).
                index.FillFactor = rebuildOptions.FillFactor ?? index.FillFactor;
                index.IsPadded = rebuildOptions.PadIndex ?? index.IsPadded;
                index.AllowRowLocks = rebuildOptions.AllowRowLocks ?? index.AllowRowLocks;
                index.AllowPageLocks = rebuildOptions.AllowPageLocks ?? index.AllowPageLocks;
                index.StatisticsNoRecompute = rebuildOptions.StatisticsNoRecompute ?? index.StatisticsNoRecompute;
                index.ColumnstoreArchive = rebuildOptions.ColumnstoreArchive ?? index.ColumnstoreArchive;
                ApplyRebuildCompression(ref index.DataCompression, ref index.PartitionDataCompression, rebuildOptions, partition,
                    index.IsClustered ? table.Partitioning : index.Partitioning, index.Name, "index");
                index.XmlCompression = rebuildOptions.XmlCompression ?? index.XmlCompression;
                if (!index.IsColumnstore)
                    BuildStatistics(context.Batch, table, statistic => ReferenceEquals(statistic.State, index.Statistics));
                break;
            case AlterIndexForm.Reorganize:
                if (index.IsDisabled)
                    throw SimulatedSqlException.OperationOnDisabledIndex(index.Name, writtenTableName);
                break;
            default:
                if (index.IsDisabled)
                    throw SimulatedSqlException.OperationOnDisabledIndex(index.Name, writtenTableName);
                if (compressionDelay is int delay && index.IsColumnstore)
                    index.CompressionDelay = delay;
                if (ignoreDupKey is bool value)
                {
                    if (!index.IsUnique)
                        throw SimulatedSqlException.IgnoreDupKeyOnNonUniqueIndexAlter(index.Name);
                    if (index.Filter is not null)
                        throw SimulatedSqlException.IgnoreDupKeyOnFilteredIndex("alter", index.Name, SchemaQualifyTableName(table, context.CurrentDatabase));
                    index.IgnoreDupKey = value;
                }
                index.AllowRowLocks = rebuildOptions.AllowRowLocks ?? index.AllowRowLocks;
                index.AllowPageLocks = rebuildOptions.AllowPageLocks ?? index.AllowPageLocks;
                index.OptimizeForSequentialKey = rebuildOptions.OptimizeForSequentialKey ?? index.OptimizeForSequentialKey;
                index.StatisticsNoRecompute = rebuildOptions.StatisticsNoRecompute ?? index.StatisticsNoRecompute;
                break;
        }
    }

    /// <summary>
    /// Parses the <c>SET ( option = value [, …] )</c> list, returning the
    /// <c>IGNORE_DUP_KEY</c> setting when the list carried one and
    /// <see langword="null"/> when it didn't — the distinction matters, because
    /// only a list that mentions the option can raise the constraint / non-unique
    /// / filtered rejections — with the locking options it records and the
    /// columnstore <c>COMPRESSION_DELAY</c>. Every other recognized option is
    /// discarded.
    /// Cursor on entry: the <c>SET</c> keyword. On exit: first token past the
    /// closing <c>)</c>.
    /// </summary>
    private static (bool? IgnoreDupKey, int? CompressionDelay, IndexOptions LockOptions) ParseAlterIndexSetOptions(ParserContext context, bool? targetColumnstore)
    {
        bool? allowRowLocks = null, allowPageLocks = null, optimizeForSequentialKey = null, statisticsNoRecompute = null;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        bool? ignoreDupKey = null;
        int? compressionDelay = null;
        while (true)
        {
            // An empty list is a syntax error on real, so a name is required.
            // Read it off the raw source rather than as an identifier: FILLFACTOR
            // is a reserved keyword, so it arrives as a ReservedKeyword while
            // every other option name is an ordinary unquoted identifier.
            if (context.GetNextRequired() is not (StringToken or ReservedKeyword))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var optionName = context.Token!.Source.ToString();
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var value = context.GetNextRequired();
            if (OnOffIndexOptions.Contains(optionName))
            {
                var on = ReadOnOffOptionValue(context, value, optionName);
                if (IgnoreDupKeyOption.Equals(optionName, StringComparison.OrdinalIgnoreCase))
                {
                    ignoreDupKey = on;
                }
                else if (targetColumnstore != true && optionName.Equals("ALLOW_ROW_LOCKS", StringComparison.OrdinalIgnoreCase))
                {
                    allowRowLocks = on;
                }
                else if (targetColumnstore != true && optionName.Equals("ALLOW_PAGE_LOCKS", StringComparison.OrdinalIgnoreCase))
                {
                    allowPageLocks = on;
                }
                else if (targetColumnstore != true && optionName.Equals("OPTIMIZE_FOR_SEQUENTIAL_KEY", StringComparison.OrdinalIgnoreCase))
                {
                    optimizeForSequentialKey = on;
                }
                else if (targetColumnstore != true && optionName.Equals("STATISTICS_NORECOMPUTE", StringComparison.OrdinalIgnoreCase))
                {
                    statisticsNoRecompute = on;
                }
                // A columnstore index refuses the locking and statistics
                // options as its rebuild does (probed 2026-09-26 against SQL
                // Server 2025).
                else if (targetColumnstore == true)
                {
                    var upper = optionName.ToUpperInvariant();
                    throw ColumnstoreRefusedLockOptions.Contains(upper)
                        ? SimulatedSqlException.ColumnstoreRebuildLockOption(upper)
                        : SimulatedSqlException.ColumnstoreRebuildOption(upper);
                }
            }
            else if (NumericIndexOptions.Contains(optionName))
            {
                if (value is not Numeric { Value: var delay })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (targetColumnstore == false)
                    throw SimulatedSqlException.CompressionDelayOnRowstoreIndex();
                compressionDelay = delay.AsInt32 > 10080 ? throw SimulatedSqlException.CompressionDelayOutOfRange(delay.AsInt32) : delay.AsInt32;
            }
            else
            {
                throw SimulatedSqlException.UnrecognizedAlterIndexOption(optionName, NonSettableIndexOptions.Contains(optionName));
            }

            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return (ignoreDupKey, compressionDelay, new IndexOptions(false, null, null, allowRowLocks: allowRowLocks, allowPageLocks: allowPageLocks, optimizeForSequentialKey: optimizeForSequentialKey, statisticsNoRecompute: statisticsNoRecompute));
    }

    /// <summary>
    /// Whether the index an <c>ALTER INDEX</c> names — or, for <c>ALL</c>, any
    /// of the table's — is a columnstore one, whose option rules differ; null
    /// when the statement is being skipped or its target doesn't resolve, which
    /// the statement reports on its own.
    /// </summary>
    private static bool? TargetsColumnstoreIndex(ParserContext context, MultiPartName tableName, string? indexName)
    {
        if (context.Batch.IsSkipping || !context.Batch.TryResolveTable(tableName, out var table))
            return null;
        var collation = context.Batch.CurrentDatabase.Collation;
        if (indexName is null)
            return table.Indexes.Exists(index => index.IsColumnstore);
        foreach (var index in table.Indexes)
        {
            if (collation.Equals(index.Name, indexName))
                return index.IsColumnstore;
        }
        return table.KeyConstraints.Exists(key => collation.Equals(key.Name, indexName)) ? false : null;
    }

    /// <summary>
    /// Whether the named index is a JSON index, or — for <c>ALL</c> — the table
    /// has one; false while the batch is skipping or the table doesn't resolve.
    /// </summary>
    private static bool TargetsJsonIndex(ParserContext context, MultiPartName tableName, string? indexName)
    {
        if (context.Batch.IsSkipping || !context.Batch.TryResolveTable(tableName, out var table))
            return false;
        var collation = context.Batch.CurrentDatabase.Collation;
        return indexName is null ? table.JsonIndexes.Count > 0 : table.JsonIndexes.Exists(index => collation.Equals(index.Name, indexName));
    }

    /// <summary>
    /// <c>ALTER INDEX … REORGANIZE WITH (…)</c> options. Both take
    /// <c>ON</c> / <c>OFF</c>; real accepts the columnstore-shaped
    /// <c>COMPRESS_ALL_ROW_GROUPS</c> on a rowstore index without complaint
    /// (probe-confirmed), so neither name is gated on the index kind.
    /// </summary>
    private static readonly FrozenSet<string> ReorganizeOptions = new[]
    {
        "COMPRESS_ALL_ROW_GROUPS",
        "LOB_COMPACTION",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>ALTER INDEX … RESUME WITH (…)</c> options. Recognized by name and
    /// discarded — there is never an operation to resume, so nothing reads
    /// them.
    /// </summary>
    private static readonly FrozenSet<string> ResumeOptions = new[]
    {
        "MAXDOP",
        "MAX_DURATION",
        "WAIT_AT_LOW_PRIORITY",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The options a rebuild of one partition takes (probed 2026-10-05 against SQL Server 2025).</summary>
    private static readonly FrozenSet<string> SinglePartitionRebuildOptions = new[]
    {
        "DATA_COMPRESSION", "MAX_DURATION", "MAXDOP", "ONLINE", "RESUMABLE", "SORT_IN_TEMPDB", "XML_COMPRESSION",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses a known index option a rebuild of one partition doesn't take,
    /// with Msg 155 in its <c>ALTER INDEX REBUILD PARTITION</c> wording. Cursor
    /// on entry: the token past the partition clause, where a <c>WITH</c> may
    /// stand; left there.
    /// </summary>
    private static void RejectSinglePartitionRebuildOptions(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return;
        var checkpoint = context.SaveCheckpoint();
        try
        {
            if (!context.MoveNext() || context.Token is not Operator { Character: '(' })
                return;
            var depth = 1;
            var expectName = true;
            while (depth > 0 && context.MoveNext())
            {
                if (expectName && depth == 1 && context.Token is StringToken or ReservedKeyword)
                {
                    var name = context.Token.Source.ToString();
                    if (IndexOptionNames.Contains(name) && !SinglePartitionRebuildOptions.Contains(name))
                        throw SimulatedSqlException.UnrecognizedIndexOption(name, "ALTER INDEX REBUILD PARTITION");
                }
                expectName = depth == 1 && context.Token is Operator { Character: ',' };
                switch (context.Token)
                {
                    case Operator { Character: '(' }:
                        depth++;
                        break;
                    case Operator { Character: ')' }:
                        depth--;
                        break;
                }
            }
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>Whether the cursor sits on <c>PARTITION = ALL</c>, leaving it there.</summary>
    private static bool IsPartitionAll(ParserContext context)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Partition })
            return false;
        var checkpoint = context.SaveCheckpoint();
        var all = context.MoveNext() && context.Token is Operator { Character: '=' }
            && context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.All };
        context.RestoreCheckpoint(checkpoint);
        return all;
    }

    /// <summary>
    /// Parses the optional <c>PARTITION = { ALL | &lt;expression&gt; }</c>
    /// clause shared by <c>REBUILD</c> and <c>REORGANIZE</c>, returning the
    /// partition-number expression when one was named, which the caller reads
    /// through <see cref="ReadPartitionNumber"/> once it knows what to name.
    /// Cursor on entry: the first token past the form keyword. On exit: the
    /// first token past the clause.
    /// </summary>
    private static Expression? ParseOptionalIndexPartitionClause(ParserContext context)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Partition })
            return null;
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.All })
        {
            context.MoveNextOptional();
            return null;
        }
        return Expression.Parse(context);
    }

    /// <summary>
    /// The value of a partition-number expression in <c>ALTER INDEX</c>,
    /// <c>ALTER TABLE … REBUILD</c> or <c>ALTER TABLE … SWITCH</c>: an
    /// expression not of an integer type (<c>bit</c>, a decimal and a NULL
    /// literal included) is Msg 4957, and a number outside 1 to 15,000 Msg 7722,
    /// both before anything looks at the object's partitions (probed
    /// 2026-10-05 against SQL Server 2025). <paramref name="statement"/> and
    /// <paramref name="kind"/> word the refusals: <c>ALTER INDEX</c> naming an
    /// index, or <c>ALTER TABLE</c> naming a table.
    /// </summary>
    internal static long ReadPartitionNumber(BatchContext batch, Expression written, string statement, string kind, string objectName)
    {
        var type = Expression.IsUntypedNullLiteral(written) ? null : written.GetSqlType(batch, NoColumnTypeResolver);
        if (type is not (TinyIntSqlType or SmallIntSqlType or Int32SqlType or BigIntSqlType))
            throw SimulatedSqlException.PartitionNumberNotInteger(statement, kind, objectName);
        var value = written.Run(new RuntimeContext(NoColumnResolver, batch));
        var number = value.IsNull ? 0 : value.CoerceTo(SqlType.BigInt).AsInt64;
        if (number is < 1 or > MaxPartitionBoundaries + 1)
            throw SimulatedSqlException.PartitionNumberOutOfRange(number, kind, objectName);
        return number;
    }

    /// <summary>
    /// Refuses a partition number an index can't take: Msg 7729 for an index
    /// on a filegroup, Msg 7730 for a number past a partitioned one's
    /// partitions — Msg 2586 for <c>REORGANIZE</c> (probed 2026-09-27 and
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    private static void RejectPartitionNumber(long number, Schemas.PartitionPlacement? placement, string indexName, string tableName, bool reorganize = false)
    {
        if (placement is null)
            throw SimulatedSqlException.PartitionNumberOnUnpartitionedIndex(indexName);
        if (number > placement.Fanout)
        {
            throw reorganize
                ? SimulatedSqlException.ReorganizePartitionNotFound(number, indexName, tableName)
                : SimulatedSqlException.AlterIndexPartitionNotFound(number, indexName);
        }
    }

    /// <summary>
    /// Parses <c>REORGANIZE</c>'s own <c>WITH ( option = ON | OFF [, …] )</c>
    /// block. Unlike <c>SET</c>'s, an unrecognized name here reports the
    /// REORGANIZE-flavoured Msg 155 and a non-<c>ON</c>/<c>OFF</c> value reports
    /// Msg 153 rather than a syntax error.
    /// </summary>
    private static bool ParseOptionalReorganizeWithClause(ParserContext context)
    {
        var compressAll = false;
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return compressAll;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        while (true)
        {
            if (context.GetNextRequired() is not (StringToken or ReservedKeyword))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var optionName = context.Token.Source.ToString();
            if (!ReorganizeOptions.Contains(optionName))
                throw SimulatedSqlException.UnrecognizedAlterIndexReorganizeOption(optionName);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } value)
                throw SimulatedSqlException.InvalidUsageOfIndexOption(optionName);
            if (value.Keyword == Keyword.On && optionName.Equals("COMPRESS_ALL_ROW_GROUPS", StringComparison.OrdinalIgnoreCase))
                compressAll = true;

            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return compressAll;
    }

    /// <summary>
    /// Parses <c>RESUME</c>'s <c>WITH ( … )</c> block. Each option's value
    /// grammar differs (<c>MAX_DURATION = &lt;n&gt; [MINUTES]</c>,
    /// <c>MAXDOP = &lt;n&gt;</c>, <c>WAIT_AT_LOW_PRIORITY ( … )</c>), so the
    /// names are validated and the values skipped to the matching close paren.
    /// </summary>
    private static void ParseOptionalResumeWithClause(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var depth = 1;
        var expectingName = true;
        while (depth > 0)
        {
            var token = context.GetNextRequired();
            if (expectingName)
            {
                if (token is not (StringToken or ReservedKeyword))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (!ResumeOptions.Contains(token.Source.ToString()))
                    throw SimulatedSqlException.UnrecognizedAlterIndexOption(token.Source.ToString());
                expectingName = false;
                continue;
            }

            switch (token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: ',' } when depth == 1:
                    expectingName = true;
                    break;
            }
        }

        context.MoveNextOptional();
    }

    /// <summary>
    /// Reads an <c>ON</c> / <c>OFF</c> option value. An integer (signed or not) is Msg 153 naming the option as
    /// written, except for <c>IGNORE_DUP_KEY</c>, whose grammar reads it as a syntax error (probed 2026-09-30
    /// against SQL Server 2025).
    /// </summary>
    private static bool ReadOnOffOptionValue(ParserContext context, Token value, string? optionName = null)
    {
        if (value is ReservedKeyword { Keyword: var keyword } && keyword is Keyword.On or Keyword.Off)
            return keyword == Keyword.On;
        if (optionName is not null && !IgnoreDupKeyOption.Equals(optionName, StringComparison.OrdinalIgnoreCase) && value is Numeric)
            throw SimulatedSqlException.InvalidUsageOfIndexOption(optionName);
        if (optionName is not null && !IgnoreDupKeyOption.Equals(optionName, StringComparison.OrdinalIgnoreCase) && value is Operator { Character: '-' })
        {
            var checkpoint = context.SaveCheckpoint();
            var signed = context.MoveNext() && context.Token is Numeric;
            context.RestoreCheckpoint(checkpoint);
            if (signed)
                throw SimulatedSqlException.InvalidUsageOfIndexOption(optionName);
        }
        throw SimulatedSqlException.SyntaxErrorNear(context);
    }
}
