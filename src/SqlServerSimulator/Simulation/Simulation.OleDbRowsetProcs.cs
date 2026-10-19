using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The schema-rowset procedures SQL Server's OLE DB provider (MSOLEDBSQL, the
// provider of a linked server) calls on the server it links to: the tables,
// columns, indexes, check constraints and statistics of a table a four-part
// name reaches, and the catalog the provider's sp_tables_ex / sp_columns_ex /
// sp_primarykeys / sp_foreignkeys / sp_catalogs / privilege procedures read.
// Each family has a version per provider generation (the plain one, _90 and
// _100) and a "2" form that lists every object rather than the one named; see
// docs/claude/catalog-views.md for the per-version type mapping.
partial class Simulation
{
    private static readonly NVarcharSqlType RowsetName = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetName134 = NVarcharSqlType.Get(134, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetType = NVarcharSqlType.Get(30, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetDescription = NVarcharSqlType.Get(1, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetDefault = NVarcharSqlType.Get(2000, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetText4000 = NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetRule = NVarcharSqlType.Get(11, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetFilter = NVarcharSqlType.Get(SqlType.MaxLengthSentinel, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetLiteral = NVarcharSqlType.Get(32, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetParameterDefault = NVarcharSqlType.Get(255, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType RowsetParameterDescription = NVarcharSqlType.Get(50, Collation.Catalog, Coercibility.Implicit);

    private static readonly SqlType[] RowsetTablesInfoSchema =
    [
        RowsetName, RowsetName, RowsetName, RowsetType, SqlType.UniqueIdentifier, SqlType.Bit, SqlType.Int32, SqlType.SmallInt,
        SqlType.Int32, SqlType.Int32, SqlType.BigInt, SqlType.Int32, RowsetDescription, SqlType.Int32, SqlType.Int32,
    ];

    private static readonly string[] RowsetTablesInfoNames =
    [
        "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE", "TABLE_GUID", "BOOKMARKS", "BOOKMARK_TYPE", "BOOKMARK_DATATYPE",
        "BOOKMARK_MAXIMUM_LENGTH", "BOOKMARK_INFORMATION", "TABLE_VERSION", "CARDINALITY", "DESCRIPTION", "TABLE_PROPID", "TABLE_FLAGS",
    ];

    private static readonly SqlType[] RowsetTablesSchema =
        [RowsetName, RowsetName, RowsetName, RowsetType, SqlType.UniqueIdentifier, RowsetDescription, SqlType.Int32, SqlType.DateTime, SqlType.DateTime];

    private static readonly string[] RowsetTablesNames =
        ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE", "TABLE_GUID", "DESCRIPTION", "TABLE_PROPID", "DATE_CREATED", "DATE_MODIFIED"];

    private static readonly SqlType[] RowsetColumnsSchema =
    [
        RowsetName, RowsetName, RowsetName, RowsetName, SqlType.UniqueIdentifier, SqlType.Int32, SqlType.Int32, SqlType.Bit, RowsetDefault,
        SqlType.Int32, SqlType.Bit, SqlType.SmallInt, SqlType.UniqueIdentifier, SqlType.Int32, SqlType.Int32, SqlType.SmallInt, SqlType.SmallInt,
        SqlType.Int32, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName,
        RowsetDescription, SqlType.Int32, SqlType.Int32, SqlType.Int32, BinarySqlType.Get(5), SqlType.Bit,
        RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetText4000, SqlType.Bit, SqlType.Bit,
    ];

    private static readonly string[] RowsetColumnsNames =
    [
        "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "COLUMN_GUID", "COLUMN_PROPID", "ORDINAL_POSITION", "COLUMN_HASDEFAULT",
        "COLUMN_DEFAULT", "COLUMN_FLAGS", "IS_NULLABLE", "DATA_TYPE", "TYPE_GUID", "CHARACTER_MAXIMUM_LENGTH", "CHARACTER_OCTET_LENGTH",
        "NUMERIC_PRECISION", "NUMERIC_SCALE", "DATETIME_PRECISION", "CHARACTER_SET_CATALOG", "CHARACTER_SET_SCHEMA", "CHARACTER_SET_NAME",
        "COLLATION_CATALOG", "COLLATION_SCHEMA", "COLLATION_NAME", "DOMAIN_CATALOG", "DOMAIN_SCHEMA", "DOMAIN_NAME", "DESCRIPTION",
        "COLUMN_LCID", "COLUMN_COMPFLAGS", "COLUMN_SORTID", "COLUMN_TDSCOLLATION", "IS_COMPUTED", "SS_XML_SCHEMACOLLECTION_CATALOGNAME",
        "SS_XML_SCHEMACOLLECTION_SCHEMANAME", "SS_XML_SCHEMACOLLECTIONNAME", "SS_UDT_CATALOGNAME", "SS_UDT_SCHEMANAME", "SS_UDT_NAME",
        "SS_UDT_ASSEMBLY_TYPENAME", "SS_IS_SPARSE", "SS_IS_COLUMN_SET",
    ];

    private static readonly SqlType[] RowsetIndexesSchema =
    [
        RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.SmallInt,
        SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Bit, SqlType.Bit, SqlType.Int32, SqlType.Int32, RowsetName, SqlType.UniqueIdentifier,
        SqlType.Int32, SqlType.SmallInt, SqlType.Int32, SqlType.Int32, RowsetFilter, SqlType.Bit, SqlType.Int32,
    ];

    private static readonly string[] RowsetIndexesNames =
    [
        "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "INDEX_CATALOG", "INDEX_SCHEMA", "INDEX_NAME", "PRIMARY_KEY", "UNIQUE", "CLUSTERED",
        "TYPE", "FILL_FACTOR", "INITIAL_SIZE", "NULLS", "SORT_BOOKMARKS", "AUTO_UPDATE", "NULL_COLLATION", "ORDINAL_POSITION", "COLUMN_NAME",
        "COLUMN_GUID", "COLUMN_PROPID", "COLLATION", "CARDINALITY", "PAGES", "FILTER_CONDITION", "INTEGRATED", "STATUS",
    ];

    private static readonly SqlType[] RowsetCheckConstraintsSchema =
        [RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetText4000, RowsetDescription];

    private static readonly string[] RowsetCheckConstraintsNames =
        ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "CONSTRAINT_CATALOG", "CONSTRAINT_SCHEMA", "CONSTRAINT_NAME", "CHECK_CLAUSE", "DESCRIPTION"];

    private static readonly SqlType[] RowsetStatisticsSchema =
    [
        RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, SqlType.SmallInt, RowsetName, SqlType.UniqueIdentifier,
        SqlType.Int32, SqlType.Int32, SqlType.SmallInt, SqlType.DateTime, SqlType.Int32, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.Real,
    ];

    private static readonly string[] RowsetStatisticsNames =
    [
        "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "STATISTICS_CATALOG", "STATISTICS_SCHEMA", "STATISTICS_NAME", "STATISTICS_TYPE",
        "COLUMN_NAME", "COLUMN_GUID", "COLUMN_PROPID", "ORDINAL_POSITION", "SAMPLE_PCT", "LAST_UPDATE_TIME", "NO_OF_RANGES",
        "COLUMN_CARDINALITY", "TUPLE_CARDINALITY", "TABLE_CARDINALITY", "AVG_COLUMN_LENGTH",
    ];

    private static readonly SqlType[] RowsetPrimaryKeysSchema =
        [RowsetName, RowsetName, RowsetName, RowsetName, SqlType.UniqueIdentifier, SqlType.Int32, SqlType.Int32, RowsetName];

    private static readonly string[] RowsetPrimaryKeysNames =
        ["TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "COLUMN_GUID", "COLUMN_PROPID", "ORDINAL", "PK_NAME"];

    private static readonly SqlType[] RowsetForeignKeysSchema =
    [
        RowsetName, RowsetName, RowsetName, RowsetName, SqlType.UniqueIdentifier, SqlType.Int32, RowsetName, RowsetName, RowsetName,
        RowsetName, SqlType.UniqueIdentifier, SqlType.Int32, SqlType.Int32, RowsetRule, RowsetRule, RowsetName, RowsetName, SqlType.SmallInt,
    ];

    private static readonly string[] RowsetForeignKeysNames =
    [
        "PK_TABLE_CATALOG", "PK_TABLE_SCHEMA", "PK_TABLE_NAME", "PK_COLUMN_NAME", "PK_COLUMN_GUID", "PK_COLUMN_PROPID", "FK_TABLE_CATALOG",
        "FK_TABLE_SCHEMA", "FK_TABLE_NAME", "FK_COLUMN_NAME", "FK_COLUMN_GUID", "FK_COLUMN_PROPID", "ORDINAL", "UPDATE_RULE", "DELETE_RULE",
        "PK_NAME", "FK_NAME", "DEFERRABILITY",
    ];

    private static readonly SqlType[] RowsetTablePrivilegesSchema =
        [RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetType, SqlType.Bit];

    private static readonly string[] RowsetTablePrivilegesNames =
        ["GRANTOR", "GRANTEE", "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "PRIVILEGE_TYPE", "IS_GRANTABLE"];

    private static readonly SqlType[] RowsetColumnPrivilegesSchema =
        [RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, SqlType.UniqueIdentifier, SqlType.Int32, RowsetType, SqlType.Bit];

    private static readonly string[] RowsetColumnPrivilegesNames =
        ["GRANTOR", "GRANTEE", "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "COLUMN_GUID", "COLUMN_PROPID", "PRIVILEGE_TYPE", "IS_GRANTABLE"];

    /// <summary>The provider generation a schema-rowset procedure answers for.</summary>
    private enum RowsetVersion
    {
        Classic,
        V90,
        V100,
    }

    private static SystemProcedureParameter RowsetNameParameter(string name, bool required = false) =>
        new(name, SqlType.NVarchar, 128, required ? null : SqlValue.Null(SqlType.NVarchar));

    private static SystemProcedureParameter RowsetTypeParameter() =>
        new("table_type", SqlType.NVarchar, 255, SqlValue.Null(SqlType.NVarchar));

    /// <summary>
    /// Runs one of the OLE DB schema-rowset procedures, or returns null for a
    /// name that is none of them.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome>? InvokeOleDbRowsetProcedure(BatchContext batch, string procedure, string calledAs) => procedure switch
    {
        "sp_catalogs_rowset" or "sp_catalogs_rowset2" => InvokeCatalogsRowset(batch, procedure, calledAs),
        "sp_check_constbytable_rowset" or "sp_check_constbytable_rowset2" => InvokeCheckConstraintsRowset(batch, procedure, calledAs),
        "sp_column_privileges_rowset" or "sp_column_privileges_rowset2" => InvokeColumnPrivilegesRowset(batch, procedure, calledAs),
        "sp_columns_100_rowset" or "sp_columns_100_rowset2" => InvokeColumnsRowset(batch, procedure, calledAs, RowsetVersion.V100),
        "sp_columns_90_rowset" or "sp_columns_90_rowset2" => InvokeColumnsRowset(batch, procedure, calledAs, RowsetVersion.V90),
        "sp_columns_rowset" or "sp_columns_rowset2" => InvokeColumnsRowset(batch, procedure, calledAs, RowsetVersion.Classic),
        "sp_foreign_keys_rowset" or "sp_foreign_keys_rowset2" or "sp_foreign_keys_rowset3" => InvokeForeignKeysRowset(batch, procedure, calledAs),
        "sp_indexes_100_rowset" or "sp_indexes_100_rowset2" or "sp_indexes_90_rowset" or "sp_indexes_90_rowset2" => InvokeIndexesRowset(batch, procedure, calledAs, withStatus: true),
        "sp_indexes_rowset" or "sp_indexes_rowset2" => InvokeIndexesRowset(batch, procedure, calledAs, withStatus: false),
        "sp_primary_keys_rowset" or "sp_primary_keys_rowset2" => InvokePrimaryKeysRowset(batch, procedure, calledAs),
        "sp_procedure_params_100_rowset" or "sp_procedure_params_100_rowset2" => InvokeProcedureParamsRowset(batch, procedure, calledAs, RowsetVersion.V100),
        "sp_procedure_params_90_rowset" or "sp_procedure_params_90_rowset2" => InvokeProcedureParamsRowset(batch, procedure, calledAs, RowsetVersion.V90),
        "sp_procedure_params_rowset" or "sp_procedure_params_rowset2" => InvokeProcedureParamsRowset(batch, procedure, calledAs, RowsetVersion.Classic),
        "sp_procedures_rowset" or "sp_procedures_rowset2" => InvokeProceduresRowset(batch, procedure, calledAs),
        "sp_provider_types_100_rowset" => InvokeProviderTypesRowset(batch, procedure, calledAs, RowsetVersion.V100),
        "sp_provider_types_90_rowset" => InvokeProviderTypesRowset(batch, procedure, calledAs, RowsetVersion.V90),
        "sp_provider_types_rowset" => InvokeProviderTypesRowset(batch, procedure, calledAs, RowsetVersion.Classic),
        "sp_table_privileges_rowset" or "sp_table_privileges_rowset2" => InvokeTablePrivilegesRowset(batch, procedure, calledAs),
        "sp_table_statistics2_rowset" => InvokeTableStatisticsRowset(batch, calledAs),
        "sp_table_statistics_rowset" => InvokeEmptyTableStatisticsRowset(batch, calledAs),
        "sp_table_type_columns_100_rowset" => InvokeTableTypeColumnsRowset(batch, procedure, calledAs),
        "sp_table_type_primary_keys_rowset" => InvokeTableTypePrimaryKeysRowset(batch, procedure, calledAs),
        "sp_table_types_rowset" => InvokeTableTypesRowset(batch, procedure, calledAs),
        "sp_tables_info_90_rowset" or "sp_tables_info_90_rowset2" => InvokeTablesInfoRowset(batch, procedure, calledAs, withFlags: true, bookmark64: false),
        "sp_tables_info_90_rowset_64" or "sp_tables_info_90_rowset2_64" => InvokeTablesInfoRowset(batch, procedure, calledAs, withFlags: true, bookmark64: true),
        "sp_tables_info_rowset" or "sp_tables_info_rowset2" => InvokeTablesInfoRowset(batch, procedure, calledAs, withFlags: false, bookmark64: false),
        "sp_tables_info_rowset_64" or "sp_tables_info_rowset2_64" => InvokeTablesInfoRowset(batch, procedure, calledAs, withFlags: false, bookmark64: true),
        "sp_tables_rowset" or "sp_tables_rowset2" => InvokeTablesRowset(batch, procedure, calledAs),
        _ => null,
    };

    /// <summary>
    /// Whether a system procedure is one of the schema-rowset procedures, or
    /// the schema-lock pair the provider brackets a statement with — reads
    /// all, as far as the cached catalog rows go.
    /// </summary>
    private static bool IsOleDbRowsetProcedure(string procedure) =>
        procedure.EndsWith("_rowset", StringComparison.Ordinal) || procedure.EndsWith("_rowset2", StringComparison.Ordinal)
        || procedure.EndsWith("_rowset3", StringComparison.Ordinal) || procedure.EndsWith("_64", StringComparison.Ordinal)
        || procedure is "sp_getschemalock" or "sp_releaseschemalock";

    /// <summary>Whether a schema-rowset procedure is the "2" form, which lists every object rather than the one its first parameter names.</summary>
    private static bool ListsEveryObject(string procedure) =>
        procedure.EndsWith("rowset2", StringComparison.Ordinal) || procedure.EndsWith("rowset2_64", StringComparison.Ordinal);

    private static string? RowsetText(SqlValue value) => value.IsNull ? null : value.AsString;

    /// <summary>
    /// A table, view, synonym or catalog view a schema rowset describes, with
    /// the columns it has, the dates its <c>sys.objects</c> row reports and,
    /// for a system table, its row count.
    /// </summary>
    private readonly struct RowsetObject(string schema, string name, string type, SchemaObject? owner, HeapTable? table, HeapColumn[] columns, DateTime createDate, DateTime modifyDate, long? systemRows = null)
    {
        public readonly long? SystemRows = systemRows;

        public readonly string Schema = schema;
        public readonly string Name = name;
        public readonly string Type = type;
        public readonly SchemaObject? Object = owner;
        public readonly HeapTable? Table = table;
        public readonly HeapColumn[] Columns = columns;
        public readonly DateTime CreateDate = createDate;
        public readonly DateTime ModifyDate = modifyDate;
    }

    /// <summary>
    /// The current database's tables, views and synonyms, and the catalog
    /// views, that <paramref name="name"/> and <paramref name="schemaName"/>
    /// name exactly under the database's collation — every one for a null —
    /// sorted by type, schema and name (probed 2026-10-11 against SQL Server
    /// 2025: a name is never a pattern).
    /// </summary>
    private static List<RowsetObject> RowsetObjects(BatchContext batch, string? name, string? schemaName)
    {
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var visible = PermissionEnforcement.ObjectVisibility(batch, database);
        var objects = new List<RowsetObject>();
        bool Named(string candidate) => name is null || collation.Equals(name, candidate);
        foreach (var (_, schema) in database.Schemas)
        {
            if (schemaName is not null && !collation.Equals(schemaName, schema.Name))
                continue;
            foreach (var (_, table) in schema.HeapTables)
            {
                if (!table.IsTableVariable && Named(table.Name) && visible?.Invoke(table) != false)
                    objects.Add(new(schema.Name, table.Name, "TABLE", table, table, table.Columns, table.CreateDate, table.ModifyDate));
            }
            foreach (var (_, view) in schema.Views)
            {
                if (Named(view.Name) && visible?.Invoke(view) != false)
                    objects.Add(new(schema.Name, view.Name, "VIEW", view, null, view.OutputColumns, view.CreateDate, view.ModifyDate));
            }
            foreach (var (_, synonym) in schema.Synonyms)
            {
                if (Named(synonym.Name) && visible?.Invoke(synonym) != false)
                    objects.Add(new(schema.Name, synonym.Name, "SYNONYM", synonym, null, [], synonym.CreateDate, synonym.ModifyDate));
            }
            // A system table — a system base table, or one real ships beside the
            // catalog views — lists with its row count and none of its columns
            // (probed 2026-10-19 against SQL Server 2025).
            foreach (var (systemView, schemaId) in BuiltInResources.SystemViews.Value)
            {
                if (schemaId != schema.SchemaId || !Named(systemView.Name))
                    continue;
                objects.Add(systemView.IsTable
                    ? new(schema.Name, systemView.Name, "SYSTEM TABLE", null, null, [], BuiltInResources.SystemObjectDate, BuiltInResources.SystemObjectDate,
                        systemView.RowGenerator(batch, database).LongCount())
                    : new(schema.Name, systemView.Name, "SYSTEM VIEW", null, null, systemView.Columns, BuiltInResources.SystemObjectDate, BuiltInResources.SystemObjectDate));
            }
            if (schema.SchemaId != Database.SysSchemaId)
                continue;
            foreach (var systemTable in BuiltInResources.SystemTableObjects(database))
            {
                if (systemTable.IsInternal || !Named(systemTable.Name) || !Parser.Expressions.ObjectProperty.SystemTableVisible(batch, database, systemTable))
                    continue;
                var created = systemTable.CreateDate ?? BuiltInResources.SysDatabasesCreateDate;
                objects.Add(new(schema.Name, systemTable.Name, "SYSTEM TABLE", null, null, [], created, systemTable.ModifyDate ?? created,
                    BuiltInResources.SystemTableCardinality(batch, database, systemTable)));
            }
        }
        objects.Sort((a, b) =>
        {
            var byType = string.CompareOrdinal(a.Type, b.Type);
            if (byType != 0)
                return byType;
            var bySchema = collation.Compare(a.Schema, b.Schema);
            return bySchema != 0 ? bySchema : collation.Compare(a.Name, b.Name);
        });
        return objects;
    }

    /// <summary>
    /// <c>TABLE_VERSION</c> and <c>sp_getschemalock</c>'s version: the
    /// object's <c>modify_date</c> as a <c>datetime</c>'s eight bytes read as
    /// a <c>bigint</c> — days since 1900 in the high half, 1/300 seconds in
    /// the low (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    internal static long ObjectSchemaVersion(DateTime modifyDate)
    {
        var bytes = SqlValue.FromDateTime(modifyDate).CoerceTo(BinarySqlType.Get(8)).AsBytes;
        long version = 0;
        foreach (var b in bytes)
            version = (version << 8) | b;
        return version;
    }

    /// <summary>
    /// <c>sp_tables_info_rowset</c> and its versions: the OLE DB
    /// <c>TABLES_INFO</c> rowset a linked server reads first for a four-part
    /// name — the table's type, its bookmark (a four-byte one, or eight under
    /// <c>_64</c>), its schema version and row count; <c>_90</c> adds
    /// <c>TABLE_FLAGS</c>. <c>@table_type</c> matches the type exactly, case
    /// aside.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeTablesInfoRowset(BatchContext batch, string procedure, string calledAs, bool withFlags, bool bookmark64)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("table_schema"), RowsetTypeParameter()]
            : [RowsetNameParameter("table_name", required: true), RowsetNameParameter("table_schema"), RowsetTypeParameter()];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var offset = everyObject ? -1 : 0;
        var name = everyObject ? null : RowsetText(values[0]);
        var schema = RowsetText(values[1 + offset]);
        var type = RowsetText(values[2 + offset]);

        var columnCount = withFlags ? 15 : 14;
        var catalog = SqlValue.FromNVarchar(RowsetName, batch.CurrentDatabase.Name);
        var rows = new List<SqlValue[]>();
        if (everyObject || name is not null)
        {
            foreach (var item in RowsetObjects(batch, name, schema))
            {
                if (type is not null && !string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase))
                    continue;
                var row = new SqlValue[columnCount];
                row[0] = catalog;
                row[1] = SqlValue.FromNVarchar(RowsetName, item.Schema);
                row[2] = SqlValue.FromNVarchar(RowsetName, item.Name);
                row[3] = SqlValue.FromNVarchar(RowsetType, item.Type);
                row[4] = SqlValue.Null(SqlType.UniqueIdentifier);
                row[5] = SqlValue.FromBoolean(true);
                row[6] = SqlValue.FromInt32(1);
                row[7] = SqlValue.FromInt16(bookmark64 ? (short)21 : (short)19);
                row[8] = SqlValue.FromInt32(bookmark64 ? 8 : 4);
                row[9] = SqlValue.FromInt32(0);
                row[10] = SqlValue.FromInt64(ObjectSchemaVersion(item.ModifyDate));
                row[11] = item.Table is { } table ? SqlValue.FromInt32((int)table.Heap.CountLiveRows())
                    : item.SystemRows is { } systemRows ? SqlValue.FromInt32((int)systemRows)
                    : SqlValue.Null(SqlType.Int32);
                row[12] = SqlValue.Null(RowsetDescription);
                row[13] = SqlValue.Null(SqlType.Int32);
                if (withFlags)
                    row[14] = SqlValue.FromInt32(0);
                rows.Add(row);
            }
        }
        yield return new SimulatedSqlResultSet(RowsetTablesInfoSchema[..columnCount], RowsetTablesInfoNames[..columnCount], rows);
    }

    /// <summary>
    /// <c>sp_tables_rowset</c> / <c>sp_tables_rowset2</c>: the OLE DB
    /// <c>TABLES</c> rowset — type and creation date — which a linked
    /// server's <c>sp_tables_ex</c> reads.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeTablesRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("table_schema"), RowsetTypeParameter()]
            : [RowsetNameParameter("table_name", required: true), RowsetNameParameter("table_schema"), RowsetTypeParameter()];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var offset = everyObject ? -1 : 0;
        var name = everyObject ? null : RowsetText(values[0]);
        var schema = RowsetText(values[1 + offset]);
        var type = RowsetText(values[2 + offset]);

        var catalog = SqlValue.FromNVarchar(RowsetName, batch.CurrentDatabase.Name);
        var rows = new List<SqlValue[]>();
        if (everyObject || name is not null)
        {
            foreach (var item in RowsetObjects(batch, name, schema))
            {
                if (type is not null && !string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase))
                    continue;
                rows.Add([
                    catalog, SqlValue.FromNVarchar(RowsetName, item.Schema), SqlValue.FromNVarchar(RowsetName, item.Name),
                    SqlValue.FromNVarchar(RowsetType, item.Type), SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(RowsetDescription),
                    SqlValue.Null(SqlType.Int32), SqlValue.FromDateTime(item.CreateDate), SqlValue.Null(SqlType.DateTime),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(RowsetTablesSchema, RowsetTablesNames, rows);
    }

    /// <summary>
    /// <c>sp_columns_rowset</c> and its versions: the OLE DB <c>COLUMNS</c>
    /// rowset, one row per column of the tables and views named, in column
    /// order, each described in the provider generation's own terms (see
    /// <see cref="RowsetColumnRow"/>). <c>_90</c> adds the XML schema
    /// collection and CLR type names, <c>_100</c> the sparse pair.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeColumnsRowset(BatchContext batch, string procedure, string calledAs, RowsetVersion version)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("table_schema"), RowsetNameParameter("column_name")]
            : [RowsetNameParameter("table_name", required: true), RowsetNameParameter("table_schema"), RowsetNameParameter("column_name")];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var offset = everyObject ? -1 : 0;
        var name = everyObject ? null : RowsetText(values[0]);
        var schema = RowsetText(values[1 + offset]);
        var column = RowsetText(values[2 + offset]);

        var columnCount = version switch { RowsetVersion.Classic => 33, RowsetVersion.V90 => 40, _ => 42 };
        var database = batch.CurrentDatabase;
        var rows = new List<SqlValue[]>();
        if (everyObject || name is not null)
        {
            foreach (var item in RowsetObjects(batch, name, schema))
            {
                for (var i = 0; i < item.Columns.Length; i++)
                {
                    var col = item.Columns[i];
                    if (column is not null && !database.Collation.Equals(column, col.Name))
                        continue;
                    if (RowsetColumnRow(database, item, col, i, version) is { } row)
                        rows.Add(row[..columnCount]);
                }
            }
        }
        yield return new SimulatedSqlResultSet(RowsetColumnsSchema[..columnCount], RowsetColumnsNames[..columnCount], rows);
    }

    /// <summary>
    /// How a column's type reads in a schema rowset: its OLE DB type code, the
    /// lengths, precision and scale, and the <c>DBCOLUMNFLAGS</c> its type
    /// contributes.
    /// </summary>
    private readonly struct RowsetColumnType(short dataType, int? maxLength, int? octetLength, short? precision, short? scale, int? datetimePrecision, int flags, bool collated, bool udtNamed)
    {
        public readonly short DataType = dataType;
        public readonly int? MaxLength = maxLength;
        public readonly int? OctetLength = octetLength;
        public readonly short? Precision = precision;
        public readonly short? Scale = scale;
        public readonly int? DatetimePrecision = datetimePrecision;
        public readonly int Flags = flags;
        public readonly bool Collated = collated;
        public readonly bool UdtNamed = udtNamed;
    }

    private const int ColumnFlagMayDefer = 0x2;
    private const int ColumnFlagWrite = 0x4;
    private const int ColumnFlagFixedLength = 0x10;
    private const int ColumnFlagNullable = 0x20 | 0x40;
    private const int ColumnFlagLong = 0x80 | ColumnFlagMayDefer;
    private const int ColumnFlagRowVersion = 0x200;
    private const int ColumnFlagVariableScale = 0x40000000;

    /// <summary>
    /// A column's type as the OLE DB rowsets describe it (probed 2026-10-11
    /// against SQL Server 2025 for every system type): <c>_100</c> reports
    /// each type natively, the date and time types after <c>datetime</c> with
    /// a variable-scale flag; <c>_90</c> and the plain version report those
    /// as an <c>nvarchar</c> of their text length; the plain version reports
    /// a MAX type as its legacy LOB, <c>xml</c> as <c>ntext</c> and a CLR type
    /// as <c>varbinary</c>, where <c>_90</c> keeps <c>xml</c> and a
    /// <c>hierarchyid</c> but reports a spatial type as a <c>varbinary</c> of
    /// length -1. A <c>vector</c> reads as the <c>varbinary</c> it is stored
    /// as, and a <c>json</c> column isn't listed (null).
    /// </summary>
    private static RowsetColumnType? DescribeRowsetColumnType(HeapColumn col, RowsetVersion version)
    {
        const int Fixed = ColumnFlagFixedLength;
        var classic = version == RowsetVersion.Classic;
        var isMax = col.MaxLength == SqlType.MaxLengthSentinel;
        static RowsetColumnType Downlevel(int length) => new(130, length, length * 2, null, null, null, 0, collated: true, udtNamed: false);
        return col.Type switch
        {
            JsonSqlType => null,
            _ when col.Type == SqlType.Bit => new(11, null, null, null, null, null, Fixed, false, false),
            _ when col.Type == SqlType.TinyInt => new(17, null, null, 3, null, null, Fixed, false, false),
            _ when col.Type == SqlType.SmallInt => new(2, null, null, 5, null, null, Fixed, false, false),
            _ when col.Type == SqlType.Int32 => new(3, null, null, 10, null, null, Fixed, false, false),
            _ when col.Type == SqlType.BigInt => new(20, null, null, 19, null, null, Fixed, false, false),
            DecimalSqlType d => new(131, null, null, d.precision, d.scale, null, Fixed, false, false),
            _ when col.Type == SqlType.Money => new(6, null, null, 19, null, null, Fixed, false, false),
            _ when col.Type == SqlType.SmallMoney => new(6, null, null, 10, null, null, Fixed, false, false),
            _ when col.Type == SqlType.Float => new(5, null, null, 15, null, null, Fixed, false, false),
            _ when col.Type == SqlType.Real => new(4, null, null, 7, null, null, Fixed, false, false),
            _ when col.Type == SqlType.Date => version == RowsetVersion.V100 ? new(133, null, null, null, null, 0, Fixed, false, false) : Downlevel(10),
            _ when col.Type == SqlType.SmallDateTime => new(135, null, null, null, null, 0, Fixed, false, false),
            _ when col.Type == SqlType.DateTime => new(135, null, null, null, null, 3, Fixed, false, false),
            DateTime2SqlType dt2 => version == RowsetVersion.V100
                ? new(135, null, null, null, null, dt2.precision, Fixed | ColumnFlagVariableScale, false, false)
                : Downlevel(ScaledTemporalPrecision(19, dt2.precision)),
            TimeSqlType tm => version == RowsetVersion.V100
                ? new(145, null, null, null, null, tm.precision, Fixed | ColumnFlagVariableScale, false, false)
                : Downlevel(ScaledTemporalPrecision(8, tm.precision)),
            DateTimeOffsetSqlType dto => version == RowsetVersion.V100
                ? new(146, null, null, null, null, dto.precision, Fixed | ColumnFlagVariableScale, false, false)
                : Downlevel(ScaledTemporalPrecision(26, dto.precision)),
            _ when col.Type == SqlType.UniqueIdentifier => new(72, null, null, null, null, null, Fixed, false, false),
            _ when col.Type == SqlType.RowVersion => new(128, 8, 8, null, null, null, Fixed | ColumnFlagRowVersion, false, false),
            _ when col.Type == SqlType.Text => new(129, int.MaxValue, int.MaxValue, null, null, null, ColumnFlagLong, true, false),
            _ when col.Type == SqlType.NText => new(130, 1073741823, 2147483646, null, null, null, ColumnFlagLong, true, false),
            _ when col.Type == SqlType.Image => new(128, int.MaxValue, int.MaxValue, null, null, null, ColumnFlagLong, false, false),
            _ when col.Type is SystemNameSqlType => new(130, 128, 256, null, null, null, 0, true, false),
            CharSqlType c => new(129, c.length, c.length, null, null, null, Fixed, true, false),
            NCharSqlType nc => new(130, nc.length, nc.length * 2, null, null, null, Fixed, true, false),
            BinarySqlType bn => new(128, bn.length, bn.length, null, null, null, Fixed, false, false),
            VarcharSqlType vc when isMax || vc.length < 1 => classic
                ? new(129, int.MaxValue, int.MaxValue, null, null, null, ColumnFlagLong, true, false)
                : new(129, 0, 0, null, null, null, ColumnFlagLong, true, false),
            NVarcharSqlType nv when isMax || nv.length < 1 => classic
                ? new(130, 1073741823, 2147483646, null, null, null, ColumnFlagLong, true, false)
                : new(130, 0, 0, null, null, null, ColumnFlagLong, true, false),
            VarbinarySqlType vb when isMax || vb.length < 1 => classic
                ? new(128, int.MaxValue, int.MaxValue, null, null, null, ColumnFlagLong, false, false)
                : new(128, 0, 0, null, null, null, ColumnFlagLong, false, false),
            VarcharSqlType vc => new(129, vc.length, vc.length, null, null, null, 0, true, false),
            NVarcharSqlType nv => new(130, nv.length, nv.length * 2, null, null, null, 0, true, false),
            VarbinarySqlType vb => new(128, vb.length, vb.length, null, null, null, 0, false, false),
            XmlSqlType => classic
                ? new(130, 1073741823, 2147483646, null, null, null, ColumnFlagLong, true, false)
                : new(141, null, null, null, null, null, ColumnFlagLong, false, false),
            SqlVariantSqlType => new(12, null, null, null, null, null, Fixed, false, false),
            VectorSqlType vector => new(128, vector.ByteLength, vector.ByteLength, null, null, null, 0, false, false),
            HierarchyIdSqlType => classic
                ? new(128, 892, 892, null, null, null, 0, false, false)
                : new(132, 892, 892, null, null, null, 0, false, true),
            SpatialSqlType => version switch
            {
                RowsetVersion.Classic => new(128, int.MaxValue, int.MaxValue, null, null, null, ColumnFlagLong, false, false),
                RowsetVersion.V90 => new(128, -1, -1, null, null, null, ColumnFlagLong, false, true),
                _ => new(132, 0, 0, null, null, null, ColumnFlagLong, false, true),
            },
            ClrUdtSqlType udt => DescribeClrUdtColumn(udt.Udt.MaxByteSize, version),
            _ => throw new NotSupportedException($"The OLE DB schema rowsets do not model {col.Type} columns."),
        };
    }

    /// <summary>
    /// A user CLR type's column, described as the system CLR types are: a
    /// type of unlimited size as the spatial types, a bounded one as
    /// <c>hierarchyid</c>.
    /// </summary>
    private static RowsetColumnType DescribeClrUdtColumn(int maxByteSize, RowsetVersion version)
    {
        var unlimited = maxByteSize < 0;
        var flags = unlimited ? ColumnFlagLong : 0;
        return version switch
        {
            RowsetVersion.Classic => new(128, unlimited ? int.MaxValue : maxByteSize, unlimited ? int.MaxValue : maxByteSize, null, null, null, flags, false, false),
            RowsetVersion.V90 when unlimited => new(128, -1, -1, null, null, null, flags, false, true),
            _ => new(132, unlimited ? 0 : maxByteSize, unlimited ? 0 : maxByteSize, null, null, null, flags, false, true),
        };
    }

    /// <summary>
    /// One <c>COLUMNS</c> rowset row, all 42 cells; the older versions take a
    /// prefix. Null for a column the rowset leaves out.
    /// </summary>
    private static SqlValue[]? RowsetColumnRow(Database database, RowsetObject item, HeapColumn col, int index, RowsetVersion version)
    {
        if (DescribeRowsetColumnType(col, version) is not { } described)
            return null;
        var nullName = SqlValue.Null(RowsetName);
        SqlValue Name(string? value) => value is null ? nullName : SqlValue.FromNVarchar(RowsetName, value);
        static SqlValue Int(int? value) => value is { } v ? SqlValue.FromInt32(v) : SqlValue.Null(SqlType.Int32);
        static SqlValue Small(short? value) => value is { } v ? SqlValue.FromInt16(v) : SqlValue.Null(SqlType.SmallInt);

        // An identity or a rowversion can't be written, and a view's column
        // passing an identity through reads as one.
        var isIdentity = (col.Identity ?? col.IdentitySource) is not null;
        var flags = described.Flags;
        if (!isIdentity && (flags & ColumnFlagRowVersion) == 0)
            flags |= ColumnFlagWrite;
        if (col.Nullable)
            flags |= ColumnFlagNullable;

        // A date or time type a down-level version reports as a string is in
        // the database's collation.
        var collation = described.Collated ? (col.Type.Collation is { } own && col.Type is not (DateSqlType or TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType or XmlSqlType) ? own : database.Collation) : null;
        var row = new SqlValue[42];
        row[0] = Name(database.Name);
        row[1] = Name(item.Schema);
        row[2] = Name(item.Name);
        row[3] = Name(col.Name);
        row[4] = SqlValue.Null(SqlType.UniqueIdentifier);
        row[5] = SqlValue.Null(SqlType.Int32);
        row[6] = SqlValue.FromInt32(col.ColumnId == 0 ? index + 1 : col.ColumnId);
        var definition = col.DefaultConstraint?.Definition;
        row[7] = SqlValue.FromBoolean(definition is not null);
        row[8] = definition is null ? SqlValue.Null(RowsetDefault) : SqlValue.FromNVarchar(RowsetDefault, definition.Length > 2000 ? definition[..2000] : definition);
        row[9] = SqlValue.FromInt32(flags);
        row[10] = SqlValue.FromBoolean(col.Nullable);
        row[11] = SqlValue.FromInt16(described.DataType);
        row[12] = SqlValue.Null(SqlType.UniqueIdentifier);
        row[13] = Int(described.MaxLength);
        row[14] = Int(described.OctetLength);
        row[15] = Small(described.Precision);
        row[16] = Small(described.Scale);
        row[17] = Int(described.DatetimePrecision);
        if (collation is not null && Collation.TryGetMetrics(collation.Name, out var metrics))
        {
            var characterSet = metrics.CodePage switch
            {
                1252 => "iso_1",
                65001 => "utf8",
                var codePage => "cp" + codePage.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            row[18] = Name("master");
            row[19] = Name(Database.DefaultSchemaName);
            row[20] = Name(characterSet);
            row[21] = Name("master");
            row[22] = Name(Database.DefaultSchemaName);
            row[23] = Name(collation.Name);
            row[28] = SqlValue.FromInt32(metrics.Lcid);
            row[29] = SqlValue.FromInt32(ComparisonFlags(collation.Name, metrics.ComparisonStyle));
            row[31] = SqlValue.FromBinary(BinarySqlType.Get(5), Network.TdsCollationCodec.For(collation).PropertyBytes());
        }
        else
        {
            for (var i = 18; i <= 23; i++)
                row[i] = nullName;
            row[28] = SqlValue.Null(SqlType.Int32);
            row[29] = SqlValue.Null(SqlType.Int32);
            row[31] = SqlValue.Null(BinarySqlType.Get(5));
        }

        // An alias type names itself as the domain, sysname under dbo.
        var (domainSchema, domainName) = col.AliasType is { } alias
            ? (alias.Schema.Name, alias.Name)
            : col.Type is SystemNameSqlType ? (Database.DefaultSchemaName, "sysname") : (null, null);
        row[24] = Name(domainName is null ? null : database.Name);
        row[25] = Name(domainSchema);
        row[26] = Name(domainName);
        row[27] = SqlValue.Null(RowsetDescription);
        var sortId = Network.TdsCollationCodec.For(database.Collation).SortId;
        row[30] = sortId == 0 ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(sortId);
        row[32] = SqlValue.FromBoolean(col.Computed is not null && !col.IsColumnSet);
        row[33] = Name(col.XmlSchemaCollection is null ? null : database.Name);
        row[34] = Name(col.XmlSchemaCollection is { } collection ? SchemaNameById(database, collection.SchemaId) : null);
        row[35] = Name(col.XmlSchemaCollection?.Name);
        if (described.UdtNamed)
        {
            var (udtSchema, udtName) = col.Type is ClrUdtSqlType udt ? (udt.Udt.Schema.Name, udt.Udt.Name) : ("sys", SpColumnsTypeName(col.Type));
            row[36] = Name(database.Name);
            row[37] = Name(udtSchema);
            row[38] = Name(udtName);
            row[39] = SqlValue.FromNVarchar(RowsetText4000, SpColumnsClrAssemblyName(col.Type)!);
        }
        else
        {
            row[36] = nullName;
            row[37] = nullName;
            row[38] = nullName;
            row[39] = SqlValue.Null(RowsetText4000);
        }
        row[40] = SqlValue.FromBoolean(col.IsSparse);
        row[41] = SqlValue.FromBoolean(col.IsColumnSet);
        return row;
    }

    private static string? SchemaNameById(Database database, int schemaId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            if (schema.SchemaId == schemaId)
                return schema.Name;
        }
        return null;
    }

    /// <summary>
    /// <c>COLUMN_COMPFLAGS</c>: the collation's comparison style, a binary
    /// collation's being its own flag — <c>0x800000</c> for <c>_BIN</c> and
    /// <c>0x400000</c> for <c>_BIN2</c> (probed 2026-10-11 against SQL Server
    /// 2025).
    /// </summary>
    private static int ComparisonFlags(string collationName, int comparisonStyle) =>
        collationName.Contains("_BIN2", StringComparison.OrdinalIgnoreCase) ? 0x400000
        : collationName.EndsWith("_BIN", StringComparison.OrdinalIgnoreCase) || collationName.Contains("_BIN_", StringComparison.OrdinalIgnoreCase) ? 0x800000
        : comparisonStyle;
}
