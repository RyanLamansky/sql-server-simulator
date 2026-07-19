using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// One user-defined view. Created via <c>CREATE VIEW schema.name [(col_list)]
/// [WITH SCHEMABINDING|ENCRYPTION] AS SELECT ... [WITH CHECK OPTION]</c>,
/// dropped via <c>DROP VIEW</c>, and referenced from a FROM clause
/// (<c>FROM schema.view [alias]</c>) the same way tables are. Lives in its
/// owning <see cref="Schema"/>'s <see cref="Schema.Views"/> dict alongside
/// (but in a separate dict from) heap tables; the name namespace is shared
/// across both for collision detection.
/// </summary>
/// <remarks>
/// <para>
/// The body source is captured at CREATE time as the raw <see cref="string"/>
/// between <c>AS</c> and end-of-statement, and re-parsed per call. Re-
/// parsing cost is negligible at the simulator's scale; a parse-once cache
/// is a perf optimization for later if a real workload needs it.
/// </para>
/// <para>
/// At CREATE time the body is parsed once to derive <see cref="OutputColumns"/>
/// (column names + types) and the updatable-DML metadata
/// (<see cref="BaseTable"/>, <see cref="BaseColumnOrdinals"/>,
/// <see cref="RejectionReason"/>, <see cref="VisibilityCheck"/>,
/// <see cref="CheckOptionCheck"/>). INSERT/UPDATE/DELETE through the view
/// route to <see cref="BaseTable"/> with the projection's column-to-base
/// mapping; non-updatable shapes surface <strong>Msg 4403</strong> /
/// <strong>Msg 4405</strong> / <strong>Msg 4406</strong> at the DML site.
/// </para>
/// </remarks>
internal sealed class View(
    Schema schema,
    string name,
    int objectId,
    HeapColumn[] outputColumns,
    string bodyText,
    bool withCheckOption,
    bool isSchemaBound,
    DateTime createDate,
    HeapTable? baseTable,
    int[] baseColumnOrdinals,
    ViewUpdatabilityRejection rejectionReason,
    Func<SqlValue[], BatchContext, bool>? visibilityCheck,
    Func<SqlValue[], BatchContext, bool>? checkOptionCheck)
    : SchemaObject(name, objectId, schema.SchemaId, createDate)
{
    public Schema Schema = schema;

    public override string ObjectTypeCode => "V ";
    public override string ObjectTypeDescription => "VIEW";

    /// <summary>
    /// One <see cref="HeapColumn"/> per projection column of the body's
    /// SELECT, derived at <c>CREATE VIEW</c> time. Column names come from
    /// the explicit rename list (<c>CREATE VIEW v(a, b) AS …</c>) when one
    /// was supplied, otherwise from the SELECT projection's aliases (or
    /// the underlying column name for direct refs). Nullability is
    /// conservatively True everywhere (same fidelity gap as inline TVFs —
    /// see CLAUDE.md).
    /// </summary>
    public readonly HeapColumn[] OutputColumns = outputColumns;

    /// <summary>
    /// Raw source text of the body's SELECT statement (between <c>AS</c>
    /// and the end of the statement). Re-parsed per FROM-clause reference.
    /// Surfaces verbatim in <c>INFORMATION_SCHEMA.VIEWS.VIEW_DEFINITION</c>
    /// when the view wasn't created with <c>WITH ENCRYPTION</c>.
    /// </summary>
    public readonly string BodyText = bodyText;

    /// <summary>
    /// True when the view was declared with a trailing <c>WITH CHECK
    /// OPTION</c>. Parsed and surfaced through
    /// <c>sys.views.with_check_option</c> /
    /// <c>INFORMATION_SCHEMA.VIEWS.CHECK_OPTION</c>; enforced at INSERT /
    /// UPDATE time via <see cref="CheckOptionCheck"/> (Msg 550 on
    /// violation).
    /// </summary>
    public readonly bool WithCheckOption = withCheckOption;

    /// <summary>
    /// True when the view was declared <c>WITH SCHEMABINDING</c>. Surfaced
    /// through <c>sys.sql_modules.is_schema_bound</c> and
    /// <c>OBJECTPROPERTY(id, 'IsSchemaBound')</c>, and required before a view
    /// can carry an index (a non-schema-bound view raises Msg 1939 at
    /// CREATE INDEX). The simulator doesn't otherwise enforce schema-binding
    /// (a referenced table can still be dropped — see
    /// <c>Simulation.CreateView.cs</c>).
    /// </summary>
    public readonly bool IsSchemaBound = isSchemaBound;

    /// <summary>
    /// Unique-clustered (and any secondary) indexes declared on this view via
    /// <c>CREATE INDEX ON &lt;view&gt;</c> — an indexed (materialized) view.
    /// Reuses <see cref="Storage.Index"/>: a view index's key / include
    /// ordinals are <b>view OUTPUT-column</b> ordinals (the view row bytes are
    /// encoded in <see cref="OutputColumns"/> order, so
    /// <see cref="Storage.IndexKeyColumn.StorageOrdinal"/> ==
    /// <see cref="Storage.IndexKeyColumn.ColumnOrdinal"/> == the output
    /// ordinal). Empty for an ordinary view. Surfaced through
    /// <c>sys.indexes</c> / <c>sys.index_columns</c> / <c>sys.stats</c>;
    /// UNIQUE entries drive live DML uniqueness enforcement (Msg 2601) as base
    /// rows change under the view.
    /// </summary>
    public readonly List<Storage.Index> Indexes = [];

    /// <summary>
    /// Base <see cref="HeapTable"/>s the body references, collected the first
    /// time an index is created on this view. Used only to wire each base
    /// table's <see cref="HeapTable.DependentIndexedViews"/> so a base-table
    /// INSERT / UPDATE re-validates this view's unique indexes. Empty for an
    /// ordinary (unindexed) view.
    /// </summary>
    public HeapTable[] ReferencedBaseTables = [];

    /// <summary>
    /// The <c>(index_id, type, name, index)</c> rows this indexed view projects
    /// into <c>sys.indexes</c> — the view analog of
    /// <see cref="HeapTable.IndexIdentities"/>. A view is never a heap (no
    /// synthetic index_id-0 row): the clustered index takes index_id 1, every
    /// other index takes 2..N in object-id (creation) order. Empty for an
    /// ordinary view (ordinary views carry no <c>sys.indexes</c> rows —
    /// probe-confirmed).
    /// </summary>
    public List<IndexIdentity> IndexIdentities()
    {
        if (this.Indexes.Count == 0)
            return [];
        var ordered = new List<Storage.Index>(this.Indexes);
        ordered.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));
        var clusteredIndex = ordered.FindIndex(static ix => ix.IsClustered);
        var result = new List<IndexIdentity>(ordered.Count);
        if (clusteredIndex >= 0)
            result.Add(new IndexIdentity(1, 1, ordered[clusteredIndex].Name, null, ordered[clusteredIndex]));
        var nextId = 2;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i == clusteredIndex)
                continue;
            result.Add(new IndexIdentity(nextId++, 2, ordered[i].Name, null, ordered[i]));
        }
        return result;
    }

    /// <summary>
    /// Resolved underlying heap table that DML through this view writes to,
    /// or null when the view is not updatable. Updatable shapes: single FROM
    /// source (a heap table OR another updatable view, walked transitively
    /// to a heap), no DISTINCT / aggregates / GROUP BY / HAVING / JOIN /
    /// set-op / window functions. A non-null value here means DML can
    /// proceed at the column-modifiability level
    /// (<see cref="BaseColumnOrdinals"/>); null routes to
    /// <see cref="RejectionReason"/> → Msg 4403 / 4405.
    /// </summary>
    public readonly HeapTable? BaseTable = baseTable;

    /// <summary>
    /// Per-output-column mapping to <see cref="BaseTable"/>'s
    /// <see cref="HeapTable.Columns"/> ordinal, or <c>-1</c> when the view
    /// projection at that index is a derived expression (arithmetic /
    /// function / literal, etc.). Touching a <c>-1</c> column in an INSERT
    /// or UPDATE SET list raises <strong>Msg 4406</strong>. Empty when
    /// <see cref="BaseTable"/> is null. Computed by composing per-level
    /// projection maps up the view-on-view chain.
    /// </summary>
    public readonly int[] BaseColumnOrdinals = baseColumnOrdinals;

    /// <summary>
    /// Non-<see cref="ViewUpdatabilityRejection.None"/> reason explaining
    /// why <see cref="BaseTable"/> is null. Drives error number selection
    /// at the DML site: <c>Aggregate</c> / <c>Distinct</c> / <c>GroupBy</c>
    /// → Msg 4403; <c>MultipleSources</c> → Msg 4405; <c>UnsupportedShape</c>
    /// → Msg 4403 (closest available message).
    /// </summary>
    public readonly ViewUpdatabilityRejection RejectionReason = rejectionReason;

    /// <summary>
    /// Pre-bound closure that evaluates the AND of every WHERE clause up
    /// the view-on-view chain against a base-table row's
    /// <see cref="SqlValue"/> array (indexed by
    /// <see cref="HeapTable.Columns"/> ordinal). Returns <c>true</c> iff
    /// the row is visible through this view. Null when the view isn't
    /// updatable. UPDATE / DELETE call this to filter the heap scan;
    /// INSERT doesn't (real SQL Server passes through the view's WHERE
    /// unless <c>WITH CHECK OPTION</c> is set — that's
    /// <see cref="CheckOptionCheck"/>).
    /// </summary>
    public readonly Func<SqlValue[], BatchContext, bool>? VisibilityCheck = visibilityCheck;

    /// <summary>
    /// Pre-bound closure that returns <c>true</c> iff a base-table row
    /// satisfies every <c>WITH CHECK OPTION</c>-bearing WHERE up the chain.
    /// Null when no level in the chain has <c>WITH CHECK OPTION</c> set.
    /// INSERT and UPDATE through the view call this on each post-mutation
    /// row; a <c>false</c> raises <strong>Msg 550</strong>. Distinct from
    /// <see cref="VisibilityCheck"/> because real SQL Server only enforces
    /// CHECK OPTION post-write: a view with WHERE but without WITH CHECK
    /// OPTION freely accepts INSERTs that produce rows outside its WHERE,
    /// and UPDATEs that move rows out of view (probe-confirmed).
    /// </summary>
    public readonly Func<SqlValue[], BatchContext, bool>? CheckOptionCheck = checkOptionCheck;
}
