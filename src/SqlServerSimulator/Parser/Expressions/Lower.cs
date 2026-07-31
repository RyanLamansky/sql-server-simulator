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
        var raw = source.Run(runtime);
        StringScalars.RejectLegacyLob(raw, "lower");
        if (raw.IsNull)
            return SqlValue.Null(StringScalars.ResolveResultType(raw.Type, runtime.Batch));
        var value = StringScalars.CoerceToVarchar(raw, runtime.Batch, "lower");
        var lowered = value.AsString.ToLower(CultureInfo.InvariantCulture);
        return SqlValue.FromString(value.Type, lowered);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        StringScalars.ResolveResultType(source.GetSqlType(batch, resolveColumnType), batch);

    internal override string DebugDisplay() => $"LOWER({source.DebugDisplay()})";
}
