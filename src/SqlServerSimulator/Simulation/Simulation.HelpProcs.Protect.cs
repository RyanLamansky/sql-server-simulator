using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_helprotect — the object-level and statement-level permission report.
// Column names, types, the dynamic column widths, the action vocabulary, the
// (All) / (All+New) / (New) column markers and the ordering are
// probe-confirmed against SQL Server 2025 (2026-08-01); the algorithm mirrors
// the shipped procedure's own body (read back through sp_helptext on the
// reference instance) applied to Database.Permissions rather than being
// re-derived.
partial class Simulation
{
    // The report's char(10) state column. Real's #t1_Prots declares it that
    // wide and never substrings it, so the values arrive space-padded.
    private static readonly CharSqlType HelpProtectStateType =
        CharSqlType.Get(10, Collation.Baseline, Coercibility.Implicit);

    private static readonly string[] SpHelpProtectColumnNames =
        ["Owner", "Object", "Grantee", "Grantor", "ProtectType", "Action", "Column"];

    // The action names real reads out of sys.syspalnames' HPRT class — the
    // Shiloh-era mixed-case spellings, which cover exactly the permissions
    // sysprotects could express. Every other permission falls through to
    // permission_name()'s uppercase canonical spelling (ALTER, CONTROL,
    // VIEW DEFINITION, …). Keyed case-insensitively because an off-catalog
    // permission stores the caller's own casing.
    private static readonly Dictionary<string, string> HelpProtectLegacyActionNames = new(BuiltInToken.Comparer)
    {
        ["BACKUP DATABASE"] = "Backup Database",
        ["BACKUP LOG"] = "Backup Transaction",
        ["CREATE DATABASE"] = "Create Database",
        ["CREATE DEFAULT"] = "Create Default",
        ["CREATE FUNCTION"] = "Create Function",
        ["CREATE PROCEDURE"] = "Create Procedure",
        ["CREATE RULE"] = "Create Rule",
        ["CREATE TABLE"] = "Create Table",
        ["CREATE VIEW"] = "Create View",
        ["DELETE"] = "Delete",
        ["EXECUTE"] = "Execute",
        ["INSERT"] = "Insert",
        ["REFERENCES"] = "References",
        ["SELECT"] = "Select",
        ["UPDATE"] = "Update",
    };

