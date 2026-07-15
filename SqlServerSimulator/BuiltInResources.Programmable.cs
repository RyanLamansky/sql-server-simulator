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
        void Iso(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["INFORMATION_SCHEMA." + name] = new CatalogView(name, columns, rows);
        // INFORMATION_SCHEMA.TABLES: ISO-standard 4-column shape. TABLE_TYPE
        // is 'BASE TABLE' for every user table; 'VIEW' (not modeled) would be
        // the other shipped value.
        var baseTable = SqlValue.FromVarchar("BASE TABLE");
        var viewTableType = SqlValue.FromVarchar("VIEW");
        Iso("TABLES",
        [
            new("TABLE_CATALOG", SqlType.SystemName, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("TABLE_TYPE", SqlType.Varchar, 10, true),
        ], (batch, database) =>
            EnumerateInformationSchemaTables(batch, database, baseTable, viewTableType));

        // INFORMATION_SCHEMA.COLUMNS: ISO-standard 23-column shape. Tooling
        // does SELECT * here so the full column set ships even though many
        // are always NULL in the simulator (DOMAIN_*, CHARACTER_SET_SCHEMA,
        // COLLATION_CATALOG, etc.). COLUMN_DEFAULT is always NULL until
        // expression-to-SQL serialization lands (separate bundle).
        var unicodeCs = SqlValue.FromSystemName("UNICODE");
        var isoCs = SqlValue.FromSystemName("iso_1");
        var radix10 = SqlValue.FromInt16(10);
        var radix2 = SqlValue.FromInt16(2);
        Iso("COLUMNS",
        [
            new("TABLE_CATALOG", SqlType.SystemName, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("COLUMN_NAME", SqlType.SystemName, 128, true),
            new("ORDINAL_POSITION", SqlType.Int32, null, true),
            new("COLUMN_DEFAULT", SqlType.NVarchar, 4000, true),
            new("IS_NULLABLE", SqlType.Varchar, 3, true),
            new("DATA_TYPE", SqlType.SystemName, 128, true),
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
        ], (batch, database) =>
            EnumerateInformationSchemaColumns(batch, database, defaultCollation, unicodeCs, isoCs, radix10, radix2));

        // INFORMATION_SCHEMA.SCHEMATA: ISO-standard 6-column shape. Lists
        // only the schemas the simulator actually models — no padding for
        // role principals (db_owner / db_datareader / …) since there's no
        // principal model. SCHEMA_OWNER mirrors SCHEMA_NAME (matches real
        // SQL Server's behavior for built-in / user schemas without explicit
        // AUTHORIZATION).
        var defaultCsName = SqlValue.FromSystemName("iso_1");
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        Iso("SCHEMATA",
        [
            new("CATALOG_NAME", SqlType.SystemName, 128, true),
            new("SCHEMA_NAME", SqlType.SystemName, 128, false),
            new("SCHEMA_OWNER", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_CATALOG", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_SCHEMA", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_NAME", SqlType.SystemName, 128, true),
        ], (batch, database) =>
            database.Schemas.Values.OrderBy(s => s.SchemaId).Select(s => new SqlValue[]
            {
                SqlValue.FromSystemName(database.Name),
                SqlValue.FromSystemName(s.Name),
                SqlValue.FromSystemName(s.Name),
                nullSysName,
                nullSysName,
                defaultCsName,
            }));

        // sys.parameters: one row per declared parameter + one row with
        // parameter_id=0 for the return type. The shipped column set covers
        // what real SQL Server's documented shape exposes: object_id / name /
        // parameter_id / system_type_id / user_type_id / max_length /
        // precision / scale / is_output / is_nullable. Probe-confirmed
        // ordering: return type emits first (parameter_id=0, empty name),
        // declared params follow in source order.
        Sys("parameters",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("parameter_id", SqlType.Int32, null, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("max_length", SqlType.SmallInt, null, false),
            new("precision", SqlType.TinyInt, null, false),
            new("scale", SqlType.TinyInt, null, false),
            new("is_output", SqlType.Bit, null, false),
            new("is_nullable", SqlType.Bit, null, false),
            new("is_readonly", SqlType.Bit, null, false),
        ], EnumerateParameters);

        // sys.views: per-view rows. Load-bearing subset of real SQL Server's
        // sys.views shape — object_id / name / schema_id / with_check_option /
        // is_date_correlation_view. Other documented columns (principal_id,
        // is_replicated, has_replication_filter, etc.) aren't modeled.
        Sys("views",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("with_check_option", SqlType.Bit, null, false),
            new("is_date_correlation_view", SqlType.Bit, null, false),
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
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("with_check_option", SqlType.Bit, null, false),
            new("is_date_correlation_view", SqlType.Bit, null, false),
        ], EnumerateViews);

        // sys.procedures: per-procedure rows. Shipped column subset matches
        // the load-bearing surface — object_id / name / schema_id /
        // create_date / modify_date / is_ms_shipped. Other documented
        // columns (principal_id, is_auto_executed, is_execution_replicated,
        // etc.) aren't modeled.
        Sys("procedures",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
        ], (batch, database) =>
            EnumerateProcedures(batch, database, charTwo, notMsShipped));

        // INFORMATION_SCHEMA.ROUTINES: ISO-shape view listing both procedures
        // and functions. The simulator ships the load-bearing column subset:
        // ROUTINE_CATALOG / SCHEMA / NAME / TYPE / DATA_TYPE. For procedures
        // DATA_TYPE is NULL (procs have no scalar return type); for scalar
        // UDFs it carries the return type's family name; for inline TVFs it
        // is 'TABLE'. ROUTINE_DEFINITION carries the module source text
        // (nvarchar(4000), truncated like SQL Server). Real SQL Server ships
        // dozens of further columns (CREATED, LAST_ALTERED, etc.) not modeled.
        var procedureRoutineType = SqlValue.FromVarchar("PROCEDURE");
        var functionRoutineType = SqlValue.FromVarchar("FUNCTION");
        var tableDataType = SqlValue.FromSystemName("TABLE");
        Iso("ROUTINES",
        [
            new("ROUTINE_CATALOG", SqlType.SystemName, 128, true),
            new("ROUTINE_SCHEMA", SqlType.SystemName, 128, true),
            new("ROUTINE_NAME", SqlType.SystemName, 128, false),
            new("ROUTINE_TYPE", SqlType.Varchar, 9, true),
            new("DATA_TYPE", SqlType.SystemName, 128, true),
            new("ROUTINE_DEFINITION", SqlType.NVarchar, 4000, true),
        ], (batch, database) =>
            EnumerateInformationSchemaRoutines(batch, database, procedureRoutineType, functionRoutineType, tableDataType));

        // INFORMATION_SCHEMA.PARAMETERS: ISO-shape view listing parameters
        // for procedures and functions. PARAMETER_MODE is 'IN' / 'OUT' /
        // 'INOUT'; the simulator emits 'IN' for non-output params, 'INOUT'
        // for OUTPUT-declared params (probe-confirmed: real SQL Server uses
        // INOUT for OUTPUT in procedures). CHARACTER_MAXIMUM_LENGTH is set
        // only for string types.
        var modeIn = SqlValue.FromVarchar("IN");
        var modeInOut = SqlValue.FromVarchar("INOUT");
        Iso("PARAMETERS",
        [
            new("SPECIFIC_CATALOG", SqlType.SystemName, 128, true),
            new("SPECIFIC_SCHEMA", SqlType.SystemName, 128, true),
            new("SPECIFIC_NAME", SqlType.SystemName, 128, false),
            new("ORDINAL_POSITION", SqlType.Int32, null, true),
            new("PARAMETER_MODE", SqlType.Varchar, 10, true),
            new("PARAMETER_NAME", SqlType.SystemName, 128, true),
            new("DATA_TYPE", SqlType.SystemName, 128, true),
            new("CHARACTER_MAXIMUM_LENGTH", SqlType.Int32, null, true),
        ], (batch, database) =>
            EnumerateInformationSchemaParameters(batch, database, modeIn, modeInOut));

        // INFORMATION_SCHEMA.VIEWS: ISO-standard 6-column shape. Probe-
        // confirmed: VIEW_DEFINITION is NULL only for WITH ENCRYPTION views
        // (the simulator parses ENCRYPTION but doesn't track it — minor
        // fidelity gap, the body text always surfaces). IS_UPDATABLE is
        // probe-confirmed to always report 'NO' in real SQL Server even for
        // views that are actually updatable — matching that by hardcoding.
        var checkOptionNone = SqlValue.FromVarchar("NONE");
        var checkOptionCascade = SqlValue.FromVarchar("CASCADE");
        var isUpdatableNo = SqlValue.FromVarchar("NO");
        Iso("VIEWS",
        [
            new("TABLE_CATALOG", SqlType.SystemName, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("VIEW_DEFINITION", SqlType.NVarchar, 4000, true),
            new("CHECK_OPTION", SqlType.Varchar, 7, true),
            new("IS_UPDATABLE", SqlType.Varchar, 2, true),
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
            new("is_nullable", SqlType.Bit, null, false),
            // is_assembly_type: 1 only for the CLR-backed system types
            // (hierarchyid / geometry / geography); 0 for every other built-in,
            // table type, and scalar alias. SMO's SSMS column-node query reads
            // it off sys.types (baset) to pick the base-type join arm.
            new("is_assembly_type", SqlType.Bit, null, false),
        ], EnumerateSysTypes);

        // sys.table_types: per-database list of user-defined table types
        // only. Probe-confirmed shipped subset: name / type_table_object_id /
        // is_user_defined / schema_id / user_type_id / is_memory_optimized.
        // is_memory_optimized is a constant 0 — memory-optimized table types
        // aren't modeled, and SMO's SSMS Object-Explorer index/key/FK
        // sub-node queries read it via
        // (SELECT tt.is_memory_optimized FROM sys.table_types tt WHERE
        //  tt.type_table_object_id = i.object_id).
        Sys("table_types",
        [
            new("name", SqlType.SystemName, 128, false),
            new("type_table_object_id", SqlType.Int32, null, false),
            new("is_user_defined", SqlType.Bit, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("is_memory_optimized", SqlType.Bit, null, false),
        ], EnumerateSysTableTypes);

        // sys.sequences: per-database list of user-defined sequence objects.
        // Probe-confirmed shipped subset: name / object_id / schema_id /
        // start_value / increment / minimum_value / maximum_value /
        // is_cycling / is_cached / cache_size / current_value /
        // system_type_id / user_type_id / is_exhausted. cache_size is NULL
        // when no explicit CACHE n was given (real SQL Server behavior;
        // the simulator never tracks an explicit value so this is always
        // NULL). Values widen to decimal(38, 0) to match SQL Server's
        // sql_variant-typed columns in real sys.sequences, but the simulator
        // emits bigint here since all sequence state is tracked as long
        // (a minor projection-schema divergence; SqlClient surfaces these
        // as long, which matches what HiLo-style apps assert on).
        Sys("sequences",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("start_value", SqlType.BigInt, null, false),
            new("increment", SqlType.BigInt, null, false),
            new("minimum_value", SqlType.BigInt, null, false),
            new("maximum_value", SqlType.BigInt, null, false),
            new("is_cycling", SqlType.Bit, null, false),
            new("is_cached", SqlType.Bit, null, false),
            new("cache_size", SqlType.Int32, null, true),
            new("current_value", SqlType.BigInt, null, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("is_exhausted", SqlType.Bit, null, false),
        ], EnumerateSysSequences);
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
        // user_type_id). is_user_defined is derived from a hardcoded set:
        // sysname is user-defined; everything else system.
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
                name == "sysname" ? trueBit : falseBit,
                falseBit,
                trueBit,
                name is "hierarchyid" or "geometry" or "geography" ? trueBit : falseBit,
            ];
        }
        // User-defined table types: probe-confirmed system_type_id 243.
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var tt in schema.TableTypes.Values.OrderBy(t => t.UserTypeId))
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
                ];
            }
        }
        // Scalar alias types (UDDTs): probe-confirmed against SQL Server 2025
        // — `system_type_id` is the **underlying** built-in's id (e.g. 56 for
        // an alias of int, 231 for an alias of nvarchar), `is_user_defined`
        // is true, `is_table_type` is false, and `is_nullable` reflects the
        // alias-defined NULL/NOT NULL marker from CREATE TYPE.
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var alias in schema.AliasTypes.Values.OrderBy(a => a.UserTypeId))
            {
                yield return [
                    SqlValue.FromSystemName(alias.Name),
                    SqlValue.FromByte(alias.UnderlyingType.SystemTypeId),
                    SqlValue.FromInt32(alias.UserTypeId),
                    schemaId,
                    trueBit,
                    falseBit,
                    alias.IsNullable ? trueBit : falseBit,
                    falseBit,
                ];
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysTableTypes(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var tt in schema.TableTypes.Values.OrderBy(t => t.UserTypeId))
            {
                yield return [
                    SqlValue.FromSystemName(tt.Name),
                    SqlValue.FromInt32(tt.ObjectId),
                    trueBit,
                    schemaId,
                    SqlValue.FromInt32(tt.UserTypeId),
                    falseBit,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.sequences</c>: one per registered sequence object,
    /// schema-ordered. <c>cache_size</c> is always NULL (the simulator
    /// doesn't model the batched-allocation cache; real SQL Server returns
    /// NULL when no explicit <c>CACHE n</c> was given anyway). Type-id
    /// columns derive from the declared type via <see cref="SystypesRowData"/>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSequences(Parser.BatchContext batch, Database database)
    {
        var nullCache = SqlValue.Null(SqlType.Int32);
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var seq in schema.Sequences.Values.OrderBy(s => s.ObjectId))
            {
                var (systemTypeId, userTypeId) = SequenceTypeIds(seq.DeclaredType);
                yield return [
                    SqlValue.FromSystemName(seq.Name),
                    SqlValue.FromInt32(seq.ObjectId),
                    schemaId,
                    SqlValue.FromInt64(seq.StartValue),
                    SqlValue.FromInt64(seq.Increment),
                    SqlValue.FromInt64(seq.MinValue),
                    SqlValue.FromInt64(seq.MaxValue),
                    seq.Cycle ? trueBit : falseBit,
                    trueBit,
                    nullCache,
                    SqlValue.FromInt64(seq.CurrentValue),
                    SqlValue.FromByte(systemTypeId),
                    SqlValue.FromInt32(userTypeId),
                    seq.IsExhausted ? trueBit : falseBit,
                ];
            }
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
    /// Rows for <c>sys.procedures</c>: one row per <see cref="Procedure"/> in
    /// every schema. The full <c>create_date</c> / <c>modify_date</c> story
    /// in real SQL Server tracks ALTER PROCEDURE separately; the simulator
    /// uses the ALTER-preserving <see cref="SchemaObject.CreateDate"/> for both
    /// columns (the original create date survives ALTER, matching the way
    /// <see cref="SchemaObject.ObjectId"/> survives — minor fidelity gap on
    /// modify_date which would shift on each ALTER in real SQL Server).
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
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var proc in schema.Procedures.Values.OrderBy(p => p.ObjectId))
            {
                var createDate = SqlValue.FromDateTime(proc.CreateDate);
                yield return [
                    SqlValue.FromInt32(proc.ObjectId),
                    SqlValue.FromSystemName(proc.Name),
                    SqlValue.FromInt32(proc.Schema.SchemaId),
                    procType,
                    procTypeDesc,
                    createDate,
                    createDate,
                    notMsShipped,
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
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var view in schema.Views.Values.OrderBy(v => v.ObjectId))
            {
                yield return [
                    SqlValue.FromInt32(view.ObjectId),
                    SqlValue.FromSystemName(view.Name),
                    SqlValue.FromInt32(view.Schema.SchemaId),
                    viewType,
                    viewTypeDesc,
                    SqlValue.FromBoolean(view.WithCheckOption),
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
    /// against SQL Server 2025.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateParameters(Parser.BatchContext batch, Database database)
    {
        var emptyName = SqlValue.FromSystemName("");
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var proc in schema.Procedures.Values.OrderBy(p => p.ObjectId))
            {
                var procObjectId = SqlValue.FromInt32(proc.ObjectId);
                for (var i = 0; i < proc.Parameters.Length; i++)
                {
                    var param = proc.Parameters[i];
                    // TVP parameters surface system_type_id 243 (table type)
                    // and the user_type_id of the referenced TableType.
                    // is_readonly is true only for TVP params (probe-confirmed
                    // — scalar params with a future READONLY shape don't ship).
                    var isTvp = param.TableType is not null;
                    yield return [
                        procObjectId,
                        SqlValue.FromSystemName("@" + param.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(isTvp ? (byte)243 : param.Type.SystemTypeId),
                        SqlValue.FromInt32(isTvp ? param.TableType!.UserTypeId : param.Type.UserTypeId),
                        SqlValue.FromInt16(0),
                        zeroByte,
                        zeroByte,
                        SqlValue.FromBoolean(param.IsOutput),
                        trueBit,
                        SqlValue.FromBoolean(isTvp),
                    ];
                }
            }
            foreach (var fn in schema.Functions.Values.OrderBy(f => f.ObjectId))
            {
                var fnObjectId = SqlValue.FromInt32(fn.ObjectId);
                // Scalar UDFs get a synthetic parameter_id=0 return-type row;
                // inline TVFs don't (their TABLE shape lives in sys.columns).
                // max_length stays at 0 for v1 — see scalar-UDF section in
                // CLAUDE.md.
                if (fn is ScalarFunction scalarFn)
                {
                    yield return [
                        fnObjectId,
                        emptyName,
                        SqlValue.FromInt32(0),
                        SqlValue.FromByte(scalarFn.ReturnType.SystemTypeId),
                        SqlValue.FromInt32(scalarFn.ReturnType.UserTypeId),
                        SqlValue.FromInt16(0),
                        zeroByte,
                        zeroByte,
                        trueBit,
                        trueBit,
                        falseBit,
                    ];
                }
                for (var i = 0; i < fn.Parameters.Length; i++)
                {
                    var p = fn.Parameters[i];
                    yield return [
                        fnObjectId,
                        SqlValue.FromSystemName("@" + p.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(p.Type.SystemTypeId),
                        SqlValue.FromInt32(p.Type.UserTypeId),
                        SqlValue.FromInt16(0),
                        zeroByte,
                        zeroByte,
                        falseBit,
                        trueBit,
                        falseBit,
                    ];
                }
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaTables(Parser.BatchContext batch, Database database, SqlValue baseTable, SqlValue viewTableType)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var t in schema.HeapTables.Values.OrderBy(t => t.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(t.Name),
                    baseTable,
                ];
            }
            foreach (var view in schema.Views.Values.OrderBy(v => v.ObjectId))
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
        SqlValue defaultCollation,
        SqlValue unicodeCs,
        SqlValue isoCs,
        SqlValue radix10,
        SqlValue radix2)
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
        _ = defaultCollation;
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var t in schema.HeapTables.Values.OrderBy(t => t.ObjectId))
            {
                var tableName = SqlValue.FromSystemName(t.Name);
                for (var i = 0; i < t.Columns.Length; i++)
                {
                    var col = t.Columns[i];
                    var (charLength, octetLength, numericPrecision, numericRadix, numericScale, dateTimePrecision) = GetInformationSchemaColumnMetadata(col);
                    var cs = col.Type.Category switch
                    {
                        SqlTypeCategory.String when col.Type is NVarcharSqlType or NCharSqlType || col.Type == SqlType.SystemName || col.Type == SqlType.NText => unicodeCs,
                        SqlTypeCategory.String => isoCs,
                        _ => nullSysName,
                    };
                    var collation = col.Type.Category != SqlTypeCategory.String ? nullSysName
                        : col.Collation is { } overrideName ? SqlValue.FromSystemName(overrideName)
                        : dbDefaultCollation;
                    yield return [
                        catalog,
                        schemaName,
                        tableName,
                        SqlValue.FromSystemName(col.Name),
                        SqlValue.FromInt32(i + 1),
                        nullString,
                        col.Nullable ? yesNullable : noNullable,
                        SqlValue.FromSystemName(col.Type.SqlServerName),
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
                        nullSysName,
                        nullSysName,
                        nullSysName,
                    ];
                }
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
    /// Rows for <c>INFORMATION_SCHEMA.ROUTINES</c>: procedures plus functions.
    /// ROUTINE_TYPE distinguishes PROCEDURE vs FUNCTION; DATA_TYPE carries
    /// the return-type family for scalar UDFs, 'TABLE' for inline TVFs, NULL
    /// for procedures.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaRoutines(
        Parser.BatchContext batch,
        Database database,
        SqlValue procedureRoutineType,
        SqlValue functionRoutineType,
        SqlValue tableDataType)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var nullDataType = SqlValue.Null(SqlType.SystemName);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var proc in schema.Procedures.Values.OrderBy(p => p.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(proc.Name),
                    procedureRoutineType,
                    nullDataType,
                    RoutineDefinition(proc.DefinitionText),
                ];
            }
            foreach (var fn in schema.Functions.Values.OrderBy(f => f.ObjectId))
            {
                var dataType = fn is ScalarFunction scalarFn
                    ? SqlValue.FromSystemName(scalarFn.ReturnType.SqlServerName)
                    : tableDataType;
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(fn.Name),
                    functionRoutineType,
                    dataType,
                    RoutineDefinition(fn.DefinitionText),
                ];
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
    /// Rows for <c>INFORMATION_SCHEMA.PARAMETERS</c>: per-parameter entries
    /// for procedures plus functions. ORDINAL_POSITION is 1-based for
    /// declared parameters; CHARACTER_MAXIMUM_LENGTH is set only for string
    /// types. PARAMETER_MODE is 'IN' for non-OUTPUT params, 'INOUT' for
    /// OUTPUT-declared procedure params (probe-confirmed); functions have
    /// no OUTPUT semantics so all UDF params project as 'IN'.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaParameters(
        Parser.BatchContext batch,
        Database database,
        SqlValue modeIn,
        SqlValue modeInOut)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var nullInt = SqlValue.Null(SqlType.Int32);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var proc in schema.Procedures.Values.OrderBy(p => p.ObjectId))
            {
                for (var i = 0; i < proc.Parameters.Length; i++)
                {
                    var param = proc.Parameters[i];
                    yield return [
                        catalog,
                        schemaName,
                        SqlValue.FromSystemName(proc.Name),
                        SqlValue.FromInt32(i + 1),
                        param.IsOutput ? modeInOut : modeIn,
                        SqlValue.FromSystemName("@" + param.Name),
                        SqlValue.FromSystemName(param.Type.SqlServerName),
                        param.Type.Category == SqlTypeCategory.String && param.DeclaredMaxLength is int len && len > 0
                            ? SqlValue.FromInt32(len)
                            : nullInt,
                    ];
                }
            }
            foreach (var fn in schema.Functions.Values.OrderBy(f => f.ObjectId))
            {
                for (var i = 0; i < fn.Parameters.Length; i++)
                {
                    var param = fn.Parameters[i];
                    yield return [
                        catalog,
                        schemaName,
                        SqlValue.FromSystemName(fn.Name),
                        SqlValue.FromInt32(i + 1),
                        modeIn,
                        SqlValue.FromSystemName("@" + param.Name),
                        SqlValue.FromSystemName(param.Type.SqlServerName),
                        nullInt,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.VIEWS</c>: per-view ISO-shape entries.
    /// VIEW_DEFINITION surfaces the stored body text (real SQL Server
    /// returns NULL for WITH ENCRYPTION views; the simulator currently
    /// always surfaces it — minor fidelity gap). CHECK_OPTION is 'CASCADE'
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
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var view in schema.Views.Values.OrderBy(v => v.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(view.Name),
                    SqlValue.FromNVarchar(view.BodyText),
                    view.WithCheckOption ? checkOptionCascade : checkOptionNone,
                    isUpdatableNo,
                ];
            }
        }
    }
}
