using SqlServerSimulator.Storage;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LOWER(x)</c>: lowercases each character. Mirror of
/// <see cref="Upper"/>.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/lower-transact-sql</remarks>
internal sealed class Lower(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "SQL LOWER lowercases user-facing data; the rule's normalization concern doesn't apply here.")]
    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = source.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(value.Type);
        if (!SqlType.IsStringCategory(value.Type))
            throw new NotSupportedException($"LOWER expects a string operand; got {value.Type}.");
        var lowered = value.AsString.ToLower(CultureInfo.InvariantCulture);
        return SqlValue.FromString(value.Type, lowered);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => source.GetSqlType(batch, resolveColumnType);

    internal override string DebugDisplay() => $"LOWER({source.DebugDisplay()})";
}
