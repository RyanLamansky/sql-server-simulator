using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Reference(Name name) : Expression
{
    private readonly List<string> name = [name.Value];

    public override string Name => this.name[^1];

    public void AddMultiPartComponent(Name name) => this.name.Add(name.Value);

    public override DataValue Run(Func<List<string>, DataValue> getColumnValue) => getColumnValue(this.name);

#if DEBUG
    public override string ToString() => string.Join('.', name);
#endif
}
