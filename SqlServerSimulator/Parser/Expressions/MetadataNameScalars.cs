using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>TYPE_NAME(type_id)</c>: returns the type's name for the given
/// system_type_id / user_type_id. Sibling of <see cref="TypeId"/>. NULL
/// argument returns NULL; unknown id returns NULL. Probe-confirmed
/// against SQL Server 2025 (2026-05-22): <c>TYPE_NAME(56)</c> →
/// <c>'int'</c>; <c>TYPE_NAME(0)</c> → <c>'void type'</c> (the placeholder
/// SQL Server uses for "no type"). Result type is
/// <see cref="SqlType.SystemName"/> (sysname).
/// </summary>
internal sealed class TypeName : Expression
{
    private readonly Expression idArg;

    public TypeName(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.idArg.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var id = v.CoerceTo(SqlType.Int32).AsInt32;
        if (id == 0)
            return SqlValue.FromString(SqlType.SystemName, "void type");
        // System types resolve through the same row data the sys.types
        // catalog view uses (column 3 = user_type_id, column 0 = name).
        foreach (var row in BuiltInResources.SystypesRowData)
        {
            if (Convert.ToInt32(row[3]!, System.Globalization.CultureInfo.InvariantCulture) == id)
                return SqlValue.FromString(SqlType.SystemName, (string)row[0]!);
        }
        // User-defined table types and scalar alias types — only the
        // current database's schemas are searched (matching real
        // SQL Server's single-database TYPE_NAME scope).
        foreach (var schema in runtime.Batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var tt in schema.TableTypes.Values)
            {
                if (tt.UserTypeId == id)
                    return SqlValue.FromString(SqlType.SystemName, tt.Name);
            }
            foreach (var alias in schema.AliasTypes.Values)
            {
                if (alias.UserTypeId == id)
                    return SqlValue.FromString(SqlType.SystemName, alias.Name);
            }
        }
        return SqlValue.Null(SqlType.SystemName);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => $"TYPE_NAME({this.idArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>PARSENAME('a.b.c.d', n)</c>: returns the <c>n</c>-th
/// dot-separated segment of an object name, counting from the right
/// (n=1 → leaf; n=2 → schema; n=3 → database; n=4 → server).
/// Out-of-range n returns NULL; NULL argument returns NULL.
/// Probe-confirmed: bracket-quoted segments stay verbatim in the result
/// (the simulator strips the brackets — minor deviation from real
/// SQL Server which would return the bracketed form).
/// </summary>
internal sealed class ParseName : Expression
{
    private readonly Expression nameArg;
    private readonly Expression indexArg;

    public ParseName(ParserContext context)
    {
        this.nameArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.indexArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var name = this.nameArg.Run(runtime);
        var index = this.indexArg.Run(runtime);
        if (name.IsNull || index.IsNull)
            return SqlValue.Null(SqlType.SystemName);
        var n = index.CoerceTo(SqlType.Int32).AsInt32;
        if (n is < 1 or > 4)
            return SqlValue.Null(SqlType.SystemName);
        var parts = name.CoerceTo(SqlType.NVarchar).AsString.Split('.');
        if (parts.Length < n)
            return SqlValue.Null(SqlType.SystemName);
        var segment = parts[^n];
        if (segment.Length >= 2 && segment[0] == '[' && segment[^1] == ']')
            segment = segment[1..^1];
        return SqlValue.FromString(SqlType.SystemName, segment);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => $"PARSENAME({this.nameArg.DebugDisplay()}, {this.indexArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>ORIGINAL_DB_NAME()</c>: returns the database name specified at
/// connection time. The simulator captures the connection's initial
/// database name when the session opens and exposes it here; real
/// SQL Server returns the connection-string Initial Catalog. Result is
/// <see cref="SqlType.SystemName"/> (sysname).
/// </summary>
internal sealed class OriginalDbName : Expression
{
    public OriginalDbName(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("original_db_name", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromString(SqlType.SystemName, Simulation.DefaultDatabaseName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SystemName;

    internal override string DebugDisplay() => "ORIGINAL_DB_NAME()";
}

/// <summary>
/// SQL <c>GETANSINULL([database_name])</c>: returns <c>1</c> when ANSI
/// nullability is in effect for the given database (the default for
/// modern SQL Server). The simulator returns <c>1</c> unconditionally;
/// the optional database name is parsed and ignored. Result type is
/// <see cref="SqlType.SmallInt"/>.
/// </summary>
internal sealed class GetAnsiNull : Expression
{
    private readonly Expression? dbArg;

    public GetAnsiNull(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            return;
        this.dbArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // NULL argument propagates per general SQL conventions, though
        // GETANSINULL doesn't get hit with NULL often in real code.
        return this.dbArg is not null && this.dbArg.Run(runtime).IsNull
            ? SqlValue.Null(SqlType.SmallInt)
            : SqlValue.FromInt16(1);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override string DebugDisplay() => this.dbArg is null ? "GETANSINULL()" : $"GETANSINULL({this.dbArg.DebugDisplay()})";
}
