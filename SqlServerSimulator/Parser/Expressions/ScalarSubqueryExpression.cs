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
/// Each <see cref="Run"/> re-executes the inner plan against the caller's
/// outer-scope resolver, so correlated scalar subqueries see fresh outer-row
/// values per evaluation. Cardinality enforcement is greedy: pull the first
/// row, then check whether a second exists by advancing the enumerator one
/// more time. If it does, raise Msg 512 immediately — no need to materialize
/// the rest of the result set.
/// </para>
/// <para>
/// <see cref="GetSqlType"/> reads the inner plan's static schema, so the
/// projection column type is known at parse time without executing.
/// </para>
/// </remarks>
internal sealed class ScalarSubqueryExpression(Selection inner) : Expression
{
    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
    {
        var resultSet = inner.Execute(getColumnValue);
        using var enumerator = resultSet.RowBytes.GetEnumerator();
        if (!enumerator.MoveNext())
            return SqlValue.Null(resultSet.Schema[0]);
        var firstRow = enumerator.Current;
        return enumerator.MoveNext()
            ? throw SimulatedSqlException.SubqueryReturnedMoreThanOneValue()
            : RowDecoder.DecodeColumn(resultSet.Schema, firstRow, 0);
    }

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => inner.Schema[0];

    internal override string DebugDisplay() => "(SELECT ...)";
}
