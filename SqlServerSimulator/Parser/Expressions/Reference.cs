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

    public override string Name => this.name.Leaf;

    public void AddMultiPartComponent(Name next) => this.name = this.name.WithAddedPart(next.Value);

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue) => getColumnValue(this.name);

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => resolveColumnType(this.name);

    internal override string DebugDisplay() => this.name.ToString();
}
