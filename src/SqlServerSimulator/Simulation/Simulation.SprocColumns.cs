using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    // sp_sproc_columns: sp_columns' first 18 columns under their procedure
    // names with COLUMN_TYPE inserted after COLUMN_NAME, then SS_DATA_TYPE;
    // sp_sproc_columns_100 puts the SS_TYPE_* pair and sp_columns_100's UDT /
    // XML-collection columns ahead of SS_DATA_TYPE (probed 2026-09-26 against
    // SQL Server 2025).
    private static readonly SqlType[] SprocColumnsSchema =
    [
        SqlType.SystemName, SqlType.SystemName, CatalogNVarchar134, SqlType.SystemName, SqlType.SmallInt,
        .. SpColumnsSchema[4..18], SpColumnsSchema[28],
    ];

    private static readonly string[] SprocColumnsColumnNames =
    [
        "PROCEDURE_QUALIFIER", "PROCEDURE_OWNER", "PROCEDURE_NAME", "COLUMN_NAME", "COLUMN_TYPE",
        .. SpColumnsColumnNames[4..18], SpColumnsColumnNames[28],
    ];

    private static readonly SqlType[] SprocColumns100Schema =
    [
        .. SprocColumnsSchema[..19], SqlType.SystemName, SqlType.SystemName, .. SpColumnsSchema[22..29],
    ];

    private static readonly string[] SprocColumns100ColumnNames =
    [
        .. SprocColumnsColumnNames[..19], "SS_TYPE_CATALOG_NAME", "SS_TYPE_SCHEMA_NAME", .. SpColumnsColumnNames[22..29],
    ];

    // sp_sproc_columns_90: _100's columns without the SS_TYPE_* pair (probed
    // 2026-10-16 against SQL Server 2025).
    private static readonly SqlType[] SprocColumns90Schema = [.. SprocColumns100Schema[..19], .. SprocColumns100Schema[21..]];

    private static readonly string[] SprocColumns90ColumnNames = [.. SprocColumns100ColumnNames[..19], .. SprocColumns100ColumnNames[21..]];

    private const short SqlParamInput = 1;
    private const short SqlParamInputOutput = 2;
    private const short SqlResultColumn = 3;
    private const short SqlReturnValue = 5;

    /// <summary>
    /// Handles <c>EXEC sp_sproc_columns</c> / <c>sp_sproc_columns_100</c> —
    /// ODBC's <c>SQLProcedureColumns</c>. For each procedure (named
    /// <c>name;1</c>) or function (<c>name;0</c>) matching the name and owner
    /// patterns: a <c>@RETURN_VALUE</c> row at ordinal 0 (a procedure's
    /// <c>int</c> status, non-nullable; a scalar function's declared return
    /// type), or for a table-valued function a single
    /// <c>@TABLE_RETURN_VALUE</c> result row rather than its columns, then one
    /// row per parameter, each nullable and with no <c>COLUMN_DEF</c>. The type
    /// columns are <c>sp_columns</c>' for the same type, in its classic or
    /// <c>_100</c> spelling; a table-valued parameter reads as type -153 named
    /// after its table type. <c>@fUsePattern = 0</c> matches names exactly
    /// (probed 2026-09-26 against SQL Server 2025). <c>_90</c> types each
    /// parameter as <c>sp_columns_90</c> types a column, without the
    /// SS_TYPE_* pair (probed 2026-10-16).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSprocColumns(BatchContext batch, SpColumnsVersion version)
    {
        var classic = version == SpColumnsVersion.Classic;
        var procedureName = version switch { SpColumnsVersion.Classic => "sp_sproc_columns", SpColumnsVersion.V90 => "sp_sproc_columns_90", _ => "sp_sproc_columns_100" };
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (name, owner, qualifier, column, odbcVer, usePattern) = ParseSpSprocColumnsArgs(arguments, procedureName, classic);
        var database = batch.CurrentDatabase;
        var byName = (odbcVer >= 3 ? SpDatatypeInfoByNameV3 : SpDatatypeInfoByNameV2).Value;
        var qualifierValue = SqlValue.FromSystemName(database.Name);
        var namePattern = usePattern ? CompileCatalogPattern(name) : null;
        var ownerPattern = usePattern ? CompileCatalogPattern(owner) : null;
        var columnPattern = usePattern ? CompileCatalogPattern(column) : null;
        var collation = database.Collation;
        bool NameMatches(LikeMatcher? pattern, string? written, string value) =>
            usePattern ? Matches(pattern, value) : written is null || collation.Equals(written, value);

        var rows = new List<SqlValue[]>();
        var routineFound = false;
        if (qualifier is null || collation.Equals(qualifier, database.Name))
        {
            foreach (var schema in database.Schemas.EnumerateValues().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!NameMatches(ownerPattern, owner, schema.Name))
                    continue;
                var ownerValue = SqlValue.FromSystemName(schema.Name);
                // Every number of a numbered procedure group is listed (probed
                // 2026-10-16 against SQL Server 2025).
                IEnumerable<(string Name, short Number, SchemaObject Routine)> routines =
                [
                    .. schema.Procedures.EnumerateValues().SelectMany(p => (IEnumerable<(string, short, SchemaObject)>)
                        [(p.Name, 1, p), .. (p.Numbered ?? []).Where(static n => n.Key > 1).Select(n => (p.Name, n.Key, (SchemaObject)n.Value))]),
                    .. schema.Functions.EnumerateValues().Select(f => (f.Name, (short)0, (SchemaObject)f)),
                ];
                foreach (var (routineName, number, routine) in routines.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Number))
                {
                    if (!NameMatches(namePattern, name, routineName))
                        continue;
                    routineFound = true;
                    var nameValue = SqlValue.FromString(CatalogNVarchar134, routineName + ";" + number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    void Add(string columnName, short columnType, SqlValue[] row)
                    {
                        if (NameMatches(columnPattern, column, columnName))
                            rows.Add(row);
                    }

                    switch (routine)
                    {
                        case Procedure procedure:
                            Add("@RETURN_VALUE", SqlReturnValue, TypedRow(new HeapColumn("@RETURN_VALUE", SqlType.Int32, null, nullable: false), 0, SqlReturnValue));
                            for (var i = 0; i < procedure.Parameters.Length; i++)
                            {
                                var parameter = procedure.Parameters[i];
                                // As sp_columns lists no vector or json column,
                                // this lists no such parameter (probed
                                // 2026-10-02 against SQL Server 2025).
                                if (parameter.Type is VectorSqlType or JsonSqlType)
                                    continue;
                                var parameterName = "@" + parameter.Name;
                                var columnType = parameter.IsOutput ? SqlParamInputOutput : SqlParamInput;
                                Add(parameterName, columnType, parameter.TableType is { } tableType
                                    ? TableTypeRow(parameterName, tableType, i + 1)
                                    : TypedRow(new HeapColumn(parameterName, parameter.Type, parameter.DeclaredMaxLength, nullable: true, spelledNumeric: parameter.SpelledNumeric) { AliasType = parameter.AliasType, XmlSchemaCollection = parameter.XmlSchemaCollection }, i + 1, columnType));
                            }
                            break;
                        case UserDefinedFunction function:
                            if (function is ScalarFunction { ReturnType: not (VectorSqlType or JsonSqlType) } scalar)
                                Add("@RETURN_VALUE", SqlReturnValue, TypedRow(new HeapColumn("@RETURN_VALUE", scalar.ReturnType, null, nullable: true, spelledNumeric: scalar.ReturnSpelledNumeric) { AliasType = scalar.ReturnAliasType }, 0, SqlReturnValue));
                            else if (function is not ScalarFunction)
                                Add("@TABLE_RETURN_VALUE", SqlResultColumn, TableReturnRow());
                            for (var i = 0; i < function.Parameters.Length; i++)
                            {
                                var parameter = function.Parameters[i];
                                if (parameter.Type is VectorSqlType or JsonSqlType)
                                    continue;
                                var parameterName = "@" + parameter.Name;
                                Add(parameterName, SqlParamInput, TypedRow(new HeapColumn(parameterName, parameter.Type, null, nullable: true, spelledNumeric: parameter.SpelledNumeric) { AliasType = parameter.AliasType, XmlSchemaCollection = parameter.XmlSchemaCollection }, i + 1, SqlParamInput));
                            }
                            break;
                    }

                    SqlValue[] TypedRow(HeapColumn typed, int ordinal, short columnType)
                    {
                        var full = BuildSpColumnsRow(qualifierValue, ownerValue, nameValue, typed, ordinal, byName);
                        // A parameter has no default a client can read here.
                        full[12] = SqlValue.Null(CatalogNVarchar4000);
                        // A rowversion parameter keeps the NOT NULL type token,
                        // and a typed xml one names its schema collection
                        // (probed 2026-10-16 against SQL Server 2025).
                        if (typed.Type == SqlType.RowVersion)
                            full[28] = SqlValue.FromByte(45);
                        if (typed.XmlSchemaCollection is { } collection)
                        {
                            full[25] = qualifierValue;
                            full[26] = SqlValue.FromSystemName(SchemaNameOf(database, collection.SchemaId) ?? "");
                            full[27] = SqlValue.FromSystemName(collection.Name);
                        }
                        if (version == SpColumnsVersion.V90)
                        {
                            var downlevel90 = SpColumns90Row(full, typed);
                            return [.. downlevel90[..4], SqlValue.FromInt16(columnType), .. downlevel90[4..]];
                        }
                        if (!classic)
                            return Shape(full, columnType, classic, typed.Type is HierarchyIdSqlType or SpatialSqlType or ClrUdtSqlType);
                        var downlevel = ClassicSpColumnsRow(full, typed);
                        // Where sp_columns blanks a downlevel temporal column's
                        // scale, a parameter keeps its fractional scale and
                        // reports its own datetime subcode.
                        if (typed.Type is DateSqlType or TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType)
                        {
                            downlevel[8] = SqlValue.FromInt16((short)(typed.Type is DateSqlType ? 0 : BuiltInResources.GetSysColumnMetadata(typed).Scale));
                            downlevel[14] = SqlValue.FromInt16(typed.Type switch { DateSqlType => 1, DateTime2SqlType => 3, _ => 0 });
                        }
                        return Shape(downlevel, columnType, classic);
                    }

                    SqlValue[] TableTypeRow(string parameterName, TableType tableType, int ordinal)
                    {
                        var nullSmall = SqlValue.Null(SqlType.SmallInt);
                        var tvp = SqlValue.FromInt16(-153);
                        SqlValue[] head =
                        [
                            qualifierValue, ownerValue, nameValue, SqlValue.FromSystemName(parameterName), SqlValue.FromInt16(SqlParamInput),
                            tvp, SqlValue.FromSystemName(tableType.Name), SqlValue.FromInt32(0), SqlValue.FromInt32(int.MaxValue), nullSmall, nullSmall,
                            SqlValue.FromInt16(1), SqlValue.Null(CatalogVarchar254), SqlValue.Null(CatalogNVarchar4000), tvp, nullSmall,
                            SqlValue.FromInt32(int.MaxValue), SqlValue.FromInt32(ordinal), SqlValue.FromVarchar(CatalogVarchar254, "YES"),
                        ];
                        return Tail(head, SqlValue.FromSystemName(database.Name), SqlValue.FromSystemName(tableType.Schema.Name));
                    }

                    SqlValue[] TableReturnRow()
                    {
                        var zeroSmall = SqlValue.FromInt16(0);
                        var zeroInt = SqlValue.FromInt32(0);
                        SqlValue[] head =
                        [
                            qualifierValue, ownerValue, nameValue, SqlValue.FromSystemName("@TABLE_RETURN_VALUE"), SqlValue.FromInt16(SqlResultColumn),
                            SqlValue.Null(SqlType.SmallInt), SqlValue.FromSystemName("table"), zeroInt, zeroInt, zeroSmall, zeroSmall, zeroSmall,
                            SqlValue.FromVarchar(CatalogVarchar254, "Result table returned by table valued function"), SqlValue.Null(CatalogNVarchar4000),
                            SqlValue.Null(SqlType.SmallInt), SqlValue.Null(SqlType.SmallInt), SqlValue.Null(SqlType.Int32), zeroInt,
                            SqlValue.FromVarchar(CatalogVarchar254, "NO"),
                        ];
                        return Tail(head, SqlValue.Null(SqlType.SystemName), SqlValue.Null(SqlType.SystemName));
                    }

                    SqlValue[] Tail(SqlValue[] head, SqlValue typeCatalog, SqlValue typeSchema)
                    {
                        var ssDataType = SqlValue.FromByte(0);
                        if (classic)
                            return [.. head, ssDataType];
                        var nullName = SqlValue.Null(SqlType.SystemName);
                        return version == SpColumnsVersion.V90
                            ? [.. head, nullName, nullName, SqlValue.Null(CatalogNVarchar4000), nullName, nullName, nullName, ssDataType]
                            : [.. head, typeCatalog, typeSchema, nullName, nullName, SqlValue.Null(CatalogNVarchar4000), nullName, nullName, nullName, ssDataType];
                    }
                }
            }
        }

        if (CatalogQualifierRefusal(batch, procedureName, qualifier, "III", 31) is { } refused)
        {
            foreach (var outcome in refused)
                yield return outcome;
            yield break;
        }
        foreach (var step in ProcedureSteps(batch, SpSprocColumnsSteps(owner, qualifier, NamesOneObject(name, routineFound))))
            yield return step;
        yield return version switch
        {
            SpColumnsVersion.Classic => new SimulatedSqlResultSet(SprocColumnsSchema, SprocColumnsColumnNames, rows),
            SpColumnsVersion.V90 => new SimulatedSqlResultSet(SprocColumns90Schema, SprocColumns90ColumnNames, rows),
            _ => new SimulatedSqlResultSet(SprocColumns100Schema, SprocColumns100ColumnNames, rows),
        };
    }

    /// <summary>
    /// Reshapes an <c>sp_columns</c> row (19 classic cells or 29 <c>_100</c>
    /// cells) into <c>sp_sproc_columns</c>' layout: <c>COLUMN_TYPE</c> after
    /// <c>COLUMN_NAME</c>, and for <c>_100</c> the SS_TYPE_* pair in place of
    /// the SS_IS_* flags, which name a CLR type's catalog and schema as its
    /// SS_UDT_* pair does (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static SqlValue[] Shape(SqlValue[] row, short columnType, bool classic, bool clrType = false)
    {
        SqlValue[] head = [.. row[..4], SqlValue.FromInt16(columnType), .. row[4..18]];
        if (classic)
            return [.. head, row[18]];
        var nullName = SqlValue.Null(SqlType.SystemName);
        return clrType ? [.. head, row[22], row[23], .. row[22..29]] : [.. head, nullName, nullName, .. row[22..29]];
    }

    // Real's positional order is @procedure_name, @procedure_owner,
    // @procedure_qualifier, @column_name, @ODBCVer, then (_100 and classic
    // alike) @fUsePattern.
    private static (string? Name, string? Owner, string? Qualifier, string? Column, int OdbcVer, bool UsePattern) ParseSpSprocColumnsArgs(
        List<ProcArgument> arguments, string procedureName, bool classic)
    {
        string? name = null, owner = null, qualifier = null, column = null;
        var odbcVer = 2;
        var usePattern = true;
        var positional = 0;
        foreach (var arg in arguments)
        {
            var slot = arg.Name switch
            {
                null => positional++,
                var n when BuiltInToken.Equals(n, "procedure_name") => 0,
                var n when BuiltInToken.Equals(n, "procedure_owner") => 1,
                var n when BuiltInToken.Equals(n, "procedure_qualifier") => 2,
                var n when BuiltInToken.Equals(n, "column_name") => 3,
                var n when BuiltInToken.Equals(n, "ODBCVer") => 4,
                var n when BuiltInToken.Equals(n, "fUsePattern") => 5,
                _ => throw CatalogBindingError(SimulatedSqlException.NotAParameterForProcedure(arg.Name!, procedureName)),
            };
            switch (slot)
            {
                case 0: name = CatalogStringArg(arg); break;
                case 1: owner = CatalogStringArg(arg); break;
                case 2: qualifier = CatalogStringArg(arg); break;
                case 3: column = CatalogStringArg(arg); break;
                case 4: odbcVer = CatalogOdbcVer(arg); break;
                case 5: usePattern = arg.Value.IsNull || arg.Value.CoerceTo(SqlType.Int32).AsInt32 != 0; break;
                default: throw CatalogBindingError(SimulatedSqlException.TooManyArgumentsToFunction(procedureName));
            }
        }
        return (name, owner, qualifier, column, classic ? (odbcVer == 3 ? 3 : 2) : odbcVer < 3 ? 2 : 3, usePattern);
    }
}
