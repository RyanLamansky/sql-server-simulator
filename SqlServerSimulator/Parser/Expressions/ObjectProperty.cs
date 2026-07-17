using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>OBJECTPROPERTY(object_id, 'property')</c>: returns metadata
/// flags / values for a schema object. Property values come back as
/// <c>int</c> in real SQL Server; the simulator returns <c>int</c> for
/// the boolean Is-X properties and falls through to NULL for unknown
/// properties. The most common boolean checks (IsTable, IsView,
/// IsProcedure, IsTrigger, IsScalarFunction, IsTableFunction) are
/// supported.
/// </summary>
internal sealed class ObjectProperty : Expression
{
    private readonly Expression idArg;
    private readonly Expression propertyArg;

    public ObjectProperty(ParserContext context)
    {
        this.idArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.propertyArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var idValue = this.idArg.Run(runtime);
        var propValue = this.propertyArg.Run(runtime);
        if (idValue.IsNull || propValue.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var id = idValue.CoerceTo(SqlType.Int32).AsInt32;
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;
        var obj = FindObject(runtime.Batch.CurrentDatabase, id);
        return obj is null ? SqlValue.Null(SqlType.Int32)
            : EvaluateProperty(obj, prop) is int result
                ? SqlValue.FromInt32(result)
                : SqlValue.Null(SqlType.Int32);
    }

    internal static SchemaObject? FindObject(Database database, int id)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var t in schema.HeapTables.Values)
                if (t.ObjectId == id) return t;
            foreach (var v in schema.Views.Values)
                if (v.ObjectId == id) return v;
            foreach (var p in schema.Procedures.Values)
                if (p.ObjectId == id) return p;
            foreach (var f in schema.Functions.Values)
                if (f.ObjectId == id) return f;
            foreach (var tr in schema.Triggers.Values)
                if (tr.ObjectId == id) return tr;
            foreach (var s in schema.Sequences.Values)
                if (s.ObjectId == id) return s;
        }
        return null;
    }

    /// <summary>
    /// Finds the schema that owns <paramref name="obj"/>, or returns
    /// <c>null</c> if the object isn't reachable through the database's
    /// per-schema dictionaries. Used by OBJECTPROPERTYEX's <c>SchemaId</c>
    /// property; the lookup is linear (no back-pointer on <see cref="SchemaObject"/>).
    /// </summary>
    internal static Schema? FindOwningSchema(Database database, SchemaObject obj)
    {
        foreach (var schema in database.Schemas.Values)
        {
            if (schema.HeapTables.Values.Contains(obj)
                || schema.Views.Values.Contains(obj)
                || schema.Procedures.Values.Contains(obj)
                || schema.Functions.Values.Contains(obj)
                || schema.Triggers.Values.Contains(obj)
                || schema.Sequences.Values.Contains(obj))
            {
                return schema;
            }
        }
        return null;
    }

    /// <summary>
    /// Boolean Is-X property dispatch shared with <c>OBJECTPROPERTYEX</c>.
    /// Returns <c>1</c> / <c>0</c> for the recognized properties, <c>null</c>
    /// for unknown names. Property-name comparison is case-insensitive via
    /// the SSS003-friendly <see cref="ReadOnlySpan{T}"/> overload.
    /// </summary>
    internal static int? EvaluateProperty(SchemaObject obj, string property)
    {
        // Boolean Is-X checks based on concrete type. Returns 1 if true,
        // 0 if false, NULL for unknown property names (matching real
        // SQL Server's convention). SSS003: use the Span<char> overload
        // to avoid the temp-string alloc in the switch.
        Span<char> upper = stackalloc char[property.Length];
        return property.AsSpan().ToUpperInvariant(upper) switch
        {
            6 => upper switch { "ISVIEW" => obj is View ? 1 : 0, _ => null },
            7 => upper switch { "ISTABLE" => obj is HeapTable ? 1 : 0, _ => null },
            9 => upper switch
            {
                "ISTRIGGER" => obj is Trigger ? 1 : 0,
                _ => null,
            },
            11 => upper switch
            {
                // IsEncrypted is module-scoped: 0 for any SQL module (WITH
                // ENCRYPTION isn't modeled), NULL for non-module objects —
                // probe-confirmed (view → 0, table → NULL). DacFx enumerates
                // encrypted procedures with `IsEncrypted = 1 OR IsEncrypted
                // IS NULL`, so the NULL-for-unknown fallback enrolled every
                // procedure as encrypted.
                "ISENCRYPTED" => IsSqlModule(obj) ? 0 : null,
                "ISMSSHIPPED" => 0,
                "ISPROCEDURE" => obj is Procedure ? 1 : 0,
                "ISUSERTABLE" => obj is HeapTable ? 1 : 0,
                _ => null,
            },
            13 => upper switch
            {
                "ISSCHEMABOUND" => 0,
                // 0 for every resolvable object — probe-confirmed even for
                // catalog views (real's legacy system-table sense never
                // applies to modeled objects). DacFx's default-constraint
                // populator filters on `= 0`, so a NULL here silently drops
                // every DEFAULT constraint from a bacpac export.
                "ISSYSTEMTABLE" => 0,
                _ => null,
            },
            15 => upper switch
            {
                "ISDETERMINISTIC" => obj is ScalarFunction ? 1 : 0,
                "ISTABLEFUNCTION" => obj is InlineTableValuedFunction or MultiStatementTableValuedFunction ? 1 : 0,
                _ => null,
            },
            16 => upper switch
            {
                "ISINLINEFUNCTION" => obj is InlineTableValuedFunction ? 1 : 0,
                "ISSCALARFUNCTION" => obj is ScalarFunction ? 1 : 0,
                _ => null,
            },
            // The module SET-option snapshot pair: every simulator module is
            // created under QUOTED_IDENTIFIER ON / ANSI_NULLS ON (mirroring
            // sys.sql_modules' constant uses_quoted_identifier /
            // uses_ansi_nulls), and real returns NULL for non-module objects
            // (probe-confirmed table → NULL).
            17 => upper switch
            {
                "EXECISANSINULLSON" => IsSqlModule(obj) ? 1 : null,
                _ => null,
            },
            19 => upper switch
            {
                "EXECISQUOTEDIDENTON" => IsSqlModule(obj) ? 1 : null,
                _ => null,
            },
            _ => null,
        };
    }

    /// <summary>
    /// True for objects carrying a SQL module body (the objects
    /// <c>sys.sql_modules</c> rows exist for): procedures, views, triggers,
    /// and the function family. Module-scoped OBJECTPROPERTY names return
    /// NULL for everything else.
    /// </summary>
    private static bool IsSqlModule(SchemaObject obj) =>
        obj is Procedure or View or Trigger or ScalarFunction or InlineTableValuedFunction or MultiStatementTableValuedFunction;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"OBJECTPROPERTY({this.idArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
