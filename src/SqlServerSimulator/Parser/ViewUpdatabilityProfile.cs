using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Reasons a <see cref="Selection"/> can't back DML through a view, mapped
/// to the SQL Server error number raised at the DML site (probe-confirmed
/// against SQL Server 2025, 2026-05-12):
/// <list type="bullet">
/// <item><see cref="Aggregate"/> / <see cref="Distinct"/> / <see cref="GroupBy"/>
/// — Msg 4403 ("aggregates, or a DISTINCT or GROUP BY clause").</item>
/// <item><see cref="MultipleSources"/> — Msg 4405 ("multiple base tables").
/// A view whose body reads several sources carries this even though an
/// UPDATE whose SET list lands entirely in one of them is accepted: the
/// UPDATE path re-reads the body's profile and routes to that base table,
/// while INSERT and DELETE — which real refuses on a multi-source view
/// whatever they touch — raise off this reason.</item>
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
/// captured at view-body parse time. Holds the FROM sources (each a real
/// heap table, another view, or a derived source) and the joins between
/// them, the per-output-column projection expressions (one per
/// <see cref="Selection.Schema"/> entry, possibly wrapped in
/// <see cref="NamedExpression"/> for <c>AS alias</c>), and the WHERE
/// excluders.
/// </summary>
/// <remarks>
/// <see cref="View"/> reads a single-source profile once at CREATE VIEW
/// time to derive the base-table reference, the
/// <see cref="View.BaseColumnOrdinals"/> map, and the
/// <see cref="View.VisibilityCheck"/> / <see cref="View.CheckOptionCheck"/>
/// closures. A multi-source profile can't collapse to those — its WHERE and
/// join predicates read columns of several tables — so the view records
/// only <see cref="View.IsJoinUpdatable"/> and the UPDATE path re-parses
/// the body to reach a live profile, which is where <see cref="Sources"/> /
/// <see cref="Joins"/> matter.
/// </remarks>
internal sealed class ViewUpdatabilityProfile(FromSource[] sources, JoinSpec[] joins, Expression[] projections, BooleanExpression[] excluders)
{
    public readonly FromSource[] Sources = sources;
    public readonly JoinSpec[] Joins = joins;
    public readonly Expression[] Projections = projections;
    public readonly BooleanExpression[] Excluders = excluders;
}
