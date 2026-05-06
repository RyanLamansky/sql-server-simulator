namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// An expression that's wrapped in parentheses, potentially affecting the
/// order of operations. Constructed by <see cref="Expression.Parse"/>'s
/// grouped-expression dispatch after the inner body is parsed; that
/// dispatch also handles the alternative <c>(SELECT ...)</c> shape, which
/// produces a <see cref="ScalarSubqueryExpression"/> instead.
/// </summary>
internal sealed class Parenthesized(Expression wrapped) : Expression
{
    public override Storage.SqlValue Run(Func<List<string>, Storage.SqlValue> getColumnValue) => wrapped.Run(getColumnValue);

    public override Storage.SqlType GetSqlType(Func<List<string>, Storage.SqlType> resolveColumnType) => wrapped.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"( {wrapped.DebugDisplay()} )";
}
