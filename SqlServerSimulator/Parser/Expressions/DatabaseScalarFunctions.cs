using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DB_ID([name])</c>: returns the database id for the named
/// database, or the current database's id when called with no argument.
/// Result type is <see cref="SqlType.SmallInt"/> (smallint) — matches real
/// SQL Server's projected column type for this function.
/// </summary>
/// <remarks>
/// The simulator allocates ids by iterating
/// <see cref="Simulation.Databases"/> in case-insensitive name order
/// and assigning 1-based positions on each call — the same convention
/// used to project <c>sys.databases.database_id</c>. Unknown name
/// returns NULL; NULL argument returns NULL.
/// </remarks>
internal sealed class DbId : Expression
{
    private readonly Expression? nameArg;

    public DbId(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var targetName = this.nameArg is null ? runtime.Batch.CurrentDatabase.Name : ResolveNameArgument(runtime);
        if (targetName is null)
            return SqlValue.Null(SqlType.SmallInt);
        short id = 1;
        foreach (var db in OrderedDatabases(runtime.Batch.Connection.Simulation))
        {
            if (BuiltInToken.Comparer.Equals(db.Name, targetName))
                return SqlValue.FromInt16(id);
            id++;
        }
        return SqlValue.Null(SqlType.SmallInt);
    }

    private string? ResolveNameArgument(RuntimeContext runtime)
    {
        var v = this.nameArg!.Run(runtime);
        return v.IsNull ? null : v.CoerceTo(SqlType.NVarchar).AsString;
    }

    internal static IEnumerable<Database> OrderedDatabases(Simulation simulation) =>
        simulation.Databases.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override string DebugDisplay() => this.nameArg is null ? "DB_ID()" : $"DB_ID({this.nameArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>DB_NAME([id])</c>: returns the database name for the given
/// <c>database_id</c>, or the current database's name when called with
/// no argument. NULL argument or unknown id returns NULL. Result type
/// is <see cref="SqlType.SystemName"/> (sysname).
/// </summary>
internal sealed class DbName : Expression
{
    private readonly Expression? idArg;

    public DbName(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.idArg is null)
            return SqlValue.FromString(SqlType.SystemName, runtime.Batch.CurrentDatabase.Name);
        var v = this.idArg.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var requested = v.CoerceTo(SqlType.Int32).AsInt32;
        short id = 1;
        foreach (var db in DbId.OrderedDatabases(runtime.Batch.Connection.Simulation))
        {
            if (id == requested)
                return SqlValue.FromString(SqlType.SystemName, db.Name);
            id++;
        }
        return SqlValue.Null(SqlType.SystemName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => this.idArg is null ? "DB_NAME()" : $"DB_NAME({this.idArg.DebugDisplay()})";
}
