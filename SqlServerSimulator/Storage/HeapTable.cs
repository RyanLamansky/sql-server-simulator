using System.Diagnostics;

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
    /// FOREIGN KEY constraints from other tables that reference this table.
    /// The mirror of <see cref="OutgoingForeignKeys"/>: every FK whose
    /// <see cref="ForeignKey.ReferencedTable"/> is this table appears here on
    /// the parent. Drives parent-side enforcement (DELETE / UPDATE of a
    /// referenced row → Msg 547 or cascade), plus DROP TABLE rejection
    /// (Msg 3726) when this table is still referenced.
    /// </summary>
    public readonly List<ForeignKey> IncomingForeignKeys = [];

    /// <summary>Iterates the rows in allocation order, paging through the underlying <see cref="Heap"/>.</summary>
    public IEnumerable<byte[]> Rows => this.Heap.EnumerateRows();

    internal string DebugDisplay() => $"{this.Name} ({string.Join(", ", this.Columns.Select(c => c.Name))})";
}
