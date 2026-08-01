using System.Collections.Concurrent;
using System.Diagnostics;
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
    public readonly (int StartOrdinal, int EndOrdinal)? PeriodColumns;

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
    /// Non-null on global temp tables (<c>##foo</c>): the connection that ran
    /// the <c>CREATE TABLE</c>. Used by <see cref="SimulatedDbConnection.Dispose"/>
    /// to auto-drop the owner's <c>##</c> tables at session close — probe-
    /// confirmed against SQL Server 2025 (with pooling disabled) that the drop
    /// fires unconditionally on owner-disconnect, regardless of other sessions
    /// having referenced or currently referencing the table. Always null for
    /// local temps, table variables, and regular tables.
    /// </summary>
    public SimulatedDbConnection? OwnerConnection;

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
    /// <see cref="TableDataLock"/>). Maintained by <see cref="LockManager"/>
    /// via <see cref="Interlocked"/> under its gate; read
    /// lock-free with <c>Volatile.Read</c> by the READ COMMITTED reader's
    /// per-row conflict check (<c>BatchContext.TouchRowForRead</c>). When
    /// zero, every row is committed-readable, so the reader skips the per-row
    /// lock-resource intern and the manager gate entirely — the common
    /// read-mostly path.
    /// </summary>
    public int ActiveDataWriters;

    /// <summary>
    /// Returns the <see cref="LockResource"/> for <paramref name="pageIndex"/>
    /// / <paramref name="slotIndex"/>, allocating one (back-referenced to this
    /// table) on first reference.
    /// </summary>
    public LockResource GetOrCreateRowLock(int pageIndex, int slotIndex) =>
        this.RowLocks.GetOrAdd((pageIndex, slotIndex), static (_, t) => new LockResource { OwningTable = t }, this);

    /// <summary>
    /// Table-level data lock used when an INSERT / UPDATE / DELETE / MERGE
    /// has escalated its per-row X locks to a single table-X (the escalation
    /// threshold lives in <see cref="SimulatedDbTransaction.RowLockEscalationThreshold"/>),
    /// or when <c>WITH (TABLOCK)</c> / <c>WITH (TABLOCKX)</c> was specified.
    /// Distinct from <see cref="SchemaObject.SchemaLock"/> — the schema lock
    /// only takes Sch-S / Sch-M; this one takes IS / IX / SIX / S / U / X.
    /// </summary>
    public readonly LockResource TableDataLock = new();

    /// <summary>Iterates the rows in allocation order, paging through the underlying <see cref="Heap"/>.</summary>
    public IEnumerable<byte[]> Rows => this.Heap.EnumerateRows();

    /// <summary>
    /// Per-row version chains used by SNAPSHOT and READ_COMMITTED_SNAPSHOT
    /// readers. Each entry maps a slot's <c>(PageIndex, SlotIndex)</c> tuple
    /// to a <see cref="RowVersionChain"/> that records the slot's commit
    /// timeline (live-row Xmin + history of superseded payloads, oldest
    /// first walked newest-first by visibility logic). Populated lazily on
    /// the first INSERT / UPDATE / DELETE the slot participates in; pre-
    /// existing rows that have never been touched have no entry and are
    /// implicitly committed at Xmin = 0 (visible to every snapshot). Skipped
    /// for table variables / local temp tables / system tables — same set
    /// that bypasses <see cref="RowLocks"/>. Concurrent dict for the same
    /// reason: visibility lookups must run without the lock-manager gate so
    /// SNAPSHOT readers don't serialize behind writers.
    /// </summary>
    public readonly ConcurrentDictionary<(int PageIndex, int SlotIndex), RowVersionChain> RowVersions = new();

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
    public List<IndexIdentity> IndexIdentities()
    {
        var entries = new List<(int ObjectId, bool Clustered, KeyConstraint? Key, Index? Index)>(this.KeyConstraints.Count + this.Indexes.Count);
        foreach (var k in this.KeyConstraints)
            entries.Add((k.ObjectId, k.IsClustered, k, null));
        foreach (var ix in this.Indexes)
            entries.Add((ix.ObjectId, ix.IsClustered, null, ix));
        entries.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));

        var clusteredIndex = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Clustered)
            {
                clusteredIndex = i;
                break;
            }
        }

        var result = new List<IndexIdentity>(entries.Count + 1);
        if (clusteredIndex < 0)
        {
            result.Add(new IndexIdentity(0, 0, null, null, null));
        }
        else
        {
            var clustered = entries[clusteredIndex];
            result.Add(new IndexIdentity(1, 1, clustered.Key is not null ? clustered.Key.Name : clustered.Index!.Name, clustered.Key, clustered.Index));
        }

        var nextId = 2;
        for (var i = 0; i < entries.Count; i++)
        {
            if (i == clusteredIndex)
                continue;
            var entry = entries[i];
            result.Add(new IndexIdentity(nextId++, 2, entry.Key is not null ? entry.Key.Name : entry.Index!.Name, entry.Key, entry.Index));
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
/// (HEAP), 1 (CLUSTERED), or 2 (NONCLUSTERED).
/// </summary>
internal readonly record struct IndexIdentity(int IndexId, byte Type, string? Name, KeyConstraint? Constraint, Index? Index)
{
    /// <summary>True for the synthetic HEAP row (index_id 0, no backing object).</summary>
    public bool IsHeap => this.Type == 0;
}

/// <summary>
/// Tracks the commit timeline for a single heap slot. The live heap row
/// represents the most-recent version (or the in-flight writer's
/// pre-commit version when <see cref="WriterTx"/> is non-null);
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
    /// Updated atomically alongside <see cref="WriterTx"/> = null at
    /// the writer's commit-time finalization step.
    /// </summary>
    internal long LiveXmin;

    /// <summary>
    /// Non-null while a transaction is currently writing to this slot —
    /// the live heap payload reflects the writer's pre-commit value and
    /// must not be returned to SI / RCSI readers. Cleared on writer
    /// commit (with <see cref="LiveXmin"/> bumped to the new commit
    /// stamp) or rollback (with <see cref="LiveXmin"/> left at its
    /// pre-tx value — the undo log restores the heap row).
    /// </summary>
    internal SimulatedDbTransaction? WriterTx;

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
}
