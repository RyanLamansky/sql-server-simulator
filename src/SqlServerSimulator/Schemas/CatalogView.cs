using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// A virtual table in the <c>sys</c> schema — backs <c>sys.tables</c>,
/// <c>sys.objects</c>, <c>sys.schemas</c>, etc. Unlike a <see cref="HeapTable"/>,
/// a catalog view has no persistent storage: its rows are projected from
/// live <see cref="Database"/> / <see cref="Schema"/> / <see cref="HeapTable"/>
/// metadata at query time. A fresh row sequence materializes on every
/// SELECT, so changes made earlier in the same batch (CREATE TABLE,
/// CREATE SCHEMA, DROP TABLE, etc.) appear immediately in the next read.
/// </summary>
/// <remarks>
/// <para>
/// Registered process-wide in <see cref="Simulation.CatalogViews"/> and
/// looked up by leaf name when a FROM clause resolves a <c>sys.&lt;view&gt;</c>
/// reference. The simulator only recognizes the leaf names registered here;
/// other <c>sys.*</c> names raise Msg 208 — same as a missing user table.
/// </para>
/// <para>
/// The row generator produces <see cref="SqlValue"/> arrays in column order
/// (matching <see cref="Columns"/>). The FROM-source wiring encodes each
/// row into bytes via <see cref="RowEncoder"/> on iteration; the byte
/// representation is never cached, so the view stays in sync with live
/// metadata without bookkeeping.
/// </para>
/// </remarks>
internal sealed class CatalogView(
    string name,
    HeapColumn[] columns,
    Func<BatchContext, Database, IEnumerable<SqlValue[]>> rowGenerator,
    bool masterScoped = false,
    Func<BatchContext, Database, CatalogFilter, IEnumerable<SqlValue[]>>? filteredRowGenerator = null,
    string[]? pushdownColumns = null)
{
    public readonly string Name = name;

    /// <summary>
    /// When true, this view resolves only when the reference lands in the
    /// <c>master</c> database — the <c>master.dbo.spt_values</c> compatibility
    /// table lives solely in <c>master</c>, so unqualified / <c>dbo.</c>-qualified
    /// forms bind only while <c>master</c> is the current (or 3-part-qualified)
    /// database. Enforced in <see cref="Parser.BatchContext.TryResolveCatalogView"/>.
    /// Every <c>sys.*</c> / <c>INFORMATION_SCHEMA.*</c> view is server-wide, so
    /// this defaults false.
    /// </summary>
    public readonly bool MasterScoped = masterScoped;

    /// <summary>
    /// Deterministic, process-stable <c>object_id</c> surfaced by
    /// <c>OBJECT_ID('sys.&lt;view&gt;')</c>. Catalog views are registered
    /// process-wide (not per-database) so they can't draw from a
    /// <see cref="Database"/>'s object-id allocator; instead the id is a
    /// 32-bit FNV-1a hash of the leaf name forced negative, keeping it stable
    /// across runs and disjoint from the positive ids user objects allocate
    /// (from 100). Load-bearing only for OBJECT_ID resolving to non-NULL —
    /// SSMS's Query Store probe gates on
    /// <c>OBJECT_ID(N'[sys].[database_query_store_options]') IS NOT NULL</c>.
    /// Not byte-identical to real SQL Server's small fixed system-view ids.
    /// </summary>
    public readonly int ObjectId = ComputeObjectId(name);

    private static int ComputeObjectId(string leafName)
    {
        var hash = Simulation.Fnv1a32.Initial;
        hash.Mix(leafName);
        return (int)(hash.Value | 0x8000_0000);
    }

    public readonly HeapColumn[] Columns = columns;

    /// <summary>
    /// Row generator. The <see cref="Database"/> parameter is the database
    /// the view was scoped to — for an unqualified or 2-part reference
    /// (<c>sys.tables</c>) it's the connection's
    /// <see cref="SimulatedDbConnection.CurrentDatabase"/>; for a 3-part
    /// reference (<c>WideWorldImporters.sys.tables</c>) it's whichever
    /// <see cref="Database"/> the qualifier resolved to in
    /// <see cref="Simulation.Databases"/>. Enumerators must read from this
    /// parameter rather than <c>batch.CurrentDatabase</c> so cross-database
    /// catalog inspection lands correctly.
    /// </summary>
    public readonly Func<BatchContext, Database, IEnumerable<SqlValue[]>> RowGenerator = rowGenerator;

    /// <summary>
    /// Predicate-pushdown row generator. Non-null only for pushdown-aware views;
    /// the extra <see cref="CatalogFilter"/> carries a resolved WHERE-equality key
    /// (<c>object_id</c> / <c>major_id</c>) so the generator can enumerate only
    /// the matching object(s) instead of materializing every row. Purely an
    /// optimization — the enclosing SELECT keeps applying the full WHERE as a
    /// residual filter, so this generator may over-produce but must never drop a
    /// row the predicate keeps. Null when the view can't exploit a pushed key, in
    /// which case <see cref="RowGenerator"/> always runs.
    /// </summary>
    public readonly Func<BatchContext, Database, CatalogFilter, IEnumerable<SqlValue[]>>? FilteredRowGenerator = filteredRowGenerator;

    /// <summary>
    /// The view-column names a WHERE equality can push into
    /// <see cref="FilteredRowGenerator"/> (e.g. <c>["object_id"]</c>); null when
    /// the view isn't pushdown-aware. The pushdown detector matches a top-level
    /// AND-conjunct <c>&lt;col&gt; = &lt;row-independent comparand&gt;</c> against
    /// this set. Non-null exactly when <see cref="FilteredRowGenerator"/> is.
    /// </summary>
    public readonly string[]? PushdownColumns = pushdownColumns;

    /// <summary>
    /// How a restricted principal's metadata-visibility filter reads this view's
    /// governing object per row; <see langword="null"/> for views that aren't
    /// object-scoped (always fully visible to everyone, e.g. <c>sys.schemas</c> /
    /// <c>sys.types</c> / the principal / permission views). Set once at
    /// registration by <c>BuiltInResources</c>. A <c>dbo</c> or full-visibility
    /// session never consults it — the filter short-circuits on the session
    /// principal first.
    /// </summary>
    internal MetadataVisibilityKey? MetadataKey;

    /// <summary>
    /// How a restricted session's server-state gate treats this DMV;
    /// <see langword="null"/> for every non-DMV view and the three ungated DMVs
    /// (<c>sys.dm_os_host_info</c> / <c>sys.fn_helpcollations</c> /
    /// <c>sys.dm_db_xtp_table_memory_stats</c>). Set once at registration by
    /// <c>BuiltInResources</c>. A <c>dbo</c> / sysadmin session never consults it —
    /// the gate short-circuits on <see cref="SessionSecurityContext.EffectiveIsDbo"/>
    /// first, so existing in-process DMV reads pay one bool read.
    /// </summary>
    internal DmvGateKind? DmvGate;
}

