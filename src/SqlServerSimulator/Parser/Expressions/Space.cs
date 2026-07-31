using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SPACE(count)</c>: returns a <c>varchar</c> consisting of
/// <c>count</c> U+0020 spaces. Probe-confirmed:
/// <list type="bullet">
/// <item><description>NULL or negative count → NULL.</description></item>
/// <item><description>Result type is always <c>varchar</c> (not <c>nvarchar</c>).</description></item>
/// <item><description>Truncated to 8000 chars for the default non-MAX result type.</description></item>
/// </list>
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/space-transact-sql</remarks>
internal sealed class Space(ParserContext context) : Expression
{
    private const int MaxBytes = 8000;
    private readonly Expression count = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var resultType = ResolveResultType(runtime.Batch);
        var countValue = this.count.Run(runtime);
        if (countValue.IsNull)
            return SqlValue.Null(resultType);
        var times = StringScalars.CoerceLengthArgument(countValue);
        if (times < 0)
            return SqlValue.Null(resultType);
        if (times > MaxBytes)
            times = MaxBytes;
        return SqlValue.FromVarchar((VarcharSqlType)resultType, new string(' ', times));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResolveResultType(batch);

    /// <summary>
    /// SPACE is always <c>varchar</c> in the active database collation. A
    /// constant count projects the exact width <c>varchar(min(8000, n))</c>
    /// (<c>SPACE(5)</c> → <c>varchar(5)</c>, <c>SPACE(0)</c> → <c>varchar(1)</c>,
    /// probe-confirmed); a non-constant count falls back to the
    /// <c>varchar(8000)</c> container, matching real.
    /// </summary>
    private SqlType ResolveResultType(BatchContext batch) =>
        StringScalars.TryConstantCount(this.count, out var n)
            ? StringScalars.SizedResultType(SqlType.Varchar, Math.Min(MaxBytes, n), batch)
            : StringScalars.ContainerResultType(SqlType.Varchar, batch);

    internal override string DebugDisplay() => $"SPACE({this.count.DebugDisplay()})";
}
