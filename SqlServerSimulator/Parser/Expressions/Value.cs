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

    public override SqlValue Run(RuntimeContext runtime) => this.Constant;

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => this.Constant.Type;

    internal override string DebugDisplay() => this.Constant.DebugDisplay();

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => this.Constant.IsNull;
}
