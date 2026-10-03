using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Storage;

/// <summary>
/// A user table: schema is <see cref="HeapColumn"/>s typed in
/// <see cref="SqlType"/>; rows are stored in an 8KB-page <see cref="Heap"/>
/// whose page bytes are produced by <see cref="RowEncoder"/>.
/// </summary>
[DebuggerDisplay("{DebugDisplay(),nq}")]
internal sealed class HeapTable : SchemaObject
{
    /// <summary>
    /// <c>ALTER TABLE … SET (LOCK_ESCALATION = …)</c> as sys.tables reports
    /// it: 0 TABLE (the default), 1 DISABLE, 2 AUTO.
    /// </summary>
    public byte LockEscalation;

    /// <summary>
    /// The partition scheme and column the table's base rows — the heap, or
    /// the clustered index — are placed on, or null when they sit on a
    /// filegroup. Set by <c>CREATE TABLE … ON scheme(column)</c> or a clustered
    /// index created on one, cleared by a clustered index moved onto a filegroup.
    /// </summary>
    public Schemas.PartitionPlacement? Partitioning;

    /// <summary>
    /// The filegroup (<c>data_space_id</c>) the table's rows — the heap, or the
    /// clustered index — are on when <see cref="Partitioning"/> is null: the
    /// <c>ON</c> clause's, else the database's default filegroup at creation,
    /// moved by a clustered index created on another and kept when one is
    /// dropped.
    /// </summary>
    public int FilegroupId = Database.PrimaryFilegroupId;

    /// <summary>
    /// The filegroup the table's LOB data is on: <c>TEXTIMAGE_ON</c>'s, else
    /// where the rows were at creation, which a clustered index moving the rows
    /// leaves behind (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    public int LobFilegroupId = Database.PrimaryFilegroupId;

    /// <summary>
    /// Whether a column routes through the LOB-page chain
    /// (<see cref="HeapColumn.IsLob"/>) — what gives the table a LOB allocation
    /// unit and a non-zero <c>sys.tables.lob_data_space_id</c>, and what
    /// <c>TEXTIMAGE_ON</c> requires.
    /// </summary>
    public bool HasLobColumn()
    {
        foreach (var column in this.Columns)
        {
            if (column.IsLob)
                return true;
        }
        return false;
    }

