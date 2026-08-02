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
        // Application-role activation: sp_setapprole swaps the session's
        // database principal for the role's and pins the session to its
        // database; sp_unsetapprole takes it back with the issued cookie.
        // See docs/claude/permissions.md.
        "sp_setapprole",
        "sp_unsetapprole",
        // sp_configure: reads / stages server-configuration options, installed
        // by RECONFIGURE. 'nested triggers' is the one option with behavior
        // wired — see docs/claude/triggers.md.
        "sp_configure",
        // sp_rename: object / column / index rename — schema-migration tools
        // (Alembic's rename_table / alter_column, SSMS) emit it. Mutates
        // catalog state and surfaces the sev-10 "Caution" info message.
        "sp_rename",
        // sp_settriggerorder: pins a trigger first / last among the AFTER
        // triggers an action runs; see docs/claude/triggers.md.
        "sp_settriggerorder",
        // xp_msver returns a version/host-info table (SSMS calls it on connect);
        // xp_qv is the AlwaysOn-availability probe; xp_instance_regread reads
        // instance registry defaults.
        "xp_msver",
        "xp_qv",
        "xp_instance_regread",
        // sp_tablecollations_100: SqlClient's SqlBulkCopy metadata batch calls
        // it (`exec ..sp_tablecollations_100 N'[schema].[table]'`) to read the
        // destination columns' TDS collation structures before streaming rows.
        "sp_tablecollations_100",
        // sp_datatype_info_100: the ODBC SQLGetTypeInfo backing proc — ODBC
        // Driver 18 / JDBC call it on connect to learn each type's
        // precision/scale, so temporal parameters bind at the right scale.
        "sp_datatype_info_100",
        // sp_tables / sp_columns_100: the ODBC SQLTables / SQLColumns backing
        // procs — JDBC's DatabaseMetaData.getTables / getColumns (Hibernate
        // schema validation, generic tooling) call them on connect to
        // enumerate the live catalog. Unlike sp_datatype_info_100's static
        // type table, these project the current database's schema objects.
        "sp_tables",
        "sp_columns_100",
        // sp_pkeys / sp_statistics_100 / sp_stored_procedures: the ODBC
        // SQLPrimaryKeys / SQLStatistics / SQLProcedures backing procs — JDBC's
        // DatabaseMetaData.getPrimaryKeys / getIndexInfo / getProcedures call
        // them to enumerate the current database's keys, indexes, and stored
        // procedures.
        "sp_pkeys",
        "sp_statistics_100",
        "sp_stored_procedures",
        // The sp_help family — the formatted-metadata procs interactive
        // sessions and SSMS's scripting fall back on. sp_help delegates to
        // sp_helpindex / sp_helpconstraint for its per-table detail sets;
        // sp_helptext reads the same stored module definition sys.sql_modules
        // and OBJECT_DEFINITION project. See docs/claude/catalog-views.md.
        "sp_help",
        "sp_helpconstraint",
        "sp_helpindex",
        "sp_helptext",
        // sp_helpdb / sp_helpfile / sp_helpuser / sp_helptrigger: the same
        // formatted-metadata family one scope out — the database list, its file
        // allocation on its own, the database's users and roles, and a table's
        // or view's DML triggers.
        "sp_helpdb",
        "sp_helpfile",
        "sp_helptrigger",
        "sp_helpuser",
        // sp_helprotect / sp_helpstats: the permission report over
        // sys.database_permissions' object and statement rows, and a table's
        // or indexed view's statistics.
        "sp_helprotect",
        "sp_helpstats",
        // sp_depends: the deprecated dependency report, over the same analysis
        // sys.sql_expression_dependencies and the dm_sql_referen*_entities pair
        // project. See docs/claude/catalog-views.md.
        "sp_depends",
        // sp_spaceused: the size report for one object or the whole database,
        // computed from the same page counts sys.dm_db_partition_stats projects.
        "sp_spaceused",
        // sp_who / sp_who2: the session lists, projected over the live
        // connection registry with the lock manager supplying the blocking spid.
        "sp_who",
        "sp_who2",
        // sp_MSforeachdb / sp_MSforeachtable: run command templates once per
        // accessible database / per user table, with that name substituted for
        // each '?'.
        "sp_MSforeachdb",
        "sp_MSforeachtable",
        // sp_xml_preparedocument / sp_xml_removedocument: the session-scoped
        // document store OPENXML reads. See docs/claude/xml.md.
        "sp_xml_preparedocument",
        "sp_xml_removedocument",
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
        RegisterSptValues(views);
        RegisterSysconfigures(views);
    }

    /// <summary>
    /// Registers the legacy <c>sysconfigures</c> compatibility view — the
    /// SQL-Server-2000-shaped projection of the server configuration catalog
    /// that DacFx's bacpac-export preamble reads
    /// (<c>SELECT [c].[value] FROM [master].[dbo].[sysconfigures] AS [c] WITH
    /// (NOLOCK) WHERE [c].[config] = 1126</c>). Probe-confirmed against SQL
    /// Server 2025: it resolves from every database under the bare leaf
    /// (<c>sysconfigures</c>), the <c>sys.</c> qualifier, and the <c>dbo.</c>
    /// qualifier — the three-part <c>master.dbo.sysconfigures</c> form DacFx
    /// uses routes through the <c>dbo.</c> key. Not master-scoped (unlike
    /// <c>spt_values</c>): every database exposes it.
    /// <para>
    /// Four columns (<c>value int</c>, <c>config int</c>, <c>comment
    /// nvarchar</c>, <c>status smallint</c>) — narrower than
    /// <c>sys.configurations</c>, and with no <c>name</c> column
    /// (probe-confirmed: selecting <c>name</c> raises Msg 207). Rows mirror
    /// <see cref="ConfigurationData"/>: <c>value</c> = the configured value,
    /// <c>config</c> = configuration_id, <c>comment</c> = description, and
    /// <c>status</c> = <c>is_dynamic + 2 * is_advanced</c> (probe-confirmed
    /// mapping — config 1126 reports status 3 = dynamic + advanced, config 102
    /// reports 1 = dynamic only).
    /// </para>
    /// </summary>
    private static void RegisterSysconfigures(Dictionary<string, CatalogView> views)
    {
        HeapColumn[] columns =
        [
            new("value", SqlType.Int32, null, true),
            new("config", SqlType.Int32, null, false),
            new("comment", SqlType.NVarchar, 255, true),
            new("status", SqlType.SmallInt, null, true),
        ];
        // The stock rows are built here rather than in a static field because
        // they read ConfigurationData, which lives in a sibling partial whose
        // static initializers may not have run yet.
        var stockRows = BuildSysconfiguresRows();
        var view = new CatalogView("sysconfigures", columns, (batch, _) => SysconfiguresRowsFor(batch.Connection.Simulation, stockRows));
        views["sysconfigures"] = view;
        views["sys.sysconfigures"] = view;
        views["dbo.sysconfigures"] = view;
    }

    /// <summary>
    /// The stock <c>sysconfigures</c> rows, with the simulation's
    /// <c>sp_configure</c> writes layered over the <c>value</c> column so the
    /// legacy projection agrees with <c>sys.configurations</c>. The narrower
    /// legacy shape carries only the staged value, not the installed one.
    /// </summary>
    private static SqlValue[][] SysconfiguresRowsFor(Simulation simulation, SqlValue[][] stockRows)
    {
        if (simulation.ServerConfiguration.IsEmpty && !simulation.EnableClr)
            return stockRows;

        var rows = (SqlValue[][])stockRows.Clone();
        for (var i = 0; i < rows.Length; i++)
        {
            var (configured, _) = EffectiveConfigurationValues(simulation, i);
            if (configured == ConfigurationData[i].Value)
                continue;

            var row = (SqlValue[])rows[i].Clone();
            row[0] = SqlValue.FromInt32(configured);
            rows[i] = row;
        }

        return rows;
    }

    private static SqlValue[][] BuildSysconfiguresRows()
    {
        var rows = new SqlValue[ConfigurationData.Length][];
        for (var i = 0; i < ConfigurationData.Length; i++)
        {
            var (id, _, value, _, _, _, description, isDynamic, isAdvanced) = ConfigurationData[i];
            var status = (short)((isDynamic ? 1 : 0) + (isAdvanced ? 2 : 0));
            rows[i] =
            [
                SqlValue.FromInt32(value),
                SqlValue.FromInt32(id),
                SqlValue.FromNVarchar(description),
                SqlValue.FromInt16(status),
            ];
        }

        return rows;
    }

    /// <summary>
    /// Registers <c>master.dbo.spt_values</c> — the static SQL-Server compatibility
    /// helper table (a <c>dbo</c>-schema table in <c>master</c>, not a catalog
    /// view). SMO's Table space math reads it for the page size:
    /// <c>select @PageSize = v.low / 1024.0 from master.dbo.spt_values v where
    /// v.number = 1 and v.type = 'E'</c> (the <c>WINDOWS/NT</c> row, <c>low</c> =
    /// 8192 → 8 KB). The table is registered under the <c>dbo.spt_values</c> key
    /// (serving the 3-part <c>master.dbo.spt_values</c> and 2-part
    /// <c>dbo.spt_values</c> forms) and the bare <c>spt_values</c> key (the
    /// unqualified 1-part form), both marked <see cref="CatalogView.MasterScoped"/>
    /// so they bind only when the reference lands in <c>master</c> —
    /// <see cref="Parser.BatchContext.TryResolveCatalogView"/> enforces it.
    /// <para>
    /// Only the two type codes SMO / SSMS actually reference are modeled: type
    /// <c>'E'</c> (the four environment rows, probe-confirmed — the load-bearing
    /// page-size source) and type <c>'P'</c> (the 2048-row power-of-2 helper,
    /// <c>number</c> 0..2047 with <c>low = number / 8 + 1</c>, <c>high =
    /// 1 &lt;&lt; (number % 8)</c>, <c>name</c> NULL — the commonly-referenced
    /// bitmask/numbers helper). The other ~27 type codes a live <c>master</c>
    /// carries (A/B/D/D2/DBR/…) are deliberately omitted; no modeled tooling reads
    /// them. Shape probe-confirmed (SQL Server 2025): <c>name nvarchar(35)</c>,
    /// <c>number int</c>, <c>type nchar(3)</c>, <c>low</c>/<c>high</c>/<c>status
    /// int</c>. Static data — the row generator ignores the database argument.
    /// </para>
    /// </summary>
    private static void RegisterSptValues(Dictionary<string, CatalogView> views)
    {
        var nameType = NVarcharSqlType.Get(35, Collation.Baseline, Coercibility.Implicit);
        var typeType = NCharSqlType.Get(3, Collation.Baseline, Coercibility.Implicit);
        HeapColumn[] columns =
        [
            new("name", nameType, 35, true),
            new("number", SqlType.Int32, null, false),
            new("type", typeType, 3, false),
            new("low", SqlType.Int32, null, true),
            new("high", SqlType.Int32, null, true),
            new("status", SqlType.Int32, null, true),
        ];
        var rows = BuildSptValuesRows(nameType, typeType);
        var view = new CatalogView("spt_values", columns, (_, _) => rows, masterScoped: true);
        views["dbo.spt_values"] = view;
        views["spt_values"] = view;
    }

    /// <summary>
    /// Materializes the <c>master.dbo.spt_values</c> rows once: the four
    /// type <c>'E'</c> environment rows (probe-confirmed values) followed by the
    /// 2048 type <c>'P'</c> power-of-2 rows (<c>number</c> 0..2047).
    /// </summary>
    private static SqlValue[][] BuildSptValuesRows(NVarcharSqlType nameType, NCharSqlType typeType)
    {
        var typeE = SqlValue.FromNChar(typeType, "E");
        var typeP = SqlValue.FromNChar(typeType, "P");
        var nullName = SqlValue.Null(nameType);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var status0 = SqlValue.FromInt32(0);

        SqlValue[] ERow(int number, string name, int low) =>
        [
            SqlValue.FromNVarchar(nameType, name),
            SqlValue.FromInt32(number),
            typeE,
            SqlValue.FromInt32(low),
            nullInt,
            status0,
        ];

        var rows = new SqlValue[4 + 2048][];
        rows[0] = ERow(0, "SQLSERVER HOST TYPE", 0);
        rows[1] = ERow(1, "WINDOWS/NT", 8192);
        rows[2] = ERow(2, "int high bit", int.MinValue);
        rows[3] = ERow(3, "int4 high byte", 1);
        for (var number = 0; number < 2048; number++)
        {
            rows[4 + number] =
            [
                nullName,
                SqlValue.FromInt32(number),
                typeP,
                SqlValue.FromInt32((number / 8) + 1),
                SqlValue.FromInt32(1 << (number % 8)),
                status0,
            ];
        }
        return rows;
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
