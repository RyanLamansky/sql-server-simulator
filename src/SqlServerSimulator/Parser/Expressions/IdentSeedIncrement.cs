using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>IDENT_SEED('table')</c> and <c>IDENT_INCR('table')</c>: read the
/// declared seed / increment values for the named table's identity
/// column. Both project as <c>numeric(38, 0)</c>. Returns NULL when the
/// table doesn't exist or has no identity column. Sibling of
/// <see cref="IdentCurrent"/>; the seed/increment values live on
/// <see cref="IdentityState"/> alongside the running high-water mark.
/// </summary>
internal sealed class IdentSeedIncrement : Expression
{
    private static readonly SqlType ResultType = SqlType.GetDecimal(38, 0);

    private readonly bool isSeed;
    private readonly Expression tableName;

    public IdentSeedIncrement(ParserContext context, bool isSeed)
    {
        this.isSeed = isSeed;
        this.tableName = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        IdentCurrent.IdentityOf(runtime, this.tableName) is { } identity
            ? SqlValue.FromDecimal(ResultType, this.isSeed ? identity.Seed : identity.Increment)
            : SqlValue.Null(ResultType);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = this.tableName.GetSqlType(batch, resolveColumnType);
        return ResultType;
    }

    internal override string DebugDisplay() => $"{(this.isSeed ? "IDENT_SEED" : "IDENT_INCR")}({this.tableName.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Local(this.isSeed).Child(this.tableName);
}
