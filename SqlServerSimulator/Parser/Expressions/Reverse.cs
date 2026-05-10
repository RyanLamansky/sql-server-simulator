using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>REVERSE(x)</c>: returns the source with its characters in reverse
/// order. Surrogate pairs are reversed as a unit (their high/low order is
/// preserved) so emoji and supplementary-plane characters don't get torn.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/reverse-transact-sql</remarks>
internal sealed class Reverse(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = source.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(value.Type);
        if (!SqlType.IsStringCategory(value.Type))
            throw new NotSupportedException($"REVERSE expects a string operand; got {value.Type}.");

        var input = value.AsString;
        var reversed = new char[input.Length];
        var sourceIndex = 0;
        var destIndex = input.Length;
        while (sourceIndex < input.Length)
        {
            var c = input[sourceIndex];
            if (char.IsHighSurrogate(c) && sourceIndex + 1 < input.Length && char.IsLowSurrogate(input[sourceIndex + 1]))
            {
                destIndex -= 2;
                reversed[destIndex] = c;
                reversed[destIndex + 1] = input[sourceIndex + 1];
                sourceIndex += 2;
            }
            else
            {
                destIndex--;
                reversed[destIndex] = c;
                sourceIndex++;
            }
        }
        return SqlValue.FromString(value.Type, new string(reversed));
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => source.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"REVERSE({source.DebugDisplay()})";
}
