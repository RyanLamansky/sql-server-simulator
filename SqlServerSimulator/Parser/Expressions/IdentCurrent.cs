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

    private readonly Simulation simulation;

    private readonly string tableName;

    public IdentCurrent(ParserContext context)
    {
        this.simulation = context.Simulation;
        var argument = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.tableName = argument
            .Run(name => throw SimulatedSqlException.InvalidColumnName(name))
            .CoerceTo(SqlType.NVarchar)
            .AsString;
    }

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue)
    {
        if (!this.simulation.ActiveBatch!.CurrentDatabase.HeapTables.TryGetValue(this.tableName, out var table))
            return SqlValue.Null(ResultType);
        var identityOrdinal = table.IdentityOrdinal;
        return identityOrdinal < 0
            ? SqlValue.Null(ResultType)
            : SqlValue.FromDecimal(ResultType, table.Columns[identityOrdinal].Identity!.Current);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => ResultType;

    internal override string DebugDisplay() => $"IDENT_CURRENT('{this.tableName}')";
}
