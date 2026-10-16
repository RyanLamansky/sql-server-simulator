using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The OLE DB schema-rowset procedures over the provider's types and the
// database's procedures and their parameters.
partial class Simulation
{
    private static readonly SqlType[] RowsetProviderTypesSchema =
    [
        RowsetName, SqlType.SmallInt, SqlType.Int32, RowsetLiteral, RowsetLiteral, RowsetLiteral, SqlType.Bit, SqlType.Bit, SqlType.Int32,
        SqlType.TinyInt, SqlType.Bit, SqlType.TinyInt, RowsetName, SqlType.SmallInt, SqlType.SmallInt, SqlType.UniqueIdentifier,
        RowsetDescription, RowsetDescription, SqlType.Bit, SqlType.Bit, SqlType.Bit,
    ];

    private static readonly string[] RowsetProviderTypesNames =
    [
        "TYPE_NAME", "DATA_TYPE", "COLUMN_SIZE", "LITERAL_PREFIX", "LITERAL_SUFFIX", "CREATE_PARAMS", "IS_NULLABLE", "CASE_SENSITIVE",
        "SEARCHABLE", "UNSIGNED_ATTRIBUTE", "FIXED_PREC_SCALE", "AUTO_UNIQUE_VALUE", "LOCAL_TYPE_NAME", "MINIMUM_SCALE", "MAXIMUM_SCALE",
        "GUID", "TYPELIB", "VERSION", "IS_LONG", "BEST_MATCH", "IS_FIXEDLENGTH",
    ];

    // The PROVIDER_TYPES rows per provider generation in real's order, each
    // TYPE_NAME, DATA_TYPE, COLUMN_SIZE, LITERAL_PREFIX, LITERAL_SUFFIX,
    // CREATE_PARAMS, IS_NULLABLE, CASE_SENSITIVE, SEARCHABLE,
    // UNSIGNED_ATTRIBUTE, FIXED_PREC_SCALE, AUTO_UNIQUE_VALUE, MINIMUM_SCALE,
    // MAXIMUM_SCALE, IS_LONG, BEST_MATCH and IS_FIXEDLENGTH; LOCAL_TYPE_NAME
    // repeats TYPE_NAME and GUID / TYPELIB / VERSION are NULL (probed
    // 2026-10-11 against SQL Server 2025).
    private static readonly object?[][] ProviderTypes100 =
    [
        ["smallint", 2, 5, null, null, null, true, false, 3, 0, true, 1, null, null, false, true, true],
        ["int", 3, 10, null, null, null, true, false, 3, 0, true, 1, null, null, false, true, true],
        ["real", 4, 7, null, null, null, true, false, 3, 0, false, 0, null, null, false, true, true],
        ["float", 5, 15, null, null, null, true, false, 3, 0, false, 0, null, null, false, true, true],
        ["money", 6, 19, "$", null, null, true, false, 3, 0, true, 0, null, null, false, true, true],
        ["smallmoney", 6, 10, "$", null, null, true, false, 3, 0, true, 0, null, null, false, false, true],
        ["bit", 11, 1, null, null, null, true, false, 3, null, false, 0, null, null, false, true, true],
        ["sql_variant", 12, 16, null, null, null, true, false, 3, null, false, 0, null, null, false, true, true],
        ["tinyint", 17, 3, null, null, null, true, false, 3, 1, true, 1, null, null, false, true, true],
        ["bigint", 20, 19, null, null, null, true, false, 3, 0, true, 1, null, null, false, true, true],
        ["uniqueidentifier", 72, 16, "'", "'", null, true, false, 3, null, false, 0, null, null, false, true, true],
        ["image", 128, 2147483647, "0x", null, null, true, false, 1, null, false, 0, null, null, true, false, false],
        ["varbinary", 128, 8000, "0x", null, "max length", true, false, 3, null, false, 0, null, null, false, true, false],
        ["binary", 128, 8000, "0x", null, "length", true, false, 3, null, false, 0, null, null, false, false, true],
        ["timestamp", 128, 8, "0x", null, null, false, false, 3, null, false, 0, null, null, false, false, true],
        ["char", 129, 8000, "'", "'", "length", true, false, 4, null, false, 0, null, null, false, false, true],
        ["varchar", 129, 8000, "'", "'", "max length", true, false, 4, null, false, 0, null, null, false, true, false],
        ["text", 129, 2147483647, "'", "'", null, true, false, 2, null, false, 0, null, null, true, false, false],
        ["ntext", 130, 1073741823, "N'", "'", null, true, false, 2, null, false, 0, null, null, true, false, false],
        ["nvarchar", 130, 4000, "N'", "'", "max length", true, false, 4, null, false, 0, null, null, false, true, false],
        ["nchar", 130, 4000, "N'", "'", "length", true, false, 4, null, false, 0, null, null, false, false, true],
        ["decimal", 131, 38, null, null, "precision,scale", true, false, 3, 0, true, 1, 0, 38, false, false, true],
        ["numeric", 131, 38, null, null, "precision,scale", true, false, 3, 0, true, 1, 0, 38, false, true, true],
        ["date", 133, 10, "'", "'", null, true, false, 4, null, false, 0, null, null, false, true, true],
        ["smalldatetime", 135, 16, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, true],
        ["datetime2", 135, 27, "'", "'", "scale", true, false, 4, null, false, 0, 0, 7, false, true, true],
        ["datetime", 135, 23, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, true],
        ["xml", 141, 0, "N'", "'", null, true, true, 1, null, false, 0, null, null, true, true, false],
        ["time", 145, 16, "'", "'", "scale", true, false, 4, null, false, 0, 0, 7, false, true, true],
        ["datetimeoffset", 146, 34, "'", "'", "scale", true, false, 4, null, false, 0, 0, 7, false, true, true],
    ];

