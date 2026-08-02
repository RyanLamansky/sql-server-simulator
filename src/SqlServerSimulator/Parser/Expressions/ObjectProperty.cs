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
        var id = ScalarArguments.CoerceToInt(idValue);
        var prop = propValue.CoerceTo(SqlType.NVarchar).AsString;
        var database = runtime.Batch.CurrentDatabase;
        var result = FindObject(database, id) is { } obj
            ? EvaluateProperty(database, obj, prop)
            : TryFindConstraint(database, id, out var parsesAnExpression)
                ? EvaluateConstraintProperty(parsesAnExpression, prop)
                : null;
        return result is int value ? SqlValue.FromInt32(value) : SqlValue.Null(SqlType.Int32);
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
            foreach (var sn in schema.Synonyms.Values)
                if (sn.ObjectId == id) return sn;
        }
        return null;
    }

    /// <summary>
    /// Resolves a constraint's object id through <see cref="ConstraintLookup"/>
    /// — the ids <c>sys.objects</c> projects as its <c>C</c> / <c>D</c> /
    /// <c>PK</c> / <c>UQ</c> / <c>F</c> rows, none of which is a
    /// <see cref="SchemaObject"/> so <see cref="FindObject(Database, int)"/>
    /// can't reach them.
    /// <paramref name="parsesAnExpression"/> is set for the two families whose
    /// declaration carries an expression (CHECK and DEFAULT), which is the one
    /// property answer that splits the five apart.
    /// </summary>
    internal static bool TryFindConstraint(Database database, int id, out bool parsesAnExpression)
    {
        if (!ConstraintLookup.TryResolveById(database, id, out var constraint))
        {
            parsesAnExpression = false;
            return false;
        }
        parsesAnExpression = constraint.TypeCode is "C" or "D";
        return true;
    }

    /// <summary>
    /// OBJECTPROPERTY's answers for a constraint object id. Probe-confirmed
    /// against SQL Server 2025: every object-kind discriminator answers 0 (a
    /// constraint is resolvable, just none of those kinds), <c>IsEncrypted</c>
    /// and <c>IsMSShipped</c> and <c>IsSystemTable</c> answer 0, and the
    /// module-scoped names answer NULL.
    /// <para>
    /// <c>IsQuotedIdentOn</c> is the interesting one: a CHECK or DEFAULT
    /// constraint answers a constant <b>0</b> — not the creating session's
    /// setting, which is 0 even for one created with <c>QUOTED_IDENTIFIER</c>
    /// ON (probe-confirmed both ways, and uniformly 0 across msdb's 229
    /// shipped constraints) — while a key or foreign-key constraint answers
    /// NULL. <c>IsAnsiNullsOn</c> is NULL for all five.
    /// </para>
    /// </summary>
    internal static int? EvaluateConstraintProperty(bool parsesAnExpression, string property)
    {
        // SSS003: switch on the Span<char> overload rather than allocating an
        // uppercased temp, the same shape EvaluateProperty uses.
        Span<char> upper = stackalloc char[property.Length];
        return property.AsSpan().ToUpperInvariant(upper) switch
        {
            6 => upper switch { "ISVIEW" => 0, _ => null },
            7 => upper switch { "ISTABLE" => 0, _ => null },
            9 => upper switch { "ISTRIGGER" => 0, _ => null },
            11 => upper switch
            {
                "ISENCRYPTED" => 0,
                "ISMSSHIPPED" => 0,
                "ISPROCEDURE" => 0,
                "ISUSERTABLE" => 0,
                _ => null,
            },
            13 => upper switch { "ISSYSTEMTABLE" => 0, _ => null },
            15 => upper switch
            {
                "ISQUOTEDIDENTON" => parsesAnExpression ? 0 : null,
                "ISTABLEFUNCTION" => 0,
                _ => null,
            },
            16 => upper switch
            {
                "ISINLINEFUNCTION" => 0,
                "ISSCALARFUNCTION" => 0,
                _ => null,
            },
            _ => null,
        };
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
                || schema.Sequences.Values.Contains(obj)
                || schema.Synonyms.Values.Contains(obj))
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
    /// <paramref name="database"/> carries the schema dictionaries
    /// <c>IsDeterministic</c>'s transitive module walk resolves through.
    /// </summary>
    internal static int? EvaluateProperty(Database database, SchemaObject obj, string property)
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
                // The creation-time ANSI_NULLS capture, under the spelling
                // that also answers for a table — same kind filter as
                // IsQuotedIdentOn, and NULL for a sequence / synonym /
                // constraint (probe-confirmed). Unlike QUOTED_IDENTIFIER a
                // table's answer is the captured value, not a constant 1.
                "ISANSINULLSON" => obj is HeapTable || IsSqlModule(obj) ? (obj.UsesAnsiNulls ? 1 : 0) : null,
                "ISSCHEMABOUND" => ModuleDeterminism.EvaluateSchemaBound(obj),
                // 0 for every resolvable object — probe-confirmed even for
                // catalog views (real's legacy system-table sense never
                // applies to modeled objects). DacFx's default-constraint
                // populator filters on `= 0`, so a NULL here silently drops
                // every DEFAULT constraint from a bacpac export.
                "ISSYSTEMTABLE" => 0,
                "TABLEHASINDEX" => TableFlag(obj, upper),
                _ => null,
            },
            15 => upper switch
            {
                "ISDETERMINISTIC" => ModuleDeterminism.Evaluate(database, obj),
                // The creation-time QUOTED_IDENTIFIER capture, under the
                // spelling that also answers for a table. Real reports 1 for
                // any table regardless of the creating session and NULL for a
                // sequence / synonym / key constraint (probe-confirmed), which
                // the UsesQuotedIdentifier default and the kind filter here
                // reproduce; the ExecIs… spelling below is module-only.
                "ISQUOTEDIDENTON" => obj is HeapTable || IsSqlModule(obj) ? (obj.UsesQuotedIdentifier ? 1 : 0) : null,
                "ISTABLEFUNCTION" => obj is InlineTableValuedFunction or MultiStatementTableValuedFunction ? 1 : 0,
                _ => null,
            },
            16 => upper switch
            {
                "ISINLINEFUNCTION" => obj is InlineTableValuedFunction ? 1 : 0,
                "ISSCALARFUNCTION" => obj is ScalarFunction ? 1 : 0,
                "TABLEHASIDENTITY" => TableFlag(obj, upper),
                _ => null,
            },
            // The module SET-option snapshot pair, both reading the
            // creation-time capture. Both return NULL for a non-module object
            // — including a table, which the shorter IsAnsiNullsOn /
            // IsQuotedIdentOn spellings answer for (probe-confirmed: the two
            // spellings agree on modules and diverge on tables).
            17 => upper switch
            {
                "EXECISANSINULLSON" => IsSqlModule(obj) ? (obj.UsesAnsiNulls ? 1 : 0) : null,
                "TABLEHASCHECKCNST" => TableFlag(obj, upper),
                _ => null,
            },
            // The rest of the TableHas* family, all 18 characters. Real
            // answers every one of these from the plain OBJECTPROPERTY as
            // well as the EX form (probe-confirmed); only BaseType and
            // Cardinality are genuinely EX-only.
            18 => TableFlag(obj, upper),
            19 => upper switch
            {
                "EXECISQUOTEDIDENTON" => IsSqlModule(obj) ? (obj.UsesQuotedIdentifier ? 1 : 0) : null,
                _ => null,
            },
            // The sp_settriggerorder read-backs, split by name length: the
            // Last… spellings are one character shorter than the First… ones.
            // NULL for a non-trigger, matching real (probe-confirmed against a
            // table's object_id).
            23 => upper switch
            {
                "EXECISLASTDELETETRIGGER" => TriggerOrderFlag(obj, TriggerActions.Delete, first: false),
                "EXECISLASTINSERTTRIGGER" => TriggerOrderFlag(obj, TriggerActions.Insert, first: false),
                "EXECISLASTUPDATETRIGGER" => TriggerOrderFlag(obj, TriggerActions.Update, first: false),
                _ => null,
            },
            24 => upper switch
            {
                "EXECISFIRSTDELETETRIGGER" => TriggerOrderFlag(obj, TriggerActions.Delete, first: true),
                "EXECISFIRSTINSERTTRIGGER" => TriggerOrderFlag(obj, TriggerActions.Insert, first: true),
                "EXECISFIRSTUPDATETRIGGER" => TriggerOrderFlag(obj, TriggerActions.Update, first: true),
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
    /// <summary>
    /// 1 / 0 when <paramref name="obj"/> is a trigger holding (or not holding)
    /// the requested ordering slot for <paramref name="action"/>; NULL for
    /// anything that isn't a trigger.
    /// </summary>
    private static int? TriggerOrderFlag(object? obj, TriggerActions action, bool first) =>
        obj is not Trigger trigger
            ? null
            : ((first ? trigger.FirstForActions : trigger.LastForActions) & action) != 0 ? 1 : 0;

    private static bool IsSqlModule(SchemaObject obj) =>
        obj is Procedure or View or Trigger or ScalarFunction or InlineTableValuedFunction or MultiStatementTableValuedFunction;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    /// <summary>
    /// Adapts <see cref="ObjectPropertyEx.TableFlagByName"/> to this function's
    /// <c>int?</c> result shape: real answers the whole <c>TableHas*</c> family
    /// from the plain <c>OBJECTPROPERTY</c> as well as the EX form
    /// (probe-confirmed), so both route through the one mapping.
    /// </summary>
    private static int? TableFlag(SchemaObject obj, ReadOnlySpan<char> upperName) =>
        ObjectPropertyEx.TableFlagByName(obj, upperName) switch
        {
            true => 1,
            false => 0,
            null => null,
        };

    internal override string DebugDisplay() => $"OBJECTPROPERTY({this.idArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";
}
