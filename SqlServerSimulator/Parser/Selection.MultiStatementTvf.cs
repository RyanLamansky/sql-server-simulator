using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Wraps a multi-statement TVF call (<c>FROM schema.fn(arg1, arg2)</c>)
    /// as a <see cref="Selection"/> suitable for use as a
    /// <see cref="FromSource.LateralPlan"/>. Each execution evaluates the
    /// argument expressions in the outer scope, then dispatches the
    /// function's stored body in a fresh child <see cref="BatchContext"/>
    /// with the declared <c>@r</c> return-table variable pre-seeded as a
    /// table variable; after the body runs the accumulated rows in <c>@r</c>
    /// stream to the join driver. The schema reported through
    /// <see cref="Schema"/> / <see cref="ColumnNames"/> mirrors
    /// <see cref="MultiStatementTableValuedFunction.OutputColumns"/>
    /// declared at CREATE-FUNCTION time on the <c>RETURNS @r TABLE (...)</c>
    /// clause.
    /// </summary>
    /// <remarks>
    /// Same correlated-call shape as inline TVFs: argument expressions
    /// evaluate against the outer <see cref="RuntimeContext"/>, so
    /// <c>CROSS APPLY dbo.fn(t.col)</c> works through the join driver's
    /// per-outer-row resolver. <c>DEFAULT</c> argument slots route through
    /// the same default-expression evaluation path scalar UDFs and inline
    /// TVFs use.
    /// </remarks>
    internal static Selection ForMultiStatementTvf(MultiStatementTableValuedFunction function, Expression?[] arguments)
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
                outerBatch.Connection.Simulation.InvokeMultiStatementTvf(outerBatch, outerResolver, function, arguments));
    }
}
