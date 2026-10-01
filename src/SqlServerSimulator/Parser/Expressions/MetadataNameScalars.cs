using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>TYPE_NAME(type_id)</c>: returns the type's name for the given
/// system_type_id / user_type_id. Sibling of <see cref="TypeId"/>. NULL
/// argument returns NULL; unknown id returns NULL. Probe-confirmed
/// against SQL Server 2025 (2026-05-22): <c>TYPE_NAME(56)</c> →
/// <c>'int'</c>; <c>TYPE_NAME(0)</c> → <c>'void type'</c> (the placeholder
/// SQL Server uses for "no type"). Result type is
/// <see cref="Expression.MetadataNameType"/>.
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
            return SqlValue.Null(MetadataNameType(runtime.Batch));
        var id = ScalarArguments.CoerceToInt(v);
        // Three ids name no sys.types row but answer all the same (probed
        // 2026-09-25 against SQL Server 2025).
        switch (id)
        {
            case 0: return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), "void type");
            case 1: return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), "table");
            case 243: return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), "table type");
        }
        // System types resolve through the same row data the sys.types
        // catalog view uses (column 3 = user_type_id, column 0 = name).
        foreach (var row in BuiltInResources.SystypesRowData)
        {
            if (Convert.ToInt32(row[3]!, System.Globalization.CultureInfo.InvariantCulture) == id)
                return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), (string)row[0]!);
        }
        // User-defined table types and scalar alias types — only the
        // current database's schemas are searched (matching real
        // SQL Server's single-database TYPE_NAME scope).
        foreach (var (_, schema) in runtime.Batch.CurrentDatabase.Schemas)
        {
            foreach (var (_, tt) in schema.TableTypes)
            {
                if (tt.UserTypeId == id)
                    return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), tt.Name);
            }
            foreach (var (_, alias) in schema.AliasTypes)
            {
                if (alias.UserTypeId == id)
                    return SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), alias.Name);
            }
        }
        return SqlValue.Null(MetadataNameType(runtime.Batch));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = AssignmentRules.ArgumentType(this.idArg, SqlType.Int32, batch, resolveColumnType);
        return MetadataNameType(batch);
    }

    internal override string DebugDisplay() => $"TYPE_NAME({this.idArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.idArg);
}

/// <summary>
/// SQL <c>PARSENAME('a.b.c.d', n)</c>: returns the <c>n</c>-th
/// dot-separated segment of an object name, counting from the right
/// (n=1 → leaf; n=2 → schema; n=3 → database; n=4 → server).
/// Out-of-range n returns NULL; NULL argument returns NULL. The name is read
/// the way real reads a multi-part identifier (<see cref="Split"/>), and a
/// name it can't read answers NULL for every part.
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
            return SqlValue.Null(MetadataNameType(runtime.Batch));
        var n = StringScalars.CoerceLengthArgument(index);
        if (n is < 1 or > 4)
            return SqlValue.Null(MetadataNameType(runtime.Batch));
        var parts = Split(name.CoerceTo(SqlType.NVarchar).AsString);
        return parts is null || parts.Count < n || parts[^n] is not { Length: > 0 } segment
            ? SqlValue.Null(MetadataNameType(runtime.Batch))
            : SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), segment);
    }

    /// <summary>
    /// Splits a one- to four-part name at its dots, unquoting a part delimited
    /// by <c>[…]</c> (a doubled <c>]</c> inside) or <c>"…"</c> (a doubled
    /// <c>"</c> inside), or <see langword="null"/> when the text is no such
    /// name: more than four parts, an empty last part (<c>'a.b.'</c>,
    /// <c>''</c>), an unterminated or trailing-garbage quote (<c>'[a'</c>,
    /// <c>'[a]x'</c>), a bracket or quote inside an unquoted part
    /// (<c>'a]'</c>), or a part past 128 characters. An empty part elsewhere is
    /// kept and answers NULL when asked for, and spaces are part of the name
    /// (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    private static List<string>? Split(string text)
    {
        var parts = new List<string>(4);
        var i = 0;
        while (true)
        {
            var part = new System.Text.StringBuilder();
            if (i < text.Length && text[i] is '[' or '"')
            {
                var close = text[i] == '[' ? ']' : '"';
                i++;
                while (true)
                {
                    if (i >= text.Length)
                        return null;
                    if (text[i] == close)
                    {
                        if (i + 1 < text.Length && text[i + 1] == close)
                        {
                            _ = part.Append(close);
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    _ = part.Append(text[i++]);
                }
                if (i < text.Length && text[i] != '.')
                    return null;
            }
            else
            {
                while (i < text.Length && text[i] != '.')
                {
                    if (text[i] is '[' or ']' or '"')
                        return null;
                    _ = part.Append(text[i++]);
                }
            }
            if (part.Length > 128)
                return null;
            parts.Add(part.ToString());
            if (parts.Count > 4)
                return null;
            if (i >= text.Length)
                return parts[^1].Length == 0 ? null : parts;
            i++;
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // The name reads as text, which an xml or sql_variant isn't (Msg 8116,
        // probed 2026-09-25 against SQL Server 2025).
        if (this.nameArg.GetSqlType(batch, resolveColumnType) is (XmlSqlType or SqlVariantSqlType) and var nameType)
            throw SimulatedSqlException.InvalidArgumentDataType(nameType.SqlServerName, 1, "parsename");
        _ = AssignmentRules.ArgumentType(this.indexArg, SqlType.Int32, batch, resolveColumnType);
        return MetadataNameType(batch);
    }

    internal override string DebugDisplay() => $"PARSENAME({this.nameArg.DebugDisplay()}, {this.indexArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.nameArg).Child(this.indexArg);
}

/// <summary>
/// SQL <c>ORIGINAL_DB_NAME()</c>: the database the login asked for — the
/// connection string's Initial Catalog — or the empty string when it named
/// none. Result is <see cref="Expression.MetadataNameType"/>.
/// </summary>
internal sealed class OriginalDbName : Expression
{
    public OriginalDbName(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("original_db_name", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromNVarchar(MetadataNameType(runtime.Batch), runtime.Batch.Connection.OriginalDatabaseName);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => MetadataNameType(batch);

    internal override string DebugDisplay() => "ORIGINAL_DB_NAME()";

    internal override void Describe(NodeShape shape) { }
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

    /// <summary>
    /// 1 for the named database, or the current one when the argument is
    /// omitted or NULL; NULL for a name no database has (probed 2026-09-26
    /// against SQL Server 2025).
    /// </summary>
    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.dbArg?.Run(runtime) is not { IsNull: false } name)
            return SqlValue.FromInt16(1);
        return runtime.Batch.Connection.Simulation.Databases.ContainsKey(name.CoerceTo(SqlType.NVarchar).AsString)
            ? SqlValue.FromInt16(1)
            : SqlValue.Null(SqlType.SmallInt);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override string DebugDisplay() => this.dbArg is null ? "GETANSINULL()" : $"GETANSINULL({this.dbArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.dbArg);
}
