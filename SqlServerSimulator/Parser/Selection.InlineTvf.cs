using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Wraps an inline TVF call (<c>FROM schema.fn(arg1, arg2)</c>) as a
    /// <see cref="Selection"/> suitable for use as a
    /// <see cref="FromSource.LateralPlan"/>. Each execution evaluates the
    /// argument expressions in the outer scope, then re-parses the function's
    /// stored body in a fresh child <see cref="BatchContext"/> with
    /// parameters seeded as typed variables, and yields the body's encoded
    /// rows. The schema reported through <see cref="Schema"/> /
    /// <see cref="ColumnNames"/> mirrors
    /// <see cref="InlineTableValuedFunction.OutputColumns"/> derived at
    /// CREATE-FUNCTION time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Argument expressions evaluate against the outer
    /// <see cref="RuntimeContext"/> — for a correlated call like
    /// <c>CROSS APPLY dbo.fn(t.col)</c> the join driver feeds the
    /// per-outer-row resolver through <paramref name="arguments"/>'s
    /// closures via <see cref="Execute"/>'s <c>outerResolver</c> parameter.
    /// The body's own execution sees only its declared parameters; it
    /// doesn't reach back into the caller's column scope.
    /// </para>
    /// <para>
    /// <c>DEFAULT</c> argument slots (null entries) materialize from the
    /// parameter's stored default expression in the child batch — same path
    /// scalar UDF invocation uses, so the behavior stays uniform across
    /// kinds.
    /// </para>
    /// </remarks>
    internal static Selection ForInlineTvf(InlineTableValuedFunction function, Expression?[] arguments)
    {
        var schema = new SqlType[function.OutputColumns.Length];
        var columnNames = new string[function.OutputColumns.Length];
        for (var i = 0; i < function.OutputColumns.Length; i++)
        {
            schema[i] = function.OutputColumns[i].Type;
            columnNames[i] = function.OutputColumns[i].Name;
        }
        return new Selection(
            schema,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (outerBatch, outerResolver) =>
                outerBatch.Connection.Simulation.InvokeInlineTvf(outerBatch, outerResolver, function, arguments));
    }
}
