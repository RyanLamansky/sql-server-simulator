using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Value : Expression
{
    /// <summary>
    /// The literal this expression represents. Exposed so callers (e.g. the
    /// ORDER BY parser) can detect the integer-ordinal form syntactically
    /// rather than waiting for runtime evaluation.
    /// </summary>
    public readonly SqlValue Constant;

    /// <summary>Bare <c>NULL</c> literal — typed as <see cref="SqlType.Int32"/>; SQL Server has no truly untyped NULL, so we pick a default type.</summary>
    public Value() => this.Constant = SqlValue.Null(SqlType.Int32);

    public Value(SqlValue value) => this.Constant = value;

    public Value(AtPrefixedString atPrefixed, ParserContext context) =>
        this.Constant = context.GetVariableValue(atPrefixed.Value);

    public Value(DoubleAtPrefixedString doubleAtPrefixedString)
    {
        switch (doubleAtPrefixedString.Parse())
        {
            case AtAtKeyword.Version:
                this.Constant = SqlValue.FromNVarchar("SQL Server Simulator");
                return;
        }

        throw new NotSupportedException($"Simulator doesn't recognize {doubleAtPrefixedString}.");
    }

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue) => this.Constant;

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => this.Constant.Type;

#if DEBUG
    public override string ToString() => this.Constant.ToString();
#endif
}
