using SqlServerSimulator.Parser.Expressions;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Reasons a <see cref="Selection"/> can't back DML through a view, mapped
/// to the SQL Server error number raised at the DML site (probe-confirmed
/// against SQL Server 2025, 2026-05-12):
/// <list type="bullet">
/// <item><see cref="Aggregate"/> / <see cref="Distinct"/> / <see cref="GroupBy"/>
/// — Msg 4403 ("aggregates, or a DISTINCT or GROUP BY clause").</item>
/// <item><see cref="MultipleSources"/> — Msg 4405 ("multiple base tables"),
/// raised even when only one base table's columns are written but the
/// modeled behavior chooses simplicity over the SQL Server quirk where
/// JOIN-view single-base UPDATEs are allowed.</item>
/// <item><see cref="UnsupportedShape"/> — catch-all (set ops, window
/// functions, HAVING, derived-table-as-source, CTE) — DML raises
/// Msg 4403 as the closest message.</item>
/// <item><see cref="None"/> — the profile is non-null.</item>
/// </list>
/// Note Msg 4406 ("derived or constant field") is per-touched-column, not
/// per-view; that fires at the DML site against the
/// <see cref="ViewUpdatabilityProfile.Projections"/> array directly.
/// </summary>
internal enum ViewUpdatabilityRejection
{
    None,
    Aggregate,
    Distinct,
    GroupBy,
    MultipleSources,
    UnsupportedShape,
}

/// <summary>
/// Snapshot of a <see cref="Selection"/>'s shape-eligible-for-DML state,
/// captured at view-body parse time. Holds the single FROM source (a real
/// heap table or another updatable view), the per-output-column
/// projection expressions (one per <see cref="Selection.Schema"/> entry,
/// possibly wrapped in <see cref="NamedExpression"/> for <c>AS alias</c>),
/// and the WHERE excluders. <see cref="View"/> reads this once at CREATE
/// VIEW time to derive the base-table reference, the
/// <see cref="View.BaseColumnOrdinals"/> map, and the
/// <see cref="View.VisibilityCheck"/> / <see cref="View.CheckOptionCheck"/>
/// closures.
/// </summary>
internal sealed class ViewUpdatabilityProfile(FromSource source, Expression[] projections, BooleanExpression[] excluders)
{
    public readonly FromSource Source = source;
    public readonly Expression[] Projections = projections;
    public readonly BooleanExpression[] Excluders = excluders;
}