    private static readonly object?[][] ProviderTypes90 =
    [
        ["smallint", 2, 5, null, null, null, true, false, 3, 0, true, 1, null, null, false, true, true],
        ["int", 3, 10, null, null, null, true, false, 3, 0, true, 1, null, null, false, true, true],
        ["real", 4, 7, null, null, null, true, false, 3, 0, false, 0, null, null, false, true, true],
        ["float", 5, 15, null, null, null, true, false, 3, 0, false, 0, null, null, false, true, true],
        ["money", 6, 19, "$", null, null, true, false, 3, 0, true, 0, null, null, false, true, true],
        ["smallmoney", 6, 10, "$", null, null, true, false, 3, 0, true, 0, null, null, false, false, true],
        ["bit", 11, 1, null, null, null, true, false, 3, null, false, 0, null, null, false, true, true],
        ["sql_variant", 12, 16, null, null, null, true, false, 3, null, false, 0, null, null, false, true, true],
        ["tinyint", 17, 3, null, null, null, true, false, 3, 1, true, 1, null, null, false, true, true],
        ["bigint", 20, 19, null, null, null, true, false, 3, 0, true, 1, null, null, false, true, true],
        ["uniqueidentifier", 72, 16, "'", "'", null, true, false, 3, null, false, 0, null, null, false, true, true],
        ["image", 128, 2147483647, "0x", null, null, true, false, 1, null, false, 0, null, null, true, false, false],
        ["varbinary", 128, 8000, "0x", null, "max length", true, false, 3, null, false, 0, null, null, false, true, false],
        ["binary", 128, 8000, "0x", null, "length", true, false, 3, null, false, 0, null, null, false, false, true],
        ["timestamp", 128, 8, "0x", null, null, false, false, 3, null, false, 0, null, null, false, false, true],
        ["char", 129, 8000, "'", "'", "length", true, false, 4, null, false, 0, null, null, false, false, true],
        ["varchar", 129, 8000, "'", "'", "max length", true, false, 4, null, false, 0, null, null, false, true, false],
        ["text", 129, 2147483647, "'", "'", null, true, false, 2, null, false, 0, null, null, true, false, false],
        ["date", 130, 10, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, false],
        ["time", 130, 16, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, false],
        ["datetime2", 130, 27, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, false],
        ["datetimeoffset", 130, 34, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, false],
        ["ntext", 130, 1073741823, "N'", "'", null, true, false, 2, null, false, 0, null, null, true, false, false],
        ["nvarchar", 130, 4000, "N'", "'", "max length", true, false, 4, null, false, 0, null, null, false, true, false],
        ["nchar", 130, 4000, "N'", "'", "length", true, false, 4, null, false, 0, null, null, false, false, true],
        ["decimal", 131, 38, null, null, "precision,scale", true, false, 3, 0, true, 1, 0, 38, false, false, true],
        ["numeric", 131, 38, null, null, "precision,scale", true, false, 3, 0, true, 1, 0, 38, false, true, true],
        ["datetime", 135, 23, "'", "'", null, true, false, 4, null, false, 0, null, null, false, true, true],
        ["smalldatetime", 135, 16, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, true],
        ["xml", 141, 0, "N'", "'", null, true, true, 1, null, false, 0, null, null, true, true, false],
    ];