    /// <summary>
    /// Handles <c>EXEC sp_helprotect [@name] [, @username] [, @grantorname]
    /// [, @permissionarea]</c> — the permission report as
    /// <c>Owner</c> / <c>Object</c> / <c>Grantee</c> / <c>Grantor</c> /
    /// <c>ProtectType char(10)</c> / <c>Action</c> / <c>Column</c>, sorted by
    /// permission area (object rows first), then owner, object, grantee,
    /// grantor, protect type, action and column ordinal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>@permissionarea</c> selects the areas by letter — <c>o</c> for
    /// object permissions (<c>sys.database_permissions.class</c> 1),
    /// <c>s</c> for statement permissions (class 0, major_id 0), default
    /// <c>'o s'</c>; a value carrying neither letter → <b>Msg 15300</b>.
    /// Schema-scope (class 3) and principal-scope (class 4) grants have no
    /// area letter, so this report never shows them — real's own coverage.
    /// <c>@name</c> filters the object's schema and name, or a statement
    /// permission's name (<c>'CREATE TABLE'</c>); a database qualifier on it
    /// → <b>Msg 15302</b> and an unparseable identifier → <b>Msg 15253</b>.
    /// An <c>@username</c> / <c>@grantorname</c> naming no principal matches
    /// nothing rather than raising. A filter that selects no rows →
    /// <b>Msg 15330</b>.
    /// </para>
    /// <para>
    /// The <c>Column</c> cell is <c>.</c> for a permission with no column
    /// form. For SELECT / UPDATE / REFERENCES it is <c>(All)</c> when the
    /// object-level grant stands alone, <c>(All+New)</c> on a table (whose
    /// column set can still grow) and the column's own name for a
    /// column-level row; an object-level grant that coexists with column-level
    /// rows for the same grantee, grantor and action is additionally
    /// <em>expanded</em> into one row per column it still covers.
    /// </para>
    /// <para>
    /// The six name columns are typed the way real's generated <c>EXEC()</c>
    /// types them: <c>substring(col, 1, max(datalength(col)))</c>, so each
    /// width is twice the longest value's character count, capped at the
    /// source column's own width.
    /// </para>
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpProtect(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (name, userName, grantorName, permissionArea) = ParseSpHelpProtectArgs(arguments);
        var area = (permissionArea ?? "?").ToUpperInvariant();
        var includeObjects = area.Contains('O', StringComparison.Ordinal);
        var includeStatements = area.Contains('S', StringComparison.Ordinal);
        if (!includeObjects && !includeStatements)
            throw SimulatedSqlException.HelpPermissionAreaIsNotValid(area);

        var database = batch.CurrentDatabase;
        var (ownerName, targetName) = ParseHelpProtectName(name);

        // A named owner that isn't a schema resolves to real's "void schema id"
        // 0, which matches nothing; a named principal that doesn't exist
        // resolves to -1, likewise. Null means "no filter".
        int? schemaId = ownerName is null ? null : ResolveSchemaId(database, ownerName);
        int? granteeId = userName is null ? null : ResolvePrincipalId(database, userName);
        int? grantorId = grantorName is null ? null : ResolvePrincipalId(database, grantorName);

        var objects = HelpProtectObjectsById(database);
        var rows = new List<HelpProtectRow>();
        if (includeObjects)
        {
            HelpProtectObjectRows(database, objects, schemaId, targetName, granteeId, grantorId, rows);
            if (rows.Count > 0)
            {
                HelpProtectMarkColumnCells(rows);
                HelpProtectExpandToColumns(database, objects, rows);
            }
        }

        if (includeStatements)
            HelpProtectStatementRows(database, targetName, granteeId, grantorId, rows);

        if (rows.Count == 0)
            throw SimulatedSqlException.HelpNoMatchingRowsToReport();

        rows.Sort(HelpProtectOrder);
        yield return HelpProtectResultSet(rows);
    }

