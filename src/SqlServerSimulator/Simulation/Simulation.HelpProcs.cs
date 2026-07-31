using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The sp_help family — the formatted-metadata procs SSMS, sqlcmd sessions and
// ad-hoc T-SQL reach for: sp_helptext (module source), sp_helpindex and
// sp_helpconstraint (per-table detail), and sp_help itself (in the sibling
// Simulation.HelpProcs.SpHelp.cs partial, which delegates to the other two).
// Result-set shapes, column types, wording and ordering are probe-confirmed
// against SQL Server 2025 (2026-07-31); the per-proc algorithms mirror the
// shipped system procedures' own bodies (read back through OBJECT_DEFINITION on
// the reference instance) rather than being re-derived.
partial class Simulation
{
    // Real's help procs build their display strings in temp tables whose
    // column widths become the result-set types: nvarchar(255) for a
    // sp_helptext line, varchar(210) for an index description, nvarchar(2126)
    // for a key list ((16 * 128) + (15 * 2) + (16 * 3)), and so on.
    private static readonly NVarcharSqlType HelpTextLine =
        NVarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit);

    private static readonly VarcharSqlType HelpIndexDescriptionType =
        VarcharSqlType.Get(210, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType HelpKeyListType =
        NVarcharSqlType.Get(2126, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType HelpConstraintTypeType =
        NVarcharSqlType.Get(256, Collation.Baseline, Coercibility.Implicit);

    private static readonly VarcharSqlType HelpActionType =
        VarcharSqlType.Get(11, Collation.Baseline, Coercibility.Implicit);

    private static readonly VarcharSqlType HelpEnabledType =
        VarcharSqlType.Get(8, Collation.Baseline, Coercibility.Implicit);

    private static readonly VarcharSqlType HelpReplicationType =
        VarcharSqlType.Get(19, Collation.Baseline, Coercibility.Implicit);

    // The @objname parameter's own declared width, which sp_helpconstraint's
    // "Object Name" echo column inherits.
    private static readonly NVarcharSqlType HelpObjectNameType =
        NVarcharSqlType.Get(776, Collation.Baseline, Coercibility.Implicit);

    // db_name() + '.' + schema + '.' + table + ': ' + constraint — real types
    // the concatenation as nvarchar(516).
    private static readonly NVarcharSqlType HelpReferencingFkType =
        NVarcharSqlType.Get(516, Collation.Baseline, Coercibility.Implicit);

    // Allocation is a flat page list with no filegroup model, so every index
    // and table reports the one filegroup a default SQL Server database has.
    private const string HelpFilegroupName = "PRIMARY";

    private static readonly SqlType[] SpHelpTextSchema = [HelpTextLine];

    private static readonly string[] SpHelpTextColumnNames = ["Text"];

    private static readonly SqlType[] SpHelpIndexSchema =
        [SqlType.SystemName, HelpIndexDescriptionType, HelpKeyListType];

    private static readonly string[] SpHelpIndexColumnNames =
        ["index_name", "index_description", "index_keys"];

    private static readonly SqlType[] SpHelpConstraintNameSchema = [HelpObjectNameType];

    private static readonly string[] SpHelpConstraintNameColumnNames = ["Object Name"];

    private static readonly SqlType[] SpHelpConstraintSchema =
    [
        HelpConstraintTypeType, SqlType.SystemName, HelpActionType, HelpActionType,
        HelpEnabledType, HelpReplicationType, HelpKeyListType,
    ];

    private static readonly string[] SpHelpConstraintColumnNames =
    [
        "constraint_type", "constraint_name", "delete_action", "update_action",
        "status_enabled", "status_for_replication", "constraint_keys",
    ];

    private static readonly SqlType[] SpHelpReferencingFkSchema = [HelpReferencingFkType];

    private static readonly string[] SpHelpReferencingFkColumnNames = ["Table is referenced by foreign key"];

    /// <summary>
    /// Handles <c>EXEC sp_helptext @objname [, @columnname]</c> — the source
    /// text of a programmable module (procedure / view / function / trigger),
    /// a CHECK / DEFAULT constraint, or (with <c>@columnname</c>) a computed
    /// column. One <c>Text nvarchar(255)</c> column; the definition is split
    /// at each CR+LF pair (the pair stays on the end of its line) and any
    /// resulting line longer than 255 characters is further cut into 255-char
    /// pieces — so a definition written with LF-only newlines comes back as a
    /// single row with the newlines embedded (probe-confirmed).
    /// </summary>
    /// <remarks>
    /// Error paths mirror real: a three-part name whose database component
    /// isn't the current database → Msg 15250; an unresolvable name → Msg
    /// 15009; an object that stores no text (table / sequence / synonym / key
    /// constraint) → Msg 15197; <c>@columnname</c> against a non-table → Msg
    /// 15218, against an unknown column → Msg 15645, against a non-computed
    /// column → Msg 15646. A <c>WITH ENCRYPTION</c> module raises no error:
    /// real emits the severity-10 Msg 15471 and returns <em>no</em> result set.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpText(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (objectName, columnName) = ParseHelpArgs(arguments, "sp_helptext", "columnname");
        var target = ResolveHelpTarget(batch, "sp_helptext", objectName);

        if (columnName is not null)
        {
            // Real gates the column form on the object being a table or a
            // table-valued function, then on the column existing, then on it
            // being computed — three distinct messages, in that order.
            if (target.Object is not (HeapTable or MultiStatementTableValuedFunction))
                throw SimulatedSqlException.HelpObjectIsNotATable(objectName!);
            var collation = batch.CurrentDatabase.Collation;
            var column = Array.Find(target.Columns!, c => collation.Equals(c.Name, columnName))
                ?? throw SimulatedSqlException.HelpColumnDoesNotExist(columnName);
            yield return HelpTextResultSet(column.Computed is null
                ? throw SimulatedSqlException.HelpColumnIsNotComputed(columnName)
                : column.ComputedDefinition ?? "");
            yield break;
        }

        if (target.DefinitionText is { } definition)
        {
            yield return HelpTextResultSet(definition);
        }
        else if (SchemaObject.IsSqlModule(target.Object))
        {
            // A module whose text is absent is one created WITH ENCRYPTION:
            // severity-10 message, no result set, no error. Everything else
            // (table / sequence / synonym / key constraint / CLR routine)
            // stores no text at all.
            batch.AppendInfoError(@class: 10, state: 1, number: 15471,
                message: $"The text for object '{objectName}' is encrypted.");
        }
        else
        {
            throw SimulatedSqlException.HelpNoTextForObject(objectName!);
        }
    }

    private static SimulatedSqlResultSet HelpTextResultSet(string definition)
    {
        var rows = new List<SqlValue[]>();
        foreach (var line in SplitHelpTextLines(definition))
            rows.Add([SqlValue.FromString(HelpTextLine, line)]);
        return new SimulatedSqlResultSet(SpHelpTextSchema, SpHelpTextColumnNames, rows);
    }

    /// <summary>
    /// Reproduces sp_helptext's line splitter: scan for CR+LF pairs, emit each
    /// segment through and including its pair, and cut any segment longer than
    /// the proc's 255-character line width into 255-char pieces first. A lone
    /// CR or a lone LF is <em>not</em> a break (probe-confirmed — only the pair
    /// counts), and a definition ending in CR+LF yields no trailing empty row.
    /// </summary>
    private static IEnumerable<string> SplitHelpTextLines(string definition)
    {
        const int lineWidth = 255;
        var position = 0;
        while (position < definition.Length)
        {
            var breakAt = definition.IndexOf("\r\n", position, StringComparison.Ordinal);
            var end = breakAt < 0 ? definition.Length : breakAt + 2;
            while (end - position > lineWidth)
            {
                yield return definition.Substring(position, lineWidth);
                position += lineWidth;
            }

            yield return definition[position..end];
            position = end;
        }
    }

    /// <summary>
    /// Binds a help proc's arguments, which are uniformly <c>@objname</c> plus
    /// at most one more. Positional and named forms both bind; anything the
    /// proc doesn't declare — an extra positional or an unknown name — is
    /// Msg 8144, matching real. <paramref name="secondName"/> is null for the
    /// single-parameter procs, which then reject a second positional.
    /// </summary>
    private static (string? First, string? Second) ParseHelpArgs(
        List<ProcArgument> arguments, string procedureName, string? secondName = null)
    {
        string? first = null, second = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: first = CatalogStringArg(arg); break;
                    case 1 when secondName is not null: second = CatalogStringArg(arg); break;
                    default: throw SimulatedSqlException.InvalidProcedureParameters(procedureName);
                }

                continue;
            }

            if (BuiltInToken.Equals(arg.Name, "objname"))
                first = CatalogStringArg(arg);
            else if (secondName is not null && BuiltInToken.Equals(arg.Name, secondName))
                second = CatalogStringArg(arg);
            else
                throw SimulatedSqlException.InvalidProcedureParameters(procedureName);
        }

        return (first, second);
    }

    /// <summary>
    /// Handles <c>EXEC sp_helpindex @objname</c> — one row per index on a table
    /// or indexed view: <c>index_name sysname</c>, <c>index_description
    /// varchar(210)</c> (the comma-separated attribute phrase real builds,
    /// ending in <c>" located on PRIMARY"</c>) and <c>index_keys
    /// nvarchar(2126)</c> (key columns only — INCLUDE columns never appear —
    /// with <c>(-)</c> marking a descending key). Rows sort by index name. An
    /// object with no indexes emits the severity-10 Msg 15472 and no result
    /// set.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpIndex(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (objectName, _) = ParseHelpArgs(arguments, "sp_helpindex");
        var target = ResolveHelpTarget(batch, "sp_helpindex", objectName);
        foreach (var outcome in HelpIndexResultSets(batch, target, objectName!))
            yield return outcome;
    }

    private static IEnumerable<SimulatedStatementOutcome> HelpIndexResultSets(
        BatchContext batch, HelpTarget target, string objectName)
    {
        var rows = new List<SqlValue[]>();
        foreach (var identity in target.IndexIdentities())
        {
            if (identity.IsHeap)
                continue;
            rows.Add([
                SqlValue.FromSystemName(identity.Name!),
                SqlValue.FromString(HelpIndexDescriptionType, HelpIndexDescription(identity)),
                SqlValue.FromString(HelpKeyListType, HelpIndexKeys(target, identity)),
            ]);
        }

        if (rows.Count == 0)
        {
            HelpNoIndexes(batch, objectName);
            yield break;
        }

        rows.Sort(ByFirstCell);
        yield return new SimulatedSqlResultSet(SpHelpIndexSchema, SpHelpIndexColumnNames, rows);
    }

    // The attribute phrase, in real's fixed clause order: clustered-ness,
    // ignore-duplicate-keys, uniqueness, the constraint role, then the
    // filegroup. The hypothetical / columnstore / hash / auto-create /
    // stats-no-recompute clauses real can also emit have no simulator
    // counterpart, so they never appear.
    private static string HelpIndexDescription(IndexIdentity identity)
    {
        var ignoreDupKey = identity.Constraint?.IgnoreDupKey ?? identity.Index!.IgnoreDupKey;
        var isUnique = identity.Constraint is not null || identity.Index!.IsUnique;
        var kind = identity.Constraint?.Kind;
        return (identity.IndexId == 1 ? "clustered" : "nonclustered")
            + (ignoreDupKey ? ", ignore duplicate keys" : "")
            + (isUnique ? ", unique" : "")
            + (kind == KeyConstraintKind.PrimaryKey ? ", primary key" : "")
            + (kind == KeyConstraintKind.Unique ? ", unique key" : "")
            + " located on " + HelpFilegroupName;
    }

    // Key columns in key order, comma-separated, with real's "(-)" suffix on a
    // descending key. Constraint-backed indexes read their storage ordinals;
    // CREATE INDEX-backed ones read their declared key columns.
    private static string HelpIndexKeys(HelpTarget target, IndexIdentity identity)
    {
        if (identity.Constraint is { } constraint)
            return HelpKeyList(target.Table!, constraint);

        var columns = target.Columns!;
        var names = new List<string>(identity.Index!.KeyColumns.Length);
        foreach (var key in identity.Index.KeyColumns)
            names.Add(columns[key.ColumnOrdinal].Name + (key.IsDescending ? "(-)" : ""));
        return string.Join(", ", names);
    }

    // A PRIMARY KEY / UNIQUE constraint's key columns in the same rendering
    // sp_helpindex and sp_helpconstraint both use.
    private static string HelpKeyList(HeapTable table, KeyConstraint constraint)
    {
        var names = new List<string>(constraint.StorageOrdinals.Length);
        for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
        {
            names.Add(table.StoredColumns[constraint.StorageOrdinals[i]].Name
                + (constraint.IsDescending(i) ? "(-)" : ""));
        }

        return string.Join(", ", names);
    }

    /// <summary>
    /// Handles <c>EXEC sp_helpconstraint @objname [, @nomsg]</c> — the
    /// object-name echo (suppressed by <c>@nomsg = 'nomsg'</c>, which is how
    /// <c>sp_help</c> calls it), then one seven-column row per CHECK / DEFAULT
    /// / PRIMARY KEY / UNIQUE / FOREIGN KEY constraint on the table (a foreign
    /// key contributes a second, blank-named <c>REFERENCES …</c> row), then a
    /// row per foreign key that references the table. Real emits the
    /// severity-10 Msg 15469 in place of an empty constraint set and Msg 15470
    /// in place of an empty referencing set.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpConstraint(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (objectName, nomsgArg) = ParseHelpArgs(arguments, "sp_helpconstraint", "nomsg");
        var nomsg = nomsgArg is not null && BuiltInToken.Equals(nomsgArg, "nomsg");
        var target = ResolveHelpTarget(batch, "sp_helpconstraint", objectName);
        if (!nomsg)
        {
            List<SqlValue[]> echo = [[SqlValue.FromString(HelpObjectNameType, objectName!)]];
            yield return new SimulatedSqlResultSet(
                SpHelpConstraintNameSchema, SpHelpConstraintNameColumnNames, echo);
        }

        foreach (var outcome in HelpConstraintResultSets(batch, target, objectName!))
            yield return outcome;
    }

    private static IEnumerable<SimulatedStatementOutcome> HelpConstraintResultSets(
        BatchContext batch, HelpTarget target, string objectName)
    {
        var database = batch.CurrentDatabase;
        var rows = target.Table is { } table ? BuildHelpConstraintRows(database, table) : [];
        if (rows.Count == 0)
        {
            HelpNoConstraints(batch, objectName);
        }
        else
        {
            yield return new SimulatedSqlResultSet(SpHelpConstraintSchema, SpHelpConstraintColumnNames, rows);
        }

        var referencing = new List<SqlValue[]>();
        if (target.Table is { } referenced)
        {
            foreach (var fk in referenced.IncomingForeignKeys)
            {
                referencing.Add([SqlValue.FromString(HelpReferencingFkType,
                    $"{HelpTableReference(database, fk.ChildTable)}: {fk.Name}")]);
            }
        }

        if (referencing.Count == 0)
        {
            HelpNoReferencingForeignKeys(batch, objectName);
            yield break;
        }

        referencing.Sort(ByFirstCell);
        yield return new SimulatedSqlResultSet(
            SpHelpReferencingFkSchema, SpHelpReferencingFkColumnNames, referencing);
    }

    // The severity-10 "nothing to report" messages real prints in place of an
    // empty result set. One home each, since sp_help emits the constraint and
    // foreign-key pair itself for a view rather than routing through
    // sp_helpconstraint.
    private static void HelpNoConstraints(BatchContext batch, string objectName) =>
        batch.AppendInfoError(@class: 10, state: 1, number: 15469,
            message: $"No constraints are defined on object '{objectName}', or you do not have permissions.");

    private static void HelpNoReferencingForeignKeys(BatchContext batch, string objectName) =>
        batch.AppendInfoError(@class: 10, state: 1, number: 15470,
            message: $"No foreign keys reference table '{objectName}', or you do not have permissions on referencing tables.");

    private static void HelpNoIndexes(BatchContext batch, string objectName) =>
        batch.AppendInfoError(@class: 10, state: 1, number: 15472,
            message: $"The object '{objectName}' does not have any indexes, or you do not have permissions.");

    private static void HelpNoReferencingViews(BatchContext batch, string objectName) =>
        batch.AppendInfoError(@class: 10, state: 1, number: 15647,
            message: $"No views with schema binding reference table '{objectName}'.");

    // Row order for the single-column help sets: the one cell, ordinal
    // case-insensitive.
    private static readonly Comparison<SqlValue[]> ByFirstCell =
        static (a, b) => string.Compare(a[0].AsString, b[0].AsString, StringComparison.OrdinalIgnoreCase);

    // One row per constraint (two for a foreign key: the declaration row plus a
    // blank-named REFERENCES continuation). Real sorts by the constraint's own
    // name with the continuation second, which the (name, continuation-last)
    // comparison reproduces.
    private static List<SqlValue[]> BuildHelpConstraintRows(Database database, HeapTable table)
    {
        var rows = new List<(string SortName, bool IsContinuation, SqlValue[] Cells)>();

        static SqlValue[] Cells(string type, string name, string deleteAction, string updateAction,
            string enabled, string forReplication, string keys) =>
        [
            SqlValue.FromString(HelpConstraintTypeType, type),
            SqlValue.FromSystemName(name),
            SqlValue.FromString(HelpActionType, deleteAction),
            SqlValue.FromString(HelpActionType, updateAction),
            SqlValue.FromString(HelpEnabledType, enabled),
            SqlValue.FromString(HelpReplicationType, forReplication),
            SqlValue.FromString(HelpKeyListType, keys),
        ];

        foreach (var check in table.CheckConstraints)
        {
            rows.Add((check.Name, false, Cells(
                check.InlineColumn is { } column ? "CHECK on column " + column : "CHECK Table Level ",
                check.Name, "(n/a)", "(n/a)",
                check.IsDisabled ? "Disabled" : "Enabled", "Is_For_Replication",
                check.Definition ?? "")));
        }

        foreach (var column in table.Columns)
        {
            if (column.DefaultConstraint is not { } def)
                continue;
            rows.Add((def.Name, false, Cells(
                "DEFAULT on column " + column.Name, def.Name,
                "(n/a)", "(n/a)", "(n/a)", "(n/a)", def.Definition ?? "")));
        }

        foreach (var key in table.KeyConstraints)
        {
            rows.Add((key.Name, false, Cells(
                (key.Kind == KeyConstraintKind.PrimaryKey ? "PRIMARY KEY" : "UNIQUE")
                    + (key.IsClustered ? " (clustered)" : " (non-clustered)"),
                key.Name, "(n/a)", "(n/a)", "(n/a)", "(n/a)", HelpKeyList(table, key))));
        }

        foreach (var fk in table.OutgoingForeignKeys)
        {
            var childColumns = fk.ChildColumnOrdinals.Select(o => table.Columns[o].Name);
            var parentColumns = fk.ReferencedColumnOrdinals.Select(o => fk.ReferencedTable.Columns[o].Name);
            rows.Add((fk.Name, false, Cells(
                "FOREIGN KEY", fk.Name,
                HelpReferentialAction(fk.DeleteAction), HelpReferentialAction(fk.UpdateAction),
                fk.IsDisabled ? "Disabled" : "Enabled", "Is_For_Replication",
                string.Join(", ", childColumns))));
            rows.Add((fk.Name, true, Cells(" ", " ", " ", " ", " ", " ",
                $"REFERENCES {HelpTableReference(database, fk.ReferencedTable)} ({string.Join(", ", parentColumns)})")));
        }

        rows.Sort(static (a, b) =>
        {
            var cmp = string.Compare(a.SortName, b.SortName, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : a.IsContinuation.CompareTo(b.IsContinuation);
        });
        return rows.ConvertAll(static r => r.Cells);
    }

    private static string HelpReferentialAction(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "Cascade",
        ReferentialAction.SetDefault => "Set Default",
        ReferentialAction.SetNull => "Set Null",
        _ => "No Action",
    };

    // The db.schema.table form real builds from db_name() + schema_name() +
    // object_name() for its REFERENCES and referenced-by strings.
    private static string HelpTableReference(Database database, HeapTable table) =>
        $"{database.Name}.{ResolveSchemaName(database, table.SchemaId)}.{table.Name}";

    /// <summary>
    /// Resolves a help proc's <c>@objname</c> argument to a
    /// <see cref="HelpTarget"/>, applying real's shared preamble: a missing
    /// argument is Msg 201, a three-part name naming another database is Msg
    /// 15250, and an unresolvable name is Msg 15009.
    /// </summary>
    private static HelpTarget ResolveHelpTarget(BatchContext batch, string procedureName, string? objectName)
    {
        if (objectName is null)
            throw SimulatedSqlException.ProcedureExpectsParameter(procedureName, "objname");

        var database = batch.CurrentDatabase;
        var parsed = ParseHelpObjectName(database, objectName);
        return parsed.Count is >= 1 and <= 3 && TryResolveHelpTarget(batch, parsed, out var target)
            ? target
            : throw SimulatedSqlException.HelpObjectDoesNotExist(objectName, database.Name);
    }

    // Splits a help proc's @objname into its parts and enforces real's
    // "must be the current database" rule on a three-part name. An
    // unparseable name comes back with Count 0, which every caller treats as
    // an unresolvable object.
    private static MultiPartName ParseHelpObjectName(Database database, string objectName)
    {
        var parsed = ObjectId.TryParseObjectName(objectName, out var name) ? name : default;
        return parsed.Count == 3 && !database.Collation.Equals(parsed[0], database.Name)
            ? throw SimulatedSqlException.HelpObjectNotInCurrentDatabase()
            : parsed;
    }

    // Name resolution across the schema's whole object namespace plus the
    // table-attached constraints (real exposes CHECK / DEFAULT / key / foreign
    // key constraints as objects with their own ids, so `sp_helptext 'CK_x'`
    // and `sp_help 'CK_x'` both resolve). Database-scoped DDL triggers are
    // absent by design: probe-confirmed that OBJECT_ID can't see one, so real's
    // sp_helptext answers Msg 15009 for it.
    private static bool TryResolveHelpTarget(BatchContext batch, MultiPartName name, out HelpTarget target)
    {
        target = null!;
        if (!batch.TryResolveSchema(name, out var schema))
            return false;

        if (schema.TryFindInSharedNamespace(name.Leaf, out var found))
        {
            target = new HelpTarget(schema, found);
            return true;
        }

        var collation = batch.CurrentDatabase.Collation;
        foreach (var constraint in HelpConstraintObjects(schema))
        {
            if (collation.Equals(constraint.Name, name.Leaf))
            {
                target = new HelpTarget(
                    schema, constraint.Table, constraint.Name, constraint.TypeCode, constraint.Definition);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every constraint in <paramref name="schema"/> that real exposes as an
    /// object in its own right — a CHECK, a column DEFAULT, a PRIMARY KEY /
    /// UNIQUE key, or a FOREIGN KEY — with the <c>sys.objects.type</c> code it
    /// reports and the definition text <c>sp_helptext</c> serves for the two
    /// kinds that have one. Shared by the name resolver and <c>sp_help</c>'s
    /// no-argument object listing so the kind set and its type codes are
    /// stated once.
    /// </summary>
    private static IEnumerable<(string Name, string TypeCode, HeapTable Table, string? Definition)> HelpConstraintObjects(
        Schema schema)
    {
        foreach (var table in schema.HeapTables.Values)
        {
            foreach (var check in table.CheckConstraints)
                yield return (check.Name, "C ", table, check.Definition);
            foreach (var column in table.Columns)
            {
                if (column.DefaultConstraint is { } def)
                    yield return (def.Name, "D ", table, def.Definition);
            }

            foreach (var key in table.KeyConstraints)
                yield return (key.Name, key.Kind == KeyConstraintKind.PrimaryKey ? "PK" : "UQ", table, null);
            foreach (var fk in table.OutgoingForeignKeys)
                yield return (fk.Name, "F ", table, null);
        }
    }
}

/// <summary>
/// One resolved <c>@objname</c>: either a schema object (table, view, module,
/// sequence, synonym) or a constraint attached to a table — real exposes both
/// as objects with an id, a type code and a create date, and the help procs
/// treat them uniformly.
/// </summary>
internal sealed class HelpTarget
{
    /// <summary>Schema the name resolved through.</summary>
    public readonly Schema Schema;

    /// <summary>
    /// The resolved schema object — null when the name matched a constraint,
    /// which is the discriminator between this type's two shapes.
    /// </summary>
    public readonly SchemaObject? Object;

    /// <summary>Display name — the object's or the constraint's.</summary>
    public readonly string Name;

    /// <summary><c>sys.objects.type</c> code, for the display cell only.</summary>
    public readonly string TypeCode;

    /// <summary>Creation timestamp; a constraint reports its parent table's.</summary>
    public readonly DateTime CreateDate;

    /// <summary>
    /// Definition text for a module or a CHECK / DEFAULT constraint; null when
    /// the module is <c>WITH ENCRYPTION</c> or the object has no text at all.
    /// <see cref="SchemaObject.IsSqlModule"/> distinguishes those two cases.
    /// </summary>
    public readonly string? DefinitionText;

    /// <summary>
    /// The backing table: the constraint's parent, or the object itself when
    /// it is a table. Null for every other object kind.
    /// </summary>
    public readonly HeapTable? Table;

    /// <summary>
    /// The column set <c>sp_help</c> describes and <c>sp_helptext</c>'s column
    /// form searches — a table's columns, a view's or table-valued function's
    /// output columns. Null for objects with no columns.
    /// </summary>
    public readonly HeapColumn[]? Columns;

    public HelpTarget(Schema schema, SchemaObject schemaObject)
    {
        this.Schema = schema;
        this.Object = schemaObject;
        this.Name = schemaObject.Name;
        this.TypeCode = schemaObject.ObjectTypeCode;
        this.CreateDate = schemaObject.CreateDate;
        this.Table = schemaObject as HeapTable;
        this.DefinitionText = schemaObject.DefinitionText;
        this.Columns = schemaObject switch
        {
            HeapTable t => t.Columns,
            View v => v.OutputColumns,
            InlineTableValuedFunction f => f.OutputColumns,
            MultiStatementTableValuedFunction f => f.OutputColumns,
            _ => null,
        };
    }

    public HelpTarget(Schema schema, HeapTable table, string constraintName, string typeCode, string? definition)
    {
        this.Schema = schema;
        this.Name = constraintName;
        this.TypeCode = typeCode;
        this.CreateDate = table.CreateDate;
        this.Table = table;
        this.DefinitionText = definition;
    }

    /// <summary>Index rows for the object; empty when it can't carry indexes.</summary>
    public List<IndexIdentity> IndexIdentities() => this.Object switch
    {
        HeapTable t => t.IndexIdentities(),
        View v => v.IndexIdentities(),
        _ => [],
    };
}
