using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Reference : Expression
{
    /// <summary>
    /// The referenced name. Mutated by <see cref="AddMultiPartComponent"/>
    /// as the parser walks dotted qualifiers. Internal so SELECT INTO
    /// schema inference can resolve the source column for identity
    /// propagation — direct column refs (a top-level <see cref="Reference"/>,
    /// possibly wrapped in <see cref="NamedExpression"/>) propagate identity
    /// from the source when the FROM clause is a single non-joined heap.
    /// </summary>
    internal MultiPartName ReferencedName;

    public Reference(Name name)
    {
        this.ReferencedName = new MultiPartName(name.Value);
    }

    /// <summary>
    /// Constructs a reference whose first part is a literal string. Used for
    /// reserved-keyword function names (e.g. LEFT, RIGHT) that aren't tokenized
    /// as <see cref="Name"/> but participate in the function-call dispatch.
    /// </summary>
    public Reference(string name)
    {
        this.ReferencedName = new MultiPartName(name);
    }

    /// <summary>
    /// Two-part reference (<c>qualifier.column</c>). Used by star-expansion
    /// in <see cref="Selection"/> to emit per-column references qualified by
    /// the FROM source's alias / table name, so multi-source <c>SELECT *</c>
    /// includes same-named columns from different sources without triggering
    /// Msg 209.
    /// </summary>
    public Reference(string qualifier, string column)
    {
        this.ReferencedName = new MultiPartName(qualifier).WithAddedPart(column);
    }

    public override string Name => this.ReferencedName.Leaf;

    public void AddMultiPartComponent(Name next) => this.ReferencedName = this.ReferencedName.WithAddedPart(next.Value);

    public override SqlValue Run(RuntimeContext runtime) => runtime.ResolveColumn(this.ReferencedName);

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => resolveColumnType(this.ReferencedName);

    internal override string DebugDisplay() => this.ReferencedName.ToString();

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => resolveColumnNullable(this.ReferencedName);

    internal override void VisitColumnReferences(Action<MultiPartName> visit) => visit(this.ReferencedName);
}
