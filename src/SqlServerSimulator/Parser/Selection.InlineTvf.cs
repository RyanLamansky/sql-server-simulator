using SqlServerSimulator.Schemas;
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
    /// <param name="function">The function called.</param>
    /// <param name="arguments">The call's arguments, null for a <c>DEFAULT</c>.</param>
    /// <param name="writtenName">The name the call was written with.</param>
    /// <param name="columns">The columns the call reads, the function's own unless a browse statement flattens it.</param>
    /// <param name="browseFlatten">
    /// Whether a browse statement flattens the body, which then carries its
    /// base tables' hidden browse columns after the function's own (see
    /// <see cref="ParserContext.BrowseFlatten"/>), counted in <paramref name="columns"/>.
    /// </param>
    internal static Selection ForInlineTvf(InlineTableValuedFunction function, Expression?[] arguments, MultiPartName writtenName, HeapColumn[]? columns = null, bool browseFlatten = false)
    {
        columns ??= function.OutputColumns;
        var schema = new SqlType[columns.Length];
        var columnNames = new string[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            schema[i] = columns[i].Type;
            columnNames[i] = columns[i].Name;
        }
        // The plan the closure belongs to, whose reader demand each run hands
        // the body; assigned once the plan exists.
        Selection? self = null;
        self = new Selection(
            schema,
            columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            rowSource: (outerBatch, outerResolver) =>
                outerBatch.Connection.Simulation.InvokeInlineTvf(outerBatch, outerResolver, function, arguments, writtenName, self!.ReaderDemand, browseFlatten))
        {
            // An identity column reads through the function as an ordinary
            // updatable one (probed 2026-09-26 against SQL Server 2025).
            ColumnWireFlags = function.OutputWireFlags is { } flags
                ? Array.ConvertAll(flags, flag => (flag & 0x10) != 0 ? (byte)((flag & ~0x10) | 0x08) : flag)
                : null,
        }.TakingReaderDemand(null);
        return self;
    }
}
