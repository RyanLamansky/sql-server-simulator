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
        var raw = source.Run(runtime);
        StringScalars.RejectLegacyLob(raw, "upper");
        if (raw.IsNull)
            return SqlValue.Null(StringScalars.ResolveResultType(raw.Type, runtime.Batch));
        var value = StringScalars.CoerceToVarchar(raw, runtime.Batch, "upper");
        var uppered = value.AsString.ToUpperInvariant();
        return SqlValue.FromString(value.Type, uppered);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        StringScalars.ResolveResultType(StringScalars.BindArgument(source, batch, resolveColumnType, "upper"), batch);

    internal override string DebugDisplay() => $"UPPER({source.DebugDisplay()})";
}
