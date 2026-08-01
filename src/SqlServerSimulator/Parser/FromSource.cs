using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-source state captured during a SELECT's FROM clause parsing — one
/// instance per table (or derived table) that participates in the row
/// stream. Bundles the column metadata, the underlying byte stream, and
/// any qualifier (alias or table name) used for column resolution.
/// </summary>
/// <remarks>
/// <para>
/// Pre-JOIN, every Selection had exactly one source; multi-table FROM
/// (with <see cref="JoinSpec"/>) extends that to <c>FromSource[]</c>.
/// Column lookup walks the array in source order; a qualified reference
/// (<c>alias.col</c> / <c>tableName.col</c>) restricts to the matching
/// source, an unqualified reference searches all and raises Msg 209 when
/// the column name appears in more than one.
/// </para>
/// <para>
/// <see cref="StoredSchema"/> equals <see cref="Columns"/> for ordinary
/// heap rows but diverges when computed-column projections are stripped
/// from storage; <see cref="StorageOrdinals"/> maps logical (Columns)
/// indices to physical (StoredSchema) indices and is null for derived
/// tables that don't have a separate stored layout.
/// </para>
/// </remarks>
internal sealed class FromSource(
    string? qualifier,
    string[] columnNames,
    HeapColumn[] columns,
    HeapColumn[] storedSchema,
    int[]? storageOrdinals,
    Heap? lobStore,
    IEnumerable<byte[]> rows,
    Selection? lateralPlan = null,
    HeapTable? backingTable = null,
    View? backingView = null,
    DataLockPlan? heapPlan = null,
    bool materializeOnce = false,
    bool isPlaceholder = false,
    CatalogView? backingCatalogView = null,
    Database? backingCatalogDatabase = null,
    Synonym? viaSynonym = null,
    string? autoElementName = null)
{
    public readonly string? Qualifier = qualifier;

    /// <summary>
    /// The name <c>FOR XML AUTO</c> / <c>FOR JSON AUTO</c> gives this source's
    /// element / sub-array: the alias when one was written, else the object
    /// name <em>as written</em> — SQL Server keeps the qualifier there, so
    /// <c>FROM dbo.t</c> serializes as <c>&lt;dbo.t&gt;</c> while
    /// <c>FROM dbo.t AS x</c> serializes as <c>&lt;x&gt;</c>. Null for the
    /// sources that don't carry a written object name (derived tables, CTEs,
    /// table variables, rowset functions), where the AUTO serializers fall
    /// back to <see cref="Qualifier"/>.
    /// </summary>
    public readonly string? AutoElementName = autoElementName;
    public readonly string[] ColumnNames = columnNames;
    public readonly HeapColumn[] Columns = columns;
    public readonly HeapColumn[] StoredSchema = storedSchema;
    public readonly int[]? StorageOrdinals = storageOrdinals;
    public readonly Heap? LobStore = lobStore;
    public readonly IEnumerable<byte[]> Rows = rows;

    /// <summary>
    /// Back-reference to the <see cref="HeapTable"/> when this source is a
    /// table (or system table); null for derived-table sources. Used by the
    /// UPDATE / DELETE mutation paths to reach the table's
    /// <see cref="HeapTable.KeyConstraints"/> / <see cref="SchemaObject.Name"/>
    /// after FROM parsing has identified which source is the mutation target.
    /// </summary>
    public readonly HeapTable? BackingTable = backingTable;

    /// <summary>
    /// Back-reference to the <see cref="View"/> when this source is a view
    /// reference (<c>FROM schema.view</c>); null otherwise. <see cref="LateralPlan"/>
    /// holds the view's body plan, but consumers that need the view's
    /// updatability metadata (DML-through-view rewrite) read it from here.
    /// Both <see cref="BackingTable"/> and <see cref="BackingView"/> are
    /// mutually exclusive: at most one is non-null.
    /// </summary>
    public readonly View? BackingView = backingView;

    /// <summary>
    /// The <see cref="Schemas.Synonym"/> this source was written as, when the
    /// FROM clause reached <see cref="BackingTable"/> / <see cref="BackingView"/>
    /// through one; null for a direct reference. Permission enforcement checks
    /// the synonym rather than the object behind it, and skips column-grain
    /// tracking for the source (a synonym takes no column grants at all).
    /// </summary>
    public readonly Synonym? ViaSynonym = viaSynonym;

    /// <summary>
    /// When non-null, this source is the right side of a <c>CROSS APPLY</c>
    /// or <c>OUTER APPLY</c>. <see cref="Rows"/> is unused; the join driver
    /// invokes <c>lateralPlan.Execute(currentRowResolver)</c> per outer
    /// tuple to produce rows that may correlate with the left side. The
    /// <see cref="JoinSpec"/> kind paired with this source is
    /// <see cref="JoinKind.CrossApply"/> or <see cref="JoinKind.OuterApply"/>;
    /// the latter null-fills the slot when the plan yields zero rows.
    /// </summary>
    public readonly Selection? LateralPlan = lateralPlan;

    /// <summary>
    /// The reader-side <see cref="DataLockPlan"/> captured when this source is
    /// a plain base-table scan (null for derived tables, table variables, and
    /// <c>FOR SYSTEM_TIME</c> sources). The index-seek narrowing
    /// (<c>Selection.Execution.IndexSeek.cs</c>) reads it to route the seeked
    /// candidate rows through the same per-row lock / conflict pipeline the
    /// full scan would, so a seek's lock footprint covers only the rows it
    /// touches — and declines entirely when the plan holds row locks
    /// tx-scoped (REPEATABLE READ / SERIALIZABLE / UPDLOCK …), where a
    /// whole-table scan's locking is load-bearing.
    /// </summary>
    public readonly DataLockPlan? HeapPlan = heapPlan;

    /// <summary>
    /// True when this source's <see cref="LateralPlan"/> is provably
    /// uncorrelated — it never references an enclosing row — so its rows are
    /// identical on every re-execution within one query. Set only for catalog
    /// views (<c>sys.*</c>): their row generator takes only the
    /// <see cref="BatchContext"/> and the owning database, never an
    /// outer-row resolver, so it cannot correlate. The execution pass
    /// <c>MaterializeUncorrelatedDeferredSources</c> reads this to run the plan
    /// once per query and replace it with a re-enumerable
    /// <see cref="Rows"/> list, collapsing the per-outer-row re-materialization
    /// of a nested-loop join and making the source eligible for the equi-join
    /// hash path. Correlated / lateral sources (derived tables, APPLY, VALUES,
    /// TVFs, views) leave this false and keep their per-outer-row execution.
    /// </summary>
    public readonly bool MaterializeOnce = materializeOnce;

    /// <summary>
    /// True when this source stands in for an unresolvable table referenced by
    /// a statement being parsed in skip mode (an un-taken <c>IF</c> / <c>WHILE</c>
    /// branch, or a block skipped after <c>BREAK</c> / <c>CONTINUE</c> /
    /// <c>RETURN</c>). Real SQL Server binds object names lazily, so a skipped
    /// statement referencing a missing table compiles cleanly and is discarded;
    /// the simulator resolves inline with parsing, so it substitutes this
    /// placeholder to let the statement parse to completion instead of throwing
    /// mid-parse. A placeholder source carries one synthetic nullable column so
    /// <c>SELECT *</c> expands to a non-empty projection, and its presence in a
    /// source set makes unresolved column references across those sources bind
    /// leniently (see <c>Selection.ResolveColumnTypeAcrossSources</c>) — matching
    /// SQL Server's rule that any missing object defers the whole statement's
    /// binding. Only ever set in skip mode; the statement is discarded before
    /// execution, so the placeholder's rows never surface.
    /// </summary>
    public readonly bool IsPlaceholder = isPlaceholder;

    /// <summary>
    /// The <see cref="CatalogView"/> backing this source (<c>FROM sys.columns</c>
    /// etc.); null for every non-catalog source. <see cref="LateralPlan"/> holds
    /// the generator-wrapping plan, but the predicate-pushdown detector in
    /// <c>Selection.BuildSqlProjection</c> reads the view (its
    /// <see cref="CatalogView.PushdownColumns"/> / <see cref="CatalogView.FilteredRowGenerator"/>)
    /// from here to decide whether a WHERE equality can be pushed into the
    /// generator, then rebuilds <see cref="LateralPlan"/> via the pushdown-carrying
    /// <see cref="Selection.ForCatalogView(CatalogView,Database,string,Expression)"/>.
    /// </summary>
    public readonly CatalogView? BackingCatalogView = backingCatalogView;

    /// <summary>
    /// The database the catalog view was scoped to (current DB for a 2-part
    /// <c>sys.columns</c> reference, the named DB for a 3-part cross-database
    /// reference). Paired with <see cref="BackingCatalogView"/> so the pushdown
    /// rebuild reconstructs the generator plan against the same target database.
    /// Null whenever <see cref="BackingCatalogView"/> is.
    /// </summary>
    public readonly Database? BackingCatalogDatabase = backingCatalogDatabase;

    /// <summary>
    /// Builds a placeholder source for a table that failed to resolve while a
    /// statement was being parsed in skip mode. See <see cref="IsPlaceholder"/>.
    /// The single synthetic column keeps <c>SELECT *</c> from expanding to an
    /// empty projection; its type is irrelevant since the statement never
    /// executes.
    /// </summary>
    public static FromSource DeferredPlaceholder(string? qualifier)
    {
        var synthetic = new HeapColumn("placeholder", SqlType.Int32, maxLength: null, nullable: true);
        HeapColumn[] columns = [synthetic];
        return new FromSource(
            qualifier: qualifier,
            columnNames: [synthetic.Name],
            columns: columns,
            storedSchema: columns,
            storageOrdinals: null,
            lobStore: null,
            rows: [],
            isPlaceholder: true);
    }

    /// <summary>
    /// Returns a copy of this source with its deferred <see cref="LateralPlan"/>
    /// replaced by an already-materialized <paramref name="rows"/> list —
    /// clearing <see cref="LateralPlan"/> and <see cref="MaterializeOnce"/> so
    /// downstream join planning treats it as a plain re-enumerable row source.
    /// Column metadata, qualifier, and storage layout are preserved unchanged.
    /// </summary>
    public FromSource WithMaterializedRows(IEnumerable<byte[]> rows) =>
        new(this.Qualifier, this.ColumnNames, this.Columns, this.StoredSchema,
            this.StorageOrdinals, this.LobStore, rows,
            lateralPlan: null, backingTable: this.BackingTable, backingView: this.BackingView,
            heapPlan: this.HeapPlan, materializeOnce: false, viaSynonym: this.ViaSynonym,
            autoElementName: this.AutoElementName);
}

