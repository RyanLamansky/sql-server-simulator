using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Reference(Name name) : Expression
{
    private readonly List<string> name = [name.Value];

    public override string Name => this.name[^1];

    public void AddMultiPartComponent(Name name) => this.name.Add(name.Value);

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue) => getColumnValue(this.name);

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => resolveColumnType(this.name);

#if DEBUG
    public override string ToString() => string.Join('.', name);
#endif
}
