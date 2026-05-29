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

    public override SqlValue Run(RuntimeContext runtime)
    {
        var rawSource = source.Run(runtime);
        var n = count.Run(runtime);
        if (rawSource.IsNull || n.IsNull)
            return SqlValue.Null(StringScalars.ResolveResultType(rawSource.Type, runtime.Batch));
        var s = StringScalars.CoerceToVarchar(rawSource, runtime.Batch, "right");

        var len = StringScalars.CoerceLengthArgument(n);
        if (len < 0)
            throw SimulatedSqlException.NegativeLengthNotAllowed("right", 6);

        var input = s.AsString;
        var result = s.Type.Collation?.IsSupplementaryCharacterAware == true
            ? SupplementaryCharacters.RightByCodepoints(input, len)
            : len >= input.Length ? input : input[(input.Length - len)..];
        return SqlValue.FromString(s.Type, result);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        StringScalars.ResolveResultType(source.GetSqlType(batch, resolveColumnType), batch);

    internal override string DebugDisplay() => $"RIGHT({source.DebugDisplay()}, {count.DebugDisplay()})";
}
