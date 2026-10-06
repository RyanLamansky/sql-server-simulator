using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Globalization;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    private static void RegisterProgrammable(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        // Pushdown-aware sys.<view> (see BuiltInResources.CoreObjects.cs::SysP).
        void SysP(string name, HeapColumn[] columns, string[] pushdownColumns, Func<Parser.BatchContext, Database, CatalogFilter, IEnumerable<SqlValue[]>> filtered) =>
            views["sys." + name] = new CatalogView(name, columns, (batch, database) => filtered(batch, database, CatalogFilter.None), filteredRowGenerator: filtered, pushdownColumns: pushdownColumns);
        void Iso(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["INFORMATION_SCHEMA." + name] = new CatalogView(name, columns, rows);
        // INFORMATION_SCHEMA.TABLES: ISO-standard 4-column shape. TABLE_TYPE
        // is 'BASE TABLE' for every user table; 'VIEW' (not modeled) would be
        // the other shipped value.
        var baseTable = SqlValue.FromVarchar("BASE TABLE");
        var viewTableType = SqlValue.FromVarchar("VIEW");
        Iso("TABLES",
        [
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("TABLE_TYPE", SqlType.Varchar, 10, true),
        ], (batch, database) =>
            EnumerateInformationSchemaTables(batch, database, baseTable, viewTableType));

        // INFORMATION_SCHEMA.COLUMNS: ISO-standard 23-column shape. Tooling
        // does SELECT * here so the full column set ships even though many
        // are always NULL in the simulator (CHARACTER_SET_SCHEMA,
        // COLLATION_CATALOG, etc.). Rows cover base tables and view output
        // columns alike (probe-confirmed — the same two sources sys.columns
        // walks); COLUMN_DEFAULT carries the captured DEFAULT text for a table
        // column and stays NULL for a view's.
        var unicodeCs = SqlValue.FromSystemName("UNICODE");
        var radix10 = SqlValue.FromInt16(10);
        var radix2 = SqlValue.FromInt16(2);
        // INFORMATION_SCHEMA.ROUTINE_COLUMNS takes the same 23 columns over
        // the columns a table-valued function returns, its ORDINAL_POSITION
        // the column_id and NOT NULL (probed 2026-09-26 against SQL Server 2025).
        HeapColumn[] ColumnsShape(bool ordinalNullable) =>
            [
                new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
                new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
                new("TABLE_NAME", SqlType.SystemName, 128, false),
                new("COLUMN_NAME", SqlType.SystemName, 128, true),
                new("ORDINAL_POSITION", SqlType.Int32, null, ordinalNullable),
                new("COLUMN_DEFAULT", SqlType.NVarchar, 4000, true),
                new("IS_NULLABLE", SqlType.Varchar, 3, true),
                new("DATA_TYPE", nvarchar128Baseline, 128, true),
                new("CHARACTER_MAXIMUM_LENGTH", SqlType.Int32, null, true),
                new("CHARACTER_OCTET_LENGTH", SqlType.Int32, null, true),
                new("NUMERIC_PRECISION", SqlType.TinyInt, null, true),
                new("NUMERIC_PRECISION_RADIX", SqlType.SmallInt, null, true),
                new("NUMERIC_SCALE", SqlType.Int32, null, true),
                new("DATETIME_PRECISION", SqlType.SmallInt, null, true),
                new("CHARACTER_SET_CATALOG", SqlType.SystemName, 128, true),
                new("CHARACTER_SET_SCHEMA", SqlType.SystemName, 128, true),
                new("CHARACTER_SET_NAME", SqlType.SystemName, 128, true),
                new("COLLATION_CATALOG", SqlType.SystemName, 128, true),
                new("COLLATION_SCHEMA", SqlType.SystemName, 128, true),
                new("COLLATION_NAME", SqlType.SystemName, 128, true),
                new("DOMAIN_CATALOG", SqlType.SystemName, 128, true),
                new("DOMAIN_SCHEMA", SqlType.SystemName, 128, true),
                new("DOMAIN_NAME", SqlType.SystemName, 128, true),
            ];
        Iso("COLUMNS", ColumnsShape(ordinalNullable: true), (batch, database) =>
            EnumerateInformationSchemaColumns(batch, database, unicodeCs, radix10, radix2, routineColumns: false));
        Iso("ROUTINE_COLUMNS", ColumnsShape(ordinalNullable: false), (batch, database) =>
            EnumerateInformationSchemaColumns(batch, database, unicodeCs, radix10, radix2, routineColumns: true));

        // INFORMATION_SCHEMA.SCHEMATA: ISO-standard 6-column shape. Rows cover
        // the materialized schemas plus the catalog-only fixed ones (guest and
        // the nine fixed-database-role schemas) sys.schemas already injects, so
        // a fresh database lists real's 13. SCHEMA_OWNER names the owner
        // sys.schemas.principal_id reports (probe-confirmed, including a user
        // schema created with AUTHORIZATION u1 reporting u1).
        var defaultCsName = SqlValue.FromSystemName("iso_1");
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        Iso("SCHEMATA",
        [
            new("CATALOG_NAME", nvarchar128Baseline, 128, true),
            new("SCHEMA_NAME", SqlType.SystemName, 128, false),
            new("SCHEMA_OWNER", nvarchar128Baseline, 128, true),
            new("DEFAULT_CHARACTER_SET_CATALOG", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_SCHEMA", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_NAME", SqlType.SystemName, 128, true),
        ], (batch, database) =>
            EnumerateInformationSchemaSchemata(database, nullSysName, defaultCsName));

        // sys.parameters: one row per declared parameter + one row with
        // parameter_id=0 for the return type. The shipped column set covers
        // what real SQL Server's documented shape exposes: object_id / name /
        // parameter_id / system_type_id / user_type_id / max_length /
        // precision / scale / is_output / is_nullable. Probe-confirmed
        // ordering: return type emits first (parameter_id=0, empty name),
        // declared params follow in source order.
        HeapColumn[] parameterColumns =
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("parameter_id", SqlType.Int32, null, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("max_length", SqlType.SmallInt, null, false),
            new("precision", SqlType.TinyInt, null, false),
            new("scale", SqlType.TinyInt, null, false),
            new("is_output", SqlType.Bit, null, false),
            new("is_cursor_ref", SqlType.Bit, null, false),
            new("has_default_value", SqlType.Bit, null, false),
            new("is_xml_document", SqlType.Bit, null, false),
            // default_value is a first-class sql_variant matching real SQL
            // Server; always a NULL sql_variant here (the simulator doesn't
            // track parameter default values).
            new("default_value", SqlType.SqlVariant, null, true),
            new("xml_collection_id", SqlType.Int32, null, false),
            new("is_readonly", SqlType.Bit, null, false),
            new("is_nullable", SqlType.Bit, null, true),
            // A vector parameter's dimension count and element type, NULL
            // for any other (mirroring sys.columns' vector pair). DacFx's
            // parameter reverse-engineering reads both.
            new("vector_dimensions", SqlType.Int32, null, true),
            new("vector_base_type_desc", NVarcharSqlType.Get(10, Collation.Catalog, Coercibility.Implicit), 10, true),
            // The element type's id, as sys.columns carries it: 0 for float32,
            // 1 for float16 (probed 2026-10-02 against SQL Server 2025).
            new("vector_base_type", SqlType.TinyInt, null, true),
        ];
        SysP("parameters", parameterColumns, ["object_id"], EnumerateParameters);

        // sys.all_parameters: identical shape to sys.parameters in real SQL
        // Server (parameters of user objects + system objects). The simulator
        // enumerates only user-object parameters, so it shares sys.parameters'
        // rows verbatim. SMO's UserDefinedFunction / StoredProcedure scripting
        // reads the return/parameter metadata through sys.all_parameters
        // (LEFT JOIN … ret_param.object_id = udf.object_id AND
        // ret_param.is_output = 1); without the view every such property errors
        // Msg 208.
        SysP("all_parameters", parameterColumns, ["object_id"], EnumerateParameters);

        // sys.views: per-view rows. Load-bearing subset of real SQL Server's
        // sys.views shape — object_id / name / schema_id / with_check_option /
        // is_date_correlation_view. Other documented columns (principal_id,
        // is_replicated, has_replication_filter, etc.) aren't modeled.
        Sys("views",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            // principal_id is the explicit owner ALTER AUTHORIZATION set, NULL
            // when ownership follows the schema; ledger_view_type is a constant
            // 0 (ledger unmodeled). SMO's Object-Explorer Views enumeration
            // reads create_date, principal_id, is_ms_shipped, ledger_view_type.
            new("principal_id", SqlType.Int32, null, true),
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("with_check_option", SqlType.Bit, null, false),
            new("is_date_correlation_view", SqlType.Bit, null, false),
            new("ledger_view_type", SqlType.TinyInt, null, true),
            // has_opaque_metadata / is_dropped_ledger_view: has_opaque_metadata
            // is 1 for a view created WITH VIEW_METADATA, both 0 otherwise
            // (probe-confirmed against SQL Server 2025) — SMO's Script-As
            // view query reads both.
            new("has_opaque_metadata", SqlType.Bit, null, false),
            new("is_dropped_ledger_view", SqlType.Bit, null, true),
        ], EnumerateViews);

        // sys.all_views shares sys.views' shape and row generator — user-view
        // parity, like sys.all_objects / sys.all_columns. SMO's view-enumeration
        // and Script-As queries read sys.all_views (filtering on v.type = 'V');
        // the identical user-view row set suffices (the simulator surfaces no
        // system views through it).
        Sys("all_views",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
            new("with_check_option", SqlType.Bit, null, true),
            new("is_date_correlation_view", SqlType.Bit, null, true),
            new("ledger_view_type", SqlType.TinyInt, null, true),
            new("has_opaque_metadata", SqlType.Bit, null, true),
            new("is_dropped_ledger_view", SqlType.Bit, null, true),
        ], (batch, database) => EnumerateViews(batch, database).Concat(EnumerateSystemViews()));

        // sys.system_views: the catalog views themselves, the system half of
        // sys.all_views (probed 2026-09-26 against SQL Server 2025).
        Sys("system_views",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
            new("with_check_option", SqlType.Bit, null, false),
            new("is_date_correlation_view", SqlType.Bit, null, false),
            new("ledger_view_type", SqlType.TinyInt, null, true),
            new("has_opaque_metadata", SqlType.Bit, null, false),
            new("is_dropped_ledger_view", SqlType.Bit, null, false),
        ], (_, _) => EnumerateSystemViews());

        // sys.procedures: per-procedure rows. Shipped column subset matches
        // the load-bearing surface — object_id / name / schema_id /
        // create_date / modify_date / is_ms_shipped / is_auto_executed. Startup
        // procedures (sp_procoption) aren't modeled, so is_auto_executed is a
        // constant 0 (non-nullable bit); SMO's StoredProcedure property-bag
        // query projects it as [Startup], and without the column the whole bag
        // query fails Msg 207 and every StoredProcedure property errors.
        // principal_id is the explicit owner ALTER AUTHORIZATION set.
        Sys("procedures",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("is_auto_executed", SqlType.Bit, null, false),
        ], (batch, database) =>
            EnumerateProcedures(batch, database, charTwo, notMsShipped));

        // sys.numbered_procedures / sys.numbered_procedure_parameters: a
        // procedure group's members numbered 2 and up, under the group's
        // object_id, and their parameters (column shapes probed 2026-09-26
        // against SQL Server 2025). Number 1 lists in sys.procedures and
        // sys.parameters instead. SMO's StoredProcedure scripting LEFT JOINs
        // the first to detect the numbered form.
        Sys("numbered_procedures",
        [
            new("object_id", SqlType.Int32, null, false),
            new("procedure_number", SqlType.SmallInt, null, true),
            new("definition", SqlType.NVarcharMax, null, true),
        ], (_, database) => EnumerateNumberedProcedures(database));
        Sys("numbered_procedure_parameters",
        [
            new("object_id", SqlType.Int32, null, false),
            new("procedure_number", SqlType.SmallInt, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("parameter_id", SqlType.Int32, null, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("max_length", SqlType.SmallInt, null, false),
            new("precision", SqlType.TinyInt, null, false),
            new("scale", SqlType.TinyInt, null, false),
            new("is_output", SqlType.Bit, null, false),
            new("is_cursor_ref", SqlType.Bit, null, false),
        ], (_, database) => EnumerateNumberedProcedureParameters(database));

        // INFORMATION_SCHEMA.ROUTINES: ISO-shape view listing both procedures
        // and functions. The simulator ships the load-bearing column subset:
        // ROUTINE_CATALOG / SCHEMA / NAME / TYPE / DATA_TYPE. For procedures
        // DATA_TYPE is NULL (procs have no scalar return type); for scalar
        // UDFs it carries the return type's family name; for inline TVFs it
        // is 'TABLE'. ROUTINE_DEFINITION carries the module source text
        // (nvarchar(4000), truncated like SQL Server). Real SQL Server ships
        // dozens of further columns (CREATED, LAST_ALTERED, etc.) not modeled.
        Iso("ROUTINES",
        [
            // SPECIFIC_* lead the ISO shape and mirror ROUTINE_* for T-SQL
            // routines (no overloading, so the specific name equals the routine
            // name). SSMS's aggregate-function enumeration joins
            // sysobjects.name = INFORMATION_SCHEMA.ROUTINES.SPECIFIC_NAME.
            new("SPECIFIC_CATALOG", nvarchar128Baseline, 128, true),
            new("SPECIFIC_SCHEMA", nvarchar128Baseline, 128, true),
            new("SPECIFIC_NAME", SqlType.SystemName, 128, false),
            new("ROUTINE_CATALOG", nvarchar128Baseline, 128, true),
            new("ROUTINE_SCHEMA", nvarchar128Baseline, 128, true),
            new("ROUTINE_NAME", SqlType.SystemName, 128, false),
            new("ROUTINE_TYPE", SqlType.NVarchar, 20, true),
            new("MODULE_CATALOG", SqlType.SystemName, 128, true),
            new("MODULE_SCHEMA", SqlType.SystemName, 128, true),
            new("MODULE_NAME", SqlType.SystemName, 128, true),
            new("UDT_CATALOG", SqlType.SystemName, 128, true),
            new("UDT_SCHEMA", SqlType.SystemName, 128, true),
            new("UDT_NAME", SqlType.SystemName, 128, true),
            new("DATA_TYPE", SqlType.SystemName, 128, true),
            new("CHARACTER_MAXIMUM_LENGTH", SqlType.Int32, null, true),
            new("CHARACTER_OCTET_LENGTH", SqlType.Int32, null, true),
            new("COLLATION_CATALOG", SqlType.SystemName, 128, true),
            new("COLLATION_SCHEMA", SqlType.SystemName, 128, true),
            new("COLLATION_NAME", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_CATALOG", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_SCHEMA", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_NAME", SqlType.SystemName, 128, true),
            new("NUMERIC_PRECISION", SqlType.TinyInt, null, true),
            new("NUMERIC_PRECISION_RADIX", SqlType.SmallInt, null, true),
            new("NUMERIC_SCALE", SqlType.Int32, null, true),
            new("DATETIME_PRECISION", SqlType.SmallInt, null, true),
            new("INTERVAL_TYPE", SqlType.NVarchar, 30, true),
            new("INTERVAL_PRECISION", SqlType.SmallInt, null, true),
            new("TYPE_UDT_CATALOG", SqlType.SystemName, 128, true),
            new("TYPE_UDT_SCHEMA", SqlType.SystemName, 128, true),
            new("TYPE_UDT_NAME", SqlType.SystemName, 128, true),
            new("SCOPE_CATALOG", SqlType.SystemName, 128, true),
            new("SCOPE_SCHEMA", SqlType.SystemName, 128, true),
            new("SCOPE_NAME", SqlType.SystemName, 128, true),
            new("MAXIMUM_CARDINALITY", SqlType.BigInt, null, true),
            new("DTD_IDENTIFIER", SqlType.SystemName, 128, true),
            new("ROUTINE_BODY", SqlType.NVarchar, 30, true),
            new("ROUTINE_DEFINITION", SqlType.NVarchar, 4000, true),
            new("EXTERNAL_NAME", SqlType.SystemName, 128, true),
            new("EXTERNAL_LANGUAGE", SqlType.NVarchar, 30, true),
            new("PARAMETER_STYLE", SqlType.NVarchar, 30, true),
            new("IS_DETERMINISTIC", SqlType.NVarchar, 10, true),
            new("SQL_DATA_ACCESS", SqlType.NVarchar, 30, true),
            new("IS_NULL_CALL", SqlType.NVarchar, 10, true),
            new("SQL_PATH", SqlType.SystemName, 128, true),
            new("SCHEMA_LEVEL_ROUTINE", SqlType.NVarchar, 10, true),
            new("MAX_DYNAMIC_RESULT_SETS", SqlType.SmallInt, null, true),
            new("IS_USER_DEFINED_CAST", SqlType.NVarchar, 10, true),
            new("IS_IMPLICITLY_INVOCABLE", SqlType.NVarchar, 10, true),
            new("CREATED", SqlType.DateTime, null, false),
            new("LAST_ALTERED", SqlType.DateTime, null, false),
        ], EnumerateInformationSchemaRoutines);

        // INFORMATION_SCHEMA.PARAMETERS: ISO-shape view listing parameters
        // for procedures and functions. PARAMETER_MODE is 'IN' / 'OUT' /
        // 'INOUT'; the simulator emits 'IN' for non-output params, 'INOUT'
        // for OUTPUT-declared params (probe-confirmed: real SQL Server uses
        // INOUT for OUTPUT in procedures). CHARACTER_MAXIMUM_LENGTH is set
        // only for string types.
        var modeIn = SqlValue.FromNVarchar("IN");
        var modeInOut = SqlValue.FromNVarchar("INOUT");
        var modeOut = SqlValue.FromNVarchar("OUT");
        Iso("PARAMETERS",
        [
            new("SPECIFIC_CATALOG", nvarchar128Baseline, 128, true),
            new("SPECIFIC_SCHEMA", nvarchar128Baseline, 128, true),
            new("SPECIFIC_NAME", SqlType.SystemName, 128, false),
            new("ORDINAL_POSITION", SqlType.Int32, null, false),
            new("PARAMETER_MODE", SqlType.NVarchar, 10, true),
            new("IS_RESULT", SqlType.NVarchar, 10, true),
            new("AS_LOCATOR", SqlType.NVarchar, 10, true),
            new("PARAMETER_NAME", SqlType.SystemName, 128, true),
            new("DATA_TYPE", nvarchar128Baseline, 128, false),
            new("CHARACTER_MAXIMUM_LENGTH", SqlType.Int32, null, true),
            new("CHARACTER_OCTET_LENGTH", SqlType.Int32, null, true),
            new("COLLATION_CATALOG", SqlType.SystemName, 128, true),
            new("COLLATION_SCHEMA", SqlType.SystemName, 128, true),
            new("COLLATION_NAME", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_CATALOG", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_SCHEMA", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_NAME", SqlType.SystemName, 128, true),
            new("NUMERIC_PRECISION", SqlType.TinyInt, null, true),
            new("NUMERIC_PRECISION_RADIX", SqlType.SmallInt, null, true),
            new("NUMERIC_SCALE", SqlType.Int32, null, true),
            new("DATETIME_PRECISION", SqlType.SmallInt, null, true),
            new("INTERVAL_TYPE", SqlType.NVarchar, 30, true),
            new("INTERVAL_PRECISION", SqlType.SmallInt, null, true),
            new("USER_DEFINED_TYPE_CATALOG", SqlType.SystemName, 128, true),
            new("USER_DEFINED_TYPE_SCHEMA", SqlType.SystemName, 128, true),
            new("USER_DEFINED_TYPE_NAME", SqlType.SystemName, 128, true),
            new("SCOPE_CATALOG", SqlType.SystemName, 128, true),
            new("SCOPE_SCHEMA", SqlType.SystemName, 128, true),
            new("SCOPE_NAME", SqlType.SystemName, 128, true),
        ], (batch, database) =>
            EnumerateInformationSchemaParameters(batch, database, modeIn, modeInOut, modeOut, unicodeCs, radix10, radix2));

        // INFORMATION_SCHEMA.VIEWS: ISO-standard 6-column shape. IS_UPDATABLE is
        // probe-confirmed to always report 'NO' in real SQL Server even for
        // views that are actually updatable — matching that by hardcoding.
        var checkOptionNone = SqlValue.FromVarchar("NONE");
        var checkOptionCascade = SqlValue.FromVarchar("CASCADE");
        var isUpdatableNo = SqlValue.FromVarchar("NO");
        Iso("VIEWS",
        [
            new("TABLE_CATALOG", nvarchar128Baseline, 128, true),
            new("TABLE_SCHEMA", nvarchar128Baseline, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("VIEW_DEFINITION", SqlType.NVarchar, 4000, true),
            new("CHECK_OPTION", SqlType.Varchar, 7, true),
            new("IS_UPDATABLE", SqlType.Varchar, 2, false),
        ], (batch, database) =>
            EnumerateInformationSchemaViews(batch, database, checkOptionNone, checkOptionCascade, isUpdatableNo));

        // sys.types: per-database list of system + user-defined types. Probe-
        // confirmed shipped subset: name / system_type_id / user_type_id /
        // schema_id / is_user_defined / is_table_type / is_nullable. Real SQL
        // Server has many more columns (principal_id, max_length, precision,
        // scale, collation_name, is_assembly_type, default_object_id, etc.);
        // the shipped set is what apps typically test for.
        Sys("types",
        [
            new("name", SqlType.SystemName, 128, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("is_user_defined", SqlType.Bit, null, false),
            new("is_table_type", SqlType.Bit, null, false),
            new("is_nullable", SqlType.Bit, null, true),
            // is_assembly_type: 1 only for the CLR-backed system types
            // (hierarchyid / geometry / geography); 0 for every other built-in,
            // table type, and scalar alias. SMO's SSMS column-node query reads
            // it off sys.types (baset) to pick the base-type join arm.
            new("is_assembly_type", SqlType.Bit, null, false),
            // max_length (byte width, smallint), precision, scale — probe-
            // confirmed to mirror the systypes length/xprec/xscale triple for
            // system types (int 4/10/0, nvarchar 8000/0/0, sql_variant 8016/0/0),
            // -1/0/0 for table types, and the underlying built-in's metadata
            // for scalar alias types (UDDTs). SMO's User-Defined-Data-Types
            // Object-Explorer node reads st.max_length / precision / scale.
            new("max_length", SqlType.SmallInt, null, false),
            new("precision", SqlType.TinyInt, null, false),
            new("scale", SqlType.TinyInt, null, false),
            // collation_name: the database collation for the character-family
            // types (char/varchar/nchar/nvarchar/text/ntext/sysname and alias
            // types over them — probe-confirmed real reports the database
            // collation there), NULL for everything else. principal_id:
            // the explicit owner, else NULL. default_object_id: the CREATE DEFAULT object
            // sp_bindefault bound to an alias type (else 0). DacFx's UDDT
            // scripting reads all three.
            new("collation_name", SqlType.SystemName, 128, true),
            new("principal_id", SqlType.Int32, null, true),
            new("default_object_id", SqlType.Int32, null, false),
            // rule_object_id: object_id of the CREATE RULE object sp_bindrule
            // bound to an alias type, else 0. DacFx's UDDT reverse-engineering
            // query joins sys.objects ON rule_object_id.
            new("rule_object_id", SqlType.Int32, null, false),
        ], EnumerateSysTypes);

        // sys.table_types: per-database list of user-defined table types only.
        // sys.table_types derives from sys.types, so it carries the full
        // sys.types column set plus the table-type-specific
        // type_table_object_id / is_memory_optimized. The sys.types-inherited
        // columns are constant for every table type (probe-confirmed against
        // SQL Server 2025): system_type_id 243, max_length -1, precision 0,
        // scale 0, collation_name NULL, is_nullable 0, is_assembly_type 0,
        // is_table_type 1; principal_id is the explicit owner, else NULL. is_memory_optimized reports
        // a memory-optimized table type. SMO's UDTT
        // property-bag / Script query reads tt.max_length / is_nullable /
        // collation_name / principal_id, and its SSMS index/key/FK sub-node
        // queries read is_memory_optimized via (SELECT tt.is_memory_optimized
        // FROM sys.table_types tt WHERE tt.type_table_object_id = i.object_id).
        // The sys.types-inherited columns are appended (not reordered into the
        // real sys.table_types position) so existing positional consumers of
        // the original six columns keep working; SMO reads every column by name.
        Sys("table_types",
        [
            new("name", SqlType.SystemName, 128, false),
            new("type_table_object_id", SqlType.Int32, null, false),
            new("is_user_defined", SqlType.Bit, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("is_memory_optimized", SqlType.Bit, null, true),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("max_length", SqlType.SmallInt, null, false),
            new("precision", SqlType.TinyInt, null, false),
            new("scale", SqlType.TinyInt, null, false),
            new("collation_name", SqlType.SystemName, 128, true),
            new("is_nullable", SqlType.Bit, null, true),
            new("is_assembly_type", SqlType.Bit, null, false),
            new("is_table_type", SqlType.Bit, null, false),
        ], EnumerateSysTableTypes);

        // sys.sequences: per-database list of user-defined sequence objects.
        // Probe-confirmed shipped subset: name / object_id / schema_id /
        // start_value / increment / minimum_value / maximum_value /
        // is_cycling / is_cached / cache_size / current_value /
        // system_type_id / user_type_id / is_exhausted. start_value / increment / minimum_value / maximum_value /
        // current_value are first-class sql_variant, each carrying the
        // sequence's declared scalar type as its inner base type (int → int,
        // bigint → bigint, decimal(p, s) → decimal — probe-confirmed against
        // SQL Server 2025).
        Sys("sequences",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            // principal_id is the explicit owner, NULL when ownership follows
            // the schema; create_date / modify_date come from the ALTER-preserving
            // SchemaObject timestamps. SMO's Object-Explorer Sequences
            // enumeration reads create_date and principal_id.
            new("principal_id", SqlType.Int32, null, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("start_value", SqlType.SqlVariant, null, false),
            new("increment", SqlType.SqlVariant, null, false),
            new("minimum_value", SqlType.SqlVariant, null, false),
            new("maximum_value", SqlType.SqlVariant, null, false),
            new("is_cycling", SqlType.Bit, null, true),
            new("is_cached", SqlType.Bit, null, true),
            new("cache_size", SqlType.Int32, null, true),
            new("current_value", SqlType.SqlVariant, null, false),
            // last_used_value: sql_variant carrying the last emitted value in
            // the sequence's declared type, NULL until the first NEXT VALUE FOR
            // (and after ALTER … RESTART). DacFx's sequence reverse-engineering
            // query projects [s].[last_used_value].
            new("last_used_value", SqlType.SqlVariant, null, true),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("is_exhausted", SqlType.Bit, null, false),
            // precision / scale mirror the sequence's declared numeric type
            // (int → 10/0, bigint → 19/0, decimal(p,s) → p/s). precision is
            // non-nullable tinyint, scale nullable tinyint (real SQL Server
            // shape). SMO's Sequence property-bag query projects them as
            // [NumericPrecision] / [NumericScale]; without the columns the whole
            // bag query fails Msg 207 and every Sequence property errors.
            new("precision", SqlType.TinyInt, null, false),
            new("scale", SqlType.TinyInt, null, true),
        ], EnumerateSysSequences);

        // sys.plan_guides: plan guides aren't modeled, so the view is always
        // empty. SMO's Object-Explorer Plan Guides node reads name / is_disabled;
        // the full documented shape ships so a direct SELECT sees an authentic
        // (empty) result. Probe-confirmed column shape (SQL Server 2025).
        Sys("plan_guides",
        [
            new("plan_guide_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_disabled", SqlType.Bit, null, false),
            new("query_text", SqlType.NVarcharMax, null, true),
            new("scope_type", SqlType.TinyInt, null, false),
            new("scope_type_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("scope_object_id", SqlType.Int32, null, true),
            new("scope_batch", SqlType.NVarcharMax, null, true),
            new("parameters", SqlType.NVarcharMax, null, true),
            new("hints", SqlType.NVarcharMax, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.assembly_types: the three CLR-backed system types shipped by
        // SQL Server (hierarchyid / geometry / geography), all owned by the
        // Microsoft.SqlServer.Types assembly (assembly_id 1). Probe-confirmed
        // shape (SQL Server 2025): system_type_id 240; user_type_id 128/129/130;
        // schema_id 4 (sys); is_user_defined 0; is_assembly_type 1; max_length
        // 892 for hierarchyid, -1 for the two spatial types. SMO's User-Defined
        // Types node reads name / assembly_id / is_user_defined / schema_id and
        // filters is_user_defined = 1 (so these system rows surface nothing in
        // that node — matching real SQL Server, which lists only user CLR types).
        Sys("assembly_types",
        [
            new("name", SqlType.SystemName, 128, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("max_length", SqlType.SmallInt, null, false),
            new("precision", SqlType.TinyInt, null, false),
            new("scale", SqlType.TinyInt, null, false),
            new("collation_name", SqlType.SystemName, 128, true),
            new("is_nullable", SqlType.Bit, null, true),
            new("is_user_defined", SqlType.Bit, null, false),
            new("is_assembly_type", SqlType.Bit, null, false),
            // default_object_id / rule_object_id: the column-level DEFAULT /
            // RULE object bindings (both non-nullable int, 0 for the three
            // system CLR types). Real SQL Server's column order places these
            // before assembly_id; SSMS's Table-Designer UDT query left-joins
            // sys.objects on both to detect an attached DEFAULT / RULE.
            new("default_object_id", SqlType.Int32, null, false),
            new("rule_object_id", SqlType.Int32, null, false),
            new("assembly_id", SqlType.Int32, null, false),
            // assembly_class: the CLR type name (probe-confirmed
            // Microsoft.SqlServer.Types.Sql{HierarchyId,Geometry,Geography}).
            // DacFx's assembly-type scripting query joins it.
            new("assembly_class", NVarcharSqlType.Get(128, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit), 128, true),
            // is_binary_ordered: 1 only for hierarchyid (byte-comparable
            // OrdPath), 0 for the two spatial types. is_fixed_length: 0 for
            // all three. prog_id: always NULL. assembly_qualified_name: the
            // full CLR AssemblyQualifiedName (probe-confirmed, SQL Server
            // 2025). is_table_type moves last to match real's column order.
            new("is_binary_ordered", SqlType.Bit, null, true),
            new("is_fixed_length", SqlType.Bit, null, true),
            new("prog_id", SqlType.NVarchar, 40, true),
            new("assembly_qualified_name", NVarcharSqlType.Get(4000, Collation.Get("Latin1_General_BIN"), Coercibility.Implicit), 4000, true),
            new("is_table_type", SqlType.Bit, null, false),
        ], EnumerateAssemblyTypes);

        // sys.type_assembly_usages: which assembly each CLR type lives in —
        // the three system types in Microsoft.SqlServer.Types (1), then the
        // user-defined ones (probed 2026-09-28 against SQL Server 2025).
        Sys("type_assembly_usages",
        [
            new("user_type_id", SqlType.Int32, null, false),
            new("assembly_id", SqlType.Int32, null, false),
        ], static (_, database) => EnumerateTypeAssemblyUsages(database));

        // sys.function_order_columns: ordered-set aggregate order columns
        // aren't modeled, so it ships empty with the full probe-confirmed
        // shape (SQL Server 2025, 2026-07-16).
        Sys("function_order_columns",
        [
            new("object_id", SqlType.Int32, null, false),
            new("order_column_id", SqlType.Int32, null, false),
            new("column_id", SqlType.Int32, null, false),
            new("is_descending", SqlType.Bit, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.external_languages / sys.external_libraries / sys.external_models:
        // external-language runtimes (R/Python via CREATE EXTERNAL LANGUAGE),
        // external libraries, and external AI models (sp_invoke_external_rest_
        // endpoint / AI_GENERATE_EMBEDDINGS) aren't modeled, so all three ship
        // empty with the full probe-confirmed shape (SQL Server 2025,
        // 2026-07-16), external_models' parameters column typed json as real's
        // is.
        Sys("external_languages",
        [
            new("external_language_id", SqlType.Int32, null, false),
            new("language", SqlType.SystemName, 128, true),
            new("create_date", SqlType.DateTime, null, false),
            new("principal_id", SqlType.Int32, null, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("external_libraries",
        [
            new("external_library_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("principal_id", SqlType.Int32, null, true),
            new("language", SqlType.SystemName, 128, true),
            new("scope", SqlType.Int32, null, false),
            new("scope_desc", VarcharSqlType.Get(7, Collation.Catalog, Coercibility.Implicit), 7, false),
        ], static (_, _) => EmptyCatalogRows);
        // sys.external_library_files / sys.external_language_files: the
        // per-platform binary payload rows for external libraries / languages.
        // Both unmodeled, so both ship empty with the full probe-confirmed
        // shape (SQL Server 2025 WideWorldImporters, 2026-07-16). DacFx reads
        // these when reverse-engineering EXTERNAL LIBRARY / LANGUAGE objects.
        Sys("external_library_files",
        [
            new("external_library_id", SqlType.Int32, null, false),
            new("content", VarbinarySqlType.MaxForm, null, true),
            new("platform", SqlType.TinyInt, null, true),
            new("platform_desc", nvarchar60Catalog, 60, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("external_language_files",
        [
            new("external_language_id", SqlType.Int32, null, false),
            new("content", VarbinarySqlType.MaxForm, null, true),
            new("file_name", SqlType.SystemName, 128, true),
            new("platform", SqlType.TinyInt, null, true),
            new("platform_desc", nvarchar60Catalog, 60, true),
            new("parameters", SqlType.SystemName, 128, true),
            new("environment_variables", SqlType.SystemName, 128, true),
        ], static (_, _) => EmptyCatalogRows);
        Sys("external_models",
        [
            new("external_model_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("principal_id", SqlType.Int32, null, true),
            new("location", SqlType.NVarchar, 4000, true),
            new("api_format", SqlType.NVarchar, 100, true),
            new("model_type_id", SqlType.Int32, null, true),
            new("model_type_desc", NVarcharSqlType.Get(65, Collation.Baseline, Coercibility.Implicit), 65, true),
            new("model", SqlType.NVarchar, 100, true),
            new("credential_id", SqlType.Int32, null, true),
            new("parameters", SqlType.Json, SqlType.MaxLengthSentinel, true),
            new("create_time", SqlType.GetDateTime2(7), null, true),
            new("modify_time", SqlType.GetDateTime2(7), null, true),
        ], static (_, _) => EmptyCatalogRows);
    }

    /// <summary>
    /// Rows for <c>sys.assembly_types</c> — the three built-in CLR system types
    /// (hierarchyid / geometry / geography), which the simulator models and
    /// whose <c>sys.types.is_assembly_type</c> already reads 1. They belong to
    /// the sys schema (schema_id 4) and the Microsoft.SqlServer.Types assembly
    /// (assembly_id 1); <c>is_user_defined</c> is 0 (system-shipped), matching
    /// SQL Server 2025 — followed by the database's CLR user-defined types.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateAssemblyTypes(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var sysSchemaId = SqlValue.FromInt32(Database.SysSchemaId);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var nullCollation = SqlValue.Null(SqlType.SystemName);
        var zeroByte = SqlValue.FromByte(0);
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var assemblyId = SqlValue.FromInt32(1);
        var systemTypeId = SqlValue.FromByte(240);
        var zeroInt = SqlValue.FromInt32(0);
        var nullProgId = SqlValue.Null(SqlType.NVarchar);
        SqlValue[] Row(string name, int userTypeId, short maxLength, string assemblyClass, bool binaryOrdered) =>
        [
            SqlValue.FromSystemName(name),
            systemTypeId,
            SqlValue.FromInt32(userTypeId),
            sysSchemaId,
            nullPrincipal,
            SqlValue.FromInt16(maxLength),
            zeroByte,
            zeroByte,
            nullCollation,
            trueBit,
            falseBit,
            trueBit,
            zeroInt,
            zeroInt,
            assemblyId,
            SqlValue.FromNVarchar(assemblyClass),
            SqlValue.FromBoolean(binaryOrdered),
            falseBit,
            nullProgId,
            SqlValue.FromNVarchar($"{assemblyClass}, Microsoft.SqlServer.Types, Version=11.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91"),
            falseBit,
        ];
        yield return Row("hierarchyid", 128, 892, "Microsoft.SqlServer.Types.SqlHierarchyId", true);
        yield return Row("geometry", 129, -1, "Microsoft.SqlServer.Types.SqlGeometry", false);
        yield return Row("geography", 130, -1, "Microsoft.SqlServer.Types.SqlGeography", false);

        // The CLR user-defined types, after the system three (probed
        // 2026-09-28 against SQL Server 2025).
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var alias in schema.AliasTypes.EnumerateValues().OrderBy(a => a.UserTypeId))
            {
                if (alias.UnderlyingType is not ClrUdtSqlType { Udt: var udt })
                    continue;
                yield return [
                    SqlValue.FromSystemName(alias.Name),
                    systemTypeId,
                    SqlValue.FromInt32(alias.UserTypeId),
                    SqlValue.FromInt32(schema.SchemaId),
                    Ownership.PrincipalIdValue(alias.OwnerPrincipalId),
                    SqlValue.FromInt16((short)udt.MaxByteSize),
                    zeroByte,
                    zeroByte,
                    nullCollation,
                    trueBit,
                    trueBit,
                    trueBit,
                    zeroInt,
                    zeroInt,
                    SqlValue.FromInt32(udt.Assembly.AssemblyId),
                    SqlValue.FromNVarchar(udt.ClassName),
                    SqlValue.FromBoolean(udt.IsByteOrdered),
                    SqlValue.FromBoolean(udt.IsFixedLength),
                    nullProgId,
                    SqlValue.FromNVarchar(udt.AssemblyQualifiedName),
                    falseBit,
                ];
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateTypeAssemblyUsages(Database database)
    {
        var systemAssembly = SqlValue.FromInt32(1);
        yield return [SqlValue.FromInt32(128), systemAssembly];
        yield return [SqlValue.FromInt32(129), systemAssembly];
        yield return [SqlValue.FromInt32(130), systemAssembly];
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var alias in schema.AliasTypes.EnumerateValues().OrderBy(a => a.UserTypeId))
            {
                if (alias.UnderlyingType is ClrUdtSqlType { Udt.Assembly: var assembly })
                    yield return [SqlValue.FromInt32(alias.UserTypeId), SqlValue.FromInt32(assembly.AssemblyId)];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.types</c>: every <see cref="SystypesRowData"/> entry
    /// (system types) followed by user-defined table types from each schema's
    /// <see cref="Schema.TableTypes"/> dict. Probe-confirmed (G1) shape:
    /// table-type rows surface <c>system_type_id = 243</c>,
    /// <c>is_user_defined = 1</c>, <c>is_table_type = 1</c>,
    /// <c>is_nullable = 0</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysTypes(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var sysSchemaId = SqlValue.FromInt32(Database.SysSchemaId);
        // System types: project from SystypesRowData using its name (col 0),
        // xtype (col 1, used as system_type_id), xusertype (col 3, used as
        // user_type_id). Every SystypesRowData row is is_user_defined = 0 —
        // including sysname (probe-confirmed against SQL Server 2025; DacFx's
        // UDDT scripting filters on is_user_defined = 1 and must not see it).
        // systypes columns: [4] length (max_length), [5] xprec (precision),
        // [6] xscale (scale) — probe-confirmed to equal sys.types' triple.
        var tableTypeMaxLength = SqlValue.FromInt16(-1);
        var zeroByte = SqlValue.FromByte(0);
        var nullCollation = SqlValue.Null(SqlType.SystemName);
        var databaseCollation = SqlValue.FromSystemName(database.CollationName);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var zeroDefaultObject = SqlValue.FromInt32(0);
        foreach (var row in SystypesRowData)
        {
            var name = (string)row[0]!;
            var systemTypeId = Convert.ToByte(row[1]!, CultureInfo.InvariantCulture);
            var userTypeId = Convert.ToInt32(row[3]!, CultureInfo.InvariantCulture);
            yield return [
                SqlValue.FromSystemName(name),
                SqlValue.FromByte(systemTypeId),
                SqlValue.FromInt32(userTypeId),
                sysSchemaId,
                falseBit,
                falseBit,
                // sysname and timestamp are the two built-ins whose columns
                // default to NOT NULL (probed 2026-10-02 against SQL Server 2025).
                name is "sysname" or "timestamp" ? falseBit : trueBit,
                name is "hierarchyid" or "geometry" or "geography" ? trueBit : falseBit,
                SqlValue.FromInt16(Convert.ToInt16(row[4]!, CultureInfo.InvariantCulture)),
                SqlValue.FromByte(Convert.ToByte(row[5]!, CultureInfo.InvariantCulture)),
                SqlValue.FromByte(Convert.ToByte(row[6]!, CultureInfo.InvariantCulture)),
                name is "char" or "nchar" or "ntext" or "nvarchar" or "sysname" or "text" or "varchar" ? databaseCollation : nullCollation,
                nullPrincipal,
                zeroDefaultObject,
                zeroDefaultObject,
            ];
        }
        // User-defined table types: probe-confirmed system_type_id 243,
        // max_length -1 / precision 0 / scale 0.
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var tt in schema.TableTypes.EnumerateValues().OrderBy(t => t.UserTypeId))
            {
                yield return [
                    SqlValue.FromSystemName(tt.Name),
                    SqlValue.FromByte(243),
                    SqlValue.FromInt32(tt.UserTypeId),
                    schemaId,
                    trueBit,
                    trueBit,
                    falseBit,
                    falseBit,
                    tableTypeMaxLength,
                    zeroByte,
                    zeroByte,
                    nullCollation,
                    Ownership.PrincipalIdValue(tt.OwnerPrincipalId),
                    zeroDefaultObject,
                    zeroDefaultObject,
                ];
            }
        }
        // Scalar alias types (UDDTs): probe-confirmed against SQL Server 2025
        // — `system_type_id` is the **underlying** built-in's id (e.g. 56 for
        // an alias of int, 231 for an alias of nvarchar), `is_user_defined`
        // is true, `is_table_type` is false, and `is_nullable` reflects the
        // alias-defined NULL/NOT NULL marker from CREATE TYPE.
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var alias in schema.AliasTypes.EnumerateValues().OrderBy(a => a.UserTypeId))
            {
                // max_length / precision / scale come from the underlying
                // built-in — reuse the sys.columns metadata computation via a
                // synthesized column so alias-of-nvarchar(50) reports the same
                // byte width (100) sys.columns would.
                var (maxLength, precision, scale) = GetSysColumnMetadata(
                    new HeapColumn(alias.Name, alias.UnderlyingType, alias.DeclaredMaxLength, alias.IsNullable));
                yield return [
                    SqlValue.FromSystemName(alias.Name),
                    SqlValue.FromByte(alias.SystemTypeId),
                    SqlValue.FromInt32(alias.UserTypeId),
                    schemaId,
                    trueBit,
                    falseBit,
                    alias.IsNullable ? trueBit : falseBit,
                    alias.UnderlyingType is ClrUdtSqlType ? trueBit : falseBit,
                    SqlValue.FromInt16(maxLength),
                    SqlValue.FromByte(precision),
                    SqlValue.FromByte(scale),
                    alias.UnderlyingType.Collation is not null ? databaseCollation : nullCollation,
                    Ownership.PrincipalIdValue(alias.OwnerPrincipalId),
                    alias.BoundDefault is { } boundDefault ? SqlValue.FromInt32(boundDefault.ObjectId) : zeroDefaultObject,
                    alias.BoundRule is { } boundRule ? SqlValue.FromInt32(boundRule.ObjectId) : zeroDefaultObject,
                ];
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysTableTypes(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var tableTypeSystemTypeId = SqlValue.FromByte(243);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var negOneLength = SqlValue.FromInt16(-1);
        var zeroByte = SqlValue.FromByte(0);
        var nullCollation = SqlValue.Null(SqlType.SystemName);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var tt in schema.TableTypes.EnumerateValues().OrderBy(t => t.UserTypeId))
            {
                yield return [
                    SqlValue.FromSystemName(tt.Name),
                    SqlValue.FromInt32(tt.ObjectId),
                    trueBit,
                    schemaId,
                    SqlValue.FromInt32(tt.UserTypeId),
                    SqlValue.FromBoolean(tt.IsMemoryOptimized),
                    tableTypeSystemTypeId,
                    Ownership.PrincipalIdValue(tt.OwnerPrincipalId),
                    negOneLength,
                    zeroByte,
                    zeroByte,
                    nullCollation,
                    falseBit,
                    falseBit,
                    trueBit,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.sequences</c>: one per registered sequence object,
    /// in object-id order. <c>is_cached</c> is 0 only under <c>NO CACHE</c>, and
    /// <c>cache_size</c> is the explicit <c>CACHE n</c>, else NULL (probed
    /// 2026-09-26 against SQL Server 2025). Type-id
    /// columns derive from the declared type via <see cref="SystypesRowData"/>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSequences(Parser.BatchContext batch, Database database)
    {
        var nullCache = SqlValue.Null(SqlType.Int32);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        // Object-id order across schemas, as real lists them (probed 2026-10-02
        // against SQL Server 2025: EF Core's scaffolding query reads them
        // unordered).
        var sequences = database.Schemas.EnumerateValues()
            .SelectMany(schema => schema.Sequences.EnumerateValues().Select(seq => (Schema: schema, Sequence: seq)))
            .OrderBy(pair => pair.Sequence.ObjectId);
        foreach (var (schema, seq) in sequences)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            var (systemTypeId, userTypeId) = seq.SpelledNumeric ? ((byte)108, 108) : SequenceTypeIds(seq.DeclaredType);
            var (precision, scale) = SequencePrecisionScale(seq.DeclaredType);
            yield return [
                SqlValue.FromSystemName(seq.Name),
                SqlValue.FromInt32(seq.ObjectId),
                schemaId,
                Ownership.PrincipalIdValue(seq.OwnerPrincipalId),
                SqlValue.FromDateTime(seq.CreateDate),
                SqlValue.FromDateTime(seq.ModifyDate),
                seq.AsDeclaredVariant(seq.StartValue),
                seq.AsDeclaredVariant(seq.Increment),
                seq.AsDeclaredVariant(seq.MinValue),
                seq.AsDeclaredVariant(seq.MaxValue),
                seq.Cycle ? trueBit : falseBit,
                seq.CacheSize == 0 ? falseBit : trueBit,
                seq.CacheSize is > 0 and var size ? SqlValue.FromInt32((int)size) : nullCache,
                seq.CurrentValueAsVariant,
                seq.LastUsedValueAsVariant,
                SqlValue.FromByte(systemTypeId),
                SqlValue.FromInt32(userTypeId),
                seq.IsExhausted ? trueBit : falseBit,
                SqlValue.FromByte(precision),
                SqlValue.FromByte(scale),
            ];
        }
    }

    /// <summary>
    /// Maps a sequence's declared scalar type to the <c>(system_type_id,
    /// user_type_id)</c> pair surfaced in <c>sys.sequences</c>. The values
    /// match SQL Server's documented system-type IDs (tinyint=48, smallint=52,
    /// int=56, bigint=127, decimal=106). System types use the same id for
    /// both columns.
    /// </summary>
    private static (byte SystemTypeId, int UserTypeId) SequenceTypeIds(SqlType type) => type switch
    {
        TinyIntSqlType => (48, 48),
        SmallIntSqlType => (52, 52),
        Int32SqlType => (56, 56),
        BigIntSqlType => (127, 127),
        DecimalSqlType => (106, 106),
        _ => (0, 0),
    };

    /// <summary>
    /// Maps a sequence's declared numeric type to the <c>(precision, scale)</c>
    /// pair surfaced in <c>sys.sequences</c>: the integer types report their
    /// documented decimal precision with scale 0 (tinyint 3, smallint 5, int 10,
    /// bigint 19), and <c>decimal(p, s)</c> reports its declared precision/scale.
    /// </summary>
    private static (byte Precision, byte Scale) SequencePrecisionScale(SqlType type) => type switch
    {
        TinyIntSqlType => (3, 0),
        SmallIntSqlType => (5, 0),
        Int32SqlType => (10, 0),
        BigIntSqlType => (19, 0),
        DecimalSqlType d => (d.precision, d.scale),
        _ => (0, 0),
    };

    /// <summary>
    /// Rows for <c>sys.procedures</c>: one row per <see cref="Procedure"/> in
    /// every schema. <c>create_date</c> survives <c>ALTER PROCEDURE</c> the way
    /// <see cref="SchemaObject.ObjectId"/> does, while <c>modify_date</c> reads
    /// <see cref="SchemaObject.ModifyDate"/>, which each <c>ALTER</c> /
    /// <c>CREATE OR ALTER</c> advances.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateProcedures(
        Parser.BatchContext batch,
        Database database,
        SqlType charTwo,
        SqlValue notMsShipped)
    {
        _ = batch;
        // 'P ' / 'SQL_STORED_PROCEDURE' — matches Procedure.ObjectTypeCode /
        // Procedure.ObjectTypeDescription, kept as local constants here to
        // avoid one SqlValue allocation per row.
        var procType = SqlValue.FromChar(charTwo, "P ");
        var procTypeDesc = SqlValue.FromNVarchar("SQL_STORED_PROCEDURE");
        var notAutoExecuted = SqlValue.FromBoolean(false);
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var proc in schema.Procedures.EnumerateValues().OrderBy(p => p.ObjectId))
            {
                yield return [
                    SqlValue.FromInt32(proc.ObjectId),
                    SqlValue.FromSystemName(proc.Name),
                    SqlValue.FromInt32(proc.Schema.SchemaId),
                    Ownership.PrincipalIdValue(proc.OwnerPrincipalId),
                    procType,
                    procTypeDesc,
                    SqlValue.FromDateTime(proc.CreateDate),
                    SqlValue.FromDateTime(proc.ModifyDate),
                    notMsShipped,
                    notAutoExecuted,
                ];
            }
        }
    }

    /// <summary>
    /// One <c>sys.system_views</c> row per catalog view, shaped as
    /// <c>sys.views</c>' rows are, with <c>is_ms_shipped</c> set.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSystemViews()
    {
        var falseBit = SqlValue.FromBoolean(false);
        var viewType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "V ");
        var viewTypeDesc = SqlValue.FromNVarchar("VIEW");
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var ledgerViewTypeNone = SqlValue.FromByte(0);
        var systemDate = SqlValue.FromDateTime(SystemObjectDate);
        var msShipped = SqlValue.FromBoolean(true);
        foreach (var (view, schemaId) in SystemViews.Value)
        {
            yield return [
                SqlValue.FromInt32(view.ObjectId),
                SqlValue.FromSystemName(view.Name),
                SqlValue.FromInt32(schemaId),
                nullPrincipal,
                viewType,
                viewTypeDesc,
                systemDate,
                systemDate,
                msShipped,
                falseBit,
                falseBit,
                ledgerViewTypeNone,
                falseBit,
                falseBit,
            ];
        }
    }

    private static IEnumerable<Procedure> NumberedProcedures(Database database)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var proc in schema.Procedures.EnumerateValues().OrderBy(p => p.ObjectId))
            {
                if (proc.Numbered is { } numbered)
                {
                    foreach (var member in numbered.Values)
                        yield return member;
                }
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateNumberedProcedures(Database database)
    {
        foreach (var member in NumberedProcedures(database))
        {
            yield return [
                SqlValue.FromInt32(member.ObjectId),
                SqlValue.FromInt16(member.GroupNumber),
                member.DefinitionText is { } definition ? SqlValue.FromNVarchar(definition) : SqlValue.Null(SqlType.NVarchar),
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateNumberedProcedureParameters(Database database)
    {
        foreach (var member in NumberedProcedures(database))
        {
            for (var i = 0; i < member.Parameters.Length; i++)
            {
                var param = member.Parameters[i];
                var isTvp = param.TableType is not null;
                var (maxLength, precision, scale) = isTvp
                    ? ((short)SqlType.MaxLengthSentinel, (byte)0, (byte)0)
                    : GetSysColumnMetadata(new HeapColumn(param.Name, param.Type, param.DeclaredMaxLength, nullable: true));
                yield return [
                    SqlValue.FromInt32(member.ObjectId),
                    SqlValue.FromInt16(member.GroupNumber),
                    SqlValue.FromSystemName("@" + param.Name),
                    SqlValue.FromInt32(i + 1),
                    SqlValue.FromByte(isTvp ? (byte)243 : SpelledTypeId(param.Type, param.SpelledNumeric)),
                    SqlValue.FromInt32(isTvp ? param.TableType!.UserTypeId : param.AliasType?.UserTypeId ?? (SpelledTypeId(param.Type, param.SpelledNumeric) is 108 ? 108 : param.Type.UserTypeId)),
                    SqlValue.FromInt16(maxLength),
                    SqlValue.FromByte(precision),
                    SqlValue.FromByte(scale),
                    SqlValue.FromBoolean(param.IsOutput),
                    SqlValue.FromBoolean(param.IsCursor),
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.views</c>: one row per <see cref="View"/> in every
    /// schema. <c>is_date_correlation_view</c> is always False (the feature
    /// isn't modeled).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateViews(Parser.BatchContext batch, Database database)
    {
        var falseBit = SqlValue.FromBoolean(false);
        var viewType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "V ");
        var viewTypeDesc = SqlValue.FromNVarchar("VIEW");
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var ledgerViewTypeNone = SqlValue.FromByte(0);
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var view in schema.Views.EnumerateValues().OrderBy(v => v.ObjectId))
            {
                yield return [
                    SqlValue.FromInt32(view.ObjectId),
                    SqlValue.FromSystemName(view.Name),
                    SqlValue.FromInt32(view.Schema.SchemaId),
                    Ownership.PrincipalIdValue(view.OwnerPrincipalId),
                    viewType,
                    viewTypeDesc,
                    SqlValue.FromDateTime(view.CreateDate),
                    SqlValue.FromDateTime(view.ModifyDate),
                    falseBit,
                    SqlValue.FromBoolean(view.WithCheckOption),
                    falseBit,
                    ledgerViewTypeNone,
                    SqlValue.FromBoolean(view.HasViewMetadata),
                    falseBit,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.parameters</c>. A <see cref="ScalarFunction"/> emits a
    /// row with <c>parameter_id=0</c> for its return type (empty <c>name</c>,
    /// <c>is_output=1</c>) followed by one row per declared parameter. An
    /// <see cref="InlineTableValuedFunction"/> emits one row per declared
    /// parameter only — no return-row, because the return shape is a TABLE
    /// (the columns surface in <c>sys.columns</c> instead). Probe-confirmed
    /// against SQL Server 2025. <c>max_length</c> / <c>precision</c> /
    /// <c>scale</c> come from the same <see cref="GetSysColumnMetadata"/>
    /// computation <c>sys.columns</c> uses, so an <c>nvarchar(40)</c>
    /// parameter reports the byte width 80 and an <c>int</c> reports 4 / 10 / 0;
    /// a table-valued parameter reports the MAX sentinel -1 with no
    /// precision / scale (probe-confirmed).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateParameters(Parser.BatchContext batch, Database database, CatalogFilter filter)
    {
        var hasIdFilter = filter.TargetsInt("object_id", out var wantObjectId, out var idMatchesNothing);
        if (hasIdFilter && idMatchesNothing)
            yield break;
        var emptyName = SqlValue.FromSystemName("");
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var zeroInt = SqlValue.FromInt32(0);
        var nullDefault = SqlValue.Null(SqlType.SqlVariant);
        var nullVectorDims = SqlValue.Null(SqlType.Int32);
        var nullVectorDesc = SqlValue.Null(SqlType.NVarchar);
        var float32Desc = SqlValue.FromString(NVarcharSqlType.Get(10, Collation.Catalog, Coercibility.Implicit), "float32");
        SqlValue VectorDims(SqlType type) => type is VectorSqlType vector ? SqlValue.FromInt32(vector.dimensions) : nullVectorDims;
        var float16Desc = SqlValue.FromString(NVarcharSqlType.Get(10, Collation.Catalog, Coercibility.Implicit), "float16");
        SqlValue VectorDesc(SqlType type) => type is VectorSqlType vector ? vector.IsFloat16 ? float16Desc : float32Desc : nullVectorDesc;
        var nullVectorTypeId = SqlValue.Null(SqlType.TinyInt);
        SqlValue VectorTypeId(SqlType type) => type is VectorSqlType vector ? SqlValue.FromByte(vector.IsFloat16 ? (byte)1 : (byte)0) : nullVectorTypeId;
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var proc in schema.Procedures.EnumerateValues().OrderBy(p => p.ObjectId))
            {
                if (hasIdFilter && proc.ObjectId != wantObjectId)
                    continue;
                var procObjectId = SqlValue.FromInt32(proc.ObjectId);
                for (var i = 0; i < proc.Parameters.Length; i++)
                {
                    var param = proc.Parameters[i];
                    // TVP parameters surface system_type_id 243 (table type)
                    // and the user_type_id of the referenced TableType.
                    // is_readonly is true only for TVP params (probe-confirmed
                    // — scalar params with a future READONLY shape don't ship).
                    var isTvp = param.TableType is not null;
                    var (maxLength, precision, scale) = isTvp
                        ? ((short)SqlType.MaxLengthSentinel, (byte)0, (byte)0)
                        : GetSysColumnMetadata(new HeapColumn(param.Name, param.Type, param.DeclaredMaxLength, nullable: true));
                    yield return [
                        procObjectId,
                        SqlValue.FromSystemName("@" + param.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(isTvp ? (byte)243 : SpelledTypeId(param.Type, param.SpelledNumeric)),
                        SqlValue.FromInt32(isTvp ? param.TableType!.UserTypeId : param.AliasType?.UserTypeId ?? (SpelledTypeId(param.Type, param.SpelledNumeric) is 108 ? 108 : param.Type.UserTypeId)),
                        SqlValue.FromInt16(maxLength),
                        SqlValue.FromByte(precision),
                        SqlValue.FromByte(scale),
                        SqlValue.FromBoolean(param.IsOutput),
                        falseBit,
                        // Only a CLR module reports its parameters' defaults
                        // (probed 2026-09-28 against SQL Server 2025).
                        proc.ClrEntry is not null && param.Default is not null ? trueBit : falseBit,
                        falseBit,
                        proc.ClrEntry is not null && param.Default is { } procDefault ? ClrDefaultValue(batch, procDefault) : nullDefault,
                        zeroInt,
                        SqlValue.FromBoolean(isTvp),
                        trueBit,
                        VectorDims(param.Type),
                        VectorDesc(param.Type),
                        VectorTypeId(param.Type),
                    ];
                }
            }
            foreach (var fn in schema.Functions.EnumerateValues().OrderBy(f => f.ObjectId))
            {
                if (hasIdFilter && fn.ObjectId != wantObjectId)
                    continue;
                var fnObjectId = SqlValue.FromInt32(fn.ObjectId);
                // Scalar UDFs get a synthetic parameter_id=0 return-type row;
                // inline TVFs don't (their TABLE shape lives in sys.columns).
                // A CLR scalar function and an aggregate carry the same row
                // (probed 2026-09-28 against SQL Server 2025).
                var (returnType, returnSpelledNumeric, returnAlias) = fn switch
                {
                    ScalarFunction scalar => (scalar.ReturnType, scalar.ReturnSpelledNumeric, scalar.ReturnAliasType),
                    ClrScalarFunction clrScalar => (clrScalar.ReturnType, false, null),
                    ClrAggregateFunction aggregate => (aggregate.ReturnType, false, null),
                    _ => (null, false, null),
                };
                if (returnType is not null)
                {
                    var (returnMaxLength, returnPrecision, returnScale) =
                        GetSysColumnMetadata(new HeapColumn(string.Empty, returnType, maxLength: null, nullable: true));
                    yield return [
                        fnObjectId,
                        emptyName,
                        SqlValue.FromInt32(0),
                        SqlValue.FromByte(SpelledTypeId(returnType, returnSpelledNumeric)),
                        SqlValue.FromInt32(returnAlias?.UserTypeId ?? (SpelledTypeId(returnType, returnSpelledNumeric) is 108 ? 108 : returnType.UserTypeId)),
                        SqlValue.FromInt16(returnMaxLength),
                        SqlValue.FromByte(returnPrecision),
                        SqlValue.FromByte(returnScale),
                        trueBit,
                        falseBit,
                        falseBit,
                        falseBit,
                        nullDefault,
                        zeroInt,
                        falseBit,
                        trueBit,
                        VectorDims(returnType),
                        VectorDesc(returnType),
                        VectorTypeId(returnType),
                    ];
                }
                for (var i = 0; i < fn.Parameters.Length; i++)
                {
                    var p = fn.Parameters[i];
                    var fnTvp = p.TableType is not null;
                    var (maxLength, precision, scale) = fnTvp
                        ? ((short)SqlType.MaxLengthSentinel, (byte)0, (byte)0)
                        : GetSysColumnMetadata(new HeapColumn(p.Name, p.Type, maxLength: null, nullable: true));
                    yield return [
                        fnObjectId,
                        SqlValue.FromSystemName("@" + p.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(fnTvp ? (byte)243 : SpelledTypeId(p.Type, p.SpelledNumeric)),
                        SqlValue.FromInt32(p.TableType?.UserTypeId ?? p.AliasType?.UserTypeId ?? (SpelledTypeId(p.Type, p.SpelledNumeric) is 108 ? 108 : p.Type.UserTypeId)),
                        SqlValue.FromInt16(maxLength),
                        SqlValue.FromByte(precision),
                        SqlValue.FromByte(scale),
                        falseBit,
                        falseBit,
                        fn is ClrFunction && p.Default is not null ? trueBit : falseBit,
                        falseBit,
                        fn is ClrFunction && p.Default is { } fnDefault ? ClrDefaultValue(batch, fnDefault) : nullDefault,
                        zeroInt,
                        SqlValue.FromBoolean(fnTvp),
                        trueBit,
                        VectorDims(p.Type),
                        VectorDesc(p.Type),
                        VectorTypeId(p.Type),
                    ];
                }
            }
        }
    }

    /// <summary>
    /// A CLR module parameter's declared default as <c>sys.parameters.default_value</c>
    /// reports it — the constant, as <c>sql_variant</c>.
    /// </summary>
    private static SqlValue ClrDefaultValue(Parser.BatchContext batch, Parser.Expression defaultExpression)
    {
        var value = defaultExpression.Run(new Parser.RuntimeContext(_ => throw SimulatedSqlException.MustDeclareScalarVariable(""), batch));
        return value.IsNull ? SqlValue.Null(SqlType.SqlVariant) : SqlValue.FromVariant(value);
    }

    /// <summary>
    /// The <c>CHARACTER_SET_NAME</c> the ISO views report for a single-byte
    /// string under <paramref name="collation"/>: its code page's
    /// <c>sys.syscharsets</c> name — <c>iso_1</c> for 1252, <c>utf8</c> for a
    /// <c>_UTF8</c> collation, <c>cp&lt;page&gt;</c> for the rest (probed
    /// 2026-10-02 against SQL Server 2025).
    /// </summary>
    internal static string CharacterSetName(Collation collation) => collation.AnsiCodePage switch
    {
        1252 => "iso_1",
        65001 => "utf8",
        var codePage => $"cp{codePage}",
    };

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.SCHEMATA</c>, ordered by schema_id over
    /// the union <c>sys.schemas</c> projects: the materialized
    /// <see cref="Database.Schemas"/> plus the catalog-only fixed schemas.
    /// <c>SCHEMA_OWNER</c> names the principal <c>sys.schemas.principal_id</c>
    /// reports — the like-named principal for an unmoved fixed schema, dbo or
    /// the <c>AUTHORIZATION</c> / <c>ALTER AUTHORIZATION</c> owner otherwise.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaSchemata(Database database, SqlValue nullSysName, SqlValue defaultCsName)
    {
        var catalog = SqlValue.FromSystemName(database.Name);
        var rows = new List<(int SchemaId, SqlValue[] Row)>();

        void Add(string name, int schemaId) =>
            rows.Add((schemaId, [
                catalog,
                SqlValue.FromSystemName(name),
                SqlValue.FromSystemName(Ownership.PrincipalName(database, Ownership.SchemaOwnerId(database, schemaId)) ?? name),
                nullSysName,
                nullSysName,
                defaultCsName,
            ]));

        foreach (var (_, s) in database.Schemas)
            Add(s.Name, s.SchemaId);
        foreach (var (name, id) in FixedCatalogOnlySchemas)
        {
            if (!database.Schemas.ContainsKey(name))
                Add(name, id);
        }

        rows.Sort((a, b) => a.SchemaId.CompareTo(b.SchemaId));
        return rows.Select(static entry => entry.Row);
    }

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaTables(Parser.BatchContext batch, Database database, SqlValue baseTable, SqlValue viewTableType)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var t in CatalogTables(schema, batch).OrderBy(t => t.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(t.CatalogName),
                    baseTable,
                ];
            }
            foreach (var view in schema.Views.EnumerateValues().OrderBy(v => v.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(view.Name),
                    viewTableType,
                ];
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaColumns(
        Parser.BatchContext batch,
        Database database,
        SqlValue unicodeCs,
        SqlValue radix10,
        SqlValue radix2,
        bool routineColumns)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var nullString = SqlValue.Null(SqlType.NVarchar);
        var nullInt32 = SqlValue.Null(SqlType.Int32);
        var nullInt16 = SqlValue.Null(SqlType.SmallInt);
        var nullByte = SqlValue.Null(SqlType.TinyInt);
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        var yesNullable = SqlValue.FromVarchar("YES");
        var noNullable = SqlValue.FromVarchar("NO");
        var dbDefaultCollation = SqlValue.FromSystemName(database.CollationName);
        // ORDINAL_POSITION resequences 1..N over the live columns — unlike
        // sys.columns.column_id it fills the hole DROP COLUMN leaves
        // (probe-confirmed).
        SqlValue[] Row(SqlValue schemaName, SqlValue tableName, HeapColumn col, int position)
        {
            var (charLength, octetLength, numericPrecision, numericRadix, numericScale, dateTimePrecision) = GetInformationSchemaColumnMetadata(col);
            // A view's column carries its collation in its type rather than as
            // a declared override.
            var collationName = col.Collation ?? col.Type.Collation?.Name;
            var cs = !SqlType.IsCollatedString(col.Type) ? nullSysName
                : SqlType.IsNationalStringCategory(col.Type) ? unicodeCs
                : SqlValue.FromSystemName(CharacterSetName(collationName is null ? database.Collation : Collation.Get(collationName)));
            var collation = !SqlType.IsCollatedString(col.Type) ? nullSysName
                : collationName is not null ? SqlValue.FromSystemName(collationName)
                : dbDefaultCollation;
            return
            [
                catalog,
                schemaName,
                tableName,
                SqlValue.FromSystemName(col.Name),
                SqlValue.FromInt32(position),
                (col.DefaultConstraint?.Definition ?? col.BoundDefault?.DefinitionText) is { } defaultText ? SqlValue.FromNVarchar(defaultText) : nullString,
                col.Nullable ? yesNullable : noNullable,
                IsoDataTypeName(col.Type, col.SpelledNumeric),
                charLength is int cl ? SqlValue.FromInt32(cl) : nullInt32,
                octetLength is int ol ? SqlValue.FromInt32(ol) : nullInt32,
                numericPrecision is byte np ? SqlValue.FromByte(np) : nullByte,
                numericRadix switch { 2 => radix2, 10 => radix10, _ => nullInt16 },
                numericScale is int ns ? SqlValue.FromInt32(ns) : nullInt32,
                dateTimePrecision is short dp ? SqlValue.FromInt16(dp) : nullInt16,
                nullSysName,
                nullSysName,
                cs,
                nullSysName,
                nullSysName,
                collation,
                col.AliasType is null ? nullSysName : catalog,
                col.AliasType is { } domain ? SqlValue.FromSystemName(domain.Schema.Name) : nullSysName,
                col.AliasType is { } domainType ? SqlValue.FromSystemName(domainType.Name) : nullSysName,
            ];
        }

        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            if (routineColumns)
            {
                foreach (var fn in schema.Functions.EnumerateValues().OrderBy(f => f.ObjectId))
                {
                    var outputColumns = fn switch
                    {
                        InlineTableValuedFunction inline => inline.OutputColumns,
                        MultiStatementTableValuedFunction multiStatement => multiStatement.OutputColumns,
                        ClrTableValuedFunction clr => clr.OutputColumns,
                        _ => [],
                    };
                    var functionName = SqlValue.FromSystemName(fn.Name);
                    for (var i = 0; i < outputColumns.Length; i++)
                        yield return Row(schemaName, functionName, outputColumns[i], i + 1);
                }
                continue;
            }
            foreach (var t in CatalogTables(schema, batch).OrderBy(t => t.ObjectId))
            {
                var tableName = SqlValue.FromSystemName(t.CatalogName);
                for (var i = 0; i < t.Columns.Length; i++)
                    yield return Row(schemaName, tableName, t.Columns[i], i + 1);
            }
            // Views project their output columns here the same way they do in
            // sys.columns — real lists them alongside base-table columns
            // (probe-confirmed), and INFORMATION_SCHEMA.TABLES already reports
            // the view itself.
            foreach (var view in schema.Views.EnumerateValues().OrderBy(v => v.ObjectId))
            {
                var viewName = SqlValue.FromSystemName(view.Name);
                for (var i = 0; i < view.OutputColumns.Length; i++)
                    yield return Row(schemaName, viewName, view.OutputColumns[i], i + 1);
            }
        }
    }

    /// <summary>
    /// Computes the INFORMATION_SCHEMA.COLUMNS numeric / character / datetime
    /// metadata triple. ISO-standard fields differ from <c>sys.columns</c>:
    /// CHARACTER_MAXIMUM_LENGTH is declared <em>char</em> length (not bytes —
    /// so <c>nvarchar(50)→50</c>), CHARACTER_OCTET_LENGTH carries the byte
    /// length. For text/ntext/image the values are the documented sentinels
    /// (<c>2147483647</c> / <c>1073741823</c> / <c>2147483647</c> for
    /// MAXIMUM_LENGTH; <c>2147483647</c> / <c>2147483646</c> / <c>2147483647</c>
    /// for OCTET_LENGTH). NUMERIC_PRECISION is NULL for bit (and for non-
    /// numeric types); float / real use radix 2 with NULL scale.
    /// </summary>
    private static (int? CharLength, int? OctetLength, byte? NumericPrecision, int? NumericRadix, int? NumericScale, short? DateTimePrecision) GetInformationSchemaColumnMetadata(HeapColumn col)
    {
        var t = col.Type;
        return t switch
        {
            _ when t == SqlType.Bit => (null, null, null, null, null, null),
            _ when t == SqlType.TinyInt => (null, null, 3, 10, 0, null),
            _ when t == SqlType.SmallInt => (null, null, 5, 10, 0, null),
            _ when t == SqlType.Int32 => (null, null, 10, 10, 0, null),
            _ when t == SqlType.BigInt => (null, null, 19, 10, 0, null),
            _ when t == SqlType.Money => (null, null, 19, 10, 4, null),
            _ when t == SqlType.SmallMoney => (null, null, 10, 10, 4, null),
            DecimalSqlType d => (null, null, d.precision, 10, d.scale, null),
            _ when t == SqlType.Float => (null, null, 53, 2, null, null),
            _ when t == SqlType.Real => (null, null, 24, 2, null, null),
            _ when t == SqlType.Date => (null, null, null, null, null, 0),
            _ when t == SqlType.SmallDateTime => (null, null, null, null, null, 0),
            _ when t == SqlType.DateTime => (null, null, null, null, null, 3),
            DateTime2SqlType dt2 => (null, null, null, null, null, (short)dt2.precision),
            TimeSqlType tm => (null, null, null, null, null, (short)tm.precision),
            DateTimeOffsetSqlType dto => (null, null, null, null, null, (short)dto.precision),
            _ when t == SqlType.Text => (2147483647, 2147483647, null, null, null, null),
            _ when t == SqlType.NText => (1073741823, 2147483646, null, null, null, null),
            _ when t == SqlType.Image => (2147483647, 2147483647, null, null, null, null),
            _ when t == SqlType.SystemName => (128, 256, null, null, null, null),
            CharSqlType c => (c.length, c.length, null, null, null, null),
            NCharSqlType nc => (nc.length, nc.length * 2, null, null, null, null),
            BinarySqlType bn => (bn.length, bn.length, null, null, null, null),
            VarcharSqlType vc => DeclaredVarLength(vc.length, col.MaxLength, octetPerChar: 1),
            NVarcharSqlType nv => DeclaredVarLength(nv.length, col.MaxLength, octetPerChar: 2),
            VarbinarySqlType vb => DeclaredVarLength(vb.length, col.MaxLength, octetPerChar: 1),
            _ when t == SqlType.UniqueIdentifier => (null, null, null, null, null, null),
            _ when t == SqlType.RowVersion => (null, null, null, null, null, null),
            // The CLR-backed / variant types report a length pair and nothing
            // else (probe-confirmed against SQL Server 2025): xml and both
            // spatial types carry the MAX sentinel -1, hierarchyid its 892-byte
            // bound, sql_variant a literal 0. Reaching the throw below for one
            // of these used to fail the *whole view* — the generator
            // materializes every column in the database before any WHERE
            // filter, so a single xml column anywhere made every
            // INFORMATION_SCHEMA.COLUMNS query raise, including one filtered to
            // an unrelated table (both AdventureWorks and WideWorldImporters
            // carry such columns).
            XmlSqlType or JsonSqlType or SpatialSqlType => (-1, -1, null, null, null, null),
            HierarchyIdSqlType => (892, 892, null, null, null, null),
            ClrUdtSqlType udt => (udt.Udt.MaxByteSize, udt.Udt.MaxByteSize, null, null, null, null),
            SqlVariantSqlType => (0, 0, null, null, null, null),
            VectorSqlType vector => (vector.ByteLength, vector.ByteLength, null, null, null, null),
            _ => throw new NotSupportedException($"No INFORMATION_SCHEMA.COLUMNS metadata for {t}."),
        };
    }

    /// <summary>
    /// Resolves the (char, octet) length pair for a variable-length string /
    /// binary column. <c>typeLength == -1</c> is the MAX form (both reported
    /// as <c>-1</c>); <c>typeLength == 0</c> means the type singleton didn't
    /// pin a length, so fall back to <see cref="HeapColumn.MaxLength"/>.
    /// </summary>
    private static (int? CharLength, int? OctetLength, byte? NumericPrecision, int? NumericRadix, int? NumericScale, short? DateTimePrecision) DeclaredVarLength(short typeLength, int? columnMaxLength, int octetPerChar)
    {
        if (typeLength == -1)
            return (-1, -1, null, null, null, null);
        var chars = typeLength == 0 ? (columnMaxLength ?? 1) : typeLength;
        return (chars, chars * octetPerChar, null, null, null, null);
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.ROUTINES</c>, one per procedure and
    /// function, in real's 51 columns (probed 2026-09-26 against SQL Server
    /// 2025): a scalar function's return type described as
    /// <c>INFORMATION_SCHEMA.PARAMETERS</c> describes it, a table-valued one's
    /// DATA_TYPE <c>TABLE</c>; ROUTINE_BODY <c>SQL</c> (<c>EXTERNAL</c> for a
    /// CLR function); a procedure <c>MODIFIES</c> SQL data with any number of
    /// result sets, a function <c>READS</c> with none; IS_DETERMINISTIC as
    /// <c>OBJECTPROPERTY(…, 'IsDeterministic')</c> answers it; CREATED /
    /// LAST_ALTERED the module's own dates.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaRoutines(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var catalog = SqlValue.FromNVarchar(database.Name);
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        var nullInt32 = SqlValue.Null(SqlType.Int32);
        var nullInt16 = SqlValue.Null(SqlType.SmallInt);
        var nullByte = SqlValue.Null(SqlType.TinyInt);
        var nullNVarchar = SqlValue.Null(SqlType.NVarchar);
        var yes = SqlValue.FromNVarchar("YES");
        var no = SqlValue.FromNVarchar("NO");
        var databaseCollation = SqlValue.FromSystemName(database.CollationName);

        SqlValue[] Row(SqlValue schemaName, SchemaObject routine, bool isProcedure, SqlType? returnType, bool spelledNumeric, string? definition)
        {
            var name = SqlValue.FromSystemName(routine.Name);
            var (charLength, octetLength, numericPrecision, numericRadix, numericScale, dateTimePrecision) = returnType is null
                ? (null, null, null, null, null, null)
                : GetInformationSchemaColumnMetadata(new HeapColumn(string.Empty, returnType, null, nullable: true));
            var isString = returnType is not null && SqlType.IsCollatedString(returnType);
            return
            [
                catalog, schemaName, name, catalog, schemaName, name,
                SqlValue.FromNVarchar(isProcedure ? "PROCEDURE" : "FUNCTION"),
                nullSysName, nullSysName, nullSysName, nullSysName, nullSysName, nullSysName,
                isProcedure ? nullSysName : returnType is null ? SqlValue.FromSystemName("TABLE") : IsoRoutineDataTypeName(returnType, spelledNumeric),
                charLength is int cl ? SqlValue.FromInt32(cl) : nullInt32,
                octetLength is int ol ? SqlValue.FromInt32(ol) : nullInt32,
                nullSysName, nullSysName,
                isString ? databaseCollation : nullSysName,
                nullSysName, nullSysName,
                !isString ? nullSysName : SqlValue.FromSystemName(SqlType.IsNationalStringCategory(returnType!) ? "UNICODE" : CharacterSetName(database.Collation)),
                numericPrecision is byte np ? SqlValue.FromByte(np) : nullByte,
                numericRadix is int radix ? SqlValue.FromInt16((short)radix) : nullInt16,
                numericScale is int ns ? SqlValue.FromInt32(ns) : nullInt32,
                dateTimePrecision is short dp ? SqlValue.FromInt16(dp) : nullInt16,
                nullNVarchar, nullInt16,
                nullSysName, nullSysName, nullSysName,
                nullSysName, nullSysName, nullSysName,
                SqlValue.Null(SqlType.BigInt),
                nullSysName,
                SqlValue.FromNVarchar(routine is ClrFunction or Procedure { ClrEntry: not null } ? "EXTERNAL" : "SQL"),
                RoutineDefinition(definition),
                nullSysName, nullNVarchar, nullNVarchar,
                ModuleDeterminism.Evaluate(database, routine) == 1 ? yes : no,
                SqlValue.FromNVarchar(isProcedure ? "MODIFIES" : "READS"),
                isProcedure ? nullNVarchar : routine is ScalarFunction { ReturnsNullOnNullInput: true } ? yes : no,
                nullSysName,
                yes,
                SqlValue.FromInt16(isProcedure ? (short)-1 : (short)0),
                no, no,
                SqlValue.FromDateTime(routine.CreateDate),
                SqlValue.FromDateTime(routine.ModifyDate),
            ];
        }

        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromNVarchar(schema.Name);
            foreach (var proc in schema.Procedures.EnumerateValues().OrderBy(p => p.ObjectId))
                yield return Row(schemaName, proc, isProcedure: true, null, false, proc.DefinitionText);
            foreach (var fn in schema.Functions.EnumerateValues().OrderBy(f => f.ObjectId))
            {
                yield return fn switch
                {
                    ScalarFunction scalar => Row(schemaName, fn, isProcedure: false, scalar.ReturnType, scalar.ReturnSpelledNumeric, fn.DefinitionText),
                    ClrScalarFunction clrScalar => Row(schemaName, fn, isProcedure: false, clrScalar.ReturnType, false, null),
                    ClrAggregateFunction aggregate => Row(schemaName, fn, isProcedure: false, aggregate.ReturnType, false, null),
                    _ => Row(schemaName, fn, isProcedure: false, null, false, fn.DefinitionText),
                };
            }
        }
    }

    /// <summary>
    /// Builds the <c>INFORMATION_SCHEMA.ROUTINES.ROUTINE_DEFINITION</c> value
    /// from a module's captured source text. The ISO column is
    /// <c>nvarchar(4000)</c>, so the definition is truncated to its first 4000
    /// characters (matching SQL Server); NULL stays NULL (encrypted modules).
    /// </summary>
    private static SqlValue RoutineDefinition(string? text) =>
        text is null
            ? SqlValue.Null(SqlType.NVarchar)
            : SqlValue.FromNVarchar(text.Length > 4000 ? text[..4000] : text);

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.PARAMETERS</c>: one per procedure and
    /// function parameter, and a scalar function's return value as row 0 —
    /// named by the empty string, <c>OUT</c> and <c>IS_RESULT</c> (probed
    /// 2026-09-26 against SQL Server 2025). The type columns follow
    /// <c>INFORMATION_SCHEMA.COLUMNS</c>, the collation is the database's
    /// for a string type, and a table-valued parameter reports
    /// <c>table type</c> and its type as the user-defined type, as an alias
    /// type parameter reports its alias.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaParameters(
        Parser.BatchContext batch,
        Database database,
        SqlValue modeIn,
        SqlValue modeInOut,
        SqlValue modeOut,
        SqlValue unicodeCs,
        SqlValue radix10,
        SqlValue radix2)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var nullInt32 = SqlValue.Null(SqlType.Int32);
        var nullInt16 = SqlValue.Null(SqlType.SmallInt);
        var nullByte = SqlValue.Null(SqlType.TinyInt);
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        var nullNVarchar = SqlValue.Null(SqlType.NVarchar);
        var yes = SqlValue.FromNVarchar("YES");
        var no = SqlValue.FromNVarchar("NO");
        var tableTypeName = SqlValue.FromSystemName("table type");
        var databaseCollation = SqlValue.FromSystemName(database.CollationName);

        SqlValue[] Row(SqlValue schemaName, string routine, int ordinal, SqlValue mode, string name, SqlType type, bool spelledNumeric, int? declaredMaxLength, TableType? tableType, AliasType? alias)
        {
            var (charLength, octetLength, numericPrecision, numericRadix, numericScale, dateTimePrecision) = tableType is null
                ? GetInformationSchemaColumnMetadata(new HeapColumn(name, type, declaredMaxLength, nullable: true))
                : (null, null, null, null, null, null);
            var isString = tableType is null && SqlType.IsCollatedString(type);
            var (userTypeSchema, userTypeName) = tableType is not null ? (tableType.Schema.Name, tableType.Name)
                : alias is not null ? (alias.Schema.Name, alias.Name)
                : (null, null);
            return
            [
                catalog,
                schemaName,
                SqlValue.FromSystemName(routine),
                SqlValue.FromInt32(ordinal),
                mode,
                ordinal == 0 ? yes : no,
                no,
                SqlValue.FromSystemName(name),
                tableType is null ? IsoRoutineDataTypeName(type, spelledNumeric) : tableTypeName,
                charLength is int cl ? SqlValue.FromInt32(cl) : nullInt32,
                octetLength is int ol ? SqlValue.FromInt32(ol) : nullInt32,
                nullSysName,
                nullSysName,
                isString ? databaseCollation : nullSysName,
                nullSysName,
                nullSysName,
                !isString ? nullSysName : SqlType.IsNationalStringCategory(type) ? unicodeCs : SqlValue.FromSystemName(CharacterSetName(database.Collation)),
                numericPrecision is byte np ? SqlValue.FromByte(np) : nullByte,
                numericRadix switch { 2 => radix2, 10 => radix10, _ => nullInt16 },
                numericScale is int ns ? SqlValue.FromInt32(ns) : nullInt32,
                dateTimePrecision is short dp ? SqlValue.FromInt16(dp) : nullInt16,
                nullNVarchar,
                nullInt16,
                userTypeName is null ? nullSysName : catalog,
                userTypeSchema is null ? nullSysName : SqlValue.FromSystemName(userTypeSchema),
                userTypeName is null ? nullSysName : SqlValue.FromSystemName(userTypeName),
                nullSysName,
                nullSysName,
                nullSysName,
            ];
        }

        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var proc in schema.Procedures.EnumerateValues().OrderBy(p => p.ObjectId))
            {
                for (var i = 0; i < proc.Parameters.Length; i++)
                {
                    var param = proc.Parameters[i];
                    yield return Row(schemaName, proc.Name, i + 1, param.IsOutput ? modeInOut : modeIn, "@" + param.Name,
                        param.Type, param.SpelledNumeric, param.DeclaredMaxLength, param.TableType, param.AliasType);
                }
            }
            foreach (var fn in schema.Functions.EnumerateValues().OrderBy(f => f.ObjectId))
            {
                // Table-valued functions have no return-value row.
                if (fn is ScalarFunction scalar)
                    yield return Row(schemaName, fn.Name, 0, modeOut, string.Empty, scalar.ReturnType, scalar.ReturnSpelledNumeric, null, null, scalar.ReturnAliasType);

                for (var i = 0; i < fn.Parameters.Length; i++)
                {
                    var param = fn.Parameters[i];
                    yield return Row(schemaName, fn.Name, i + 1, modeIn, "@" + param.Name, param.Type, param.SpelledNumeric, null, param.TableType, param.AliasType);
                }
            }
        }
    }

    /// <summary>A type's catalog id with a <c>numeric</c> spelling honored: 108 rather than 106.</summary>
    private static byte SpelledTypeId(SqlType type, bool spelledNumeric) =>
        spelledNumeric && type is DecimalSqlType ? (byte)108 : type.SystemTypeId;

    private static SqlValue IsoDataTypeName(SqlType type, bool spelledNumeric) =>
        spelledNumeric && type is DecimalSqlType ? SqlValue.FromSystemName("numeric") : IsoDataTypeName(type);

    /// <summary>
    /// The type name the INFORMATION_SCHEMA views report. These resolve an
    /// alias to the type it stands for, so a <c>sysname</c> column or
    /// parameter surfaces as <c>nvarchar</c> — probe-confirmed against SQL
    /// Server 2025 for both COLUMNS and PARAMETERS. The <c>sys.*</c> catalog
    /// views keep the alias instead, which is why this doesn't live on
    /// <see cref="SqlType.SqlServerName"/>.
    /// </summary>
    private static SqlValue IsoDataTypeName(SqlType type) =>
        SqlValue.FromSystemName(type == SqlType.SystemName ? "nvarchar" : type.SqlServerName);

    // A routine's parameter or return value of type vector reads as the
    // varbinary it is stored as, where a table column reads vector (probed
    // 2026-10-02 against SQL Server 2025).
    private static SqlValue IsoRoutineDataTypeName(SqlType type, bool spelledNumeric) =>
        type is VectorSqlType ? SqlValue.FromSystemName("varbinary") : IsoDataTypeName(type, spelledNumeric);

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.VIEWS</c>: per-view ISO-shape entries.
    /// VIEW_DEFINITION is the whole module text <c>OBJECT_DEFINITION</c>
    /// reports, cut to 4000 characters and NULL under <c>WITH ENCRYPTION</c>,
    /// as ROUTINE_DEFINITION is (probed 2026-09-26 against SQL Server 2025).
    /// CHECK_OPTION is 'CASCADE'
    /// when WITH CHECK OPTION was specified, 'NONE' otherwise. IS_UPDATABLE
    /// is hardcoded 'NO' (probe-confirmed: real SQL Server reports 'NO'
    /// even for actually-updatable views).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaViews(
        Parser.BatchContext batch,
        Database database,
        SqlValue checkOptionNone,
        SqlValue checkOptionCascade,
        SqlValue isUpdatableNo)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var (_, schema) in database.Schemas)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var view in schema.Views.EnumerateValues().OrderBy(v => v.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(view.Name),
                    RoutineDefinition(view.DefinitionText),
                    view.WithCheckOption ? checkOptionCascade : checkOptionNone,
                    isUpdatableNo,
                ];
            }
        }
    }
}
