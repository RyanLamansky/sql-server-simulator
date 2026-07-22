using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LTRIM(x)</c> / <c>LTRIM(x, chars)</c>: strips leading ASCII spaces
/// (U+0020) — or, in the SQL Server 2022+ two-argument form, any of the
/// characters in <c>chars</c> (a set) — from the start of the source value.
/// Other whitespace characters are preserved in the one-argument form,
/// matching SQL Server. A NULL <c>chars</c> yields NULL (probe-confirmed).
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/ltrim-transact-sql</remarks>
internal sealed class LeftTrim : Expression
{
    private readonly Expression source;
    private readonly Expression? trimChars;

    public LeftTrim(ParserContext context)
    {
        this.source = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            context.MoveNextRequired();
            this.trimChars = Parse(context);
        }
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var raw = source.Run(runtime);
        if (raw.IsNull)
            return SqlValue.Null(StringScalars.ResolveResultType(raw.Type, runtime.Batch));
        var value = StringScalars.CoerceToVarchar(raw, runtime.Batch, "ltrim");
        var chars = StringScalars.ResolveTrimCharacters(this.trimChars, runtime);
        if (chars is null)
            return SqlValue.Null(value.Type);
        // An empty explicit set removes nothing (.NET's TrimStart would treat
        // an empty array as "trim whitespace", which is not the set semantics).
        var trimmed = chars.Length == 0 ? value.AsString : value.AsString.TrimStart(chars);
        return SqlValue.FromString(value.Type, trimmed);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        StringScalars.ResolveResultType(source.GetSqlType(batch, resolveColumnType), batch);

    internal override string DebugDisplay() => $"LTRIM({source.DebugDisplay()})";
}
