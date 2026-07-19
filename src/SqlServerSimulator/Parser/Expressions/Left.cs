using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LEFT(x, n)</c>: returns the leftmost <c>n</c> characters of
/// <c>x</c>. Negative <c>n</c> raises an error matching SQL Server.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/left-transact-sql</remarks>
internal sealed class Left : Expression
{
    private readonly Expression source;
    private readonly Expression count;

    public Left(ParserContext context)
    {
        this.source = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.count = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var rawSource = source.Run(runtime);
        var n = count.Run(runtime);
        if (rawSource.IsNull || n.IsNull)
            return SqlValue.Null(ResolveResultType(rawSource.Type, runtime.Batch));
        var s = StringScalars.CoerceToVarchar(rawSource, runtime.Batch, "left");
        var resultType = ResolveResultType(s.Type, runtime.Batch);

        var len = StringScalars.CoerceLengthArgument(n);
        if (len < 0)
            throw SimulatedSqlException.NegativeLengthNotAllowed("left", 6);

        var input = s.AsString;
        var result = s.Type.Collation?.IsSupplementaryCharacterAware == true
            ? SupplementaryCharacters.LeftByCodepoints(input, len)
            : len >= input.Length ? input : input[..len];
        return SqlValue.FromString(resultType, result);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResolveResultType(source.GetSqlType(batch, resolveColumnType), batch);

    /// <summary>
    /// LEFT preserves the input's string family; a constant count tightens the
    /// projected width to <c>min(inputWidth, count)</c> (probe-confirmed:
    /// <c>LEFT(varchar(10), 2)</c> → <c>varchar(2)</c>, <c>LEFT(varchar(10),
    /// 20)</c> → <c>varchar(10)</c>). A MAX / LOB input, a non-constant count,
    /// or an unspecified input width leaves the width at the input's.
    /// </summary>
    private SqlType ResolveResultType(SqlType sourceType, BatchContext batch)
    {
        var stringType = StringScalars.ResolveResultType(sourceType, batch);
        if (StringScalars.IsMaxForm(stringType))
            return stringType;
        var inputWidth = StringScalars.DeclaredWidth(stringType);
        return inputWidth > 0 && StringScalars.TryConstantCount(count, out var n)
            ? StringScalars.SizedResultType(stringType, Math.Min(inputWidth, n), batch)
            : stringType;
    }

    internal override string DebugDisplay() => $"LEFT({source.DebugDisplay()}, {count.DebugDisplay()})";
}
