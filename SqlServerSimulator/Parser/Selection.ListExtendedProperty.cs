using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Built-in system TVF <c>fn_listextendedproperty</c>. Seven args, all
    /// nullable; returns <c>(objtype sysname, objname sysname, name sysname,
    /// value sql_variant)</c> rows filtered against
    /// <see cref="Database.ExtendedProperties"/>. Probe-confirmed shape
    /// against SQL Server 2025 (2026-05-14).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The simulator's filter pipeline supports the common-case combinations
    /// — all-null (DATABASE-level), <c>(SCHEMA, name)</c>, <c>(SCHEMA, dbo,
    /// TABLE/VIEW/PROC/FUNC, name)</c>, <c>(SCHEMA, dbo, TABLE, name, COLUMN,
    /// col)</c>, plus the <c>'default'</c> wildcard at any level-name slot
    /// (expands to all objects of that level-type under the parent). Other
    /// shapes (PARAMETER / INDEX / TRIGGER / CONSTRAINT level types) raise
    /// <see cref="NotSupportedException"/> — extensions for those level types
    /// land when an application needs them.
    /// </para>
    /// <para>
    /// <c>value</c> is surfaced as <c>nvarchar(MAX)</c> since the simulator
    /// doesn't model <c>sql_variant</c>; AW's properties are all nvarchar
    /// so this is lossless for the bacpac use case.
    /// </para>
    /// </remarks>
    public static Selection ParseListExtendedProperty(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // 7 args, each evaluated parse-time-constant (the same restriction
        // STRING_SPLIT's enable_ordinal carries). Real SQL Server's TVF
        // signature constrains them similarly.
        context.MoveNextRequired();
        var nameArg = ParseListExtendedPropertyArg(context);
        var l0TypeArg = ConsumeCommaAndParse(context);
        var l0NameArg = ConsumeCommaAndParse(context);
        var l1TypeArg = ConsumeCommaAndParse(context);
        var l1NameArg = ConsumeCommaAndParse(context);
        var l2TypeArg = ConsumeCommaAndParse(context);
        var l2NameArg = ConsumeCommaAndParse(context);

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var ucNVarchar = NVarcharSqlType.MaxForm;
        SqlType[] schema = [SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, ucNVarchar];
        string[] columnNames = ["objtype", "objname", "name", "value"];

        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateListExtendedPropertyRows(
                schema, nameArg, l0TypeArg, l0NameArg, l1TypeArg, l1NameArg, l2TypeArg, l2NameArg,
                batch, outerResolver));
    }

    private static Expression ConsumeCommaAndParse(ParserContext context)
    {
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        return ParseListExtendedPropertyArg(context);
    }

    private static Expression ParseListExtendedPropertyArg(ParserContext context) => Expression.Parse(context);

    private static IEnumerable<byte[]> EnumerateListExtendedPropertyRows(
        SqlType[] schema,
        Expression nameExpr,
        Expression l0TypeExpr, Expression l0NameExpr,
        Expression l1TypeExpr, Expression l1NameExpr,
        Expression l2TypeExpr, Expression l2NameExpr,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resolver = outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n));
        var runtime = new RuntimeContext(resolver, batch);
        var filter = ResolveListExtendedPropertyFilter(
            batch,
            EvalNullableString(nameExpr, runtime),
            EvalNullableString(l0TypeExpr, runtime),
            EvalNullableString(l0NameExpr, runtime),
            EvalNullableString(l1TypeExpr, runtime),
            EvalNullableString(l1NameExpr, runtime),
            EvalNullableString(l2TypeExpr, runtime),
            EvalNullableString(l2NameExpr, runtime));

        // No-match: a level-name miss (the target doesn't exist) returns
        // zero rows from fn_listextendedproperty (NOT an error — distinct
        // from the sp_addextendedproperty path's Msg 15135).
        if (filter is null)
            yield break;

        var nvMax = NVarcharSqlType.MaxForm;
        foreach (var kvp in batch.CurrentDatabase.ExtendedProperties)
        {
            var key = kvp.Key;
            if (!filter.Matches(key))
                continue;

            // Resolve objtype + objname from the key. objtype is the deepest
            // level type the key carries (SCHEMA / TABLE / VIEW / PROC / FUNC
            // / COLUMN). objname is the leaf name at that level.
            if (!TryResolveListExtendedPropertyDisplayLabels(batch, key, out var objtype, out var objname))
                continue;

            var valueAsNVarchar = kvp.Value.IsNull
                ? SqlValue.Null(nvMax)
                : kvp.Value.CoerceTo(nvMax);
            yield return RowEncoder.EncodeRow(schema, [
                SqlValue.FromSystemName(objtype),
                SqlValue.FromSystemName(objname),
                SqlValue.FromSystemName(key.Name),
                valueAsNVarchar,
            ]);
        }
    }

    private static string? EvalNullableString(Expression expr, RuntimeContext runtime)
    {
        var value = expr.Run(runtime);
        return value.IsNull ? null : value.CoerceTo(NVarcharSqlType.MaxForm).AsString;
    }

    /// <summary>
    /// Resolves the 7-arg filter spec to a target-matching predicate.
    /// Returns null when the level0/1/2 chain names an object that doesn't
    /// exist (probe-confirmed: <c>fn_listextendedproperty</c> returns zero
    /// rows on a missing target instead of raising — distinct from the
    /// sp_addextendedproperty's Msg 15135).
    /// </summary>
    private static ExtendedPropertyListFilter? ResolveListExtendedPropertyFilter(
        BatchContext batch,
        string? nameFilter,
        string? l0Type, string? l0Name,
        string? l1Type, string? l1Name,
        string? l2Type, string? l2Name)
    {
        var f = new ExtendedPropertyListFilter { NameFilter = nameFilter };

        if (l0Type is null)
        {
            f.ClassFilter = 0;
            return f;
        }
        if (!BuiltInToken.Equals(l0Type, "SCHEMA"))
            throw new NotSupportedException($"fn_listextendedproperty level0type '{l0Type}' isn't modeled (only SCHEMA / NULL).");
        if (l0Name is null)
            return null;
        if (!batch.CurrentDatabase.Schemas.TryGetValue(l0Name, out var schema)
            && !BuiltInToken.Equals(l0Name, "default"))
        {
            return null;
        }

        if (l1Type is null)
        {
            f.ClassFilter = 3;
            f.MajorIdFilter = schema?.SchemaId;
            return f;
        }
        if (l1Name is null)
            return null;

        // Level1 = TABLE / VIEW / PROCEDURE / FUNCTION / TYPE.
        // Resolve the target object if a concrete name was passed; the
        // 'default' wildcard fans out across every object of that kind in
        // the parent schema.
        SchemaObject? l1Obj = null;
        if (schema is not null && !BuiltInToken.Equals(l1Name, "default"))
        {
            Span<char> l1Kind = stackalloc char[l1Type.Length];
            _ = l1Type.ToUpperInvariant(l1Kind);
            l1Obj = l1Kind switch
            {
                "TABLE" => schema.HeapTables.TryGetValue(l1Name, out var t) ? t : null,
                "VIEW" => schema.Views.TryGetValue(l1Name, out var v) ? v : null,
                "PROCEDURE" => schema.Procedures.TryGetValue(l1Name, out var p) ? p : null,
                "FUNCTION" => schema.Functions.TryGetValue(l1Name, out var fn) ? fn : null,
                "TYPE" => schema.TableTypes.TryGetValue(l1Name, out var tt) ? tt : null,
                _ => throw new NotSupportedException($"fn_listextendedproperty level1type '{l1Type}' isn't modeled (only TABLE / VIEW / PROCEDURE / FUNCTION / TYPE)."),
            };
            if (l1Obj is null)
                return null;
        }

        if (l2Type is null)
        {
            f.ClassFilter = 1;
            f.MinorIdFilter = 0;
            if (l1Obj is not null)
                f.MajorIdFilter = l1Obj.ObjectId;
            return f;
        }
        if (l2Name is null)
            return null;
        if (!BuiltInToken.Equals(l2Type, "COLUMN"))
            throw new NotSupportedException($"fn_listextendedproperty level2type '{l2Type}' isn't modeled (only COLUMN).");

        f.ClassFilter = 1;
        if (l1Obj is HeapTable table)
        {
            f.MajorIdFilter = table.ObjectId;
            if (!BuiltInToken.Equals(l2Name, "default"))
            {
                for (var i = 0; i < table.Columns.Length; i++)
                {
                    if (BuiltInToken.Equals(table.Columns[i].Name, l2Name))
                    {
                        f.MinorIdFilter = i + 1;
                        return f;
                    }
                }
                return null;
            }
            f.MinorIdMustBeNonZero = true;
            return f;
        }
        // l1Obj null + l2Name 'default' = all columns of all tables in the schema.
        f.MinorIdMustBeNonZero = true;
        return f;
    }

    /// <summary>
    /// Reverse-resolves an <see cref="ExtendedPropertyKey"/> into the
    /// (objtype, objname) display pair for the TVF's row projection.
    /// Returns false when the key references an object that no longer
    /// exists (the dict can outlive a CREATE/DROP TABLE since extended
    /// properties don't participate in the undo log).
    /// </summary>
    private static bool TryResolveListExtendedPropertyDisplayLabels(BatchContext batch, ExtendedPropertyKey key, out string objtype, out string objname)
    {
        objtype = "";
        objname = "";
        switch (key.Class)
        {
            case 0:
                objtype = "DATABASE";
                objname = Database.DefaultSchemaName;
                return true;
            case 3:
                foreach (var s in batch.CurrentDatabase.Schemas.Values)
                {
                    if (s.SchemaId == key.MajorId)
                    {
                        objtype = "SCHEMA";
                        objname = s.Name;
                        return true;
                    }
                }
                return false;
            case 1:
                foreach (var s in batch.CurrentDatabase.Schemas.Values)
                {
                    foreach (var t in s.HeapTables.Values)
                    {
                        if (t.ObjectId == key.MajorId)
                        {
                            if (key.MinorId == 0)
                            {
                                objtype = "TABLE";
                                objname = t.Name;
                                return true;
                            }
                            if (key.MinorId - 1 < t.Columns.Length)
                            {
                                objtype = "COLUMN";
                                objname = t.Columns[key.MinorId - 1].Name;
                                return true;
                            }
                            return false;
                        }
                    }
                    foreach (var v in s.Views.Values)
                    {
                        if (v.ObjectId == key.MajorId)
                        {
                            objtype = "VIEW";
                            objname = v.Name;
                            return true;
                        }
                    }
                    foreach (var p in s.Procedures.Values)
                    {
                        if (p.ObjectId == key.MajorId)
                        {
                            objtype = "PROCEDURE";
                            objname = p.Name;
                            return true;
                        }
                    }
                    foreach (var fn in s.Functions.Values)
                    {
                        if (fn.ObjectId == key.MajorId)
                        {
                            objtype = "FUNCTION";
                            objname = fn.Name;
                            return true;
                        }
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private sealed class ExtendedPropertyListFilter
    {
        public string? NameFilter;
        public byte? ClassFilter;
        public int? MajorIdFilter;
        public int? MinorIdFilter;
        public bool MinorIdMustBeNonZero;

        public bool Matches(ExtendedPropertyKey key) =>
            (this.NameFilter is not { } n || BuiltInToken.Equals(key.Name, n))
            && (this.ClassFilter is not { } c || key.Class == c)
            && (this.MajorIdFilter is not { } m || key.MajorId == m)
            && (this.MinorIdFilter is not { } mi || key.MinorId == mi)
            && !(this.MinorIdMustBeNonZero && key.MinorId == 0);
    }
}
