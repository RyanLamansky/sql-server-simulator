using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>RIGHT(x, n)</c>: returns the rightmost <c>n</c> characters of
/// <c>x</c>. Negative <c>n</c> raises an error matching SQL Server.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/right-transact-sql</remarks>
internal sealed class Right : Expression
{
    private readonly Expression source;
    private readonly Expression count;

    public Right(ParserContext context)
    {
        this.source = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.count = Parse(context.MoveNextRequiredReturnSelf());
    }

    internal override bool ParallelSafe => this.source.ParallelSafe && this.count.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var rawSource = source.Run(runtime);
        var n = count.Run(runtime);
        StringScalars.RejectLegacyLob(rawSource, "right");
        if (rawSource.IsNull || n.IsNull)
            return SqlValue.Null(ResolveResultType(rawSource.Type, runtime.Batch));
        var s = StringScalars.CoerceToVarchar(rawSource, runtime.Batch, "right");
        var resultType = ResolveResultType(s.Type, runtime.Batch);

        var len = StringScalars.CoerceLengthArgument(n);
        if (len < 0)
            throw SimulatedSqlException.NegativeLengthNotAllowedAtRuntime(isRight: true);

        var input = s.AsString;
        var result = s.Type.Collation?.IsSupplementaryCharacterAware == true
            ? SupplementaryCharacters.RightByCodepoints(input, len)
            : len >= input.Length ? input : input[(input.Length - len)..];
        return SqlValue.FromString(resultType, result);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResolveResultType(StringScalars.BindArgument(source, batch, resolveColumnType, "right"), batch);

    /// <summary>
    /// RIGHT preserves the input's string family; a constant count tightens the
    /// projected width to <c>min(inputWidth, count)</c> (probe-confirmed,
    /// mirroring <see cref="Left"/>). A MAX / LOB input, a non-constant count,
    /// or an unspecified input width leaves the width at the input's.
    /// </summary>
    private SqlType ResolveResultType(SqlType sourceType, BatchContext batch)
    {
        if (StringScalars.IsConstantNegativeCount(count))
            throw SimulatedSqlException.NegativeLengthNotAllowed("right", 6);
        var stringType = StringScalars.ResolveResultType(sourceType, batch);
        if (StringScalars.IsMaxForm(stringType))
            return stringType;
        var inputWidth = StringScalars.DeclaredWidth(stringType);
        return inputWidth > 0 && StringScalars.TryConstantCount(count, out var n)
            ? StringScalars.SizedResultType(stringType, Math.Min(inputWidth, n), batch)
            : stringType;
    }

    internal override string DebugDisplay() => $"RIGHT({source.DebugDisplay()}, {count.DebugDisplay()})";
}
