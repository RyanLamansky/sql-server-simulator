using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>RTRIM(x)</c> / <c>RTRIM(x, chars)</c>: strips trailing ASCII spaces —
/// or, in the SQL Server 2022+ two-argument form, any of the characters in
/// <c>chars</c> (a set) — from the end of the source value. A NULL <c>chars</c>
/// yields NULL (probe-confirmed).
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/rtrim-transact-sql</remarks>
internal sealed class RightTrim : Expression
{
    private readonly Expression source;
    private readonly Expression? trimChars;

    public RightTrim(ParserContext context)
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
        StringScalars.RejectLegacyLob(raw, "rtrim");
        if (raw.IsNull)
            return SqlValue.Null(StringScalars.ResolveResultType(raw.Type, runtime.Batch));
        var value = StringScalars.CoerceToVarchar(raw, runtime.Batch, "rtrim");
        var chars = StringScalars.ResolveTrimCharacters(this.trimChars, runtime, "rtrim");
        if (chars is not { } set)
            return SqlValue.FromString(value.Type, StringScalars.TrimSpaces(value.AsString, leading: false, trailing: true));
        if (set.IsNull)
            return SqlValue.Null(value.Type);
        // An empty explicit set removes nothing.
        var setString = set.AsString;
        var trimmed = setString.Length == 0
            ? value.AsString
            : StringScalars.TrimUnderCollation(
                value.AsString,
                setString,
                StringScalars.CollationFor(runtime.Batch, value.Type, set.Type),
                leading: false,
                trailing: true);
        return SqlValue.FromString(value.Type, trimmed);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        StringScalars.ResolveResultType(StringScalars.BindTrimmed(source, trimChars, batch, resolveColumnType, "rtrim"), batch);

    internal override string DebugDisplay() => $"RTRIM({source.DebugDisplay()})";
}