    private static readonly object?[][] ProviderTypesClassic =
    [
        ["smallint", 2, 5, null, null, null, true, false, 3, 0, true, 1, null, null, false, true, true],
        ["int", 3, 10, null, null, null, true, false, 3, 0, true, 1, null, null, false, true, true],
        ["real", 4, 7, null, null, null, true, false, 3, 0, false, 0, null, null, false, true, true],
        ["float", 5, 15, null, null, null, true, false, 3, 0, false, 0, null, null, false, true, true],
        ["smallmoney", 6, 10, "$", null, null, true, false, 3, 0, true, 0, null, null, false, false, true],
        ["money", 6, 19, "$", null, null, true, false, 3, 0, true, 0, null, null, false, true, true],
        ["bit", 11, 1, null, null, null, true, false, 3, null, false, 0, null, null, false, true, true],
        ["sql_variant", 12, 16, null, null, null, true, false, 3, null, false, 0, null, null, false, true, true],
        ["tinyint", 17, 3, null, null, null, true, false, 3, 1, true, 1, null, null, false, true, true],
        ["bigint", 20, 19, null, null, null, true, false, 3, 0, true, 1, null, null, false, true, true],
        ["uniqueidentifier", 72, 16, "'", "'", null, true, false, 3, null, false, 0, null, null, false, true, true],
        ["binary", 128, 8000, "0x", null, "length", true, false, 3, null, false, 0, null, null, false, false, true],
        ["timestamp", 128, 8, "0x", null, null, false, false, 3, null, false, 0, null, null, false, false, true],
        ["image", 128, 2147483647, "0x", null, null, true, false, 1, null, false, 0, null, null, true, false, false],
        ["varbinary", 128, 8000, "0x", null, "max length", true, false, 3, null, false, 0, null, null, false, true, false],
        ["char", 129, 8000, "'", "'", "length", true, false, 4, null, false, 0, null, null, false, false, true],
        ["text", 129, 2147483647, "'", "'", null, true, false, 2, null, false, 0, null, null, true, false, false],
        ["varchar", 129, 8000, "'", "'", "max length", true, false, 4, null, false, 0, null, null, false, true, false],
        ["date", 130, 10, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, false],
        ["time", 130, 16, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, false],
        ["datetime2", 130, 27, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, false],
        ["datetimeoffset", 130, 34, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, false],
        ["nchar", 130, 4000, "N'", "'", "length", true, false, 4, null, false, 0, null, null, false, false, true],
        ["ntext", 130, 1073741823, "N'", "'", null, true, false, 2, null, false, 0, null, null, true, false, false],
        ["xml", 130, 1073741823, "N'", "'", null, true, true, 1, null, false, 0, null, null, true, false, false],
        ["nvarchar", 130, 4000, "N'", "'", "max length", true, false, 4, null, false, 0, null, null, false, true, false],
        ["decimal", 131, 38, null, null, "precision,scale", true, false, 3, 0, true, 1, 0, 38, false, false, true],
        ["numeric", 131, 38, null, null, "precision,scale", true, false, 3, 0, true, 1, 0, 38, false, true, true],
        ["smalldatetime", 135, 16, "'", "'", null, true, false, 4, null, false, 0, null, null, false, false, true],
        ["datetime", 135, 23, "'", "'", null, true, false, 4, null, false, 0, null, null, false, true, true],
    ];

    private static readonly SqlType[] RowsetProceduresSchema =
        [RowsetName, RowsetName, RowsetName134, SqlType.SmallInt, RowsetDescription, RowsetDescription, SqlType.DateTime, SqlType.DateTime];

    private static readonly string[] RowsetProceduresNames =
        ["PROCEDURE_CATALOG", "PROCEDURE_SCHEMA", "PROCEDURE_NAME", "PROCEDURE_TYPE", "PROCEDURE_DEFINITION", "DESCRIPTION", "DATE_CREATED", "DATE_MODIFIED"];

    private static readonly SqlType[] RowsetProcedureParamsSchema =
    [
        RowsetName, RowsetName, RowsetName134, RowsetName, SqlType.SmallInt, SqlType.SmallInt, SqlType.TinyInt, RowsetParameterDefault, SqlType.Bit,
        SqlType.SmallInt, SqlType.Int32, SqlType.Int32, SqlType.SmallInt, SqlType.SmallInt, RowsetParameterDescription, RowsetName, RowsetName,
        RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetName, RowsetText4000, RowsetName, RowsetName, SqlType.Int32,
    ];

    private static readonly string[] RowsetProcedureParamsNames =
    [
        "PROCEDURE_CATALOG", "PROCEDURE_SCHEMA", "PROCEDURE_NAME", "PARAMETER_NAME", "ORDINAL_POSITION", "PARAMETER_TYPE", "PARAMETER_HASDEFAULT",
        "PARAMETER_DEFAULT", "IS_NULLABLE", "DATA_TYPE", "CHARACTER_MAXIMUM_LENGTH", "CHARACTER_OCTET_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE",
        "DESCRIPTION", "TYPE_NAME", "LOCAL_TYPE_NAME", "SS_XML_SCHEMACOLLECTION_CATALOGNAME", "SS_XML_SCHEMACOLLECTION_SCHEMANAME",
        "SS_XML_SCHEMACOLLECTIONNAME", "SS_UDT_CATALOGNAME", "SS_UDT_SCHEMANAME", "SS_UDT_NAME", "SS_UDT_ASSEMBLY_TYPENAME",
        "SS_TYPE_CATALOG_NAME", "SS_TYPE_SCHEMANAME", "SS_DATETIME_PRECISION",
    ];

