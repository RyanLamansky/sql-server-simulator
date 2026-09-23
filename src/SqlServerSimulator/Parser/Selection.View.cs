using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Wraps a view reference (<c>FROM schema.view</c>) as a
    /// <see cref="Selection"/> suitable for use as a
    /// <see cref="FromSource.LateralPlan"/>. Each execution re-parses the
    /// view's stored body in a fresh child <see cref="BatchContext"/> and
    /// yields its encoded rows. The schema reported through
    /// <see cref="Schema"/> / <see cref="ColumnNames"/> mirrors
    /// <see cref="View.OutputColumns"/> derived at CREATE-VIEW time, with
    /// the view's column-rename list (if any) already applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The view's body runs in a child batch isolated from the caller's
    /// parser cursor — same pattern inline TVFs use. Unlike TVFs, views
    /// have no parameters; only the body executes. The body's
    /// <see cref="Execute"/> call passes <c>outerResolver: null</c> — view
    /// bodies are isolated from caller column scope.
    /// </para>
    /// <para>
    /// Views participate in the shared 32-level recursion cap
    /// (<see cref="SimulatedDbConnection.NestingLevel"/>) — a view that
    /// references another view (or a scalar UDF / inline TVF) counts
    /// toward the depth, and exceeding 32 → Msg 217.
    /// </para>
    /// </remarks>
    /// <param name="view">The view this source reads.</param>
    /// <param name="columns">
    /// The columns the reference reads — <see cref="Simulation.BindViewColumns"/>'s
    /// positional re-bind — or null for the CREATE-time
    /// <see cref="View.OutputColumns"/>.
    /// </param>
    /// <param name="pushedPredicates">
    /// WHERE conjunct templates an enclosing statement pushed into this
    /// reference (see <c>Selection.Execution.PredicatePushdown.cs</c>), carried
    /// to the body parse — the earliest point a view's own projection is known.
    /// Null for an ordinary reference. Every template is already reduced to
    /// output-column slots and evaluated constants, which is what lets it cross
    /// into the body's child batch (holding none of the caller's variables) and
    /// what makes the wrapper safe to rebuild per push rather than per parse.
    /// </param>
    internal static Selection ForView(View view, HeapColumn[]? columns = null, List<BooleanExpression>? pushedPredicates = null)
    {
        columns ??= view.OutputColumns;
        var schema = new SqlType[columns.Length];
        var columnNames = new string[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            schema[i] = columns[i].Type;
            columnNames[i] = columns[i].Name;
        }
        return new Selection(
            schema,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (outerBatch, _) =>
                outerBatch.Connection.Simulation.InvokeView(outerBatch, view, columns.Length, pushedPredicates))
        {
            PredicatePushdown = templates => ForView(
                view, columns, pushedPredicates is null ? templates : [.. pushedPredicates, .. templates]),
            // Whether the body groups can't be known here — it isn't parsed
            // until the reference executes — but CREATE VIEW already classified
            // it: the updatability rejection names the aggregate / GROUP BY
            // shapes, which is exactly the family a join's key set may reduce.
            // The two are reported together, so a body that aggregates without
            // grouping reaches the key collection and then declines the push.
            PushdownIsGrouped = view.RejectionReason
                is ViewUpdatabilityRejection.Aggregate or ViewUpdatabilityRejection.GroupBy,
        };
    }
}
