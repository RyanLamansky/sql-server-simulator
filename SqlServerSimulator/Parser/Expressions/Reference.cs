using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Reference : Expression
{
    private readonly List<string> name;

    public Reference(Name name)
    {
        this.name = [name.Value];
    }

    /// <summary>
    /// Constructs a reference whose first part is a literal string. Used for
    /// reserved-keyword function names (e.g. LEFT, RIGHT) that aren't tokenized
    /// as <see cref="Name"/> but participate in the function-call dispatch.
    /// </summary>
    public Reference(string name)
    {
        this.name = [name];
    }

    public override string Name => this.name[^1];

    public void AddMultiPartComponent(Name name) => this.name.Add(name.Value);

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue) => getColumnValue(this.name);

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => resolveColumnType(this.name);

#if DEBUG
    public override string ToString() => string.Join('.', name);
#endif
}
