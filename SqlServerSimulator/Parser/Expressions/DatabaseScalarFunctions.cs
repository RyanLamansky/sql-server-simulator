using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DB_ID([name])</c>: returns the database id for the named
/// database, or the current database's id when called with no argument.
/// Result type is <see cref="SqlType.SmallInt"/> (smallint) — matches real
/// SQL Server's projected column type for this function.
/// </summary>
/// <remarks>
/// The simulator allocates ids via <see cref="DatabasesWithIds"/>: the four
/// system databases carry their fixed reserved ids (master = 1, tempdb = 2,
/// model = 3, msdb = 4), and user databases take 5, 6, … in case-insensitive
/// name order — the same convention used to project
/// <c>sys.databases.database_id</c>. Unknown name returns NULL; NULL argument
/// returns NULL.
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
        foreach (var (db, id) in DatabasesWithIds(runtime.Batch.Connection.Simulation))
        {
            if (BuiltInToken.Comparer.Equals(db.Name, targetName))
                return SqlValue.FromInt16(id);
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

    /// <summary>
    /// Pairs each hosted database with its <c>database_id</c>: the four system
    /// databases carry their fixed reserved ids (master = 1, tempdb = 2,
    /// model = 3, msdb = 4, from <see cref="Simulation.SystemDatabaseIds"/>),
    /// and every user database takes 5, 6, … in case-insensitive name order.
    /// Single source of truth for <see cref="DbId"/> / <see cref="DbName"/>,
    /// <c>OBJECT_NAME</c>'s database routing, <c>sys.databases.database_id</c>,
    /// and <c>DBCC SHRINKDATABASE</c>'s numeric-id form. Yielded in
    /// ascending id order (system databases first, then user databases).
    /// </summary>
    internal static IEnumerable<(Database Database, short Id)> DatabasesWithIds(Simulation simulation)
    {
        foreach (var (name, id) in Simulation.SystemDatabaseIds)
        {
            if (simulation.Databases.TryGetValue(name, out var systemDatabase))
                yield return (systemDatabase, id);
        }
        short userId = 5;
        foreach (var db in OrderedDatabases(simulation))
        {
            if (!Simulation.SystemDatabaseNames.Contains(db.Name))
                yield return (db, userId++);
        }
    }

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
        foreach (var (db, id) in DbId.DatabasesWithIds(runtime.Batch.Connection.Simulation))
        {
            if (id == requested)
                return SqlValue.FromString(SqlType.SystemName, db.Name);
        }
        return SqlValue.Null(SqlType.SystemName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => this.idArg is null ? "DB_NAME()" : $"DB_NAME({this.idArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>HAS_DBACCESS('name')</c>: whether the current login can access the
/// named database — <c>1</c> when accessible, <c>0</c> when it exists but is
/// restricted, NULL for an unknown / empty / NULL name. Result type is
/// <see cref="SqlType.Int32"/>. Probe-confirmed against SQL Server 2025
/// (2026-07-14): a normal login reads <c>1</c> for master / tempdb / msdb and
/// any user database, but <c>0</c> for <c>model</c> (the restricted template
/// database); name lookup is case-insensitive and a missing argument raises
/// Msg 174. SSMS calls <c>has_dbaccess('msdb')</c> at connect to decide
/// whether to surface Policy Health / Agent features — the simulator seeds
/// msdb, so it answers <c>1</c> and the feature renders.
/// </summary>
internal sealed class HasDbAccess : Expression
{
    private readonly Expression nameArg;

    public HasDbAccess(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("has_dbaccess", 1);
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var arg = this.nameArg.Run(runtime);
        if (arg.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var name = arg.CoerceTo(SqlType.NVarchar).AsString;
        if (!runtime.Batch.Connection.Simulation.Databases.ContainsKey(name))
            return SqlValue.Null(SqlType.Int32);
        // model is the restricted template database — inaccessible even to a
        // normal login (probe-confirmed). Every other hosted database (system
        // or user) is accessible since the simulator has no per-login
        // database-access model.
        return SqlValue.FromInt32(BuiltInToken.Comparer.Equals(name, Simulation.ModelDatabaseName) ? 0 : 1);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"HAS_DBACCESS({this.nameArg.DebugDisplay()})";
}
