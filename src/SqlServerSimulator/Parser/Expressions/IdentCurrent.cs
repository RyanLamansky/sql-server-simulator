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

    private readonly Expression tableName;

    public IdentCurrent(ParserContext context)
    {
        this.tableName = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var identity = IdentityOf(runtime, this.tableName);
        return identity is null ? SqlValue.Null(ResultType) : SqlValue.FromDecimal(ResultType, identity.Current);
    }

    /// <summary>
    /// The identity state of the table <paramref name="tableName"/>
    /// names, evaluated per row — a variable or a column works as well as a
    /// literal — or null when the name is NULL, names no table, or names one
    /// without an identity column, all of which read NULL (probed 2026-09-26
    /// against SQL Server 2025). The name splits on <c>.</c> so
    /// <c>'schema.t'</c> routes through the named schema; bracket-quoted
    /// segments aren't decoded, and either match a literally named object or
    /// miss.
    /// </summary>
    internal static IdentityState? IdentityOf(RuntimeContext runtime, Expression tableName)
    {
        var value = tableName.Run(runtime);
        if (value.IsNull)
            return null;
        var parts = value.CoerceTo(SqlType.NVarchar).AsString.Split('.');
        var multiPart = new MultiPartName(parts[0]);
        for (var i = 1; i < parts.Length; i++)
            multiPart = multiPart.WithAddedPart(parts[i]);
        return runtime.Batch.TryResolveTable(multiPart, out var table) && table.IdentityOrdinal >= 0
            ? table.Columns[table.IdentityOrdinal].Identity
            : null;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = this.tableName.GetSqlType(batch, resolveColumnType);
        return ResultType;
    }

    internal override string DebugDisplay() => $"IDENT_CURRENT({this.tableName.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.tableName);
}
