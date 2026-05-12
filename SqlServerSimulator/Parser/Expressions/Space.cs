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
        var countValue = this.count.Run(runtime);
        if (countValue.IsNull)
            return SqlValue.Null(SqlType.Varchar);
        var times = countValue.CoerceTo(SqlType.Int32).AsInt32;
        if (times < 0)
            return SqlValue.Null(SqlType.Varchar);
        if (times > MaxBytes)
            times = MaxBytes;
        return SqlValue.FromVarchar(new string(' ', times));
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Varchar;

    internal override string DebugDisplay() => $"SPACE({this.count.DebugDisplay()})";
}