    public HeapTable(string name, HeapColumn[] columns, int objectId, int schemaId = Database.DboSchemaId, DateTime createDate = default, KeyConstraint[]? keyConstraints = null, CheckConstraint[]? checkConstraints = null, bool isTableVariable = false, bool isTableValuedParameter = false, (int StartOrdinal, int EndOrdinal)? periodColumns = null)
        : base(name, objectId, schemaId, createDate == default ? DateTime.UtcNow : createDate)
    {
        this.Columns = columns;
        this.KeyConstraints = keyConstraints is null ? [] : [.. keyConstraints];
        this.CheckConstraints = checkConstraints is null ? [] : [.. checkConstraints];
        this.IsTableVariable = isTableVariable;
        this.IsTableValuedParameter = isTableValuedParameter;
        this.PeriodColumns = periodColumns;
        this.TableDataLock.OwningTable = this;

        var storedCount = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].IsStored)
                storedCount++;
        }
        var storedColumns = new HeapColumn[storedCount];
        var schema = new SqlType[storedCount];
        var storageOrdinals = new int[columns.Length];
        var s = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].IsStored)
            {
                storedColumns[s] = columns[i];
                schema[s] = columns[i].Type;
                storageOrdinals[i] = s;
                s++;
            }
            else
            {
                storageOrdinals[i] = -1;
            }
        }
        this.StoredColumns = storedColumns;
        this.Schema = schema;
        this.StorageOrdinals = storageOrdinals;
        this.Heap.ReclaimColumns = HasOffRowCapableColumn(storedColumns) ? storedColumns : null;
        this.AssignColumnIds();
    }

    /// <summary>
    /// Seeds <see cref="HeapColumn.ColumnId"/> for every column that doesn't
    /// already carry one and raises <see cref="MaxColumnIdUsed"/> to cover the
    /// result. A column that arrives pre-assigned keeps its id: the trigger
    /// pseudo-tables (<c>INSERTED</c> / <c>DELETED</c>) are constructed over
    /// the parent table's own <see cref="HeapColumn"/> instances, so
    /// renumbering here would rewrite the parent's catalog identity.
    /// </summary>
    public void AssignColumnIds()
    {
        foreach (var column in this.Columns)
        {
            if (column.ColumnId == 0)
                column.ColumnId = ++this.MaxColumnIdUsed;
            else if (column.ColumnId > this.MaxColumnIdUsed)
                this.MaxColumnIdUsed = column.ColumnId;
        }
    }

    /// <summary>
    /// Highest <see cref="HeapColumn.ColumnId"/> ever handed out for this
    /// table — <c>sys.tables.max_column_id_used</c>. Monotonic: dropping a
    /// column leaves the watermark where it was, so the ids of dropped columns
    /// are never reissued (probe-confirmed — a three-column table that loses
    /// its middle column still reports 3, and the next added column takes 4).
    /// Also fixes the width of the <c>COLUMNS_UPDATED()</c> bitmask, which
    /// spans ids <c>1..MaxColumnIdUsed</c> and therefore keeps a bit position
    /// for each dropped column.
    /// </summary>
    public int MaxColumnIdUsed;

    /// <summary>
    /// Whether any stored column can land off-row — a LOB-typed column or any
    /// variable-length column (bounded var columns overflow-push when a row
    /// exceeds 8060 bytes). Purely fixed/bit tables never allocate LOB chains,
    /// so their heaps leave <see cref="Heap.ReclaimColumns"/> null and skip the
    /// reclamation decode walk entirely.
    /// </summary>
    private static bool HasOffRowCapableColumn(HeapColumn[] stored)
    {
        foreach (var column in stored)
        {
            if (column.Type != SqlType.Bit && !column.Type.IsFixedLength)
                return true;
        }
        return false;
    }

    /// <summary>Whether <c>CREATE TABLE … AS NODE | AS EDGE</c> made this a graph table.</summary>
    public GraphTableKind GraphKind;

    /// <summary>
    /// The <c>graph_id</c> the next row of a node or edge table takes. An
    /// explicit <c>$node_id</c> / <c>$edge_id</c> above it raises it past the
    /// written id, a rolled-back row's id isn't reissued, and
    /// <c>TRUNCATE TABLE</c> doesn't reset it (probed 2026-09-27 against SQL
    /// Server 2025).
    /// </summary>
    public long NextGraphId;

    /// <summary>The <c>CONNECTION</c> constraints an edge table carries.</summary>
    public readonly List<EdgeConstraint> EdgeConstraints = [];

    public override string ObjectTypeCode => "U ";
    public override string ObjectTypeDescription => "USER_TABLE";

    /// <summary>
    /// Full column set in declaration order, the surface area used for name
    /// binding and SQL-ordinal addressing. Includes non-persisted computed
    /// columns; those have <see cref="StorageOrdinals"/> entry <c>-1</c>.
    /// Mutated only by <c>ALTER TABLE ADD COLUMN</c> / <c>DROP COLUMN</c>
    /// (and the storage rewrite invoked from those paths) — every other
    /// site treats the array as effectively immutable.
    /// </summary>
    public HeapColumn[] Columns;

    /// <summary>
    /// Subset of <see cref="Columns"/> that participates in row storage —
    /// regular columns plus persisted computed columns. The schema passed
    /// to <see cref="RowEncoder"/> and <see cref="RowDecoder"/>; ordinals
    /// here index into the encoded row's column slots. Mutated by ALTER
    /// TABLE column ops alongside <see cref="Columns"/>.
    /// </summary>
    public HeapColumn[] StoredColumns;

    /// <summary>
    /// Ordinal of the table's identity column, or <c>-1</c> if there isn't
    /// one. SQL Server allows at most one identity column per table.
    /// </summary>
    public int IdentityOrdinal
    {
        get
        {
            for (var i = 0; i < this.Columns.Length; i++)
            {
                if (this.Columns[i].Identity is not null)
                    return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// Storage-ordinal mapping: <c>StorageOrdinals[i]</c> is the index in
    /// <see cref="StoredColumns"/> of <c>Columns[i]</c>, or <c>-1</c> when
    /// <c>Columns[i]</c> is a non-persisted computed column with no row
    /// slot. Identity on regular tables (no computed columns) collapses to
    /// <c>StorageOrdinals[i] == i</c>. Mutated by ALTER TABLE column ops.
    /// </summary>
    public int[] StorageOrdinals;

    /// <summary>
    /// Stored-column types in storage order; the array passed to
    /// <see cref="RowEncoder"/> and <see cref="RowDecoder"/>. Length matches
    /// <see cref="StoredColumns"/>, not <see cref="Columns"/>. Mutated by
    /// ALTER TABLE column ops.
    /// </summary>
    public SqlType[] Schema;

    /// <summary>
    /// PRIMARY KEY and UNIQUE constraints declared in the CREATE TABLE
    /// statement (or added later via <c>ALTER TABLE ADD CONSTRAINT</c>), in
    /// declaration order. Enforced linear-scan at INSERT / MERGE by
    /// <c>EnforceKeyConstraints</c>; SQL Server's NULLs-equal-for-UNIQUE rule
    /// applies. The list reference is fixed at construction; entries are
    /// appended / removed by ALTER TABLE.
    /// </summary>
    public readonly List<KeyConstraint> KeyConstraints;

    /// <summary>
    /// CHECK constraints declared on the table or its columns (or added later
    /// via <c>ALTER TABLE ADD CONSTRAINT</c>), in declaration order. Evaluated
    /// per-row at INSERT / MERGE; Msg 547 fires on any <c>false</c> predicate
    /// result. NULL operands flow through as UNKNOWN → row passes (SQL
    /// Server's standard CHECK semantics).
    /// </summary>
    public readonly List<CheckConstraint> CheckConstraints;

    /// <summary>
    /// The page-backed row store. Insert via <see cref="Heap.Insert"/>;
    /// iterate via <see cref="Heap.EnumerateRows"/>. Replaced wholesale by
    /// ALTER TABLE ADD / DROP COLUMN when existing rows are re-encoded
    /// against the new schema — every other site reads it as fixed.
    /// </summary>
    public Heap Heap = new();

    /// <summary>
    /// Recomputes <see cref="StoredColumns"/> / <see cref="StorageOrdinals"/>
    /// / <see cref="Schema"/> from the current <see cref="Columns"/> array.
    /// Called by ALTER TABLE column-mutation paths after they've assigned
    /// the new <see cref="Columns"/>; encapsulates the storage-projection
    /// invariant the constructor also relies on.
    /// </summary>
    public void RecomputeStorageProjections()
    {
        var storedCount = 0;
        for (var i = 0; i < this.Columns.Length; i++)
        {
            if (this.Columns[i].IsStored)
                storedCount++;
        }
        var storedColumns = new HeapColumn[storedCount];
        var schema = new SqlType[storedCount];
        var storageOrdinals = new int[this.Columns.Length];
        var s = 0;
        for (var i = 0; i < this.Columns.Length; i++)
        {
            if (this.Columns[i].IsStored)
            {
                storedColumns[s] = this.Columns[i];
                schema[s] = this.Columns[i].Type;
                storageOrdinals[i] = s;
                s++;
            }
            else
            {
                storageOrdinals[i] = -1;
            }
        }
        this.StoredColumns = storedColumns;
        this.Schema = schema;
        this.StorageOrdinals = storageOrdinals;
        this.Heap.ReclaimColumns = HasOffRowCapableColumn(storedColumns) ? storedColumns : null;
    }

    /// <summary>
    /// True for a <c>DECLARE @t TABLE (...)</c>-backed table. Routes a few
    /// behavioral exceptions from regular heap tables: mutations bypass the
    /// undo log (table variables are non-transactional — probe-confirmed:
    /// INSERT @t inside <c>BEGIN TRAN; ROLLBACK</c> leaves the rows intact),
    /// the table never appears in catalog views (<c>sys.tables</c> /
    /// <c>INFORMATION_SCHEMA.TABLES</c>), and constraint / NOT-NULL error
    /// messages render the bare <c>@t</c> name without a schema qualifier
    /// (matching real SQL Server's <c>table '@t'</c> wording).
    /// </summary>
    public readonly bool IsTableVariable;

    /// <summary>
    /// True when this <c>@t</c> entry was bound from a table-valued
    /// parameter — either as a stored-procedure parameter declared
    /// <c>READONLY</c> or as an ADO.NET <see cref="System.Data.SqlDbType.Structured"/>
    /// parameter materialized from a <see cref="System.Data.DataTable"/> /
    /// <see cref="System.Data.IDataReader"/>. Implies <see cref="IsTableVariable"/>
    /// is also true. DML statements targeting a TVP-flagged table variable
    /// raise Msg 10700 ("the table-valued parameter is READONLY and cannot
    /// be modified") — probe-confirmed against SQL Server 2025 for INSERT /
    /// UPDATE / DELETE / MERGE.
    /// </summary>
    public readonly bool IsTableValuedParameter;

    /// <summary>
    /// Non-null when the table declared <c>PERIOD FOR SYSTEM_TIME (startCol, endCol)</c>.
    /// Carries the ordinals of the two <c>GENERATED ALWAYS AS ROW START / END</c>
    /// columns that bound each row's system-versioned validity range. The
    /// history table (when <c>SYSTEM_VERSIONING = ON</c>) mirrors these columns
    /// at the same ordinals as the parent.
    /// </summary>
    public (int StartOrdinal, int EndOrdinal)? PeriodColumns;

    /// <summary>
    /// Non-null on the parent of a system-versioned temporal table —
    /// references the sibling history <see cref="HeapTable"/> auto-created at
    /// <c>CREATE TABLE … WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))</c>
    /// time. The history table itself has <see cref="SystemVersioning"/> =
    /// <c>null</c> and <see cref="IsHistoryTable"/> = true.
    /// </summary>
    public HeapTable? SystemVersioning;

    /// <summary>
    /// True when this table is the history sibling of a system-versioned
    /// temporal parent. Surfaces in <c>sys.tables.temporal_type</c> as 1; the
    /// parent surfaces as 2.
    /// </summary>
    public bool IsHistoryTable;

    /// <summary>
    /// A memory-optimized table (<c>WITH (MEMORY_OPTIMIZED = ON)</c>), or the
    /// backing table of a memory-optimized table type. Its rows live on the
    /// ordinary heap; what changes is the surface — the DDL it refuses, its
    /// hash indexes, its catalog — and how it is reached: every read is a
    /// snapshot read and no write ever waits, a conflicting one failing at
    /// once with Msg 41302 instead (see <c>docs/claude/memory-optimized.md</c>).
    /// </summary>
    public bool IsMemoryOptimized;

    /// <summary>
    /// A memory-optimized table's <c>DURABILITY</c>: 0 for
    /// <c>SCHEMA_AND_DATA</c>, 1 for <c>SCHEMA_ONLY</c>, as
    /// <c>sys.tables.durability</c> reports it. Both keep their rows for the
    /// life of the simulation, which has no restart to lose them in.
    /// </summary>
    public byte Durability;

    /// <summary>
    /// The catalog's shape of a table type's backing type table
    /// (<c>TableType.CatalogShape</c>): its constraints and indexes report
    /// under the <c>sys</c> schema and as <c>is_ms_shipped</c>.
    /// </summary>
    public bool IsTypeTable;

    /// <summary>
    /// True when <see cref="PeriodColumns"/> was copied from a base table
    /// while building this table as its history sibling, rather than declared
    /// by a <c>PERIOD FOR SYSTEM_TIME</c> clause of its own. The copy exists
    /// only so the <c>FOR SYSTEM_TIME</c> row source can read the period
    /// ordinals off either side; real SQL Server's history tables carry no
    /// period at all, which is why a table with a *declared* period is
    /// rejected as a history candidate (Msg 13574) while one holding a copy
    /// can be re-linked after <c>SET (SYSTEM_VERSIONING = OFF)</c>.
    /// </summary>
    public bool PeriodInheritedFromBase;

    /// <summary>
    /// The <c>HISTORY_RETENTION_PERIOD</c> count declared on this table's
    /// <c>SYSTEM_VERSIONING = ON</c> clause, paired with
    /// <see cref="HistoryRetentionUnit"/>. -1 with
    /// <see cref="Storage.HistoryRetentionUnit.Infinite"/> is the default
    /// every system-versioned table starts at, and the pair projects through
    /// <c>sys.tables.history_retention_period</c> /
    /// <c>history_retention_period_unit</c> on the base table only (NULL on
    /// history and non-temporal tables).
    /// </summary>
    public int HistoryRetentionPeriod = -1;

    /// <inheritdoc cref="HistoryRetentionPeriod"/>
    public HistoryRetentionUnit HistoryRetentionUnit = HistoryRetentionUnit.Infinite;

    /// <summary>
    /// The instant a history row must have stopped being current at or after
    /// to remain visible to <c>FOR SYSTEM_TIME</c>, or null when retention is
    /// INFINITE (every version stays visible). Real SQL Server applies the
    /// window at query time and deletes the aged rows later from a background
    /// task, so the cutoff is a read-side filter rather than a delete trigger.
    /// </summary>
    public DateTime? HistoryRetentionCutoff(DateTime asOf) => this.HistoryRetentionUnit switch
    {
        Storage.HistoryRetentionUnit.Day => asOf.AddDays(-this.HistoryRetentionPeriod),
        Storage.HistoryRetentionUnit.Week => asOf.AddDays(-7L * this.HistoryRetentionPeriod),
        Storage.HistoryRetentionUnit.Month => asOf.AddMonths(-this.HistoryRetentionPeriod),
        Storage.HistoryRetentionUnit.Year => asOf.AddYears(-this.HistoryRetentionPeriod),
        _ => null,
    };

    /// <summary>
    /// Non-null on global temp tables (<c>##foo</c>): the session that ran
    /// the <c>CREATE TABLE</c>. Used by <see cref="SimulatedDbConnection.Dispose"/>
    /// to auto-drop the owner's <c>##</c> tables at session close — probe-
    /// confirmed against SQL Server 2025 (with pooling disabled) that the drop
    /// fires unconditionally on owner-disconnect, regardless of other sessions
    /// having referenced or currently referencing the table. Always null for
    /// local temps, table variables, and regular tables.
    /// <para>
    /// The session token rather than the connection: the simulation-wide
    /// <see cref="Simulation.GlobalTempTables"/> dictionary reaches this field,
    /// so a connection here would be pinned by its own <c>##temp</c> and could
    /// never be reclaimed after an application dropped it without disposing.
    /// </para>
    /// </summary>
    public SessionToken? OwnerSession;

    /// <summary>
    /// For a local temp table, the scope that created it
    /// (<see cref="Parser.BatchContext.TempTableScopeId"/>): a nested scope's
    /// same-named table hides it rather than colliding with it
    /// (<see cref="SimulatedDbConnection.TryAddTempTable"/>).
    /// </summary>
    public int TempScopeId;

    /// <summary>
    /// For a local temp table, its padded name inside <c>tempdb</c>
    /// (<see cref="Simulation.AllocateTempTableInternalName"/>); for a table
    /// variable, its <c>#</c>-and-hex name there
    /// (<see cref="Simulation.AllocateTableVariableInternalName"/>); null
    /// otherwise.
    /// </summary>
    public string? InternalName;

    /// <summary>
    /// For a multi-statement table-valued function's return table, the
    /// function, whose name real's Msg 2628 gives the table and whose database
    /// its Msg 547 names (probed 2026-09-28 against SQL Server 2025); null
    /// otherwise.
    /// </summary>
    public Schemas.MultiStatementTableValuedFunction? ReturnTableOf;

    /// <summary>
    /// For an <c>INSTEAD OF</c> trigger's <c>inserted</c> / <c>deleted</c>,
    /// which real keeps in a work table: <c>STATISTICS IO</c> lists a read of
    /// it as <c>Worktable</c> (<see cref="Parser.IoStatistics.Touch"/>).
    /// </summary>
    public bool ReadsAsWorktable;

    /// <summary>
    /// The <see cref="Database"/> this table is registered in, stamped when it
    /// enters a <see cref="Schema.HeapTables"/> dict. Null for the tables that
    /// belong to no database — temp tables, table variables, table-valued
    /// parameters, trigger pseudo-tables, TVF return shapes, and the shared
    /// system tables — whose callers fall back to the session's current
    /// database via <see cref="Parser.BatchContext.DatabaseFor(SqlServerSimulator.Schemas.SchemaObject)"/>.
    /// Load-bearing for a write through a three-part name: the rowversion
    /// counter, the version store, and trigger dispatch are all per-database
    /// and must follow the table rather than the session.
    /// </summary>
    public Database? OwningDatabase;

    /// <summary>
    /// FOREIGN KEY constraints declared on this table (the referring side).
    /// Each entry's <see cref="ForeignKey.ReferencedTable"/> points at the
    /// parent table whose PK/UNIQUE the FK targets. Populated post-construction
    /// by <c>ResolveForeignKeys</c> so the parent-side back-pointer
    /// (<see cref="IncomingForeignKeys"/>) can wire up symmetrically once both
    /// tables exist. Enforced at INSERT/UPDATE on the child by the FK loop in
    /// <c>EnforceOutgoingForeignKeys</c>.
    /// </summary>
    public readonly List<ForeignKey> OutgoingForeignKeys = [];

    /// <summary>
    /// CREATE INDEX-declared secondary indexes on this table, in creation
    /// order. UNIQUE entries (with their optional WHERE filter) participate
    /// in INSERT / UPDATE enforcement alongside <see cref="KeyConstraints"/>;
    /// non-UNIQUE entries are catalog-only (visible through
    /// <c>sys.indexes</c> / <c>sys.index_columns</c>) since the simulator
    /// has no B-tree storage.
    /// </summary>
    public readonly List<Index> Indexes = [];

    /// <summary>
    /// Hypothetical indexes (<c>CREATE INDEX … WITH STATISTICS_ONLY = n</c>):
    /// listed apart from <see cref="Indexes"/> so nothing that reads, seeks or
    /// enforces an index meets one. They take index ids and report through
    /// <see cref="IndexIdentities"/> — a clustered one as <c>CLUSTERED</c> but
    /// without displacing the heap row — and hold no storage (probed
    /// 2026-10-02 against SQL Server 2025).
    /// </summary>
    public readonly List<Index> HypotheticalIndexes = [];

    /// <summary>
    /// <c>CREATE STATISTICS</c>-declared standalone statistics, in creation
    /// order. Catalog-only — see <see cref="UserStatistic"/>.
    /// </summary>
    public readonly List<UserStatistic> UserStatistics = [];

    /// <summary><c>sys.tables.lock_on_bulk_load</c>, set by <c>sp_tableoption 'table lock on bulk load'</c>.</summary>
    public bool LockOnBulkLoad;

    /// <summary>
    /// <c>sys.tables.text_in_row_limit</c>, set by <c>sp_tableoption 'text in row'</c>:
    /// 0 when off. Catalog only — a <c>text</c> / <c>ntext</c> / <c>image</c> value's storage doesn't follow it.
    /// </summary>
    public int TextInRowLimit;

    /// <summary>
    /// <c>sys.tables.large_value_types_out_of_row</c>, set by <c>sp_tableoption 'large value types out of row'</c>.
    /// Catalog only, as <see cref="TextInRowLimit"/> is.
    /// </summary>
    public bool LargeValueTypesOutOfRow;

    /// <summary>
    /// The heap row's <c>allow_row_locks</c> / <c>allow_page_locks</c> — a table with no
    /// clustered index has the row <c>index_id</c> 0 stands for, which <c>ALTER INDEX ALL</c>
    /// and <c>sp_indexoption</c> move like any index's.
    /// </summary>
    public bool HeapAllowRowLocks = true;

    /// <inheritdoc cref="HeapAllowRowLocks"/>
    public bool HeapAllowPageLocks = true;

    // The Heap.MutationGeneration each statistic held when it was last brought up
    // to date — by its creation, UPDATE STATISTICS or sp_updatestats. A statistic
    // is stale once a row has been inserted or deleted since, or an UPDATE has
    // assigned the column it leads with, the way real counts modifications per
    // column. There is no histogram to refresh, so this is all freshness is.
    private long statisticsBaseline;
    private Dictionary<string, long>? statisticsFreshness;
    private long[]? columnUpdatedAt;

    /// <summary>Notes that the statistic named <paramref name="name"/> was just built, so it is current.</summary>
    public void NoteStatisticsCreated(string name, Collation collation) =>
        (this.statisticsFreshness ??= new Dictionary<string, long>(collation))[name] = this.Heap.MutationGeneration;

    /// <summary>
    /// Brings the named statistics — every one when <paramref name="names"/> is
    /// null — up to date with the heap's writes.
    /// </summary>
    public void MarkStatisticsFresh(List<string>? names, Collation collation)
    {
        if (names is null)
        {
            this.statisticsBaseline = this.Heap.MutationGeneration;
            this.statisticsFreshness = null;
            return;
        }
        foreach (var name in names)
            this.NoteStatisticsCreated(name, collation);
    }

    /// <summary>Notes that an UPDATE assigned the columns at <paramref name="ordinals"/> (full-row positions).</summary>
    public void NoteColumnsUpdated(IEnumerable<int> ordinals)
    {
        var updated = this.columnUpdatedAt;
        if (updated is null || updated.Length < this.Columns.Length)
            this.columnUpdatedAt = updated = updated is null ? new long[this.Columns.Length] : [.. updated, .. new long[this.Columns.Length - updated.Length]];
        foreach (var ordinal in ordinals)
        {
            if (ordinal >= 0 && ordinal < updated.Length)
                updated[ordinal] = this.Heap.MutationGeneration;
        }
    }

    /// <summary>
    /// Whether the statistic named <paramref name="name"/>, leading with the column at
    /// <paramref name="leadingOrdinal"/>, has been outrun by a write since it was last current.
    /// </summary>
    public bool IsStatisticStale(string name, int leadingOrdinal)
    {
        var current = Math.Max(this.statisticsBaseline, this.statisticsFreshness is not null && this.statisticsFreshness.TryGetValue(name, out var at) ? at : 0);
        if (this.Heap.LastRowCountChangeGeneration > current)
            return true;
        return this.columnUpdatedAt is { } updated && leadingOrdinal >= 0 && leadingOrdinal < updated.Length && updated[leadingOrdinal] > current;
    }

    /// <summary>
    /// Indexed views (<c>Schemas.View</c> with a unique clustered index) whose
    /// body references this table as a base. Populated at CREATE INDEX-on-view
    /// time from the view's referenced tables. A base-table INSERT / UPDATE
    /// re-evaluates each listed view and enforces its unique indexes
    /// (Msg 2601), matching real SQL Server's materialized-view maintenance.
    /// Empty for the overwhelmingly common no-indexed-view case, so the
    /// enforcement hook is zero-cost then.
    /// </summary>
    public readonly List<Schemas.View> DependentIndexedViews = [];

    /// <summary>
    /// FOREIGN KEY constraints from other tables that reference this table.
    /// The mirror of <see cref="OutgoingForeignKeys"/>: every FK whose
    /// <see cref="ForeignKey.ReferencedTable"/> is this table appears here on
    /// the parent. Drives parent-side enforcement (DELETE / UPDATE of a
    /// referenced row → Msg 547 or cascade), plus DROP TABLE rejection
    /// (Msg 3726) when this table is still referenced.
    /// </summary>
    public readonly List<ForeignKey> IncomingForeignKeys = [];

    /// <summary>
    /// Optional full-text index attached to this table. At most one per
    /// table (real SQL Server's invariant). Populated by
    /// <c>CREATE FULLTEXT INDEX ON table</c>; cleared by
    /// <c>DROP FULLTEXT INDEX ON table</c>; surfaced by
    /// <c>sys.fulltext_indexes</c> / <c>sys.fulltext_index_columns</c>.
    /// The simulator never indexes for text search — the field is
    /// catalog-visible metadata only.
    /// </summary>
    public FullTextIndex? FullTextIndex;

    /// <summary>
    /// The table's change tracking, set by <c>ALTER TABLE … ENABLE
    /// CHANGE_TRACKING</c> and cleared by <c>DISABLE</c>; null for every table
    /// that isn't tracked, which is the one check a write to such a table pays.
    /// </summary>
    public TableChangeTracking? ChangeTracking;

    /// <summary>
    /// XML indexes attached to this table. At most one PRIMARY XML INDEX
    /// per column; zero or more secondary indexes per primary. Populated by
    /// <c>CREATE [PRIMARY] XML INDEX</c>; drained by <c>DROP INDEX</c>;
    /// surfaced by <c>sys.xml_indexes</c>. The simulator never indexes
    /// xml values for query acceleration — entries are catalog-visible
    /// metadata only.
    /// </summary>
    public readonly List<XmlIndex> XmlIndexes = [];

    /// <summary>
    /// Spatial indexes attached to this table. Populated by
    /// <c>CREATE SPATIAL INDEX</c>; surfaced by <c>sys.spatial_indexes</c>
    /// (per-index) and <c>sys.spatial_index_tessellations</c> (per-index
    /// bounding-box + grid-level detail). The simulator never indexes
    /// spatial values for query acceleration — entries are catalog-visible
    /// metadata only.
    /// </summary>
    public readonly List<SpatialIndex> SpatialIndexes = [];

    /// <summary>
    /// SQL Server 2025 JSON indexes on this table's <c>json</c> columns, at
    /// most one per column. Populated by <c>CREATE JSON INDEX</c>; drained by
    /// <c>DROP INDEX</c>. Catalog-visible metadata only.
    /// </summary>
    public readonly List<JsonIndex> JsonIndexes = [];

    /// <summary>
    /// SQL Server 2025 vector indexes on this table's <c>vector</c> columns,
    /// at most one per column. Populated by <c>CREATE VECTOR INDEX</c>;
    /// drained by <c>DROP INDEX</c>. While any exists the table takes no
    /// writes.
    /// </summary>
    public readonly List<VectorIndex> VectorIndexes = [];

    /// <summary>
    /// Lazily-interned per-row <see cref="LockResource"/>s keyed by
    /// <c>(pageIndex, slotIndex)</c> — the RID (row id) that
    /// <see cref="Heap.EnumerateRowsWithAddress"/> yields and that
    /// <see cref="Heap.DeleteAt"/> consumes. Accessed via
    /// <see cref="GetOrCreateRowLock"/>; <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// makes the lookup itself thread-safe without taking the lock
    /// manager's gate (the gate only protects mutations to
    /// <see cref="LockResource.Holders"/>). Entries leak on
    /// <see cref="Heap.DeleteAt"/> — same pattern as the heap's existing
    /// slot / payload leaks. Skipped entirely for table variables / local
    /// temp tables / system tables, which never participate in
    /// cross-connection contention.
    /// </summary>
    public readonly ConcurrentDictionary<(int PageIndex, int SlotIndex), LockResource> RowLocks = new();

    /// <summary>
    /// Count of connections currently holding a data-<see cref="LockMode.Exclusive"/>
    /// lock anywhere on this table (a per-row lock or the
    /// <see cref="TableDataLock"/>), or a <see cref="LockMode.RangeExclusiveExclusive"/>
    /// key lock, whose key part refuses a reader's S as a row X does. Maintained by <see cref="LockManager"/>
    /// via <see cref="Interlocked"/> under its gate; read
    /// lock-free with <c>Volatile.Read</c> by the READ COMMITTED reader's
    /// per-row conflict check (<c>BatchContext.TouchRowForRead</c>). When
    /// zero, every row is committed-readable, so the reader skips the per-row
    /// lock-resource intern and the manager gate entirely — the common
    /// read-mostly path.
    /// </summary>
    public int ActiveDataWriters;

    /// <summary>
    /// The <see cref="ActiveDataWriters"/> companion for U: holds of
    /// <see cref="LockMode.Update"/> live on this table's row locks or its
    /// <see cref="TableDataLock"/>, maintained the same way. A writer's
    /// target read reads both lock-free and skips its per-row probe at zero
    /// (<c>BatchContext.AwaitTargetRow</c>).
    /// </summary>
    public int ActiveUpdateLocks;

    /// <summary>
    /// Returns the <see cref="LockResource"/> for <paramref name="pageIndex"/>
    /// / <paramref name="slotIndex"/>, allocating one (back-referenced to this
    /// table) on first reference.
    /// </summary>
    public LockResource GetOrCreateRowLock(int pageIndex, int slotIndex) =>
        this.RowLocks.GetOrAdd((pageIndex, slotIndex), static (address, t) => new LockResource { OwningTable = t, RowAddress = address }, this);

    /// <summary>
    /// Per session, the pre-images of the rows it has deleted or rewritten
    /// while it still holds their row X, each with that lock — the rows and
    /// keys an uncommitted DELETE or key-changing UPDATE took away, which a
    /// rollback would bring back. A uniqueness check waits on a matching entry
    /// of another session's before deciding, as real waits on the deleted
    /// key's lock — so an insert of a key another transaction has deleted
    /// blocks until that transaction ends rather than succeeding and leaving
    /// two rows with the key after a rollback — and a locking scan waits on
    /// every deleted one (probed 2026-09-26 against SQL Server 2025). An
    /// entry retires with the release of its row X.
    /// </summary>
    public readonly ConcurrentDictionary<SessionToken, ConcurrentDictionary<(int PageIndex, int SlotIndex), (byte[] Image, LockResource Lock)>> SupersededKeyImages = new();

    /// <summary>
    /// Drops <paramref name="resource"/>, the lock of the row at
    /// <paramref name="address"/>, from <see cref="RowLocks"/> once its last X
    /// is released over a deleted row: the delete has settled, the slot is
    /// never used again, and nothing looks a dead row's lock up by address.
    /// Removing it as the delete ran, as was once done, let a rollback restore
    /// the row under no entry, so the next locker interned a second lock for
    /// it beside the one a waiting session had just been granted — two
    /// sessions each holding the row's U, both writing it.
    /// </summary>
    internal void RetireRowLock((int PageIndex, int SlotIndex) address, LockResource resource)
    {
        if (this.Heap.IsSlotTombstoned(address.PageIndex, address.SlotIndex))
            _ = this.RowLocks.TryRemove(KeyValuePair.Create(address, resource));
    }

    /// <summary>Retires <paramref name="owner"/>'s superseded image of <paramref name="address"/>, if any.</summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    internal void RetireSupersededKeyImage(SessionToken owner, (int PageIndex, int SlotIndex) address)
    {
        if (this.SupersededKeyImages.TryGetValue(owner, out var images) && images.TryRemove(address, out _) && images.IsEmptyLockFree())
            _ = this.SupersededKeyImages.TryRemove(owner, out _);
    }

    /// <summary>
    /// How many times a key a session deleted came back while another read
    /// might have looked for it: the deleting transaction inserted a row
    /// carrying it again (<see cref="Parser.BatchContext.NoteKeyPutBack"/>),
    /// or rolled the delete back (counted as the delete's row X goes,
    /// <see cref="LockResource.DeletedBy"/>). A read that looked the key up
    /// while the delete was in flight found it missing, and finds no
    /// <see cref="SupersededKeyImages"/> entry to wait on once that delete has
    /// settled, so it notes this count before reading and reads again when
    /// it moved. Counting every settled delete instead sent a statement
    /// draining a queue beside others to read again after each of theirs.
    /// </summary>
    public long KeysPutBack;

    /// <summary>
    /// The key locks of each key and index that ever took one, keyed by the
    /// <see cref="KeyConstraint"/> / <see cref="Index"/> instance — the
    /// resources a SERIALIZABLE / HOLDLOCK reader's key and key-range locks
    /// name, and every writer tests. Reached through
    /// <see cref="KeyLockGroup.For"/>; the
    /// <see cref="ActiveKeyRangeLocks"/> counter, not the dictionary's size, is
    /// what tells a writer whether any testing is needed.
    /// </summary>
    public readonly ConcurrentDictionary<object, KeyLockGroup> KeyLockGroups = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Count of holds live across every <see cref="KeyLockGroups"/> anchor.
    /// Maintained by <see cref="LockManager"/> via <see cref="Interlocked"/>
    /// under its gate and read lock-free with <c>Volatile.Read</c> by the
    /// writer's per-row test: at zero, nobody holds a key lock on this table,
    /// so the writer skips decoding its row and touching the gate entirely.
    /// </summary>
    public int ActiveKeyRangeLocks;

    /// <summary>
    /// Table-level data lock: the intent lock every row-level read and write
    /// takes, the S or X a statement's row and key locks escalate to (the
    /// threshold lives in <see cref="Parser.LockEscalationTally"/>), and what
    /// <c>WITH (TABLOCK)</c> / <c>WITH (TABLOCKX)</c> take directly.
    /// Distinct from <see cref="SchemaObject.SchemaLock"/> — the schema lock
    /// only takes Sch-S / Sch-M; this one takes IS / IX / SIX / S / U / X.
    /// </summary>
    public readonly LockResource TableDataLock = new();

    /// <summary>Iterates the rows in allocation order, paging through the underlying <see cref="Heap"/>.</summary>
    public IEnumerable<byte[]> Rows => this.Heap.EnumerateRows();

    /// <summary>
    /// Serializes the writes to <see cref="Heap.RowVersions"/> and its chains — a
    /// writer's capture, a commit's stamps, a rollback's discard and the
    /// version sweep — so a sweep can't drop a chain a writer just took, nor
    /// two sweeps free one version's off-row chains twice. Readers take no
    /// lock. A leaf: taken under the heap's latch (an insert publishing its
    /// chain) and the simulation's commit gate, never the other way round, so
    /// a sweep frees the chains it dropped after leaving it.
    /// </summary>
    internal readonly Lock RowVersionsGate = new();

    /// <summary>
    /// The commit stamp of the transaction that last created or redefined
    /// this table — any <c>ALTER TABLE</c>, index DDL, a trigger or rename,
    /// <c>TRUNCATE</c> or <c>SWITCH</c> — or zero when none did since a
    /// database allowing snapshot isolation could have seen it. Metadata isn't
    /// versioned, so a SNAPSHOT transaction whose snapshot is older than this
    /// can't reach the table (Msg 3961, see
    /// <see cref="VersionStore.NoteDefinitionChange"/>).
    /// </summary>
    public long DefinitionXid;

    /// <summary>
    /// How many definition changes the table has seen — each statement that
    /// <c>VersionStore.NoteDefinitionChange</c> records. A cursor reading the
    /// table notes it at OPEN and refuses to FETCH once it moved (Msg 16943),
    /// since its rows no longer decode against the plan it compiled.
    /// </summary>
    public long DefinitionVersion;

    internal string DebugDisplay() => $"{this.Name} ({string.Join(", ", this.Columns.Select(c => c.Name))})";

    /// <summary>
    /// The canonical <c>sys.indexes</c> identity rows for this table — the
    /// single source of truth for index-id allocation that every consumer
    /// reads (<c>sys.indexes</c> / <c>sys.index_columns</c> / <c>sys.stats</c>
    /// / <c>sys.stats_columns</c> / <c>sys.partitions</c> /
    /// <c>sys.dm_db_partition_stats</c> / <c>sys.allocation_units</c> /
    /// <c>sys.key_constraints.unique_index_id</c> / <c>INDEX_COL</c> /
    /// <c>INDEXKEY_PROPERTY</c> / <c>STATS_DATE</c>). Allocation mirrors SQL
    /// Server exactly (probe-confirmed against SQL Server 2025, 2026-07-16):
    /// <list type="bullet">
    /// <item><description>The single <b>clustered</b> entry — a clustered
    /// PRIMARY KEY / UNIQUE constraint (<see cref="KeyConstraint.IsClustered"/>)
    /// or a <c>CREATE CLUSTERED INDEX</c> (<see cref="Index.IsClustered"/>),
    /// whichever has the lowest object id — takes <c>index_id = 1</c>,
    /// <c>type = 1</c>, and suppresses the HEAP row.</description></item>
    /// <item><description>With no clustered entry the table is a heap: one
    /// synthetic row at <c>index_id = 0</c>, <c>type = 0</c>, no backing
    /// object.</description></item>
    /// <item><description>Every remaining (nonclustered) constraint / index —
    /// including a NONCLUSTERED PRIMARY KEY — takes <c>index_id = 2..N</c>,
    /// <c>type = 2</c>, in object-id (creation) order. On a heap the
    /// nonclustered ids still start at 2, never reusing the clustered slot's
    /// id 1.</description></item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Whether a clustered PRIMARY KEY, UNIQUE constraint or index orders the
    /// table — whose rows real locks and reports as keys rather than a heap's
    /// row ids.
    /// </summary>
    public bool HasClusteredIndex()
    {
        foreach (var key in this.KeyConstraints)
        {
            if (key.IsClustered)
                return true;
        }
        foreach (var index in this.Indexes)
        {
            if (index.IsClustered)
                return true;
        }
        return false;
    }

    private readonly Lock indexIdLock = new();

    /// <summary>
    /// Gives every index and key constraint without one its <c>index_id</c>, in
    /// object-id order: the clustered one takes 1, each other the lowest id from
    /// 2 that no index, constraint or statistic of the table holds. An id is
    /// kept until its object is dropped, so a drop leaves a gap the next object
    /// fills — the lowest free one, index or statistic alike (probed 2026-09-30
    /// against SQL Server 2025). A drop or an add settles what is there first,
    /// so only the objects one statement declares together are numbered at once.
    /// </summary>
    public void SettleIndexIds()
    {
        lock (this.indexIdLock)
        {
            var pending = new List<(int ObjectId, KeyConstraint? Key, Index? Index)>();
            var used = new HashSet<int>();
            foreach (var statistic in this.UserStatistics)
                _ = used.Add(statistic.StatsId);
            foreach (var key in this.KeyConstraints)
            {
                if (key.IsClustered)
                    key.IndexId = 1;
                else if (key.IndexId < 2)
                    pending.Add((key.ObjectId, key, null));
                else
                    _ = used.Add(key.IndexId);
            }
            foreach (var index in this.Indexes)
            {
                if (index.IsClustered)
                    index.IndexId = 1;
                else if (index.IndexId < 2)
                    pending.Add((index.ObjectId, null, index));
                else
                    _ = used.Add(index.IndexId);
            }
            foreach (var index in this.HypotheticalIndexes)
            {
                if (index.IndexId < 2)
                    pending.Add((index.ObjectId, null, index));
                else
                    _ = used.Add(index.IndexId);
            }
            pending.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));
            var next = 2;
            foreach (var (_, key, index) in pending)
            {
                while (used.Contains(next))
                    next++;
                if (key is not null)
                    key.IndexId = next;
                else
                    index!.IndexId = next;
                _ = used.Add(next);
            }
        }
    }

    /// <summary>The lowest id from 2 the table's next index or statistic can take.</summary>
    public int NextFreeIndexId()
    {
        this.SettleIndexIds();
        var used = new HashSet<int>();
        foreach (var statistic in this.UserStatistics)
            _ = used.Add(statistic.StatsId);
        foreach (var key in this.KeyConstraints)
            _ = used.Add(key.IndexId);
        foreach (var index in this.Indexes)
            _ = used.Add(index.IndexId);
        foreach (var index in this.HypotheticalIndexes)
            _ = used.Add(index.IndexId);
        var next = 2;
        while (used.Contains(next))
            next++;
        return next;
    }

    public List<IndexIdentity> IndexIdentities()
    {
        this.SettleIndexIds();
        var entries = new List<(int IndexId, bool Clustered, KeyConstraint? Key, Index? Index)>(this.KeyConstraints.Count + this.Indexes.Count);
        foreach (var k in this.KeyConstraints)
            entries.Add((k.IndexId, k.IsClustered, k, null));
        foreach (var ix in this.Indexes)
            entries.Add((ix.IndexId, ix.IsClustered, null, ix));
        foreach (var ix in this.HypotheticalIndexes)
            entries.Add((ix.IndexId, ix.IsClustered, null, ix));
        entries.Sort(static (a, b) => a.IndexId.CompareTo(b.IndexId));

        var result = new List<IndexIdentity>(entries.Count + 1);
        if (entries.Count == 0 || entries[0].IndexId != 1)
        {
            result.Add(new IndexIdentity(0, 0, null, null, null));
        }
        foreach (var entry in entries)
        {
            var columnstore = entry.Index is { IsColumnstore: true };
            var hash = entry.Key?.IsHash ?? entry.Index!.IsHash;
            result.Add(new IndexIdentity(
                entry.IndexId,
                entry.Clustered ? (columnstore ? (byte)5 : (byte)1) : columnstore ? (byte)6 : hash ? (byte)7 : (byte)2,
                entry.Key is not null ? entry.Key.Name : entry.Index!.Name,
                entry.Key,
                entry.Index));
        }
        return result;
    }
}