/// <summary>
/// The four set-operation variants the simulator parses. <c>Union</c>
/// dedupes; <c>UnionAll</c> preserves duplicates; <c>Intersect</c> keeps
/// rows present in both branches (dedupes); <c>Except</c> keeps left-side
/// rows not in the right (dedupes). NULLs are equal during dedup /
/// matching — opposite of the <c>=</c> operator's three-valued behavior,
/// matching SQL Server's documented set-op semantics.
/// </summary>
internal enum SetOpKind
{
    Union,
    UnionAll,
    Intersect,
    Except,
}

/// <summary>
/// The variants of JOIN the simulator parses. <c>Inner</c> includes the
/// bare <c>JOIN</c> keyword (which SQL Server treats as INNER) and the
/// explicit <c>INNER JOIN</c>. <c>Left</c> covers <c>LEFT [OUTER] JOIN</c>,
/// <c>Right</c> covers <c>RIGHT [OUTER] JOIN</c>, <c>Full</c> covers
/// <c>FULL [OUTER] JOIN</c>. <c>Cross</c> is the unconditional Cartesian
/// product (and rejects ON).
/// </summary>
internal enum JoinKind
{
    Inner,
    Left,

    /// <summary>
    /// <c>RIGHT [OUTER] JOIN</c>: right rows missing a left match emit
    /// with the left side null-filled; left rows missing a right match
    /// are dropped. Executed by materializing the right source and
    /// tracking a matched bitmap across the entire left iteration. A
    /// derived-table right side is materialized once via the enclosing
    /// scope's outer resolver — outer-correlated subqueries work, but
    /// lateral correlation to the left side raises Msg 207 at runtime
    /// (real SQL Server raises Msg 4104 at bind time for the same shape).
    /// </summary>
    Right,

