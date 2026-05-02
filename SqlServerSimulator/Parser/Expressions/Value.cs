using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Value : Expression
{
    private readonly SqlValue value;

    /// <summary>Bare <c>NULL</c> literal — typed as <see cref="SqlType.Int32"/>; SQL Server has no truly untyped NULL, so we pick a default type.</summary>
    public Value() => this.value = SqlValue.Null(SqlType.Int32);

    public Value(SqlValue value) => this.value = value;

    public Value(AtPrefixedString atPrefixed, ParserContext context) =>
        this.value = context.GetVariableValue(atPrefixed.Value);

    public Value(DoubleAtPrefixedString doubleAtPrefixedString)
    {
        switch (doubleAtPrefixedString.Parse())
        {
            case AtAtKeyword.Version:
                this.value = SqlValue.FromNVarchar("SQL Server Simulator");
                return;
        }

        throw new NotSupportedException($"Simulator doesn't recognize {doubleAtPrefixedString}.");
    }

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue) => this.value;

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => this.value.Type;

#if DEBUG
    public override string ToString() => this.value.ToString();
#endif
}