    /// <summary>
    /// <c>sp_provider_types_rowset</c> and its versions: the OLE DB
    /// <c>PROVIDER_TYPES</c> rowset, the static type list of the provider
    /// generation — <c>_90</c> and the plain version report the date and time
    /// types after <c>datetime</c> as strings, the plain one <c>xml</c> too —
    /// narrowed to one <c>@data_type</c> and to the best match or the rest
    /// by <c>@best_match</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeProviderTypesRowset(BatchContext batch, string procedure, string calledAs, RowsetVersion version)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments,
            [new("data_type", SqlType.SmallInt, 0, SqlValue.Null(SqlType.SmallInt)), new("best_match", SqlType.TinyInt, 0, SqlValue.Null(SqlType.TinyInt))]);
        short? dataType = values[0].IsNull ? null : values[0].AsInt16;
        bool? bestMatch = values[1].IsNull ? null : values[1].CoerceTo(SqlType.Int32).AsInt32 != 0;
        var table = version switch { RowsetVersion.V100 => ProviderTypes100, RowsetVersion.V90 => ProviderTypes90, _ => ProviderTypesClassic };
        var rows = new List<SqlValue[]>();
        foreach (var spec in table)
        {
            if ((dataType is { } wanted && (int)spec[1]! != wanted) || (bestMatch is { } best && (bool)spec[15]! != best))
                continue;
            static SqlValue Text(NVarcharSqlType type, object? cell) => cell is string text ? SqlValue.FromNVarchar(type, text) : SqlValue.Null(type);
            static SqlValue Bit(object? cell) => SqlValue.FromBoolean((bool)cell!);
            static SqlValue Small(object? cell) => cell is int value ? SqlValue.FromInt16((short)value) : SqlValue.Null(SqlType.SmallInt);
            static SqlValue Tiny(object? cell) => cell is int value ? SqlValue.FromByte((byte)value) : SqlValue.Null(SqlType.TinyInt);
            rows.Add([
                Text(RowsetName, spec[0]), Small(spec[1]), SqlValue.FromInt32((int)spec[2]!), Text(RowsetLiteral, spec[3]), Text(RowsetLiteral, spec[4]),
                Text(RowsetLiteral, spec[5]), Bit(spec[6]), Bit(spec[7]), SqlValue.FromInt32((int)spec[8]!), Tiny(spec[9]), Bit(spec[10]), Tiny(spec[11]),
                Text(RowsetName, spec[0]), Small(spec[12]), Small(spec[13]), SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(RowsetDescription),
                SqlValue.Null(RowsetDescription), Bit(spec[14]), Bit(spec[15]), Bit(spec[16]),
            ]);
        }
        yield return new SimulatedSqlResultSet(RowsetProviderTypesSchema, RowsetProviderTypesNames, rows);
    }

    /// <summary>
    /// A stored procedure a <c>PROCEDURES</c> or <c>PROCEDURE_PARAMETERS</c>
    /// rowset lists: a procedure of the database, by its number in its group,
    /// a function under number 0, or a system procedure.
    /// </summary>
    private readonly struct RowsetRoutine(string schema, string name, short number, DateTime createDate, Procedure? procedure, UserDefinedFunction? function, BuiltInResources.SystemParameter[]? systemParameters)
    {
        public readonly string Schema = schema;
        public readonly string Name = name;
        public readonly short Number = number;
        public readonly DateTime CreateDate = createDate;
        public readonly Procedure? Procedure = procedure;
        public readonly UserDefinedFunction? Function = function;
        public readonly BuiltInResources.SystemParameter[]? SystemParameters = systemParameters;
    }

    /// <summary>
    /// The current database's procedures — every number of a numbered group
    /// — then, with <paramref name="functions"/>, its functions, and the
    /// system procedures under <c>sys</c>, named exactly by
    /// <paramref name="name"/> and <paramref name="schemaName"/> (every one
    /// for a null), sorted by schema, name and number.
    /// </summary>
    private static List<RowsetRoutine> RowsetRoutines(BatchContext batch, string? name, string? schemaName, bool functions)
    {
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var visible = PermissionEnforcement.ObjectVisibility(batch, database);
        var routines = new List<RowsetRoutine>();
        bool Named(string candidate) => name is null || collation.Equals(name, candidate);
        foreach (var (_, schema) in database.Schemas)
        {
            if (schemaName is not null && !collation.Equals(schemaName, schema.Name))
                continue;
            foreach (var (_, procedure) in schema.Procedures)
            {
                if (!Named(procedure.Name) || visible?.Invoke(procedure) == false)
                    continue;
                routines.Add(new(schema.Name, procedure.Name, 1, procedure.CreateDate, procedure, null, null));
                foreach (var (number, numbered) in procedure.Numbered ?? [])
                {
                    if (number > 1)
                        routines.Add(new(schema.Name, procedure.Name, number, numbered.CreateDate, numbered, null, null));
                }
            }
            if (functions)
            {
                foreach (var (_, function) in schema.Functions)
                {
                    if (function is not ClrAggregateFunction && Named(function.Name) && visible?.Invoke(function) != false)
                        routines.Add(new(schema.Name, function.Name, 0, function.CreateDate, null, function, null));
                }
            }
            if (schema.SchemaId == Database.SysSchemaId)
            {
                foreach (var system in BuiltInResources.SystemProcedureNames)
                {
                    if (Named(system) && !BuiltInResources.IsExtendedProcedure(system))
                        routines.Add(new(schema.Name, system, 1, BuiltInResources.SystemObjectDate, null, null, BuiltInResources.SystemProcedureParameters.GetValueOrDefault(system, [])));
                }
            }
        }
        routines.Sort((a, b) =>
        {
            var bySchema = collation.Compare(a.Schema, b.Schema);
            if (bySchema != 0)
                return bySchema;
            var byName = collation.Compare(a.Name, b.Name);
            return byName != 0 ? byName : a.Number.CompareTo(b.Number);
        });
        return routines;
    }

    private static string RoutineRowsetName(RowsetRoutine routine) =>
        routine.Name + ";" + routine.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>sp_procedures_rowset</c> / <c>…2</c>: the OLE DB
    /// <c>PROCEDURES</c> rowset, each procedure as <c>name;number</c> with its
    /// creation date — a function isn't one (probed 2026-10-11 against SQL
    /// Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeProceduresRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("procedure_schema")]
            : [RowsetNameParameter("procedure_name", required: true), new("group_number", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)), RowsetNameParameter("procedure_schema")];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var name = everyObject ? null : RowsetText(values[0]);
        int? group = everyObject || values[1].IsNull ? null : values[1].AsInt32;
        var schema = RowsetText(values[everyObject ? 0 : 2]);

        var catalog = SqlValue.FromNVarchar(RowsetName, batch.CurrentDatabase.Name);
        var rows = new List<SqlValue[]>();
        if (everyObject || name is not null)
        {
            foreach (var routine in RowsetRoutines(batch, name, schema, functions: false))
            {
                if (group is { } number && number != routine.Number)
                    continue;
                rows.Add([
                    catalog, SqlValue.FromNVarchar(RowsetName, routine.Schema), SqlValue.FromNVarchar(RowsetName134, RoutineRowsetName(routine)),
                    SqlValue.FromInt16(3), SqlValue.Null(RowsetDescription), SqlValue.Null(RowsetDescription),
                    SqlValue.FromDateTime(routine.CreateDate), SqlValue.Null(SqlType.DateTime),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(RowsetProceduresSchema, RowsetProceduresNames, rows);
    }

    /// <summary>
    /// <c>sp_procedure_params_rowset</c> and its versions: the OLE DB
    /// <c>PROCEDURE_PARAMETERS</c> rowset — a procedure's return value
    /// first, then its parameters, a function's (number 0) too, a
    /// table-valued one returning <c>@TABLE_RETURN_VALUE</c>. A default is
    /// never reported, an output parameter reads as input-output, and each
    /// type reads in the provider generation's terms (see
    /// <see cref="DescribeRowsetParameterType"/>); <c>_90</c> adds the XML
    /// schema collection and CLR type names, <c>_100</c> the type's own
    /// catalog and schema and the date and time precision.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeProcedureParamsRowset(BatchContext batch, string procedure, string calledAs, RowsetVersion version)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var everyObject = ListsEveryObject(procedure);
        SystemProcedureParameter[] parameters = everyObject
            ? [RowsetNameParameter("procedure_schema"), RowsetNameParameter("parameter_name")]
            :
            [
                RowsetNameParameter("procedure_name", required: true), new("group_number", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
                RowsetNameParameter("procedure_schema"), RowsetNameParameter("parameter_name"),
            ];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var name = everyObject ? null : RowsetText(values[0]);
        int? group = everyObject || values[1].IsNull ? null : values[1].AsInt32;
        var schema = RowsetText(values[everyObject ? 0 : 2]);
        var parameterName = RowsetText(values[everyObject ? 1 : 3]);

        var columnCount = version switch { RowsetVersion.Classic => 17, RowsetVersion.V90 => 24, _ => 27 };
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
        var rows = new List<SqlValue[]>();
        if (everyObject || name is not null)
        {
            foreach (var routine in RowsetRoutines(batch, name, schema, functions: true))
            {
                if (group is { } number && number != routine.Number)
                    continue;
                var routineName = SqlValue.FromNVarchar(RowsetName134, RoutineRowsetName(routine));
                var schemaValue = SqlValue.FromNVarchar(RowsetName, routine.Schema);
                void Add(string name, short ordinal, short kind, bool nullable, RowsetParameterType described)
                {
                    if (parameterName is not null && !collation.Equals(parameterName, name))
                        return;
                    rows.Add(RowsetParameterRow(database, catalog, schemaValue, routineName, name, ordinal, kind, nullable, described)[..columnCount]);
                }

                switch (routine)
                {
                    case { Procedure: { } proc }:
                        Add("@RETURN_VALUE", 0, 4, false, DescribeRowsetParameterType(SqlType.Int32, null, null, false, version));
                        for (var i = 0; i < proc.Parameters.Length; i++)
                        {
                            var parameter = proc.Parameters[i];
                            if (parameter.IsCursor || parameter.TableType is not null)
                                continue;
                            Add("@" + parameter.Name, (short)(i + 1), parameter.IsOutput ? (short)2 : (short)1, true,
                                DescribeRowsetParameterType(parameter.Type, parameter.DeclaredMaxLength, parameter.AliasType, parameter.SpelledNumeric, version));
                        }
                        break;
                    case { Function: { } function }:
                        var returnType = function switch
                        {
                            ScalarFunction scalar => scalar.ReturnType,
                            ClrScalarFunction clrScalar => clrScalar.ReturnType,
                            _ => null,
                        };
                        if (returnType is null)
                            Add("@TABLE_RETURN_VALUE", 0, 4, false, RowsetParameterType.Table);
                        else
                            Add("@RETURN_VALUE", 0, 4, true, DescribeRowsetParameterType(returnType, null, null, false, version));
                        for (var i = 0; i < function.Parameters.Length; i++)
                        {
                            var parameter = function.Parameters[i];
                            if (parameter.TableType is not null)
                                continue;
                            Add("@" + parameter.Name, (short)(i + 1), 1, true,
                                DescribeRowsetParameterType(parameter.Type, parameter.DeclaredMaxLength, parameter.AliasType, parameter.SpelledNumeric, version));
                        }
                        break;
                    default:
                        Add("@RETURN_VALUE", 0, 4, false, DescribeRowsetParameterType(SqlType.Int32, null, null, false, version));
                        var systemParameters = routine.SystemParameters!;
                        for (var i = 0; i < systemParameters.Length; i++)
                        {
                            var parameter = systemParameters[i];
                            Add(parameter.Name, (short)(i + 1), parameter.IsOutput ? (short)2 : (short)1, true,
                                DescribeRowsetParameterType(SystemParameterType(parameter), null, null, false, version));
                        }
                        break;
                }
            }
        }
        yield return new SimulatedSqlResultSet(RowsetProcedureParamsSchema[..columnCount], RowsetProcedureParamsNames[..columnCount], rows);
    }

    /// <summary>The type a system procedure's parameter is declared with, from its <c>sys.system_parameters</c> row.</summary>
    private static SqlType SystemParameterType(BuiltInResources.SystemParameter parameter) => parameter.UserTypeId switch
    {
        35 => SqlType.Text,
        48 => SqlType.TinyInt,
        52 => SqlType.SmallInt,
        56 => SqlType.Int32,
        98 => SqlType.SqlVariant,
        104 => SqlType.Bit,
        127 => SqlType.BigInt,
        165 => VarbinarySqlType.Get(parameter.MaxLength),
        167 => VarcharSqlType.Get(parameter.MaxLength, Collation.Catalog, Coercibility.Implicit),
        175 => CharSqlType.Get(parameter.MaxLength, Collation.Catalog, Coercibility.Implicit),
        231 => NVarcharSqlType.Get(parameter.MaxLength < 0 ? parameter.MaxLength : parameter.MaxLength / 2, Collation.Catalog, Coercibility.Implicit),
        239 => NCharSqlType.Get(parameter.MaxLength / 2, Collation.Catalog, Coercibility.Implicit),
        _ => SqlType.SystemName,
    };

    /// <summary>How a parameter's type reads in <c>PROCEDURE_PARAMETERS</c>; a CLR type's carries <see cref="UdtType"/>.</summary>
    private readonly struct RowsetParameterType(short dataType, int? maxLength, int? octetLength, short? precision, short? scale, string? typeName, int? datetimePrecision, SqlType? udtType = null, string? description = null)
    {
        public readonly short DataType = dataType;
        public readonly int? MaxLength = maxLength;
        public readonly int? OctetLength = octetLength;
        public readonly short? Precision = precision;
        public readonly short? Scale = scale;
        public readonly string? TypeName = typeName;
        public readonly int? DatetimePrecision = datetimePrecision;
        public readonly SqlType? UdtType = udtType;
        public readonly string? Description = description;

        /// <summary>A table-valued function's result.</summary>
        public static RowsetParameterType Table => new(0, null, null, null, null, "table", null, description: "Result table returned by table valued function");
    }

    /// <summary>
    /// A parameter's type as <c>PROCEDURE_PARAMETERS</c> reports it (probed
    /// 2026-10-11 against SQL Server 2025 for every system type): as the
    /// <c>COLUMNS</c> rowset reports a column's, but with the type's own name
    /// — <c>sysname</c> reading as <c>nvarchar</c>, a <c>float(24)</c> as
    /// <c>real</c> — and an alias type's name left NULL from <c>_90</c> on;
    /// <c>_90</c> reports a spatial type as a <c>varbinary</c> of length 0,
    /// and the plain version names a type it reports down-level by the type
    /// it reads as (<c>text</c>, <c>ntext</c>, <c>image</c>,
    /// <c>varbinary</c>), <c>xml</c> aside.
    /// </summary>
    private static RowsetParameterType DescribeRowsetParameterType(SqlType type, int? declaredMaxLength, AliasType? alias, bool spelledNumeric, RowsetVersion version)
    {
        var classic = version == RowsetVersion.Classic;
        string? Name(string own) => alias is not null && !classic ? null : own;
        var isMax = declaredMaxLength == SqlType.MaxLengthSentinel;
        RowsetParameterType Downlevel(int length, string name) => new(130, length, length * 2, null, null, Name(name), null);
        return type switch
        {
            _ when type == SqlType.Bit => new RowsetParameterType(11, null, null, null, null, Name("bit"), null),
            _ when type == SqlType.TinyInt => new(17, null, null, 3, null, Name("tinyint"), null),
            _ when type == SqlType.SmallInt => new(2, null, null, 5, null, Name("smallint"), null),
            _ when type == SqlType.Int32 => new(3, null, null, 10, null, Name("int"), null),
            _ when type == SqlType.BigInt => new(20, null, null, 19, null, Name("bigint"), null),
            DecimalSqlType d => new(131, null, null, d.precision, d.scale, Name(spelledNumeric ? "numeric" : "decimal"), null),
            _ when type == SqlType.Money => new(6, null, null, 19, null, Name("money"), null),
            _ when type == SqlType.SmallMoney => new(6, null, null, 10, null, Name("smallmoney"), null),
            _ when type == SqlType.Float => new(5, null, null, 15, null, Name("float"), null),
            _ when type == SqlType.Real => new(4, null, null, 7, null, Name("real"), null),
            DateSqlType => version == RowsetVersion.V100 ? new(133, null, null, null, null, Name("date"), 0) : Downlevel(10, "date"),
            _ when type == SqlType.SmallDateTime => new(135, null, null, null, null, Name("smalldatetime"), 0),
            _ when type == SqlType.DateTime => new(135, null, null, null, null, Name("datetime"), 3),
            DateTime2SqlType dt2 => version == RowsetVersion.V100
                ? new(135, null, null, null, null, Name("datetime2"), dt2.precision)
                : Downlevel(ScaledTemporalPrecision(19, dt2.precision), "datetime2"),
            TimeSqlType tm => version == RowsetVersion.V100
                ? new(145, null, null, null, null, Name("time"), tm.precision)
                : Downlevel(ScaledTemporalPrecision(8, tm.precision), "time"),
            DateTimeOffsetSqlType dto => version == RowsetVersion.V100
                ? new(146, null, null, null, null, Name("datetimeoffset"), dto.precision)
                : Downlevel(ScaledTemporalPrecision(26, dto.precision), "datetimeoffset"),
            _ when type == SqlType.UniqueIdentifier => new(72, null, null, null, null, Name("uniqueidentifier"), null),
            _ when type == SqlType.RowVersion => new(128, 8, 8, null, null, Name("timestamp"), null),
            _ when type == SqlType.Text => new(129, int.MaxValue, int.MaxValue, null, null, Name("text"), null),
            _ when type == SqlType.NText => new(130, 1073741823, 2147483646, null, null, Name("ntext"), null),
            _ when type == SqlType.Image => new(128, int.MaxValue, int.MaxValue, null, null, Name("image"), null),
            _ when type == SqlType.SystemName => new(130, 128, 256, null, null, Name("nvarchar"), null),
            CharSqlType c => new(129, c.length, c.length, null, null, Name("char"), null),
            NCharSqlType nc => new(130, nc.length, nc.length * 2, null, null, Name("nchar"), null),
            BinarySqlType bn => new(128, bn.length, bn.length, null, null, Name("binary"), null),
            VarcharSqlType vc when isMax || vc.length < 1 => classic
                ? new(129, int.MaxValue, int.MaxValue, null, null, "text", null)
                : new(129, 0, 0, null, null, Name("varchar"), null),
            NVarcharSqlType nv when isMax || nv.length < 1 => classic
                ? new(130, 1073741823, 2147483646, null, null, "ntext", null)
                : new(130, 0, 0, null, null, Name("nvarchar"), null),
            VarbinarySqlType vb when isMax || vb.length < 1 => classic
                ? new(128, int.MaxValue, int.MaxValue, null, null, "image", null)
                : new(128, 0, 0, null, null, Name("varbinary"), null),
            VarcharSqlType vc => new(129, vc.length, vc.length, null, null, Name("varchar"), null),
            NVarcharSqlType nv => new(130, nv.length, nv.length * 2, null, null, Name("nvarchar"), null),
            VarbinarySqlType vb => new(128, vb.length, vb.length, null, null, Name("varbinary"), null),
            XmlSqlType => classic
                ? new(130, 1073741823, 2147483646, null, null, "xml", null)
                : new(141, null, null, null, null, Name("xml"), null),
            SqlVariantSqlType => new(12, null, null, null, null, Name("sql_variant"), null),
            VectorSqlType vector => new(128, vector.ByteLength, vector.ByteLength, null, null, Name("vector"), null),
            HierarchyIdSqlType => classic
                ? new(128, 892, 892, null, null, "varbinary", null)
                : new(132, 892, 892, null, null, Name("hierarchyid"), null, type),
            SpatialSqlType => version switch
            {
                RowsetVersion.Classic => new(128, int.MaxValue, int.MaxValue, null, null, "image", null),
                RowsetVersion.V90 => new(128, 0, 0, null, null, Name(SpColumnsTypeName(type)), null, type),
                _ => new(132, 0, 0, null, null, Name(SpColumnsTypeName(type)), null, type),
            },
            ClrUdtSqlType udt => classic
                ? new(128, udt.Udt.MaxByteSize < 0 ? int.MaxValue : udt.Udt.MaxByteSize, udt.Udt.MaxByteSize < 0 ? int.MaxValue : udt.Udt.MaxByteSize, null, null, "varbinary", null)
                : new(132, Math.Max(udt.Udt.MaxByteSize, 0), Math.Max(udt.Udt.MaxByteSize, 0), null, null, Name(udt.Udt.Name), null, type),
            _ => new(0, null, null, null, null, null, null),
        };
    }

    /// <summary>One <c>PROCEDURE_PARAMETERS</c> row, all 27 cells; the older versions take a prefix.</summary>
    private static SqlValue[] RowsetParameterRow(Database database, SqlValue catalog, SqlValue schema, SqlValue routine, string name, short ordinal, short kind, bool nullable, RowsetParameterType described)
    {
        var nullName = SqlValue.Null(RowsetName);
        SqlValue Name(string? value) => value is null ? nullName : SqlValue.FromNVarchar(RowsetName, value);
        static SqlValue Int(int? value) => value is { } v ? SqlValue.FromInt32(v) : SqlValue.Null(SqlType.Int32);
        static SqlValue Small(short? value) => value is { } v ? SqlValue.FromInt16(v) : SqlValue.Null(SqlType.SmallInt);
        var (udtSchema, udtName) = described.UdtType switch
        {
            ClrUdtSqlType udt => (udt.Udt.Schema.Name, udt.Udt.Name),
            { } system => ("sys", SpColumnsTypeName(system)),
            null => (null, null),
        };
        return
        [
            catalog, schema, routine, Name(name), SqlValue.FromInt16(ordinal), SqlValue.FromInt16(kind), SqlValue.FromByte(0),
            SqlValue.Null(RowsetParameterDefault), SqlValue.FromBoolean(nullable), SqlValue.FromInt16(described.DataType),
            Int(described.MaxLength), Int(described.OctetLength), Small(described.Precision), Small(described.Scale),
            described.Description is null ? SqlValue.Null(RowsetParameterDescription) : SqlValue.FromNVarchar(RowsetParameterDescription, described.Description),
            Name(described.TypeName), Name(described.TypeName), nullName, nullName, nullName,
            Name(udtName is null ? null : database.Name), Name(udtSchema), Name(udtName),
            described.UdtType is { } clr ? SqlValue.FromNVarchar(RowsetText4000, SpColumnsClrAssemblyName(clr)!) : SqlValue.Null(RowsetText4000),
            Name(udtName is null ? null : database.Name), Name(udtSchema), Int(described.DatetimePrecision),
        ];
    }
}
