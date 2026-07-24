using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DB_ID([name])</c>: returns the database id for the named
/// database, or the current database's id when called with no argument.
/// Result type is <see cref="SqlType.SmallInt"/> (smallint) — matches real
/// SQL Server's projected column type for this function.
/// </summary>
/// <remarks>
/// The simulator surfaces ids via <see cref="DatabasesWithIds"/>: the four
/// system databases carry their fixed reserved ids (master = 1, tempdb = 2,
/// model = 3, msdb = 4), and user databases carry the stored id assigned at
/// registration (smallest free id ≥ 5, in creation order with dropped ids
/// reused) — the same value projected by <c>sys.databases.database_id</c>.
/// Unknown name returns NULL; NULL argument returns NULL.
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

    /// <summary>
    /// Pairs each hosted database with its stored <c>database_id</c>
    /// (<see cref="Database.Id"/>): the four system databases carry their fixed
    /// reserved ids (master = 1, tempdb = 2, model = 3, msdb = 4), and every
    /// user database carries the smallest-free id it was assigned at
    /// registration (<see cref="Simulation.RegisterUserDatabase"/>) — user ids
    /// start at 5 in creation order and a dropped database's id is reused by
    /// the next create. Single source of truth for <see cref="DbId"/> /
    /// <see cref="DbName"/>, <c>OBJECT_NAME</c>'s database routing,
    /// <c>sys.databases.database_id</c>, and <c>DBCC SHRINKDATABASE</c>'s
    /// numeric-id form. Yielded in ascending id order.
    /// </summary>
    internal static IEnumerable<(Database Database, short Id)> DatabasesWithIds(Simulation simulation) =>
        simulation.Databases.Values.OrderBy(static d => d.Id).Select(static d => (d, d.Id));

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

/// <summary>
/// SQL <c>FILE_ID('file_name')</c> (smallint) / <c>FILE_IDEX('file_name')</c>
/// (int): the <c>file_id</c> of a logical file in the current database.
/// The simulator models two files per database, mirroring
/// <c>sys.database_files</c>: <c>&lt;db&gt;_Data</c> (file_id 1, primary ROWS)
/// and <c>&lt;db&gt;_Log</c> (file_id 2, LOG). An unknown / NULL file name
/// returns NULL. File-name comparison is trailing-space insensitive (SQL
/// Server's internal <c>=</c>). The two forms differ only in projected result
/// type — probe-confirmed against SQL Server 2025: FILE_ID → smallint,
/// FILE_IDEX → int; both resolve identically over the two-file model.
/// </summary>
internal sealed class FileId : Expression
{
    private readonly Expression nameArg;
    private readonly bool extended;

    public FileId(ParserContext context, bool extended)
    {
        this.extended = extended;
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        SqlType resultType = this.extended ? SqlType.Int32 : SqlType.SmallInt;
        var value = this.nameArg.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(resultType);
        // File-name matching is trailing-space insensitive (SQL Server's
        // internal = comparison); the modeled names carry no trailing spaces,
        // so trimming the argument is sufficient.
        var name = value.CoerceTo(SqlType.NVarchar).AsString.TrimEnd(' ');
        var database = runtime.Batch.CurrentDatabase;
        int fileId;
        if (Collation.Baseline.Equals(name, database.Name + "_Data"))
            fileId = 1;
        else if (Collation.Baseline.Equals(name, database.Name + "_Log"))
            fileId = 2;
        else
            return SqlValue.Null(resultType);
        return this.extended ? SqlValue.FromInt32(fileId) : SqlValue.FromInt16((short)fileId);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.extended ? SqlType.Int32 : SqlType.SmallInt;

    internal override string DebugDisplay() =>
        $"{(this.extended ? "FILE_IDEX" : "FILE_ID")}({this.nameArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>FILE_NAME(file_id)</c>: the logical name of a file in the current
/// database — <c>&lt;db&gt;_Data</c> for file_id 1, <c>&lt;db&gt;_Log</c> for
/// file_id 2 (the two-file model shared with <c>sys.database_files</c> /
/// <see cref="FileId"/> / <see cref="FileProperty"/>). Any other id (0,
/// negative, &gt; 2) or a NULL argument returns NULL. Result type is
/// <see cref="SqlType.SystemName"/> (sysname).
/// </summary>
internal sealed class FileNameLookup : Expression
{
    private readonly Expression idArg;

    public FileNameLookup(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.idArg.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var database = runtime.Batch.CurrentDatabase;
        return value.CoerceTo(SqlType.Int32).AsInt32 switch
        {
            1 => SqlValue.FromString(SqlType.SystemName, database.Name + "_Data"),
            2 => SqlValue.FromString(SqlType.SystemName, database.Name + "_Log"),
            _ => SqlValue.Null(SqlType.SystemName),
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => $"FILE_NAME({this.idArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>FILEGROUP_ID('filegroup_name')</c>: the <c>data_space_id</c> of a
/// filegroup in the current database, read from <see cref="Database.Filegroups"/>
/// (PRIMARY = 1, user filegroups 2, 3, … in registration order). An unknown /
/// NULL name returns NULL. Name lookup is case-insensitive per the database
/// collation. Result type is <see cref="SqlType.SmallInt"/> (smallint) —
/// probe-confirmed against SQL Server 2025.
/// </summary>
internal sealed class FilegroupId : Expression
{
    private readonly Expression nameArg;

    public FilegroupId(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.nameArg.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(SqlType.SmallInt);
        // Filegroup-name matching is trailing-space insensitive (probe-confirmed);
        // registered names carry no trailing spaces, so trim the argument.
        var name = value.CoerceTo(SqlType.NVarchar).AsString.TrimEnd(' ');
        return runtime.Batch.CurrentDatabase.Filegroups.TryGetValue(name, out var id)
            ? SqlValue.FromInt16((short)id)
            : SqlValue.Null(SqlType.SmallInt);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override string DebugDisplay() => $"FILEGROUP_ID({this.nameArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>FILEGROUP_NAME(filegroup_id)</c>: the name of a filegroup in the
/// current database, reverse-looked-up in <see cref="Database.Filegroups"/>.
/// An unknown id (0, negative, or unregistered) or a NULL argument returns
/// NULL. Result type is <see cref="SqlType.SystemName"/> (sysname).
/// </summary>
internal sealed class FilegroupName : Expression
{
    private readonly Expression idArg;

    public FilegroupName(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.idArg.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var requested = value.CoerceTo(SqlType.Int32).AsInt32;
        foreach (var (name, id) in runtime.Batch.CurrentDatabase.Filegroups)
        {
            if (id == requested)
                return SqlValue.FromString(SqlType.SystemName, name);
        }
        return SqlValue.Null(SqlType.SystemName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => $"FILEGROUP_NAME({this.idArg.DebugDisplay()})";
}
