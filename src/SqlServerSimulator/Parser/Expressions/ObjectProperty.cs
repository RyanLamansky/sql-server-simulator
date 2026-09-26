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
            : TryFindConstraint(database, id, out var constraint)
                ? EvaluateConstraintProperty(constraint, prop)
                : BuiltInResources.TryResolveSystemObject(id, out var system)
                    ? EvaluateSystemObjectProperty(system, prop)
                    : null;
        return result is int value ? SqlValue.FromInt32(value) : SqlValue.Null(SqlType.Int32);
    }

    /// <summary>
    /// The properties a system object answers: shipped, and a view or a
    /// (possibly extended) procedure, never a table; anything else is NULL
    /// (probed 2026-09-24 for sys.tables and sys.sp_help).
    /// </summary>
    internal static int? EvaluateSystemObjectProperty(BuiltInResources.SystemObject system, string property)
    {
        Span<char> upper = stackalloc char[Math.Min(property.Length, 32)];
        _ = property.AsSpan(0, upper.Length).ToUpperInvariant(upper);
        return upper switch
        {
            "ISEXTENDEDPROC" => system.Type == "X " ? 1 : 0,
            "ISMSSHIPPED" => 1,
            "ISPROCEDURE" => system.Type == "P " ? 1 : 0,
            "ISSYSTEMTABLE" or "ISTABLE" or "ISUSERTABLE" => 0,
            "ISVIEW" => system.Type == "V " ? 1 : 0,
            _ => null,
        };
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
            foreach (var d in schema.Defaults.Values)
                if (d.ObjectId == id) return d;
            foreach (var r in schema.Rules.Values)
                if (r.ObjectId == id) return r;
        }
        return null;
    }

    /// <summary>
    /// Resolves a constraint's object id through <see cref="ConstraintLookup"/>
    /// — the ids <c>sys.objects</c> projects as its <c>C</c> / <c>D</c> /
    /// <c>PK</c> / <c>UQ</c> / <c>F</c> rows, none of which is a
    /// <see cref="SchemaObject"/> so <see cref="FindObject(Database, int)"/>
    /// can't reach them.
    /// </summary>
    internal static bool TryFindConstraint(Database database, int id, out ConstraintLookup.ConstraintReference constraint) =>
        ConstraintLookup.TryResolveById(database, id, out constraint);

    /// <summary>
    /// OBJECTPROPERTY's answers for a constraint object id (probed 2026-09-26
    /// against SQL Server 2025): every object-kind discriminator answers 0 but
    /// the constraint's own kind and <c>IsConstraint</c>, the <c>Cnst*</c>
    /// family answers from the constraint, the module-scoped names answer
    /// NULL, and so does <c>IsEncrypted</c> for a key or foreign key.
    /// <para>
    /// <c>CnstIsColumn</c> is 1 for a DEFAULT and for a CHECK or foreign key
    /// over one column, however it was declared, and 0 for a key even over
    /// one. <c>IsQuotedIdentOn</c> is the other interesting one: a CHECK or
    /// DEFAULT constraint answers a constant <b>0</b> — not the creating
    /// session's setting — while a key or foreign-key constraint answers
    /// NULL. <c>IsAnsiNullsOn</c> is NULL for all five.
    /// </para>
    /// </summary>
    internal static int? EvaluateConstraintProperty(ConstraintLookup.ConstraintReference constraint, string property)
    {
        var typeCode = constraint.TypeCode;
        var table = constraint.Table;
        var key = typeCode is "PK" or "UQ" ? table.KeyConstraints.Find(candidate => candidate.ObjectId == constraint.ObjectId) : null;
        var foreignKey = typeCode == "F" ? table.OutgoingForeignKeys.Find(candidate => candidate.ObjectId == constraint.ObjectId) : null;
        var check = typeCode == "C" ? table.CheckConstraints.Find(candidate => candidate.ObjectId == constraint.ObjectId) : null;
        Span<char> upper = stackalloc char[property.Length];
        return upper[..property.AsSpan().ToUpperInvariant(upper)] switch
        {
            "CNSTISCLUSTKEY" => Flag(key is { IsClustered: true }),
            "CNSTISCOLUMN" => Flag(typeCode == "D" || check is { InlineColumn: not null } || foreignKey is { ChildColumnOrdinals.Length: 1 }),
            "CNSTISDELETECASCADE" => Flag(foreignKey is { DeleteAction: ReferentialAction.Cascade }),
            "CNSTISDISABLED" => Flag(check is { IsDisabled: true } || foreignKey is { IsDisabled: true }),
            "CNSTISNONCLUSTKEY" => Flag(key is { IsClustered: false }),
            "CNSTISNOTREPL" => Flag(check is { NotForReplication: true } || foreignKey is { NotForReplication: true }),
            "CNSTISNOTTRUSTED" => Flag(check is { IsNotTrusted: true } || foreignKey is { IsNotTrusted: true }),
            "CNSTISUPDATECASCADE" => Flag(foreignKey is { UpdateAction: ReferentialAction.Cascade }),
            "ISCHECKCNST" => Flag(typeCode == "C"),
            "ISCONSTRAINT" => 1,
            "ISDEFAULTCNST" => Flag(typeCode == "D"),
            "ISENCRYPTED" => typeCode is "C" or "D" ? 0 : null,
            "ISFOREIGNKEY" => Flag(typeCode == "F"),
            "ISPRIMARYKEY" => Flag(typeCode == "PK"),
            "ISQUOTEDIDENTON" => typeCode is "C" or "D" ? 0 : null,
            "ISUNIQUECNST" => Flag(typeCode == "UQ"),
            "ISDEFAULT" or "ISEXECUTED" or "ISEXTENDEDPROC" or "ISINLINEFUNCTION" or "ISMSSHIPPED" or "ISPROCEDURE" or "ISQUEUE"
                or "ISREPLPROC" or "ISRULE" or "ISSCALARFUNCTION" or "ISSYSTEMTABLE" or "ISTABLE" or "ISTABLEFUNCTION" or "ISTRIGGER"
                or "ISUSERTABLE" or "ISVIEW" => 0,
            "OWNERID" => constraint.Schema.PrincipalId,
            "SCHEMAID" => constraint.Schema.SchemaId,
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
                || schema.Synonyms.Values.Contains(obj)
                || schema.Defaults.Values.Contains(obj)
                || schema.Rules.Values.Contains(obj))
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
        // Real answers each property for the object kinds it concerns and NULL
        // for the rest (probed 2026-09-26 against SQL Server 2025, every
        // documented property over every modeled kind): the Is* kind flags for
        // every object, the Exec* family for everything that executes (a
        // procedure, function, trigger or view), the Has*Trigger family for a
        // table or view, and the Table* family for a table or table-valued
        // function — the full-text members also for an indexed view.
        // SSS003: the Span<char> case-fold avoids the temp-string alloc.
        Span<char> upper = stackalloc char[property.Length];
        var name = upper[..property.AsSpan().ToUpperInvariant(upper)];
        var executes = obj is Procedure or View or Trigger or UserDefinedFunction;
        var trigger = obj as Trigger;
        return name switch
        {
            "EXECISAFTERTRIGGER" => executes ? Flag(trigger is { Timing: TriggerTiming.After }) : null,
            // The module SET-option snapshot pair, both reading the
            // creation-time capture. Both return NULL for a non-module object
            // — including a table, which the shorter IsAnsiNullsOn /
            // IsQuotedIdentOn spellings answer for (probe-confirmed: the two
            // spellings agree on modules and diverge on tables).
            "EXECISANSINULLSON" => IsSqlModule(obj) ? (obj.UsesAnsiNulls ? 1 : 0) : null,
            "EXECISDELETETRIGGER" => executes ? Flag(trigger is not null && (trigger.Actions & TriggerActions.Delete) != 0) : null,
            // The sp_settriggerorder read-backs.
            "EXECISFIRSTDELETETRIGGER" => executes ? TriggerOrderFlag(obj, TriggerActions.Delete, first: true) ?? 0 : null,
            "EXECISFIRSTINSERTTRIGGER" => executes ? TriggerOrderFlag(obj, TriggerActions.Insert, first: true) ?? 0 : null,
            "EXECISFIRSTUPDATETRIGGER" => executes ? TriggerOrderFlag(obj, TriggerActions.Update, first: true) ?? 0 : null,
            "EXECISINSERTTRIGGER" => executes ? Flag(trigger is not null && (trigger.Actions & TriggerActions.Insert) != 0) : null,
            "EXECISINSTEADOFTRIGGER" => executes ? Flag(trigger is { Timing: TriggerTiming.InsteadOf }) : null,
            "EXECISLASTDELETETRIGGER" => executes ? TriggerOrderFlag(obj, TriggerActions.Delete, first: false) ?? 0 : null,
            "EXECISLASTINSERTTRIGGER" => executes ? TriggerOrderFlag(obj, TriggerActions.Insert, first: false) ?? 0 : null,
            "EXECISLASTUPDATETRIGGER" => executes ? TriggerOrderFlag(obj, TriggerActions.Update, first: false) ?? 0 : null,
            "EXECISQUOTEDIDENTON" => IsSqlModule(obj) ? (obj.UsesQuotedIdentifier ? 1 : 0) : null,
            "EXECISSTARTUP" or "EXECISWITHNATIVECOMPILATION" => executes ? 0 : null,
            "EXECISTRIGGERDISABLED" => executes ? Flag(trigger is { IsDisabled: true }) : null,
            "EXECISTRIGGERNOTFORREPL" => executes ? Flag(trigger is { NotForReplication: true }) : null,
            "EXECISUPDATETRIGGER" => executes ? Flag(trigger is not null && (trigger.Actions & TriggerActions.Update) != 0) : null,
            "HASAFTERTRIGGER" => TriggerPresence(database, obj, TriggerActions.Insert | TriggerActions.Update | TriggerActions.Delete, TriggerTiming.After),
            "HASDELETETRIGGER" => TriggerPresence(database, obj, TriggerActions.Delete, timing: null),
            "HASINSERTTRIGGER" => TriggerPresence(database, obj, TriggerActions.Insert, timing: null),
            "HASINSTEADOFTRIGGER" => TriggerPresence(database, obj, TriggerActions.Insert | TriggerActions.Update | TriggerActions.Delete, TriggerTiming.InsteadOf),
            "HASUPDATETRIGGER" => TriggerPresence(database, obj, TriggerActions.Update, timing: null),
            // The creation-time ANSI_NULLS capture, under the spelling that
            // also answers for a table — same kind filter as IsQuotedIdentOn,
            // and NULL for a sequence / synonym / constraint (probe-confirmed).
            // Unlike QUOTED_IDENTIFIER a table's answer is the captured value,
            // not a constant 1.
            "ISANSINULLSON" => obj is HeapTable || IsSqlModule(obj) ? (obj.UsesAnsiNulls ? 1 : 0) : null,
            // Only a constraint id answers 1 to the constraint kinds; see
            // EvaluateConstraintProperty.
            "ISCHECKCNST" or "ISCONSTRAINT" or "ISDEFAULTCNST" or "ISFOREIGNKEY" or "ISPRIMARYKEY" or "ISUNIQUECNST" => 0,
            "ISDEFAULT" => Flag(obj is DefaultObject),
            "ISDETERMINISTIC" => ModuleDeterminism.Evaluate(database, obj),
            // IsEncrypted is module-scoped: whether a SQL module was created
            // WITH ENCRYPTION, NULL for non-module objects — probe-confirmed
            // (view → 0, table → NULL). DacFx enumerates encrypted procedures
            // with `IsEncrypted = 1 OR IsEncrypted IS NULL`, so the
            // NULL-for-unknown fallback enrolled every procedure as encrypted.
            "ISENCRYPTED" => IsSqlModule(obj) ? Flag(obj.DefinitionText is null) : obj is BindableObject ? 0 : null,
            "ISEXECUTED" => Flag(executes),
            // Never answered 1 by a modeled kind: an extended procedure, a
            // shipped object, a Service Broker queue, a replication procedure.
            // IsSystemTable is 0 even for a catalog view; DacFx's
            // default-constraint populator filters on `= 0`, so a NULL here
            // silently drops every DEFAULT constraint from a bacpac export.
            "ISEXTENDEDPROC" or "ISMSSHIPPED" or "ISQUEUE" or "ISREPLPROC" or "ISSYSTEMTABLE" => 0,
            "ISINDEXABLE" => obj switch
            {
                HeapTable => 1,
                View view => Flag(view.IsSchemaBound),
                _ => null,
            },
            "ISINDEXED" => obj switch
            {
                HeapTable table => Flag(table.Indexes.Count > 0 || table.KeyConstraints.Count > 0),
                View view => Flag(view.Indexes.Count > 0),
                _ => null,
            },
            "ISINLINEFUNCTION" => Flag(obj is InlineTableValuedFunction),
            "ISPROCEDURE" => Flag(obj is Procedure),
            // The creation-time QUOTED_IDENTIFIER capture, under the spelling
            // that also answers for a table. Real reports 1 for any table
            // regardless of the creating session and NULL for a sequence /
            // synonym / key constraint (probe-confirmed), which the
            // UsesQuotedIdentifier default and the kind filter here reproduce.
            // A CREATE DEFAULT / CREATE RULE object answers a constant 0, as a
            // CHECK or DEFAULT constraint does.
            "ISQUOTEDIDENTON" => obj is BindableObject ? 0
                : obj is HeapTable || IsSqlModule(obj) ? (obj.UsesQuotedIdentifier ? 1 : 0) : null,
            "ISRULE" => Flag(obj is RuleObject),
            "ISSCALARFUNCTION" => Flag(obj is ScalarFunction or ClrScalarFunction),
            "ISSCHEMABOUND" => ModuleDeterminism.EvaluateSchemaBound(obj),
            // Real can verify a view's or a function's precision and
            // determinism exactly when it is schema-bound.
            "ISSYSTEMVERIFIED" => obj is View or UserDefinedFunction ? ModuleDeterminism.EvaluateSchemaBound(obj) : null,
            "ISTABLE" or "ISUSERTABLE" => Flag(obj is HeapTable),
            "ISTABLEFUNCTION" => Flag(obj is InlineTableValuedFunction or MultiStatementTableValuedFunction),
            "ISTRIGGER" => Flag(obj is Trigger),
            "ISVIEW" => Flag(obj is View),
            "OWNERID" => FindOwningSchema(database, obj)?.PrincipalId,
            "SCHEMAID" => FindOwningSchema(database, obj)?.SchemaId,
            "TABLEDELETETRIGGER" => FirstTriggerFor(database, obj, TriggerActions.Delete),
            "TABLEDELETETRIGGERCOUNT" => TableTriggerCount(database, obj, TriggerActions.Delete),
            "TABLEINSERTTRIGGER" => FirstTriggerFor(database, obj, TriggerActions.Insert),
            "TABLEINSERTTRIGGERCOUNT" => TableTriggerCount(database, obj, TriggerActions.Insert),
            "TABLEUPDATETRIGGER" => FirstTriggerFor(database, obj, TriggerActions.Update),
            "TABLEUPDATETRIGGERCOUNT" => TableTriggerCount(database, obj, TriggerActions.Update),
            _ => TableFlag(database, obj, name),
        };
    }

    private static int Flag(bool value) => value ? 1 : 0;

    /// <summary>
    /// <c>Has*Trigger</c>: whether a trigger with one of
    /// <paramref name="actions"/> (and <paramref name="timing"/>, when given)
    /// sits on the table or view; NULL for any other kind.
    /// </summary>
    private static int? TriggerPresence(Database database, SchemaObject obj, TriggerActions actions, TriggerTiming? timing)
    {
        if (obj is not (HeapTable or View))
            return null;
        foreach (var trigger in TriggersOn(database, obj))
        {
            if ((trigger.Actions & actions) != 0 && (timing is null || trigger.Timing == timing))
                return 1;
        }
        return 0;
    }

    /// <summary>
    /// <c>Table*TriggerCount</c>: the table's AFTER triggers for
    /// <paramref name="action"/>, disabled ones included; 0 for a table-valued
    /// function, NULL for any other kind.
    /// </summary>
    private static int? TableTriggerCount(Database database, SchemaObject obj, TriggerActions action)
    {
        if (obj is InlineTableValuedFunction or MultiStatementTableValuedFunction)
            return 0;
        if (obj is not HeapTable)
            return null;
        var count = 0;
        foreach (var trigger in TriggersOn(database, obj))
        {
            if ((trigger.Actions & action) != 0 && trigger.Timing == TriggerTiming.After)
                count++;
        }
        return count;
    }

    /// <summary>
    /// <c>Table*Trigger</c>: the object id of the table's trigger for
    /// <paramref name="action"/> — the one <c>sp_settriggerorder</c> made
    /// first, else the lowest id of any timing — or NULL when it has none.
    /// </summary>
    private static int? FirstTriggerFor(Database database, SchemaObject obj, TriggerActions action)
    {
        if (obj is not HeapTable)
            return null;
        Trigger? first = null;
        foreach (var trigger in TriggersOn(database, obj))
        {
            if ((trigger.Actions & action) == 0)
                continue;
            if ((trigger.FirstForActions & action) != 0)
                return trigger.ObjectId;
            if (first is null || trigger.ObjectId < first.ObjectId)
                first = trigger;
        }
        return first?.ObjectId;
    }

    private static IEnumerable<Trigger> TriggersOn(Database database, SchemaObject parent)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values)
            {
                if (ReferenceEquals(trigger.Parent, parent))
                    yield return trigger;
            }
        }
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

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = AssignmentRules.ArgumentType(this.idArg, SqlType.Int32, batch, resolveColumnType);
        _ = AssignmentRules.ArgumentType(this.propertyArg, SqlType.Varchar, batch, resolveColumnType);
        return SqlType.Int32;
    }

    /// <summary>
    /// Adapts <see cref="ObjectPropertyEx.TableFlagByName"/> to this function's
    /// <c>int?</c> result shape: real answers the whole <c>TableHas*</c> family
    /// from the plain <c>OBJECTPROPERTY</c> as well as the EX form
    /// (probe-confirmed), so both route through the one mapping.
    /// </summary>
    /// <summary>
    /// The rest of the <c>Table*</c> family, answered for a table and a
    /// table-valued function (off across the board for the latter, save a
    /// multi-statement function's <c>TableIsFake</c>) — the full-text members
    /// also for an indexed view — and NULL for any other kind or name.
    /// </summary>
    private static int? TableFlag(Database database, SchemaObject obj, ReadOnlySpan<char> upperName)
    {
        var table = obj as HeapTable;
        var fullText = table?.FullTextIndex;
        if (table is null && obj is not (InlineTableValuedFunction or MultiStatementTableValuedFunction))
        {
            // An indexed view answers the full-text members, all off.
            return obj is View { Indexes.Count: > 0 } && upperName is "TABLEFULLTEXTBACKGROUNDUPDATEINDEXON" or "TABLEFULLTEXTCATALOGID"
                or "TABLEFULLTEXTCHANGETRACKINGON" or "TABLEFULLTEXTKEYCOLUMN" or "TABLEFULLTEXTPOPULATESTATUS" or "TABLEHASACTIVEFULLTEXTINDEX"
                ? 0
                : null;
        }
        return upperName switch
        {
            "TABLEFULLTEXTBACKGROUNDUPDATEINDEXON" or "TABLEFULLTEXTCHANGETRACKINGON" => Flag(fullText is { ChangeTracking: FullTextChangeTracking.Auto }),
            "TABLEFULLTEXTCATALOGID" => fullText?.CatalogId ?? 0,
            "TABLEFULLTEXTKEYCOLUMN" => FullTextKeyColumnId(table, fullText),
            // Real answers 1 while a population runs; the simulator's full-text
            // searches read live rows, so none ever does.
            "TABLEFULLTEXTPOPULATESTATUS" => 0,
            "TABLEHASACTIVEFULLTEXTINDEX" => Flag(fullText is { IsEnabled: true }),
            "TABLEHASCHECKCNST" => Flag(table is { CheckConstraints.Count: > 0 }),
            "TABLEHASCLUSTINDEX" => Flag(table is not null && ObjectPropertyEx.HasClusteredIndex(table)),
            // Legacy and unmodeled storage options, off on every table.
            "TABLEHASCOLUMNSET" or "TABLEHASVARDECIMALSTORAGEFORMAT" or "TABLEISLOCKEDONBULKLOAD"
                or "TABLEISMEMORYOPTIMIZED" or "TABLEISPINNED" or "TABLETEXTINROWLIMIT" => 0,
            "TABLEHASDEFAULTCNST" => Flag(table is not null && Array.Exists(table.Columns, column => column.DefaultConstraint is not null)),
            "TABLEHASDELETETRIGGER" => Flag(table is not null && TableTriggerCount(database, table, TriggerActions.Delete) > 0),
            "TABLEHASFOREIGNKEY" => Flag(table is { OutgoingForeignKeys.Count: > 0 }),
            "TABLEHASFOREIGNREF" => Flag(table is { IncomingForeignKeys.Count: > 0 }),
            "TABLEHASIDENTITY" => Flag(table is not null && Array.Exists(table.Columns, column => column.Identity is not null)),
            "TABLEHASINDEX" => Flag(table is not null && (table.Indexes.Count > 0 || table.KeyConstraints.Count > 0)),
            "TABLEHASINSERTTRIGGER" => Flag(table is not null && TableTriggerCount(database, table, TriggerActions.Insert) > 0),
            "TABLEHASNONCLUSTINDEX" => Flag(table is not null
                && (table.Indexes.Exists(index => !index.IsClustered) || table.KeyConstraints.Exists(key => !key.IsClustered))),
            "TABLEHASPRIMARYKEY" => Flag(table is not null && table.KeyConstraints.Exists(key => key.Kind == KeyConstraintKind.PrimaryKey)),
            "TABLEHASROWGUIDCOL" => Flag(table is not null && Array.Exists(table.Columns, column => column.IsRowGuidCol)),
            // The legacy LOB types only; a MAX type or xml doesn't count.
            "TABLEHASTEXTIMAGE" => Flag(table is not null && Array.Exists(table.Columns, column => column.Type is TextSqlType or NTextSqlType or ImageSqlType)),
            "TABLEHASTIMESTAMP" => Flag(table is not null && Array.Exists(table.Columns, column => column.Type is RowVersionSqlType)),
            "TABLEHASUNIQUECNST" => Flag(table is not null && table.KeyConstraints.Exists(key => key.Kind == KeyConstraintKind.Unique)),
            "TABLEHASUPDATETRIGGER" => Flag(table is not null && TableTriggerCount(database, table, TriggerActions.Update) > 0),
            // A multi-statement function's return table is real's "fake" table.
            "TABLEISFAKE" => Flag(obj is MultiStatementTableValuedFunction),
            "TABLETEMPORALTYPE" => table is null ? 0 : table.IsHistoryTable ? 1 : table.SystemVersioning is not null ? 2 : 0,
            _ => null,
        };
    }

    private static int FullTextKeyColumnId(HeapTable? table, FullTextIndex? index)
    {
        if (table is null || index is null)
            return 0;
        foreach (var key in table.KeyConstraints)
        {
            if (Collation.Baseline.Equals(key.Name, index.KeyIndexName) && key.FullOrdinals.Length > 0)
                return table.Columns[key.FullOrdinals[0]].ColumnId;
        }
        foreach (var candidate in table.Indexes)
        {
            if (Collation.Baseline.Equals(candidate.Name, index.KeyIndexName) && candidate.KeyFullOrdinals.Length > 0)
                return table.Columns[candidate.KeyFullOrdinals[0]].ColumnId;
        }
        return 0;
    }

    internal override string DebugDisplay() => $"OBJECTPROPERTY({this.idArg.DebugDisplay()}, {this.propertyArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.idArg).Child(this.propertyArg);
}
