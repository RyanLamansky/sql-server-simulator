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

    internal override bool ParallelSafe => this.Wrapped.ParallelSafe;

    /// <summary>A parenthesized column keeps the column's name as its projection's (<c>SELECT (e)</c> names the result <c>e</c>, probed 2026-09-27 against SQL Server 2025); anything else stays unnamed.</summary>
    public override string Name => this.Wrapped is Reference or Parenthesized ? this.Wrapped.Name : string.Empty;

    internal override Schemas.AliasType? ResultAliasType => this.Wrapped.ResultAliasType;

    public override Storage.SqlValue Run(RuntimeContext runtime) => this.Wrapped.Run(runtime);

    public override Storage.SqlType GetSqlType(BatchContext batch, Func<MultiPartName, Storage.SqlType> resolveColumnType) => this.Wrapped.GetSqlType(batch, resolveColumnType);

    internal override string DebugDisplay() => $"( {this.Wrapped.DebugDisplay()} )";

    internal override void Describe(NodeShape shape) => shape.Child(this.Wrapped);

    internal override Expression? PureConversionOperand => this.Wrapped;

    internal override bool IsRowIndependent => this.Wrapped.IsRowIndependent;

    private protected override bool IsStructuralConstant => this.Wrapped.IsWrittenConstant;

    internal override bool IsNonNullConstantComputation => this.Wrapped.IsNonNullConstantComputation;

    internal override bool ResultReportsNumeric => this.Wrapped.ResultReportsNumeric;

    internal override bool ResultIsNullable(NullabilityContext context) => this.Wrapped.ResultIsNullable(context);
}
