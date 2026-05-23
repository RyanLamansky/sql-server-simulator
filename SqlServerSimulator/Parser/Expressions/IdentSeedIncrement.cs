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
    private readonly string tableName;

    public IdentSeedIncrement(ParserContext context, bool isSeed)
    {
        this.isSeed = isSeed;
        var argument = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.tableName = argument
            .Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), context.Batch))
            .CoerceTo(SqlType.NVarchar)
            .AsString;
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var parts = this.tableName.Split('.');
        var multiPart = new MultiPartName(parts[0]);
        for (var i = 1; i < parts.Length; i++)
            multiPart = multiPart.WithAddedPart(parts[i]);
        if (!runtime.Batch.TryResolveTable(multiPart, out var table))
            return SqlValue.Null(ResultType);
        var identityOrdinal = table.IdentityOrdinal;
        if (identityOrdinal < 0)
            return SqlValue.Null(ResultType);
        var identity = table.Columns[identityOrdinal].Identity!;
        return SqlValue.FromDecimal(ResultType, this.isSeed ? identity.Seed : identity.Increment);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType;

    internal override string DebugDisplay() => $"{(this.isSeed ? "IDENT_SEED" : "IDENT_INCR")}('{this.tableName}')";
}
