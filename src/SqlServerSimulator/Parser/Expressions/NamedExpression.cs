namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// An expression that has been given a name, such as with `as`.
/// </summary>
/// <param name="expression">The expression to be named.</param>
/// <param name="name">The name of the expression, exposed via the <see cref="Name"/> property.</param>
internal sealed class NamedExpression(Expression expression, string name) : Expression
{
    /// <summary>
    /// Underlying expression that was given a name. Exposed so SELECT INTO
    /// schema inference can drill past the rename wrapper to detect direct
    /// column refs (the identity-propagation rule looks at the inner shape,
    /// not the outer name).
    /// </summary>
    internal readonly Expression Inner = expression;

    private readonly string name = name;

    public override string Name => this.name;

    public override Storage.SqlValue Run(RuntimeContext runtime) => this.Inner.Run(runtime);

    public override Storage.SqlType GetSqlType(BatchContext batch, Func<MultiPartName, Storage.SqlType> resolveColumnType) => this.Inner.GetSqlType(batch, resolveColumnType);

    internal override string DebugDisplay() => $"{this.Inner.DebugDisplay()} {this.name}";

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => this.Inner.ResultIsNullable(resolveColumnNullable);

    /// <summary>
    /// Forwards to the wrapped expression: an alias renames a projection, it
    /// doesn't hide what the expression reads. Without this a caller walking
    /// references sees <c>t.col AS v</c> as reference-free.
    /// </summary>
    internal override void VisitColumnReferences(Action<MultiPartName> visit) => this.Inner.VisitColumnReferences(visit);

    internal override bool ResultReportsNumeric => this.Inner.ResultReportsNumeric;
}
