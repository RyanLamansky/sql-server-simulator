using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// A <c>(SELECT col FROM ...)</c> subquery used in an expression slot
/// (projection, WHERE comparison, arithmetic operand, etc.). The inner
/// SELECT must project exactly one column (Msg 116, enforced at parse time)
/// and at runtime must return at most one row (Msg 512, enforced per
/// evaluation). Empty result → NULL of the inner's projected type.
/// </summary>
/// <remarks>
/// <para>
/// A correlated subquery re-executes the inner plan against the caller's
/// outer-scope resolver on each <see cref="Run"/>, so it sees fresh outer-row
/// values per evaluation; one that never reads the outer row runs once per
/// statement and replays its value (see <see cref="UncorrelatedSubqueryCache"/>
/// for how the two are told apart). Cardinality enforcement is greedy: pull the
/// first row, then check whether a second exists by advancing the enumerator
/// one more time. If it does, raise Msg 512 immediately — no need to
/// materialize the rest of the result set.
/// </para>
/// <para>
/// <see cref="GetSqlType"/> reads the inner plan's static schema, so the
/// projection column type is known at parse time without executing.
/// </para>
/// </remarks>
internal sealed class ScalarSubqueryExpression(Selection inner) : Expression
{
    /// <summary>
    /// The wrapped single-column SELECT plan. Exposed so an enclosing FOR JSON
    /// serializer can detect a nested FOR JSON subquery and embed its result as
    /// raw JSON (via <c>Selection.ForJson</c>).
    /// </summary>
    internal readonly Selection Inner = inner;

    public override SqlValue Run(RuntimeContext runtime)
    {
        PermissionEnforcement.CheckSubqueryReads(runtime.Batch, this.Inner);
        var memo = UncorrelatedSubqueryCache.Open(runtime, this);
        if (memo.Result is { } cached)
            return (SqlValue)cached;

        var resultSet = this.Inner.Execute(runtime.Batch, memo.ResolverFor(runtime));
        SqlValue value;
        using (var enumerator = resultSet.RowBytes.GetEnumerator())
        {
            if (!enumerator.MoveNext())
            {
                value = SqlValue.Null(resultSet.Schema[0]);
            }
            else
            {
                var firstRow = enumerator.Current;
                value = enumerator.MoveNext()
                    ? throw SimulatedSqlException.SubqueryReturnedMoreThanOneValue()
                    : RowDecoder.DecodeColumn(resultSet.Schema, firstRow, 0);
            }
        }

        memo.Remember(runtime, this, value);
        return value;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.Inner.Schema[0];

    internal override string DebugDisplay() => "(SELECT ...)";
}
