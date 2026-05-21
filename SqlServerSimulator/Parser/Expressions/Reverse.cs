using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>REVERSE(x)</c>: returns the source with its characters in reverse
/// order. Reverses by code unit under non-<c>_SC_</c> collations (surrogate
/// pairs are split — the high/low halves swap positions) and by codepoint
/// under <c>_SC_</c> collations (surrogate pairs stay intact). Probe-
/// confirmed against SQL Server 2025: <c>REVERSE(N'😀X')</c> on a non-SC
/// collation returns <c>X</c> followed by the swapped surrogate bytes,
/// matching <c>0x580000DE3DD8</c>; the same call on
/// <c>Latin1_General_100_CI_AS_SC_UTF8</c> returns <c>X</c> followed by an
/// intact emoji (<c>0x58003DD800DE</c>).
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
        var reversed = value.Type.Collation?.IsSupplementaryCharacterAware == true
            ? SupplementaryCharacters.ReverseByCodepoints(input)
            : SupplementaryCharacters.ReverseByCodeUnits(input);
        return SqlValue.FromString(value.Type, reversed);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => source.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"REVERSE({source.DebugDisplay()})";
}
