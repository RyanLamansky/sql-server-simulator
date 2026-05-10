using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Reference : Expression
{
    private MultiPartName name;

    public Reference(Name name)
    {
        this.name = new MultiPartName(name.Value);
    }

    /// <summary>
    /// Constructs a reference whose first part is a literal string. Used for
    /// reserved-keyword function names (e.g. LEFT, RIGHT) that aren't tokenized
    /// as <see cref="Name"/> but participate in the function-call dispatch.
    /// </summary>
    public Reference(string name)
    {
        this.name = new MultiPartName(name);
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
        this.name = new MultiPartName(qualifier).WithAddedPart(column);
    }

    public override string Name => this.name.Leaf;

    public void AddMultiPartComponent(Name next) => this.name = this.name.WithAddedPart(next.Value);

    public override SqlValue Run(RuntimeContext runtime) => runtime.ResolveColumn(this.name);

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => resolveColumnType(this.name);

    internal override string DebugDisplay() => this.name.ToString();
}
