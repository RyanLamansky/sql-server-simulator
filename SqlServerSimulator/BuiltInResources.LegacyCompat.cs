using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    // Fixed process-stable timestamp surfaced as the create/modify date of the
    // simulator's system objects (catalog views + system procedures) in
    // sys.system_objects. Real SQL Server reports the resource-database build
    // date here; the simulator has no such artifact, so a stable constant keeps
    // the projection deterministic across runs. Never read for a semantic
    // decision — SSMS's system-object probes gate on name/existence only.
    private static readonly DateTime SystemObjectDate = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Canonical list of the system procedures the simulator dispatches — the
    /// single source of truth shared by <c>Simulation.ResolveSystemProcedureName</c>
    /// (which builds a per-collation lookup set from it) and the
    /// <c>sys.system_objects</c> projection (which surfaces each as a <c>P</c> /
    /// <c>X</c> row). Adding a newly modeled <c>sp_*</c> / <c>xp_*</c> here both
    /// makes <c>EXEC</c> resolve it and makes it visible to SSMS's system-object
    /// existence probes. Notably <strong>absent</strong>:
    /// <c>sp_db_vardecimal_storage_format</c> — see <see cref="RegisterLegacyCompat"/>.
    /// </summary>
    internal static readonly string[] SystemProcedureNames =
    [
        // sp_executesql: dynamic-SQL proc with a special param-def / OUTPUT
        // writeback shape parsed by ParseSpExecuteSql.
        "sp_executesql",
        // Extended-property sprocs (add / update / drop) — argument parsing +
        // target resolution via InvokeSpExtendedProperty; emitted by the bacpac
        // loader for every <SqlExtendedProperty>.
        "sp_addextendedproperty",
        "sp_updateextendedproperty",
        "sp_dropextendedproperty",
        // Linked-server sprocs — add/drop carry semantic effect; the
        // login/option variants parse-and-discard.
        "sp_addlinkedserver",
        "sp_dropserver",
        "sp_addlinkedsrvlogin",
        "sp_droplinkedsrvlogin",
        "sp_serveroption",
        "sp_set_session_context",
        "sp_getapplock",
        "sp_releaseapplock",
        // xp_msver returns a version/host-info table (SSMS calls it on connect);
        // xp_qv is the AlwaysOn-availability probe; xp_instance_regread reads
        // instance registry defaults.
        "xp_msver",
        "xp_qv",
        "xp_instance_regread",
    ];

    /// <summary>
    /// Registers the legacy SQL-Server-2000 compatibility views
    /// (<c>sysobjects</c> / <c>sysusers</c>) and <c>sys.system_objects</c>.
    /// <para>
    /// <c>sysobjects</c> / <c>sysusers</c> live in the <c>sys</c> schema but
    /// resolve <em>unqualified</em> (probe-confirmed against SQL Server 2025:
    /// bare <c>SELECT … FROM sysobjects</c> works, while bare <c>objects</c> /
    /// <c>tables</c> raise Msg 208). They are registered under both the bare
    /// leaf key (the 1-part resolution path in
    /// <see cref="Parser.BatchContext.TryResolveCatalogView"/>) and the
    /// <c>sys.&lt;name&gt;</c> key (the 2-part path). SSMS's aggregate-function
    /// enumeration joins them:
    /// <c>… FROM sysobjects so JOIN sysusers su ON so.uid = su.uid …</c>, so the
    /// mapping <c>sysobjects.id = object_id</c>, <c>sysobjects.uid = schema_id</c>,
    /// and <c>sysusers.uid = principal_id</c> is load-bearing (the fixed
    /// schema/principal ids coincide — dbo = 1, sys = 4, …).
    /// </para>
    /// <para>
    /// <c>sys.system_objects</c> is an <em>honest</em> projection of the
    /// simulator's actual system surface: the modeled catalog views (as
    /// <c>V</c> rows carrying <see cref="CatalogView.ObjectId"/>) plus the
    /// modeled system procedures (<see cref="SystemProcedureNames"/>). It
    /// deliberately does <strong>not</strong> list
    /// <c>sp_db_vardecimal_storage_format</c> — SSMS's Database-Properties
    /// vardecimal probe gates <c>insert #tmp exec sys.sp_db_vardecimal_storage_format</c>
    /// on <c>if exists (select … from sys.system_objects where name =
    /// N'sp_db_vardecimal_storage_format')</c>; the honest absence makes SSMS
    /// skip the (unmodeled) proc call and read vardecimal storage as OFF, which
    /// is the correct simulator answer.
    /// </para>
    /// </summary>
    private static void RegisterLegacyCompat(Dictionary<string, CatalogView> views)
    {
        RegisterSysobjects(views);
        RegisterSysusers(views);
        RegisterSystemObjects(views);
    }

    private static void RegisterSysobjects(Dictionary<string, CatalogView> views)
    {
        HeapColumn[] columns =
        [
            new("name", SqlType.SystemName, 128, false),
            new("id", SqlType.Int32, null, false),
            new("xtype", charTwo, 2, false),
            new("uid", SqlType.SmallInt, null, true),
            new("info", SqlType.SmallInt, null, true),
            new("status", SqlType.Int32, null, true),
            new("base_schema_ver", SqlType.Int32, null, true),
            new("replinfo", SqlType.Int32, null, true),
            new("parent_obj", SqlType.Int32, null, false),
            new("crdate", SqlType.DateTime, null, false),
            new("ftcatid", SqlType.SmallInt, null, true),
            new("schema_ver", SqlType.Int32, null, true),
            new("stats_schema_ver", SqlType.Int32, null, true),
            new("type", charTwo, 2, true),
            new("userstat", SqlType.SmallInt, null, true),
            new("sysstat", SqlType.SmallInt, null, true),
            new("indexdel", SqlType.SmallInt, null, true),
            new("refdate", SqlType.DateTime, null, false),
            new("version", SqlType.Int32, null, true),
            new("deltrig", SqlType.Int32, null, true),
            new("instrig", SqlType.Int32, null, true),
            new("updtrig", SqlType.Int32, null, true),
            new("seltrig", SqlType.Int32, null, true),
            new("category", SqlType.Int32, null, true),
            new("cache", SqlType.SmallInt, null, true),
        ];
        var view = new CatalogView("sysobjects", columns, EnumerateSysobjects);
        views["sysobjects"] = view;
        views["sys.sysobjects"] = view;
    }

    private static void RegisterSysusers(Dictionary<string, CatalogView> views)
    {
        HeapColumn[] columns =
        [
            new("uid", SqlType.SmallInt, null, true),
            new("status", SqlType.SmallInt, null, true),
            new("name", SqlType.SystemName, 128, false),
            new("sid", SqlType.Varbinary, 85, true),
            new("roles", SqlType.Varbinary, 2048, true),
            new("createdate", SqlType.DateTime, null, false),
            new("updatedate", SqlType.DateTime, null, false),
            new("altuid", SqlType.SmallInt, null, true),
            new("password", SqlType.Varbinary, 256, true),
            new("gid", SqlType.SmallInt, null, true),
            new("environ", SqlType.Varchar, 255, true),
            new("hasdbaccess", SqlType.Int32, null, true),
            new("islogin", SqlType.Int32, null, true),
            new("isntname", SqlType.Int32, null, true),
            new("isntgroup", SqlType.Int32, null, true),
            new("isntuser", SqlType.Int32, null, true),
            new("issqluser", SqlType.Int32, null, true),
            new("isaliased", SqlType.Int32, null, true),
            new("issqlrole", SqlType.Int32, null, true),
            new("isapprole", SqlType.Int32, null, true),
        ];
        var view = new CatalogView("sysusers", columns, EnumerateSysusers);
        views["sysusers"] = view;
        views["sys.sysusers"] = view;
    }

    private static void RegisterSystemObjects(Dictionary<string, CatalogView> views)
    {
        HeapColumn[] columns =
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, true),
            new("principal_id", SqlType.Int32, null, true),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
        ];
        views["sys.system_objects"] = new CatalogView("system_objects", columns, EnumerateSystemObjects);
    }

    /// <summary>
    /// Projects one <c>sysobjects</c> row per schema object (tables, views,
    /// procedures, functions, triggers) plus one row per table PK/UNIQUE ('K'),
    /// CHECK ('C '), and FK ('F ') constraint — mirroring the sys.objects row
    /// set with the legacy sysobjects <c>type</c> codes. <c>id = object_id</c>,
    /// <c>uid = schema_id</c>; columns SSMS doesn't read surface as 0.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysobjects(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var zeroSmall = SqlValue.FromInt16(0);
        var zeroInt = SqlValue.FromInt32(0);
        var keyType = SqlValue.FromChar(charTwo, "K ");
        var checkType = SqlValue.FromChar(charTwo, "C ");
        var fkType = SqlValue.FromChar(charTwo, "F ");

        SqlValue[] Row(string name, int id, SqlValue typeCode, int schemaId, int parentObj, DateTime crdate, DateTime refdate) =>
        [
            SqlValue.FromSystemName(name),
            SqlValue.FromInt32(id),
            typeCode,
            SqlValue.FromInt16((short)schemaId),
            zeroSmall,
            zeroInt,
            zeroInt,
            zeroInt,
            SqlValue.FromInt32(parentObj),
            SqlValue.FromDateTime(crdate),
            zeroSmall,
            zeroInt,
            zeroInt,
            typeCode,
            zeroSmall,
            zeroSmall,
            zeroSmall,
            SqlValue.FromDateTime(refdate),
            zeroInt,
            zeroInt,
            zeroInt,
            zeroInt,
            zeroInt,
            zeroInt,
            zeroSmall,
        ];

        foreach (var schema in database.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects().OrderBy(o => o.ObjectId))
            {
                var parentObj = obj is Trigger trigger ? trigger.Parent.ObjectId : 0;
                yield return Row(obj.Name, obj.ObjectId, SqlValue.FromChar(charTwo, obj.ObjectTypeCode), obj.SchemaId, parentObj, obj.CreateDate, obj.ModifyDate);

                if (obj is not HeapTable t) continue;
                foreach (var key in t.KeyConstraints)
                    yield return Row(key.Name, key.ObjectId, keyType, t.SchemaId, t.ObjectId, t.CreateDate, t.ModifyDate);
                foreach (var chk in t.CheckConstraints)
                    yield return Row(chk.Name, chk.ObjectId, checkType, t.SchemaId, t.ObjectId, t.CreateDate, t.ModifyDate);
                foreach (var fk in t.OutgoingForeignKeys)
                    yield return Row(fk.Name, fk.ObjectId, fkType, t.SchemaId, t.ObjectId, t.CreateDate, t.ModifyDate);
            }
        }
    }

    /// <summary>
    /// Projects <c>sysusers</c> over <see cref="Database.Principals"/> — the
    /// fixed principals (public = 0, dbo = 1, guest = 2, INFORMATION_SCHEMA = 3,
    /// sys = 4) plus any CREATE USER / CREATE ROLE principals. <c>uid =
    /// principal_id</c>, which coincides with <c>schema_id</c> for the fixed
    /// principals so the <c>sysobjects.uid = sysusers.uid</c> join lands.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysusers(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var zeroSmall = SqlValue.FromInt16(0);
        var zeroInt = SqlValue.FromInt32(0);
        var oneInt = SqlValue.FromInt32(1);
        var nullSid = SqlValue.Null(SqlType.Varbinary);
        var nullVarchar = SqlValue.Null(SqlType.Varchar);
        foreach (var p in database.Principals.Values.OrderBy(p => p.PrincipalId))
        {
            var isUser = Collation.Baseline.Equals(p.TypeCode, "S") || Collation.Baseline.Equals(p.TypeCode, "U") || Collation.Baseline.Equals(p.TypeCode, "G");
            var isRole = Collation.Baseline.Equals(p.TypeCode, "R");
            var isAppRole = Collation.Baseline.Equals(p.TypeCode, "A");
            var hasDbAccess = isUser && (Collation.Baseline.Equals(p.Name, "dbo") || p.PrincipalId > 4);
            var createDate = SqlValue.FromDateTime(p.CreateDate);
            yield return
            [
                SqlValue.FromInt16((short)p.PrincipalId),
                zeroSmall,
                SqlValue.FromSystemName(p.Name),
                nullSid,
                nullSid,
                createDate,
                createDate,
                zeroSmall,
                nullSid,
                zeroSmall,
                nullVarchar,
                hasDbAccess ? oneInt : zeroInt,
                isUser ? oneInt : zeroInt,
                zeroInt,
                zeroInt,
                zeroInt,
                isUser ? oneInt : zeroInt,
                zeroInt,
                isRole ? oneInt : zeroInt,
                isAppRole ? oneInt : zeroInt,
            ];
        }
    }

    /// <summary>
    /// Honest projection of the simulator's system surface for
    /// <c>sys.system_objects</c>: every distinct modeled catalog view (as a
    /// <c>V</c> row keyed by <see cref="CatalogView.ObjectId"/>, schema_id 4 for
    /// <c>sys.*</c> and 3 for <c>INFORMATION_SCHEMA.*</c>) plus the modeled
    /// system procedures (<see cref="SystemProcedureNames"/>, <c>P</c> for
    /// <c>sp_*</c> / <c>X</c> for <c>xp_*</c>). Deliberately omits
    /// <c>sp_db_vardecimal_storage_format</c> so SSMS reads vardecimal as OFF.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSystemObjects(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var viewType = SqlValue.FromChar(charTwo, "V ");
        var viewTypeDesc = SqlValue.FromString(nvarchar60Catalog, "VIEW");
        var procType = SqlValue.FromChar(charTwo, "P ");
        var procTypeDesc = SqlValue.FromString(nvarchar60Catalog, "SQL_STORED_PROCEDURE");
        var xpType = SqlValue.FromChar(charTwo, "X ");
        var xpTypeDesc = SqlValue.FromString(nvarchar60Catalog, "EXTENDED_STORED_PROCEDURE");
        var sysSchema = SqlValue.FromInt32(Database.SysSchemaId);
        var infoSchema = SqlValue.FromInt32(Database.InformationSchemaId);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var isMsShipped = SqlValue.FromBoolean(true);
        var createDate = SqlValue.FromDateTime(SystemObjectDate);

        SqlValue[] Row(int objectId, string name, SqlValue schemaId, SqlValue type, SqlValue typeDesc) =>
        [
            SqlValue.FromInt32(objectId),
            SqlValue.FromSystemName(name),
            schemaId,
            nullInt,
            nullInt,
            type,
            typeDesc,
            createDate,
            createDate,
            isMsShipped,
        ];

        var seen = new HashSet<int>();
        foreach (var (key, view) in Simulation.CatalogViews)
        {
            if (!seen.Add(view.ObjectId)) continue;
            var dot = key.IndexOf('.', StringComparison.Ordinal);
            var schemaId = dot >= 0 && key.AsSpan(0, dot).Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)
                ? infoSchema
                : sysSchema;
            yield return Row(view.ObjectId, view.Name, schemaId, viewType, viewTypeDesc);
        }

        foreach (var proc in SystemProcedureNames)
        {
            var isExtended = proc.StartsWith("xp_", StringComparison.OrdinalIgnoreCase);
            yield return Row(
                SystemObjectId(proc),
                proc,
                sysSchema,
                isExtended ? xpType : procType,
                isExtended ? xpTypeDesc : procTypeDesc);
        }
    }

    /// <summary>
    /// Deterministic negative <c>object_id</c> for a system procedure, computed
    /// the same way <see cref="CatalogView.ObjectId"/> derives its id — a 32-bit
    /// FNV-1a hash of the name forced negative, stable across runs and disjoint
    /// from the positive ids user objects allocate.
    /// </summary>
    private static int SystemObjectId(string name)
    {
        var hash = Simulation.Fnv1a32.Initial;
        hash.Mix(name);
        return (int)(hash.Value | 0x8000_0000);
    }
}