/// <summary>
/// One <c>(index_id, type, name, backing)</c> row a <see cref="HeapTable"/>
/// projects into <c>sys.indexes</c> — the unit of the single index-id
/// allocation authority (<see cref="HeapTable.IndexIdentities"/>). Exactly one
/// of <see cref="Constraint"/> / <see cref="Index"/> is non-null for a real
/// index row; both are null for the synthetic HEAP row. <c>type</c> is 0
/// (HEAP), 1 (CLUSTERED), 2 (NONCLUSTERED), 5 (CLUSTERED COLUMNSTORE), 6
/// (NONCLUSTERED COLUMNSTORE) or 7 (NONCLUSTERED HASH).
/// </summary>
internal readonly struct IndexIdentity(int indexId, byte type, string? name, KeyConstraint? constraint, Index? index)
{
    public readonly int IndexId = indexId;
    public readonly byte Type = type;
    public readonly string? Name = name;
    public readonly KeyConstraint? Constraint = constraint;
    public readonly Index? Index = index;

    /// <summary>True for the synthetic HEAP row (index_id 0, no backing object).</summary>
    public bool IsHeap => this.Type == 0;
}

/// <summary>
/// Tracks the commit timeline for a single heap slot. The live heap row
/// represents the most-recent version (or the in-flight writer's
/// pre-commit version when <see cref="WriterSession"/> is non-null);
/// <see cref="Head"/> chains older committed payloads newest-first.
/// Readers under SNAPSHOT / READ_COMMITTED_SNAPSHOT walk this structure
/// to find the version visible at their snapshot timestamp.
/// </summary>
internal sealed class RowVersionChain
{
    /// <summary>
    /// Commit Xid that made the live heap row current. Zero for rows
    /// that pre-date the simulator's first version-aware operation
    /// (implicitly committed at Xid 0, visible to every snapshot).
    /// Stamped before <see cref="WriterSession"/> clears at the writer's
    /// commit-time finalization step.
    /// </summary>
    internal long LiveXmin;

