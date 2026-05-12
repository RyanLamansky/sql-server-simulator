using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

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
/// (column names + types). The simulator's v1 implementation is read-only;
/// INSERT/UPDATE/DELETE on a view raises <see cref="NotSupportedException"/>.
/// Updatable-view pass-through is a future bundle.
/// </para>
/// </remarks>
internal sealed class View(
    Schema schema,
    string name,
    int objectId,
    HeapColumn[] outputColumns,
    string bodyText,
    bool withCheckOption,
    DateTime createDate)
{
    public readonly Schema Schema = schema;
    public readonly string Name = name;
    public readonly int ObjectId = objectId;

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
    /// <c>INFORMATION_SCHEMA.VIEWS.CHECK_OPTION</c>, but not enforced —
    /// CHECK OPTION only affects DML through the view, which the
    /// simulator's v1 doesn't model.
    /// </summary>
    public readonly bool WithCheckOption = withCheckOption;

    public readonly DateTime CreateDate = createDate;
}