    // Real's four parameters, positional or named.
    private static (string? Name, string? UserName, string? GrantorName, string? PermissionArea)
        ParseSpHelpProtectArgs(List<ProcArgument> arguments)
    {
        string? name = null, userName = null, grantorName = null;
        // The declared default; an explicit NULL is real's isnull(…, '?')
        // instead, which the area check then refuses.
        var permissionArea = "o s";
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: name = CatalogStringArg(arg); break;
                    case 1: userName = CatalogStringArg(arg); break;
                    case 2: grantorName = CatalogStringArg(arg); break;
                    case 3: permissionArea = HelpProtectAreaArg(arg); break;
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_helprotect");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "name"): name = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "username"): userName = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "grantorname"): grantorName = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "permissionarea"): permissionArea = HelpProtectAreaArg(arg); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_helprotect");
            }
        }

        return (name, userName, grantorName, permissionArea);
    }

    // DEFAULT restores the declared 'o s'; an explicit NULL doesn't.
    private static string? HelpProtectAreaArg(ProcArgument arg) =>
        arg.IsDefault ? "o s" : CatalogStringArg(arg);

    // real's parsename() split of @name: the leaf is the object or statement
    // permission name and the part before it is the owner. A database
    // qualifier is refused outright (Msg 15302) rather than checked against
    // the current database, and a name that doesn't parse is Msg 15253.
    private static (string? Owner, string? Target) ParseHelpProtectName(string? name)
    {
        if (name is null)
            return (null, null);
        var parsed = ObjectId.TryParseObjectName(name, out var candidate)
            ? candidate
            : throw SimulatedSqlException.HelpNameIsNotAnIdentifier(name);
        return parsed.Count >= 3
            ? throw SimulatedSqlException.HelpProtectNameIsDatabaseQualified()
            : (parsed.Count == 2 ? parsed[0] : null, parsed.Leaf);
    }

    private static int ResolveSchemaId(Database database, string schemaName) =>
        database.Schemas.TryGetValue(schemaName, out var schema) ? schema.SchemaId : 0;

    private static int ResolvePrincipalId(Database database, string principalName) =>
        database.Principals.TryGetValue(principalName, out var principal) ? principal.PrincipalId : -1;

    // Every object a class-1 permission can point at, keyed by object_id —
    // real's `join sys.all_objects obj on obj.object_id = sysp.major_id`,
    // which drops a permission whose target no longer resolves.
    private static Dictionary<int, HelpProtectObject> HelpProtectObjectsById(Database database)
    {
        var objects = new Dictionary<int, HelpProtectObject>();
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var schemaObject in schema.SchemaObjects())
            {
                objects[schemaObject.ObjectId] = new HelpProtectObject(schema.Name, schemaObject);
            }
        }

        return objects;
    }

    private static void HelpProtectObjectRows(
        Database database, Dictionary<int, HelpProtectObject> objects, int? schemaId, string? targetName,
        int? granteeId, int? grantorId, List<HelpProtectRow> rows)
    {
        foreach (var permission in database.Permissions)
        {
            if (permission.Class != PermissionChecker.ClassObject
                || !objects.TryGetValue(permission.MajorId, out var target)
                || (schemaId is { } id && target.Object.SchemaId != id)
                || (targetName is not null && !database.Collation.Equals(target.Object.Name, targetName))
                || (granteeId is { } grantee && permission.GranteePrincipalId != grantee)
                || (grantorId is { } grantor && permission.GrantorPrincipalId != grantor))
            {
                continue;
            }

            rows.Add(new HelpProtectRow(database, permission, target, permission.MinorId, columnName: "."));
        }
    }

    // Real's three ColumnName updates over the object rows it just collected,
    // in its own order: the object-level cell first reads (All) when no
    // column-level row for the same grantee / grantor / action accompanies it,
    // then a table's object-level cell picks up the "new columns are covered
    // too" marker, then a column-level cell takes the column's own name.
    private static void HelpProtectMarkColumnCells(List<HelpProtectRow> rows)
    {
        foreach (var row in rows)
        {
            if (!row.IsColumnCapable)
                continue;
            if (row.ColumnId > 0)
            {
                row.ColumnName = row.Target.ColumnName(row.ColumnId);
                continue;
            }

            var covered = !rows.Exists(other => other.ColumnId > 0
                && other.ObjectId == row.ObjectId
                && other.GranteeId == row.GranteeId
                && other.GrantorId == row.GrantorId
                && other.ActionTypeCode == row.ActionTypeCode);
            row.ColumnName = row.Target.IsTable
                ? (covered ? "(All+New)" : "(New)")
                : (covered ? "(All)" : ".");
        }
    }

    // Real's propagation insert: an object-level SELECT / UPDATE / REFERENCES
    // grant that coexists with column-level rows for the same grantee and
    // grantor is expanded into one row per column that carries no column-level
    // row of its own. Real applies **no** @name / @username / @grantorname
    // filter here (probe-confirmed: `sp_helprotect 'dbo.t'` reports another
    // table's expanded rows), so neither does this.
    private static void HelpProtectExpandToColumns(
        Database database, Dictionary<int, HelpProtectObject> objects, List<HelpProtectRow> rows)
    {
        var expanded = new List<HelpProtectRow>();
        foreach (var permission in database.Permissions)
        {
            if (permission.Class != PermissionChecker.ClassObject
                || permission.MinorId != 0
                || !HelpProtectIsColumnCapable(permission)
                || !objects.TryGetValue(permission.MajorId, out var target))
            {
                continue;
            }

            var columns = target.Columns;
            if (columns is null || !HelpProtectHasColumnPeer(database, permission, columnId: null))
                continue;
            for (var columnId = 1; columnId <= columns.Length; columnId++)
            {
                if (!HelpProtectHasColumnPeer(database, permission, columnId))
                    expanded.Add(new HelpProtectRow(database, permission, target, columnId, columns[columnId - 1].Name));
            }
        }

        rows.AddRange(expanded);
    }

    // Whether some column-level row of the same (object, grantee, grantor,
    // action) exists — for a given column when columnId is set, for any column
    // when it isn't.
    private static bool HelpProtectHasColumnPeer(Database database, DatabasePermission permission, int? columnId)
    {
        foreach (var peer in database.Permissions)
        {
            if (peer.Class == PermissionChecker.ClassObject
                && peer.MajorId == permission.MajorId
                && (columnId is { } id ? peer.MinorId == id : peer.MinorId > 0)
                && peer.GranteePrincipalId == permission.GranteePrincipalId
                && peer.GrantorPrincipalId == permission.GrantorPrincipalId
                && database.Collation.Equals(peer.DisplayName, permission.DisplayName))
            {
                return true;
            }
        }

        return false;
    }

    private static void HelpProtectStatementRows(
        Database database, string? targetName, int? granteeId, int? grantorId, List<HelpProtectRow> rows)
    {
        foreach (var permission in database.Permissions)
        {
            if (permission.Class != PermissionChecker.ClassDatabase
                || permission.MajorId != 0
                || (granteeId is { } grantee && permission.GranteePrincipalId != grantee)
                || (grantorId is { } grantor && permission.GrantorPrincipalId != grantor)
                || (targetName is not null && !database.Collation.Equals(permission.DisplayName, targetName)))
            {
                continue;
            }

            rows.Add(new HelpProtectRow(database, permission, target: null, columnId: -123, columnName: "."));
        }
    }

    // real's `order by ActionCategory desc, Owner, Object, Grantee, Grantor,
    // ProtectType, Action, ColId` — the securable class descending puts the
    // object rows ahead of the statement rows.
    private static readonly Comparison<HelpProtectRow> HelpProtectOrder = static (a, b) =>
    {
        var cmp = b.ActionCategory.CompareTo(a.ActionCategory);
        if (cmp == 0)
            cmp = string.Compare(a.OwnerName, b.OwnerName, StringComparison.OrdinalIgnoreCase);
        if (cmp == 0)
            cmp = string.Compare(a.ObjectName, b.ObjectName, StringComparison.OrdinalIgnoreCase);
        if (cmp == 0)
            cmp = string.Compare(a.GranteeName, b.GranteeName, StringComparison.OrdinalIgnoreCase);
        if (cmp == 0)
            cmp = string.Compare(a.GrantorName, b.GrantorName, StringComparison.OrdinalIgnoreCase);
        if (cmp == 0)
            cmp = string.Compare(a.StateName, b.StateName, StringComparison.OrdinalIgnoreCase);
        if (cmp == 0)
            cmp = string.Compare(a.ActionName, b.ActionName, StringComparison.OrdinalIgnoreCase);
        return cmp == 0 ? a.ColumnId.CompareTo(b.ColumnId) : cmp;
    };

    private static SimulatedSqlResultSet HelpProtectResultSet(List<HelpProtectRow> rows)
    {
        // Real measures each display column's width as the maximum
        // datalength() over the reported rows — bytes, so twice the character
        // count for the nvarchar sources — and caps it at the source column's
        // own width (sysname for the four names and the column, nvarchar(60)
        // for the action).
        var ownerType = HelpProtectWidth(rows, static r => r.OwnerName, 128);
        var objectType = HelpProtectWidth(rows, static r => r.ObjectName, 128);
        var granteeType = HelpProtectWidth(rows, static r => r.GranteeName, 128);
        var grantorType = HelpProtectWidth(rows, static r => r.GrantorName, 128);
        var actionType = HelpProtectWidth(rows, static r => r.ActionName, 60);
        var columnType = HelpProtectWidth(rows, static r => r.ColumnName, 128);
        SqlType[] schema =
            [ownerType, objectType, granteeType, grantorType, HelpProtectStateType, actionType, columnType];

        var cells = new List<SqlValue[]>(rows.Count);
        foreach (var row in rows)
        {
            cells.Add([
                SqlValue.FromString(ownerType, row.OwnerName),
                SqlValue.FromString(objectType, row.ObjectName),
                SqlValue.FromString(granteeType, row.GranteeName),
                SqlValue.FromString(grantorType, row.GrantorName),
                SqlValue.FromChar(HelpProtectStateType, row.StateName),
                SqlValue.FromString(actionType, row.ActionName),
                SqlValue.FromString(columnType, row.ColumnName),
            ]);
        }

        return new SimulatedSqlResultSet(schema, SpHelpProtectColumnNames, cells);
    }

    private static NVarcharSqlType HelpProtectWidth(
        List<HelpProtectRow> rows, Func<HelpProtectRow, string> cell, int sourceWidth)
    {
        var width = 1;
        foreach (var row in rows)
            width = Math.Max(width, cell(row).Length * 2);
        return NVarcharSqlType.Get(Math.Min(width, sourceWidth), Collation.Baseline, Coercibility.Implicit);
    }

    // Real's '1Regul' classification — the three permissions that can be
    // granted per column.
    private static bool HelpProtectIsColumnCapable(DatabasePermission permission) =>
        permission.Permission is Permission.References or Permission.Select or Permission.Update;

    private static string HelpProtectActionName(DatabasePermission permission) =>
        HelpProtectLegacyActionNames.TryGetValue(permission.DisplayName, out var legacy)
            ? legacy
            : permission.DisplayName;

    private static string HelpProtectStateName(PermissionState state) => state switch
    {
        PermissionState.Deny => "Deny",
        PermissionState.GrantWithGrantOption => "Grant_WGO",
        _ => "Grant",
    };

    /// <summary>
    /// The securable behind one class-1 permission row: its schema name plus
    /// the object itself, which supplies the <c>sys.objects.type</c> the
    /// <c>(New)</c> marker keys on and the column list the report expands to.
    /// </summary>
    private readonly struct HelpProtectObject(string schemaName, SchemaObject schemaObject)
    {
        public readonly string SchemaName = schemaName;

        public readonly SchemaObject Object = schemaObject;

        /// <summary>True for a base table, the only securable whose column set can still grow.</summary>
        public bool IsTable => this.Object is HeapTable;

        /// <summary>
        /// The columns a column-level grant addresses, in the 1-based ordinal
        /// order <c>minor_id</c> stores (<c>Simulation.ResolveColumnMinorId</c>'s
        /// convention), or null for an object that carries none.
        /// </summary>
        public HeapColumn[]? Columns => this.Object switch
        {
            HeapTable table => table.Columns,
            View view => view.OutputColumns,
            _ => null,
        };

        /// <summary>The name at a stored <c>minor_id</c>, or real's <c>.</c> placeholder when it doesn't resolve.</summary>
        public string ColumnName(int columnId)
        {
            var columns = this.Columns;
            return columns is not null && columnId >= 1 && columnId <= columns.Length
                ? columns[columnId - 1].Name
                : ".";
        }
    }

    /// <summary>
    /// One report row — real's <c>#t1_Prots</c> record, carrying both the
    /// display cells and the identity columns its post-processing passes and
    /// its ORDER BY read. <see cref="ColumnName"/> is mutable because those
    /// passes rewrite it in place, exactly as real's UPDATEs do.
    /// </summary>
    private sealed class HelpProtectRow(
        Database database, DatabasePermission permission, HelpProtectObject? target, int columnId, string columnName)
    {
        public readonly int ObjectId = target is null ? 0 : permission.MajorId;
        public readonly HelpProtectObject Target = target ?? default;
        public readonly bool IsColumnCapable = target is not null && HelpProtectIsColumnCapable(permission);
        public readonly string ActionTypeCode = permission.DisplayTypeCode;
        public readonly string ActionName = HelpProtectActionName(permission);
        public readonly byte ActionCategory = permission.Class;
        public readonly string StateName = HelpProtectStateName(permission.State);
        public readonly int ColumnId = columnId;
        public readonly string OwnerName = target is { } owner ? owner.SchemaName : ".";
        public readonly string ObjectName = target is { } named ? named.Object.Name : ".";
        public readonly int GranteeId = permission.GranteePrincipalId;
        public readonly int GrantorId = permission.GrantorPrincipalId;
        public readonly string GranteeName = HelpProtectPrincipalName(database, permission.GranteePrincipalId);
        public readonly string GrantorName = HelpProtectPrincipalName(database, permission.GrantorPrincipalId);
        public string ColumnName = columnName;
    }

    // real's user_name(principal_id): the principal's name, or the id rendered
    // as text when nothing owns it any more.
    private static string HelpProtectPrincipalName(Database database, int principalId)
    {
        foreach (var principal in database.Principals.Values)
        {
            if (principal.PrincipalId == principalId)
                return principal.Name;
        }

        return principalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
