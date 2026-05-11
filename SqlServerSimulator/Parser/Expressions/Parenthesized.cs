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
    /// <summary>The expression nested inside the parentheses. Exposed so callers (notably <see cref="Expression.IsBareNullLiteral"/>) can peer through paren wrappers.</summary>
    public readonly Expression Wrapped = wrapped;

    public override Storage.SqlValue Run(RuntimeContext runtime) => this.Wrapped.Run(runtime);

    public override Storage.SqlType GetSqlType(Func<MultiPartName, Storage.SqlType> resolveColumnType) => this.Wrapped.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"( {this.Wrapped.DebugDisplay()} )";
}
