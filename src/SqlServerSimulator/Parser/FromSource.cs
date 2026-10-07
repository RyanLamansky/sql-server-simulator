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
    string? autoElementName = null,
    bool lateralIsQueryBody = false,
    string? writtenObjectName = null,
    string? xmlReceiverName = null,
    MultiPartName? unaliasedName = null,
    bool catalogSeek = false,
    CatalogRowSet? catalogRows = null,
    VolatileProjection? volatileRefresh = null,
    CteBinding? cte = null,
    DerivedTableBinding? derivedTable = null)
{
    public readonly string? Qualifier = qualifier;

    /// <summary>
    /// The body of a derived table, view or CTE a browse statement reads
    /// through, whose columns it describes by the base tables under them
    /// (see <see cref="Selection.BrowseFlattened"/>); null for every other source.
    /// </summary>
    public Selection? BrowseBody;

    /// <summary>
    /// Written <c>FOR PATH</c>: a node or edge table a <c>SHORTEST_PATH</c>
    /// recurses over, whose columns only graph path aggregates may read.
    /// </summary>
    public bool ForPath;

    /// <summary>
    /// A table value constructor or a built-in rowset function (<c>OPENJSON</c>,
    /// <c>STRING_SPLIT</c>, …), every column of which a write through a body
    /// reading it takes as derived (Msg 4406, a <c>DELETE</c> included).
    /// </summary>
    public bool ConstructsRows;

    /// <summary>A table value constructor whose cells read an enclosing or <c>APPLY</c>-left row.</summary>
    public bool ConstructorReadsOuterRow;

    /// <summary>
    /// The table hints of a base-table source written with <c>FORCESEEK</c>,
    /// or with <c>FORCESCAN</c> beside an <c>INDEX</c> hint, which the query
    /// is checked against once it has parsed (Msg 8622 when real's optimizer
    /// can't honor them); null otherwise.
    /// </summary>
    public Selection.TableHintInfo? ForcedAccessPath;

    /// <summary>
    /// The hints a base table's own <c>WITH</c> clause wrote, which an
    /// <c>OPTION (TABLE HINT …)</c> clause for it is measured against; null
    /// for every other source.
    /// </summary>
    public Selection.TableHintInfo? WrittenHints;

    /// <summary>
    /// A <c>FORCESEEK</c> written on a view or CTE reference, which real
    /// carries to every table the body reads, so each must seek on some
    /// predicate of the body's or of the reading query's (see
    /// <see cref="Selection.SeekShape"/>); null for any other source.
    /// </summary>
    public Selection.TableHintInfo? ForcedSeekThrough;

    /// <summary>
    /// A view reference's body's <see cref="Selection.SeekShape"/>, its body
    /// being re-parsed per execution rather than held; null for any other
    /// source.
    /// </summary>
    public Selection.SeekBodyShape? BodySeekShape;

    /// <summary>
    /// The object this source names, spelled as the FROM clause wrote it and
    /// with any alias ignored — <c>g1</c>, <c>dbo.g1</c>, <c>@t</c>. Null for a
    /// source that has no object of its own (a derived table, a CTE, a table
    /// value constructor), where the alias is the only name there is.
    /// <para>
    /// Read by the GROUP BY containment diagnostics, which name the object
    /// rather than the alias: <c>SELECT a FROM g1 AS x GROUP BY b</c> reports
    /// <c>'g1.a'</c> on real, and a derived table's own <c>'z.a'</c>
    /// (probed 2026-08-05).
    /// </para>
    /// </summary>
    public readonly string? WrittenObjectName = writtenObjectName;

    /// <summary>
    /// The object's database, schema and name — as FROM wrote them, the
    /// current database and the default schema filling in what it left out —
    /// when no alias hides it: the only case in which a column may be prefixed
    /// by its schema (<c>dbo.t.a</c>) or database too, and then only by the
    /// ones the name resolved in, a synonym's own rather than its target's.
    /// An aliased source, even one aliased to its own name, and a source with
    /// no object of its own (a derived table, a CTE, a table variable) answer
    /// no such prefix (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    public readonly MultiPartName? UnaliasedName = unaliasedName;

    /// <summary>
    /// Whether the first <paramref name="prefixLength"/> parts of
    /// <paramref name="name"/> — a schema-qualified prefix (<c>dbo.t</c>,
    /// <c>db.dbo.t</c>) whose last part already matched <see cref="Qualifier"/>
    /// — name this source.
    /// </summary>
    public bool AnswersPrefix(MultiPartName name, int prefixLength) =>
        this.UnaliasedName is { } own && PrefixNames(own, name, prefixLength);

    /// <summary>
    /// <paramref name="written"/> as <see cref="UnaliasedName"/> holds it,
    /// with the parts it leaves out filled in.
    /// </summary>
    public static MultiPartName Resolved(MultiPartName written, Database currentDatabase) =>
        new MultiPartName(written.Count >= 3 ? written[written.Count - 3] : currentDatabase.Name)
            .WithAddedPart(written.Count >= 2 ? written[written.Count - 2] : Database.DefaultSchemaName)
            .WithAddedPart(written.Leaf);

    /// <summary>
    /// Whether the schema and database parts of a column prefix (the first
    /// <paramref name="prefixLength"/> parts of <paramref name="name"/>) are
    /// <paramref name="own"/>'s, a <see cref="Resolved"/> name. An empty part
    /// matches anything.
    /// </summary>
    public static bool PrefixNames(MultiPartName own, MultiPartName name, int prefixLength)
    {
        for (var part = 2; part <= Math.Min(prefixLength, 3); part++)
        {
            var written = name[prefixLength - part];
            if (written.Length != 0 && !BuiltInToken.Equals(written, own[3 - part]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// What an XML method call on this source's column writes between the
    /// brackets of its own diagnostics, replacing the ordinary
    /// <c>&lt;source&gt;.&lt;column&gt;</c> reading. Non-null only for a
    /// <c>.nodes()</c> source, whose row column reports the <em>originating</em>
    /// receiver rather than its own alias — <c>dbo.xr CROSS APPLY
    /// d.nodes(…) AS n(c)</c> names <c>dbo.xr.d</c> for a <c>.value()</c> on
    /// <c>n.c</c> — so a chain of them keeps naming the column the instance
    /// came from. Empty when that origin was a variable, which carries no
    /// prefix at all.
    /// </summary>
    public readonly string? XmlReceiverName = xmlReceiverName;

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
    /// The rows this source yields to an execution by <paramref name="batch"/>:
    /// <see cref="Rows"/>, unless that is a <see cref="PerExecutionRows"/>,
    /// which is built afresh for the executing batch. Every enumeration site
    /// reads through here, since the source belongs to a plan any session may
    /// replay.
    /// </summary>
    public IEnumerable<byte[]> RowsFor(BatchContext batch) =>
        this.Rows is PerExecutionRows perExecution ? perExecution.For(batch) : this.Rows;

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
    /// The <c>WITH</c> binding this source reads, when it is a reference to one
    /// of the statement's CTEs; null otherwise, the recursive member's
    /// self-reference included. A write through a body reading the CTE reads
    /// it as the unstored view <see cref="UpdatableView"/> builds.
    /// </summary>
    public readonly CteBinding? Cte = cte;

    /// <summary>
    /// The derived table this source is, <c>(SELECT …) alias</c>; null for
    /// every other source, an <c>APPLY</c>'s correlated body included. A write
    /// through a body reading it reads it as the unstored view
    /// <see cref="UpdatableView"/> builds.
    /// </summary>
    public readonly DerivedTableBinding? DerivedTable = derivedTable;

    /// <summary>
    /// The ordinal of the column a bare <c>$identity</c> or <c>$rowguid</c>
    /// reads in this source, or -1: a table's own, or one a view or CTE passes
    /// straight through — but never a derived table's or an <c>APPLY</c>
    /// body's (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal int KeyPseudoColumn(MultiPartName name) =>
        this.DerivedTable is not null || (this.LateralPlan is not null && this.BackingView is null && this.Cte is null)
            ? -1
            : HeapColumn.FindKeyPseudoColumn(this.Columns, name, passedThrough: true);

    /// <summary>
    /// The view a write through this source passes down: a stored view
    /// (<see cref="BackingView"/>), an inline function's call
    /// (<see cref="FunctionWriteView"/>), or a CTE or derived table analyzed as
    /// the unstored view real writes through it as. Null for every other
    /// source.
    /// </summary>
    public View? UpdatableView() =>
        this.BackingView
        ?? this.FunctionWriteView
        ?? (this.Cte is { Plan: not null } binding ? Simulation.CteDmlView(binding)
            : this.DerivedTable is { Correlated: false } derived ? Simulation.DerivedTableDmlView(derived)
            : null);

    /// <summary>
    /// For an inline function's call in a writing statement's <c>FROM</c>
    /// clause, arguments reading no column, the unstored view a joined write
    /// aliasing it passes through (<c>Simulation.FunctionDmlView</c>); null
    /// for every other source.
    /// </summary>
    public View? FunctionWriteView;

    /// <summary>
    /// For a multi-statement or CLR function's call in a writing statement's
    /// <c>FROM</c> clause, the function's name as written, which a joined write
    /// aliasing it refuses with Msg 270 (probed 2026-10-06 against SQL Server
    /// 2025); null for every other source.
    /// </summary>
    public string? UnwritableFunctionName;

    /// <summary>
    /// The view a joined <c>UPDATE</c> / <c>DELETE</c> naming this source as
    /// its target writes through: <see cref="UpdatableView"/>, or an
    /// <c>APPLY</c>'s correlated body analyzed as a derived table, which real
    /// writes through too (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    public View? WriteTargetView() =>
        this.UpdatableView() ?? (this.DerivedTable is { Correlated: true } applied ? Simulation.DerivedTableDmlView(applied) : null);

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
    /// True when <see cref="LateralPlan"/> is the source's own parsed SELECT
    /// body — a derived table <c>(SELECT …) d</c> or a CTE reference — rather
    /// than a generator wrapper (view, TVF, catalog view, VALUES, OPENJSON,
    /// PIVOT, XML <c>.nodes()</c>, linked server). Cursor planning follows
    /// only this form down to the base tables the body reads, so a source
    /// whose rows a generator produces stays non-navigable and its cursor
    /// stays STATIC. A view is navigable too, but through
    /// <see cref="BackingView"/> — its body is parsed on demand rather than
    /// held here.
    /// </summary>
    public readonly bool LateralIsQueryBody = lateralIsQueryBody;

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
    /// <see cref="Selection.ForCatalogView(CatalogView,Database,string,Expression[])"/>.
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
    /// True when <see cref="LateralPlan"/> is a catalog view's pushed-down seek
    /// rather than the whole view, so its rows are a subset
    /// <see cref="CatalogRows"/> can't stand for.
    /// </summary>
    public readonly bool CatalogSeek = catalogSeek;

    /// <summary>
    /// The cached rowset whose rows <see cref="Rows"/> is, exactly and in
    /// order — set per execution by the materialization pass for an unfiltered
    /// read of a cacheable catalog view, so the hash equi-join can probe the
    /// rowset's persisted index instead of building one. Null for every other
    /// source, including a copy whose rows were narrowed.
    /// </summary>
    public readonly CatalogRowSet? CatalogRows = catalogRows;

    /// <summary>
    /// The columns a joined reader re-draws per output row, taken from the
    /// query body this source reads (<see cref="Selection.VolatileColumns"/>) — null
    /// when there are none, or when the body draws its values once itself.
    /// Kept through materialization, which drops <see cref="LateralPlan"/>.
    /// </summary>
    public readonly VolatileProjection? VolatileRefresh = volatileRefresh
        ?? (lateralPlan?.VolatileColumns is { FixesValues: false, Ordinals.Length: > 0 } drawn ? drawn : null);

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
    /// Returns a copy of this source reading through <paramref name="plan"/> —
    /// its own body with an enclosing statement's WHERE conjunct pushed into it
    /// (see <c>Selection.Execution.PredicatePushdown.cs</c>). Every other field
    /// is preserved, including the ones later passes classify the source by
    /// (<see cref="BackingView"/>, <see cref="LateralIsQueryBody"/>,
    /// <see cref="MaterializeOnce"/>), since a pushed source is the same source
    /// reading fewer rows.
    /// </summary>
    public FromSource WithPushedPlan(Selection plan) =>
        new(this.Qualifier, this.ColumnNames, this.Columns, this.StoredSchema,
            this.StorageOrdinals, this.LobStore, this.Rows,
            lateralPlan: plan, backingTable: this.BackingTable, backingView: this.BackingView,
            heapPlan: this.HeapPlan, materializeOnce: this.MaterializeOnce, isPlaceholder: this.IsPlaceholder,
            backingCatalogView: this.BackingCatalogView, backingCatalogDatabase: this.BackingCatalogDatabase,
            viaSynonym: this.ViaSynonym, autoElementName: this.AutoElementName,
            lateralIsQueryBody: this.LateralIsQueryBody, writtenObjectName: this.WrittenObjectName,
            xmlReceiverName: this.XmlReceiverName, unaliasedName: this.UnaliasedName, catalogSeek: this.CatalogSeek, volatileRefresh: this.VolatileRefresh, cte: this.Cte, derivedTable: this.DerivedTable)
        {
            BrowseBody = this.BrowseBody,
        };

    /// <summary>
    /// Returns a copy of this source reading <paramref name="rows"/> — the same
    /// rows filtered by the WHERE conjuncts that read only this source (see
    /// <c>Selection.TryPrefilterJoinSource</c>). Every other field is preserved,
    /// <see cref="HeapPlan"/> included, because a prefiltered source is still the
    /// plain base-table scan the later join passes may seek or hash on its own
    /// terms — one that happens to hand back fewer rows.
    /// </summary>
    public FromSource WithFilteredRows(IEnumerable<byte[]> rows) =>
        new(this.Qualifier, this.ColumnNames, this.Columns, this.StoredSchema,
            this.StorageOrdinals, this.LobStore, rows,
            lateralPlan: this.LateralPlan, backingTable: this.BackingTable, backingView: this.BackingView,
            heapPlan: this.HeapPlan, materializeOnce: this.MaterializeOnce, isPlaceholder: this.IsPlaceholder,
            backingCatalogView: this.BackingCatalogView, backingCatalogDatabase: this.BackingCatalogDatabase,
            viaSynonym: this.ViaSynonym, autoElementName: this.AutoElementName,
            lateralIsQueryBody: this.LateralIsQueryBody, writtenObjectName: this.WrittenObjectName,
            xmlReceiverName: this.XmlReceiverName, unaliasedName: this.UnaliasedName, catalogSeek: this.CatalogSeek, volatileRefresh: this.VolatileRefresh, cte: this.Cte, derivedTable: this.DerivedTable)
        {
            BrowseBody = this.BrowseBody,
        };

    /// <summary>
    /// Returns a copy of this base-table source read as a joined write's
    /// target: its scan — and every seek the join passes take on it — reads
    /// the live rows without locks under <see cref="DataLockPlan.ForWriteTarget"/>,
    /// recording each row it yields in <paramref name="addresses"/>. Every
    /// other field is preserved, so the target narrows, seeks and joins as
    /// any base-table source does.
    /// </summary>
    public FromSource AsWriteTarget(RowAddressMap addresses)
    {
        var plan = DataLockPlan.ForWriteTarget(addresses);
        return new(this.Qualifier, this.ColumnNames, this.Columns, this.StoredSchema,
            this.StorageOrdinals, this.LobStore, new WriteTargetScanRows(this.BackingTable!, addresses),
            lateralPlan: this.LateralPlan, backingTable: this.BackingTable, backingView: this.BackingView,
            heapPlan: plan, materializeOnce: this.MaterializeOnce, isPlaceholder: this.IsPlaceholder,
            backingCatalogView: this.BackingCatalogView, backingCatalogDatabase: this.BackingCatalogDatabase,
            viaSynonym: this.ViaSynonym, autoElementName: this.AutoElementName,
            lateralIsQueryBody: this.LateralIsQueryBody, writtenObjectName: this.WrittenObjectName,
            xmlReceiverName: this.XmlReceiverName, unaliasedName: this.UnaliasedName, catalogSeek: this.CatalogSeek, volatileRefresh: this.VolatileRefresh, cte: this.Cte, derivedTable: this.DerivedTable)
        {
            BrowseBody = this.BrowseBody,
        };
    }

    /// <summary>
    /// Returns a copy of this source with its deferred <see cref="LateralPlan"/>
    /// replaced by an already-materialized <paramref name="rows"/> list —
    /// clearing <see cref="LateralPlan"/> and <see cref="MaterializeOnce"/> so
    /// downstream join planning treats it as a plain re-enumerable row source.
    /// Column metadata, qualifier, and storage layout are preserved unchanged.
    /// <paramref name="catalogRows"/> is the cached catalog rowset the list
    /// is, when it is one (see <see cref="CatalogRows"/>).
    /// </summary>
    public FromSource WithMaterializedRows(List<byte[]> rows, CatalogRowSet? catalogRows = null) =>
        new(this.Qualifier, this.ColumnNames, this.Columns, this.StoredSchema,
            this.StorageOrdinals, this.LobStore, rows,
            lateralPlan: null, backingTable: this.BackingTable, backingView: this.BackingView,
            heapPlan: this.HeapPlan, materializeOnce: false, viaSynonym: this.ViaSynonym,
            autoElementName: this.AutoElementName, writtenObjectName: this.WrittenObjectName,
            xmlReceiverName: this.XmlReceiverName, unaliasedName: this.UnaliasedName, catalogRows: catalogRows, volatileRefresh: this.VolatileRefresh, cte: this.Cte, derivedTable: this.DerivedTable)
        {
            BrowseBody = this.BrowseBody,
        };
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
    /// Whether this join is a comma in the FROM list rather than a written
    /// <c>CROSS JOIN</c>, which the two otherwise share; <c>MATCH</c> accepts
    /// only comma-listed sources.
    /// </summary>
    public bool IsComma;

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

    /// <summary>
    /// The flat <c>sources[]</c> range, <c>[ScopeStart, ScopeEnd)</c>, this
    /// join's <see cref="OnPredicate"/> binds against: its own chain's sources
    /// up to and including its right operand. An earlier comma-separated item,
    /// an enclosing chain's sources when the join sits in a group, and a source
    /// written after it are all out of reach (Msg 4104, probed 2026-09-24
    /// against SQL Server 2025). <see cref="ScopeEnd"/> −1 sees every source,
    /// for a join built other than by parsing an ON.
    /// </summary>
    public int ScopeStart;

    /// <inheritdoc cref="ScopeStart"/>
    public int ScopeEnd = -1;

    /// <summary>
    /// The algorithm an inline join hint names (<c>INNER HASH JOIN</c>), which
    /// the statement's <c>OPTION</c> join hints must include and the join's
    /// predicates must make buildable; <see cref="Selection.JoinAlgorithms.None"/>
    /// without one.
    /// </summary>
    public Selection.JoinAlgorithms Algorithm;

    /// <summary>An inline <c>REMOTE</c> join hint, which takes no <c>OPTION</c> join hint beside it.</summary>
    public bool Remote;
}

/// <summary>
/// A <see cref="FromSource"/>'s rows when producing them needs the executing
/// session — its locks, lock timeout, snapshot, parameters and statement clock.
/// A cached plan is replayed by every session that sends its text, so the
/// batch can't be captured when the source is parsed; each enumeration site
/// asks <see cref="FromSource.RowsFor"/> instead, and enumerating one of these
/// directly is a missed site, which throws.
/// </summary>
internal abstract class PerExecutionRows : IEnumerable<byte[]>
{
    /// <summary>The rows as an execution by <paramref name="batch"/> reads them.</summary>
    public abstract IEnumerable<byte[]> For(BatchContext batch);

    public IEnumerator<byte[]> GetEnumerator() =>
        throw new InvalidOperationException("A per-execution row source is read through FromSource.RowsFor.");

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();
}

/// <summary>
/// A base-table scan read with the per-row lock checks <paramref name="plan"/>
/// asks for, as the executing session.
/// </summary>
internal sealed class LockCheckedScanRows(HeapTable table, DataLockPlan plan) : PerExecutionRows
{
    public override IEnumerable<byte[]> For(BatchContext batch) =>
        RowSecurity.FilterLockedRows(table, plan, BatchContext.WrapWithRowConflictChecks(table, batch, plan), batch);
}

/// <summary>
/// A scan that takes no per-row lock — a <c>NOLOCK</c> read, a table
/// variable — counted in the executing statement's <c>STATISTICS IO</c>.
/// </summary>
internal sealed class UnlockedScanRows(HeapTable table) : PerExecutionRows
{
    public override IEnumerable<byte[]> For(BatchContext batch) =>
        RowSecurity.FilterRows(table, ClusteredScan.Rows(table, batch.Connection.StatementIo, batch.CurrentStatement.RowAddresses), batch);
}

/// <summary>
/// A joined write's target scan (<see cref="FromSource.AsWriteTarget"/>): the
/// live rows in heap order, past the table's filter predicate, each recorded
/// in <paramref name="addresses"/>.
/// </summary>
/// <remarks>
/// Heap order rather than <see cref="ClusteredScan"/>'s key order: a clustered
/// table's key order is rebuilt after the very writes the statement makes, so
/// a statement run again re-sorts the table every run (measured 2026-10-07:
/// 103 ms → 170 ms for a joined UPDATE that scans 200k rows to rewrite 4k).
/// Which target row the walk meets first matters only to the order the write
/// applies its rows in, which real takes from its plan.
/// </remarks>
internal sealed class WriteTargetScanRows(HeapTable table, RowAddressMap addresses) : PerExecutionRows
{
    public override IEnumerable<byte[]> For(BatchContext batch)
    {
        var counts = batch.Connection.StatementIo?.Touch(table);
        _ = counts?.ScanCount += 1;
        var lastPage = -1;
        foreach (var (page, slot, bytes) in RowSecurity.FilterAddressedRows(table, table.Heap.EnumerateRowsWithAddress(), batch))
        {
            counts?.Enter(page, ref lastPage);
            addresses.Record(bytes, page, slot);
            yield return bytes;
        }
    }
}