/// <summary>
/// The server-state gate a restricted session hits when reading a modeled DMV
/// (probe-confirmed against SQL Server 2025, 2026-07-21).
/// </summary>
internal enum DmvGateKind : byte
{
    /// <summary>Server-scope DMV — needs <c>VIEW SERVER PERFORMANCE STATE</c> (covered by <c>VIEW SERVER STATE</c>); denial raises Msg 300.</summary>
    ServerState,

    /// <summary>Database-scope DMV — needs <c>VIEW DATABASE PERFORMANCE STATE</c> at database scope or a covering server permission; denial raises Msg 262.</summary>
    DatabaseState,

    /// <summary><c>sys.dm_exec_sessions</c> — a restricted session without <c>VIEW SERVER STATE</c> sees only its own session row (a row filter, not a hard denial).</summary>
    SessionSelfFilter,
}

/// <summary>
/// Locates the object a catalog-view row's metadata visibility hinges on. Either
/// object-id-keyed (<see cref="ObjectIdOrdinal"/> ≥ 0 — the <c>sys.*</c> views,
/// whose row carries the governing <c>object_id</c> / <c>parent_object_id</c>) or
/// name-keyed (<see cref="ObjectIdOrdinal"/> &lt; 0 — the <c>INFORMATION_SCHEMA</c>
/// object views, whose row carries the owning schema + object name instead of an
/// id).
/// </summary>
internal readonly struct MetadataVisibilityKey(int objectIdOrdinal, int schemaNameOrdinal, int objectNameOrdinal)
{
    public readonly int ObjectIdOrdinal = objectIdOrdinal;

    public readonly int SchemaNameOrdinal = schemaNameOrdinal;

    public readonly int ObjectNameOrdinal = objectNameOrdinal;

    public bool IsNameKeyed => this.ObjectIdOrdinal < 0;
}

/// <summary>
/// A resolved WHERE-equality key handed to a <see cref="CatalogView.FilteredRowGenerator"/>.
/// <see cref="Column"/> names the view column being equated (matched
/// case-insensitively) and <see cref="Value"/> is the comparand evaluated once at
/// execution start. A generator ignores the filter when <see cref="Column"/> is
/// null (<see cref="None"/>) or names a column it doesn't key on. A NULL
/// <see cref="Value"/> means the predicate is <c>col = NULL</c> — UNKNOWN for
/// every row — so the generator yields nothing.
/// </summary>
internal readonly struct CatalogFilter(string? column, SqlValue value)
{
    public readonly string? Column = column;

    public readonly SqlValue Value = value;

    /// <summary>The no-pushdown sentinel — the generator enumerates everything.</summary>
    public static CatalogFilter None => default;

    /// <summary>
    /// When this filter keys on <paramref name="columnName"/> and the comparand
    /// can drive an <c>int</c> key match, reports the key via <paramref name="id"/>
    /// or sets <paramref name="matchesNothing"/> (comparand is NULL, or an integer
    /// outside <c>int</c> range that no <c>object_id</c> / <c>major_id</c> can
    /// equal — both mean the generator yields no rows). Returns false — leaving
    /// the generator to enumerate normally, so the residual WHERE decides — when
    /// the filter targets a different column (or is <see cref="None"/>), or the
    /// comparand isn't an exact integer (a decimal / string / float comparand is
    /// left to the residual filter rather than lossily narrowed). Never coerces in
    /// a way that could raise, so a pathological comparand can't turn a query the
    /// residual filter would answer into an error.
    /// </summary>
    internal bool TargetsInt(string columnName, out int id, out bool matchesNothing)
    {
        id = 0;
        matchesNothing = false;
        if (this.Column is null || !BuiltInToken.Equals(this.Column, columnName))
            return false;

        if (this.Value.IsNull)
        {
            matchesNothing = true;
            return true;
        }

        // Only exact-integer comparands drive the key match; anything else
        // (decimal / string / float) is left to the residual WHERE — the key
        // columns are int, and over-producing is safe while a lossy narrowing
        // isn't. An in-range int keys the seek; an out-of-int-range integer
        // matches no row (int key), so the generator yields nothing.
        if (!SqlType.IsIntegerCategory(this.Value.Type))
            return false;

        // Widen any integer type (tinyint/smallint/int/bigint) to bigint — always
        // lossless, never overflows — then range-check against the int key.
        var wide = this.Value.CoerceTo(SqlType.BigInt).AsInt64;
        if (wide is < int.MinValue or > int.MaxValue)
        {
            matchesNothing = true;
            return true;
        }

        id = (int)wide;
        return true;
    }
}
