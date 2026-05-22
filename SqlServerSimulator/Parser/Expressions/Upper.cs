using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>UPPER(x)</c>: uppercases each character using the invariant culture's
/// rules (which line up with the simulator's default
/// <c>SQL_Latin1_General_CP1_CI_AS</c> collation closely enough for the cases
/// it covers). NULL passes through.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/upper-transact-sql</remarks>
internal sealed class Upper(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = source.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(value.Type);
        if (!SqlType.IsStringCategory(value.Type))
            throw new NotSupportedException($"UPPER expects a string operand; got {value.Type}.");
        var uppered = value.AsString.ToUpperInvariant();
        return SqlValue.FromString(value.Type, uppered);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => source.GetSqlType(batch, resolveColumnType);

    internal override string DebugDisplay() => $"UPPER({source.DebugDisplay()})";
}