    /// <summary>
    /// The session whose uncommitted write the slot holds — a transaction
    /// or an auto-commit statement alike — so the live heap payload must not
    /// be returned to another session's SI / RCSI read, while the writer's
    /// own reads see it. Cleared on the writer's commit (with
    /// <see cref="LiveXmin"/> bumped to the new commit stamp) or rollback
    /// (with <see cref="LiveXmin"/> left at its pre-tx value — the undo log
    /// restores the heap row).
    /// </summary>
    internal SessionToken? WriterSession;

    /// <summary>
    /// True after a committed DELETE tombstones the live heap slot. SI /
    /// RCSI readers with snapshot &lt; <see cref="LiveXmin"/> still see
    /// the historical pre-delete version through <see cref="Head"/>;
    /// readers with snapshot &gt;= <see cref="LiveXmin"/> see the row as
    /// deleted. Pre-existing tombstoned slots (deleted before version
    /// tracking existed) have no chain entry at all.
    /// </summary>
    internal bool IsDeletedLive;

    /// <summary>
    /// Head of the history linked list — newest historical version
    /// first. Each entry's <c>Xmax</c> equals the commit stamp of the
    /// transaction that superseded it; the SI visibility predicate
    /// (<c>Xmin &lt;= SX &lt; Xmax</c>) selects the appropriate entry.
    /// </summary>
    internal HistoricalVersion? Head;

    /// <summary>
    /// The pending-entry list of the unit — a transaction, or an auto-commit
    /// statement — whose uncommitted write the live row holds, or null once
    /// that write has committed or rolled back. A second write by the same
    /// unit keeps the history entry its first one recorded, so the row's
    /// history holds one version per committed transaction.
    /// </summary>
    internal List<PendingVersionEntry>? PendingEntries;
}

/// <summary>
/// One older committed version of a heap row. Linked list node;
/// <see cref="RowVersionChain.Head"/> points to the newest entry and the
/// list walks newest-first via <see cref="Next"/>. Once attached to a
/// chain, an entry is immutable.
/// </summary>
internal sealed class HistoricalVersion
{
    internal byte[] Payload = [];
    internal long Xmin;
    internal long Xmax;
    internal HistoricalVersion? Next;

    /// <summary>
    /// The heap's <see cref="Heap.ReclaimColumns"/> when the payload was
    /// captured: the layout its off-row chains are found by when the version
    /// is collected. An <c>ALTER TABLE</c> since gives the table another, and
    /// a payload read through that one doesn't decode.
    /// </summary>
    internal HeapColumn[]? ReclaimColumns;
}