    /// <summary>
    /// <c>FULL [OUTER] JOIN</c>: matched pairs emit normally; unmatched
    /// left rows emit with the right side null-filled; unmatched right
    /// rows emit with the left side null-filled. Same derived-table
    /// rules as <see cref="Right"/>.
    /// </summary>
    Full,
    Cross,

    /// <summary>
    /// <c>CROSS APPLY</c>: the right source is a correlated derived table
    /// (<see cref="FromSource.LateralPlan"/>) re-executed per left-side row.
    /// Like <c>INNER JOIN</c>, an outer row with zero matches is dropped.
    /// No <c>ON</c> predicate — the correlation lives inside the lateral
    /// plan's own <c>WHERE</c>.
    /// </summary>
    CrossApply,

    /// <summary>
    /// <c>OUTER APPLY</c>: like <see cref="CrossApply"/>, but null-fills
    /// the right side when the lateral plan yields zero rows for an outer
    /// tuple — the LEFT JOIN counterpart.
    /// </summary>
    OuterApply,
}

/// <summary>
/// Describes how the next <see cref="FromSource"/> joins to the
/// accumulated row tuple. <see cref="OnPredicate"/> is null only for
/// <see cref="JoinKind.Cross"/>; the parser enforces the pairing.
/// </summary>
internal sealed class JoinSpec(JoinKind kind, BooleanExpression? onPredicate)
{
    public readonly JoinKind Kind = kind;
    public readonly BooleanExpression? OnPredicate = onPredicate;

    /// <summary>
    /// The number of contiguous flat <c>sources[]</c> slots this join's right
    /// operand spans. <c>1</c> for an ordinary single-source join (the common
    /// case). Greater than 1 when the right operand is a parenthesized join
    /// group (<c>A LEFT JOIN (B JOIN C ON …) ON …</c>): the group's interior
    /// sources occupy slots <c>[level, level + GroupCount)</c> and are joined
    /// to each other by the interior <see cref="JoinSpec"/>s immediately
    /// following this one in the flat array, while this <see cref="OnPredicate"/>
    /// joins the accumulated left spine against the group as a unit — an
    /// outer-join miss NULL-fills every slot in the range, matching SQL
    /// Server's grammar-grouping (not derived-table) semantics for the group.
    /// Set during parsing once the group's source count is known; a left-operand
    /// group needs no marker because a left-deep spine already groups the left.
    /// </summary>
    public int GroupCount = 1;
}
