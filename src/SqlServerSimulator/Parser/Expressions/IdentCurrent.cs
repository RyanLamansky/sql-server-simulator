using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>IDENT_CURRENT('table')</c>: returns the table's last generated
/// identity value (or its seed if no row has yet been inserted) as
/// <c>numeric(38, 0)</c>. Returns NULL when the table doesn't exist or
/// has no identity column. Visible across sessions on real SQL Server
/// (the high-water mark is per-table, not per-connection); the simulator
/// matches because <see cref="IdentityState"/> lives on the table.
/// </summary>
internal sealed class IdentCurrent : Expression
{
    private static readonly SqlType ResultType = SqlType.GetDecimal(38, 0);

    private readonly string tableName;

    public IdentCurrent(ParserContext context)
    {
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
        // Argument is the textual object name; split on '.' so
        // `IDENT_CURRENT('schema.t')` routes through the named schema. Bracket-
        // quoted segments aren't decoded — they'll show up with their brackets
        // in the segment and either match a literally-named identifier or miss
        // (returning NULL, matching real SQL Server's no-such-table behavior).
        var parts = this.tableName.Split('.');
        var multiPart = new MultiPartName(parts[0]);
        for (var i = 1; i < parts.Length; i++)
            multiPart = multiPart.WithAddedPart(parts[i]);
        if (!runtime.Batch.TryResolveTable(multiPart, out var table))
            return SqlValue.Null(ResultType);
        var identityOrdinal = table.IdentityOrdinal;
        return identityOrdinal < 0
            ? SqlValue.Null(ResultType)
            : SqlValue.FromDecimal(ResultType, table.Columns[identityOrdinal].Identity!.Current);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType;

    internal override string DebugDisplay() => $"IDENT_CURRENT('{this.tableName}')";
}
