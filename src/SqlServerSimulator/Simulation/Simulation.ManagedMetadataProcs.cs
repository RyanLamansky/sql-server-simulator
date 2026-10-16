using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The metadata procedures SqlClient itself calls: sp_procedure_params_100_managed
// behind SqlCommandBuilder.DeriveParameters, and sp_columns_managed /
// sp_indexes_managed / sp_indexcolumns_managed behind SqlConnection.GetSchema's
// Columns, AllColumns, ColumnSetColumns, Indexes and IndexColumns collections.
// See docs/claude/catalog-views.md.
partial class Simulation
{
    // This file's static fields initialize ahead of the OLE DB rowset file's,
    // so its shapes declare their own name and text types.
    private static readonly NVarcharSqlType ManagedName = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType ManagedDefault = NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit);

    private static readonly VarcharSqlType ManagedNullable = VarcharSqlType.Get(3, Collation.Catalog, Coercibility.Implicit);

    private static readonly NVarcharSqlType ManagedTypeDesc = NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit);

    private static readonly SqlType[] ManagedParams100Schema =
    [
        ManagedName, SqlType.SmallInt, SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt, SqlType.SmallInt,
        ManagedName, ManagedName, ManagedName, ManagedName, ManagedName, ManagedName, SqlType.Int32,
    ];

    private static readonly string[] ManagedParams100Names =
    [
        "PARAMETER_NAME", "PARAMETER_TYPE", "MANAGED_DATA_TYPE", "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE",
        "TYPE_CATALOG_NAME", "TYPE_SCHEMA_NAME", "TYPE_NAME", "XML_CATALOGNAME", "XML_SCHEMANAME", "XML_SCHEMACOLLECTIONNAME", "SS_DATETIME_PRECISION",
    ];

    private static readonly string[] ManagedParamsNames =
    [
        "PROCEDURE_CATALOG", "PROCEDURE_SCHEMA", "PROCEDURE_NAME", "PARAMETER_NAME", "ORDINAL_POSITION", "PARAMETER_TYPE", "PARAMETER_HASDEFAULT",
        "PARAMETER_DEFAULT", "IS_NULLABLE", "DATA_TYPE", "CHARACTER_MAXIMUM_LENGTH", "CHARACTER_OCTET_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE",
        "DESCRIPTION", "TYPE_NAME", "LOCAL_TYPE_NAME", "XML_CATALOGNAME", "XML_SCHEMANAME", "XML_SCHEMACOLLECTIONNAME", "UDT_CATALOG", "UDT_SCHEMA", "UDT_NAME",
    ];

    private static readonly SqlType[] ManagedColumnsSchema =
    [
        ManagedName, ManagedName, ManagedName, ManagedName, SqlType.Int32, ManagedDefault, ManagedNullable, ManagedName, SqlType.Int32, SqlType.Int32,
        SqlType.TinyInt, SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt, ManagedName, ManagedName, ManagedName, ManagedName, SqlType.Bit, SqlType.Bit, SqlType.Bit,
    ];

    private static readonly string[] ManagedColumnsNames =
    [
        "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "COLUMN_DEFAULT", "IS_NULLABLE", "DATA_TYPE",
        "CHARACTER_MAXIMUM_LENGTH", "CHARACTER_OCTET_LENGTH", "NUMERIC_PRECISION", "NUMERIC_PRECISION_RADIX", "NUMERIC_SCALE", "DATETIME_PRECISION",
        "CHARACTER_SET_CATALOG", "CHARACTER_SET_SCHEMA", "CHARACTER_SET_NAME", "COLLATION_CATALOG", "IS_SPARSE", "IS_COLUMN_SET", "IS_FILESTREAM",
    ];

    private static readonly SqlType[] ManagedIndexesSchema =
        [ManagedName, ManagedName, ManagedName, ManagedName, ManagedName, ManagedName, ManagedName, ManagedTypeDesc];

    private static readonly string[] ManagedIndexesNames =
        ["constraint_catalog", "constraint_schema", "constraint_name", "table_catalog", "table_schema", "table_name", "index_name", "type_desc"];

    private static readonly SqlType[] ManagedIndexColumnsSchema =
        [ManagedName, ManagedName, ManagedName, ManagedName, ManagedName, ManagedName, ManagedName, SqlType.Int32, SqlType.TinyInt, ManagedName];

    private static readonly string[] ManagedIndexColumnsNames =
    [
        "constraint_catalog", "constraint_schema", "constraint_name", "table_catalog", "table_schema", "table_name", "column_name",
        "ordinal_position", "KeyType", "index_name",
    ];

    /// <summary>One parameter a <c>_managed</c> procedure-parameters listing reports, with what its row needs beyond the rowset's description.</summary>
    private readonly struct ManagedParameter(string name, short ordinal, short kind, bool? nullable, RowsetParameterType described, SqlType type, TableType? tableType, XmlSchemaCollection? xmlCollection)
    {
        public readonly string Name = name;
        public readonly short Ordinal = ordinal;
        public readonly short Kind = kind;
        public readonly bool? Nullable = nullable;
        public readonly RowsetParameterType Described = described;
        public readonly SqlType Type = type;
        public readonly TableType? TableType = tableType;
        public readonly XmlSchemaCollection? XmlCollection = xmlCollection;

        /// <summary>A table-valued function's result.</summary>
        public bool IsTableResult => this.Described.Description is not null;
    }

    /// <summary>
    /// <c>sp_procedure_params_100_managed</c> (what SqlClient's
    /// <c>SqlCommandBuilder.DeriveParameters</c> calls) and
    /// <c>sp_procedure_params_managed</c>: a procedure's or function's return
    /// value and parameters, found as <c>OBJECT_ID</c> finds the name — the
    /// caller's default schema, then <c>dbo</c>, then <c>sys</c> when no
    /// schema is given — every group number of a numbered procedure for a
    /// NULL <c>@group_number</c> (probed 2026-10-16 against SQL Server 2025).
    /// The plain version carries the OLE DB <c>PROCEDURE_PARAMETERS</c>
    /// columns, the <c>_100</c> one the <c>SqlDbType</c> each parameter binds
    /// as; a table-valued parameter is listed with its type, a cursor as the
    /// <c>int</c> output real reports, and a <c>json</c> one not at all.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpProcedureParamsManaged(BatchContext batch, string procedure, string calledAs, bool v100)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments,
        [
            RowsetNameParameter("procedure_name", required: true), new("group_number", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
            RowsetNameParameter("procedure_schema"), RowsetNameParameter("parameter_name"),
        ]);
        var name = RowsetText(values[0]);
        int? group = values[1].IsNull ? null : values[1].AsInt32;
        var schema = RowsetText(values[2]);
        var parameterName = RowsetText(values[3]);

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        List<RowsetRoutine> routines = [];
        if (name is not null)
        {
            foreach (var candidate in schema is null ? (string[])[batch.DefaultSchemaName, Database.DefaultSchemaName, "sys"] : [schema])
            {
                routines = RowsetRoutines(batch, name, candidate, functions: true);
                if (routines.Count > 0)
                    break;
            }
        }

        var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
        var nullName = SqlValue.Null(RowsetName);
        SqlValue Name(string? value) => value is null ? nullName : SqlValue.FromNVarchar(RowsetName, value);
        var rows = new List<SqlValue[]>();
        foreach (var routine in routines)
        {
            if (group is { } number && number != routine.Number)
                continue;
            foreach (var parameter in ManagedParameters(routine))
            {
                if (parameterName is not null && !collation.Equals(parameterName, parameter.Name))
                    continue;
                var described = parameter.Described;
                var (typeCatalog, typeSchema) = parameter switch
                {
                    { TableType: { } tableType } => (database.Name, tableType.Schema.Name),
                    // A typed xml parameter's type schema reads as the
                    // routine's own (probed 2026-10-16 against SQL Server 2025).
                    { XmlCollection: not null } => (database.Name, routine.Schema),
                    _ => described.UdtType switch
                    {
                        ClrUdtSqlType udt => (database.Name, udt.Udt.Schema.Name),
                        { } => (database.Name, "sys"),
                        null => (null, null),
                    },
                };
                var typeName = parameter switch
                {
                    { TableType: { } tableType } => tableType.Name,
                    { Type: VectorSqlType } => "varbinary",
                    _ => described.TypeName,
                };
                var (xmlCatalog, xmlSchema, xmlName) = parameter.XmlCollection is { } collection
                    ? (database.Name, SchemaNameOf(database, collection.SchemaId), collection.Name)
                    : (null, null, null);
                static SqlValue Int(int? value) => value is { } v ? SqlValue.FromInt32(v) : SqlValue.Null(SqlType.Int32);
                static SqlValue Small(short? value) => value is { } v ? SqlValue.FromInt16(v) : SqlValue.Null(SqlType.SmallInt);
                if (v100)
                {
                    rows.Add([
                        Name(parameter.Name), SqlValue.FromInt16(parameter.Kind), SqlValue.FromInt16(ManagedDataType(parameter)),
                        Int(described.MaxLength), Small(described.Precision), Small(described.Scale),
                        Name(typeCatalog), Name(typeSchema), Name(typeName), Name(xmlCatalog), Name(xmlSchema), Name(xmlName),
                        Int(described.DatetimePrecision),
                    ]);
                    continue;
                }
                var row = RowsetParameterRow(database, catalog, SqlValue.FromNVarchar(RowsetName, routine.Schema),
                    SqlValue.FromNVarchar(RowsetName134, RoutineRowsetName(routine)), parameter.Name, parameter.Ordinal, parameter.Kind,
                    parameter.Nullable ?? false, described)[..23];
                if (parameter.Nullable is null)
                    row[8] = SqlValue.Null(SqlType.Bit);
                row[15] = row[16] = Name(typeName);
                row[17] = Name(xmlCatalog);
                row[18] = Name(xmlSchema);
                row[19] = Name(xmlName);
                rows.Add(row);
            }
        }
        yield return v100
            ? new SimulatedSqlResultSet(ManagedParams100Schema, ManagedParams100Names, rows)
            : new SimulatedSqlResultSet(RowsetProcedureParamsSchema[..23], ManagedParamsNames, rows);
    }

    /// <summary>
    /// The return value and parameters a <c>_managed</c> listing reports for
    /// <paramref name="routine"/>, in declaration order: a parameter of a
    /// numbered procedure past number 1 reports NULL nullability (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static List<ManagedParameter> ManagedParameters(RowsetRoutine routine)
    {
        var parameters = new List<ManagedParameter>();
        var returnValue = DescribeRowsetParameterType(SqlType.Int32, null, null, false, RowsetVersion.V100);
        bool? parameterNullable = routine.Number > 1 ? null : true;
        void Add(string name, short ordinal, SqlType type, int? declaredMaxLength, AliasType? alias, bool spelledNumeric, bool output, bool cursor, TableType? tableType, XmlSchemaCollection? xml)
        {
            if (type is JsonSqlType && !cursor)
                return;
            var described = cursor ? returnValue
                : tableType is not null ? new RowsetParameterType(143, null, null, null, null, tableType.Name, null)
                : DescribeRowsetParameterType(type, declaredMaxLength, alias, spelledNumeric, RowsetVersion.V100);
            parameters.Add(new("@" + name, ordinal, output ? (short)2 : (short)1, parameterNullable, described, cursor ? SqlType.Int32 : type, tableType, xml));
        }

        switch (routine)
        {
            case { Procedure: { } proc }:
                parameters.Add(new("@RETURN_VALUE", 0, 4, false, returnValue, SqlType.Int32, null, null));
                for (var i = 0; i < proc.Parameters.Length; i++)
                {
                    var p = proc.Parameters[i];
                    Add(p.Name, (short)(i + 1), p.Type, p.DeclaredMaxLength, p.AliasType, p.SpelledNumeric, p.IsOutput, p.IsCursor, p.TableType, p.XmlSchemaCollection);
                }
                break;
            case { Function: { } function }:
                var returnType = function switch
                {
                    ScalarFunction scalar => scalar.ReturnType,
                    ClrScalarFunction clrScalar => clrScalar.ReturnType,
                    _ => null,
                };
                parameters.Add(returnType is null
                    ? new("@TABLE_RETURN_VALUE", 0, 4, false, RowsetParameterType.Table, SqlType.SqlVariant, null, null)
                    : new("@RETURN_VALUE", 0, 4, true, DescribeRowsetParameterType(returnType, null, null, false, RowsetVersion.V100), returnType, null, null));
                for (var i = 0; i < function.Parameters.Length; i++)
                {
                    var p = function.Parameters[i];
                    Add(p.Name, (short)(i + 1), p.Type, p.DeclaredMaxLength, p.AliasType, p.SpelledNumeric, output: false, cursor: false, p.TableType, p.XmlSchemaCollection);
                }
                break;
            default:
                parameters.Add(new("@RETURN_VALUE", 0, 4, false, returnValue, SqlType.Int32, null, null));
                var systemParameters = routine.SystemParameters!;
                for (var i = 0; i < systemParameters.Length; i++)
                {
                    var p = systemParameters[i];
                    var type = SystemParameterType(p);
                    parameters.Add(new(p.Name, (short)(i + 1), p.IsOutput ? (short)2 : (short)1, true, DescribeRowsetParameterType(type, null, null, false, RowsetVersion.V100), type, null, null));
                }
                break;
        }
        return parameters;
    }

    /// <summary>
    /// The <c>SqlDbType</c> a parameter binds as, which
    /// <c>sp_procedure_params_100_managed</c> reports as
    /// <c>MANAGED_DATA_TYPE</c>: a <c>vector</c> as the <c>varbinary</c> it
    /// travels as, a table-valued function's result as <c>Variant</c> (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static short ManagedDataType(ManagedParameter parameter)
    {
        if (parameter.TableType is not null)
            return 30;
        if (parameter.IsTableResult)
            return 23;
        var type = parameter.Type;
        return type switch
        {
            _ when type == SqlType.BigInt => 0,
            BinarySqlType => 1,
            _ when type == SqlType.Bit => 2,
            CharSqlType => 3,
            _ when type == SqlType.DateTime => 4,
            DecimalSqlType => 5,
            _ when type == SqlType.Float => 6,
            _ when type == SqlType.Image => 7,
            _ when type == SqlType.Int32 => 8,
            _ when type == SqlType.Money => 9,
            NCharSqlType => 10,
            _ when type == SqlType.NText => 11,
            _ when type == SqlType.Real => 13,
            _ when type == SqlType.UniqueIdentifier => 14,
            _ when type == SqlType.SmallDateTime => 15,
            _ when type == SqlType.SmallInt => 16,
            _ when type == SqlType.SmallMoney => 17,
            _ when type == SqlType.Text => 18,
            _ when type == SqlType.RowVersion => 19,
            _ when type == SqlType.TinyInt => 20,
            VarbinarySqlType or VectorSqlType => 21,
            VarcharSqlType => 22,
            SqlVariantSqlType => 23,
            XmlSqlType => 25,
            HierarchyIdSqlType or SpatialSqlType or ClrUdtSqlType => 29,
            DateSqlType => 31,
            TimeSqlType => 32,
            DateTime2SqlType => 33,
            DateTimeOffsetSqlType => 34,
            _ => 12,
        };
    }

    private static string? SchemaNameOf(Database database, int schemaId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            if (schema.SchemaId == schemaId)
                return schema.Name;
        }
        return null;
    }

    /// <summary>
    /// <c>sp_columns_managed</c>, behind SqlClient's <c>GetSchema</c>
    /// <c>Columns</c> (<c>@SchemaType</c> 0), <c>AllColumns</c> (1) and
    /// <c>ColumnSetColumns</c> (2): the columns of the database's user tables
    /// and views, each argument a <c>LIKE</c> pattern under the database's
    /// collation, a sparse column left out of 0 and the only kind 2 lists, any
    /// other number listing nothing and a string that is none Msg 245 (probed
    /// 2026-10-16 against SQL Server 2025). Each column reads as
    /// <c>INFORMATION_SCHEMA.COLUMNS</c> reads it, save a <c>vector</c> as the
    /// <c>varbinary</c> it travels as and the collation's name, which isn't
    /// reported.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpColumnsManaged(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments,
        [
            RowsetNameParameter("Catalog"), RowsetNameParameter("Owner"), RowsetNameParameter("Table"), RowsetNameParameter("Column"),
            new("SchemaType", SqlType.NVarchar, 128, SqlValue.FromNVarchar("0")),
        ]);
        int? schemaType = null;
        if (RowsetText(values[4]) is { } written)
        {
            schemaType = int.TryParse(written.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw AtProcedureLine(SimulatedSqlException.ConversionFailedFromString(SqlType.NVarchar, written, SqlType.Int32), "sys.sp_columns_managed", 10);
        }

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        LikeMatcher? Pattern(SqlValue value) => RowsetText(value) is { } text ? LikeMatcher.Compile(text, escapeChar: null, collation, forPatIndex: false) : null;
        var (catalogPattern, ownerPattern, tablePattern, columnPattern) = (Pattern(values[0]), Pattern(values[1]), Pattern(values[2]), Pattern(values[3]));
        var rows = new List<(string Schema, string Table, int Ordinal, SqlValue[] Row)>();
        if (schemaType is 0 or 1 or 2 && Matches(catalogPattern, database.Name))
        {
            var visible = PermissionEnforcement.ObjectVisibility(batch, database);
            var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
            foreach (var (_, schema) in database.Schemas)
            {
                if (!Matches(ownerPattern, schema.Name))
                    continue;
                var owner = SqlValue.FromNVarchar(RowsetName, schema.Name);
                void AddColumns(SchemaObject source, string name, HeapColumn[] columns)
                {
                    if (!Matches(tablePattern, name) || visible?.Invoke(source) == false)
                        return;
                    var tableName = SqlValue.FromNVarchar(RowsetName, name);
                    for (var i = 0; i < columns.Length; i++)
                    {
                        var column = columns[i];
                        if ((schemaType == 0 && column.IsSparse) || (schemaType == 2 && !column.IsSparse) || !Matches(columnPattern, column.Name))
                            continue;
                        rows.Add((schema.Name, name, i + 1, ManagedColumnRow(database, catalog, owner, tableName, column, i + 1)));
                    }
                }
                foreach (var table in BuiltInResources.CatalogTables(schema, batch))
                    AddColumns(table, table.CatalogName, table.Columns);
                foreach (var (_, view) in schema.Views)
                    AddColumns(view, view.Name, view.CatalogColumns());
            }
        }
        // Real's own order follows its plan rather than an ORDER BY, which
        // moves a rowversion column ahead of its table's others; this lists
        // each table's columns by position.
        rows.Sort((a, b) =>
        {
            var bySchema = collation.Compare(a.Schema, b.Schema);
            if (bySchema != 0)
                return bySchema;
            var byTable = collation.Compare(a.Table, b.Table);
            return byTable != 0 ? byTable : a.Ordinal.CompareTo(b.Ordinal);
        });
        yield return new SimulatedSqlResultSet(ManagedColumnsSchema, ManagedColumnsNames, [.. rows.Select(static r => r.Row)]);
    }

    private static SqlValue[] ManagedColumnRow(Database database, SqlValue catalog, SqlValue owner, SqlValue tableName, HeapColumn column, int position)
    {
        var (charLength, octetLength, precision, radix, scale, datetimePrecision) = BuiltInResources.GetInformationSchemaColumnMetadata(column);
        var nullName = SqlValue.Null(RowsetName);
        var collationName = column.Collation ?? column.Type.Collation?.Name;
        var characterSet = !SqlType.IsCollatedString(column.Type) ? nullName
            : SqlType.IsNationalStringCategory(column.Type) ? SqlValue.FromNVarchar(RowsetName, "UNICODE")
            : SqlValue.FromNVarchar(RowsetName, BuiltInResources.CharacterSetName(collationName is null ? database.Collation : Collation.Get(collationName)));
        static SqlValue Int(int? value) => value is { } v ? SqlValue.FromInt32(v) : SqlValue.Null(SqlType.Int32);
        return
        [
            catalog, owner, tableName, SqlValue.FromNVarchar(RowsetName, column.Name), SqlValue.FromInt32(position),
            (column.DefaultConstraint?.Definition ?? column.BoundDefault?.DefinitionText) is { } defaultText
                ? SqlValue.FromNVarchar(RowsetText4000, defaultText) : SqlValue.Null(RowsetText4000),
            SqlValue.FromString(ManagedNullable, column.Nullable ? "YES" : "NO"),
            SqlValue.FromNVarchar(RowsetName, BuiltInResources.IsoRoutineDataTypeName(column.Type, column.SpelledNumeric).AsString),
            Int(charLength), Int(octetLength),
            precision is { } p ? SqlValue.FromByte(p) : SqlValue.Null(SqlType.TinyInt),
            radix is { } r ? SqlValue.FromInt16((short)r) : SqlValue.Null(SqlType.SmallInt),
            Int(scale),
            datetimePrecision is { } d ? SqlValue.FromInt16(d) : SqlValue.Null(SqlType.SmallInt),
            nullName, nullName, characterSet, nullName,
            SqlValue.FromBoolean(column.IsSparse), SqlValue.FromBoolean(column.IsColumnSet), SqlValue.FromBoolean(false),
        ];
    }

    /// <summary>
    /// <c>sp_indexes_managed</c> (<paramref name="columns"/> false) and
    /// <c>sp_indexcolumns_managed</c>, behind SqlClient's <c>GetSchema</c>
    /// <c>Indexes</c> and <c>IndexColumns</c>: every index of the database's
    /// user tables but the heap — an indexed view's aren't listed — each
    /// argument an exact name, ordered by table, index and column name; a
    /// column's <c>ordinal_position</c> is its key ordinal, 0 for an included
    /// one, and its <c>KeyType</c> its <c>system_type_id</c> (probed
    /// 2026-10-16 against SQL Server 2025). The rows come from the
    /// <c>sys.indexes</c> and <c>sys.index_columns</c> projections, so every
    /// kind of index those list reads the same here.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpIndexesManaged(BatchContext batch, string procedure, string calledAs, bool columns)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        SystemProcedureParameter[] parameters = columns
            ? [RowsetNameParameter("Catalog"), RowsetNameParameter("Owner"), RowsetNameParameter("Table"), RowsetNameParameter("ConstraintName"), RowsetNameParameter("Column")]
            : [RowsetNameParameter("Catalog"), RowsetNameParameter("Owner"), RowsetNameParameter("Table"), RowsetNameParameter("Name")];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var (catalogName, owner, tableFilter, indexFilter) = (RowsetText(values[0]), RowsetText(values[1]), RowsetText(values[2]), RowsetText(values[3]));
        var columnFilter = columns ? RowsetText(values[4]) : null;

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        bool Named(string? wanted, string candidate) => wanted is null || collation.Equals(wanted, candidate);
        var tables = new Dictionary<int, (string Schema, HeapTable Table)>();
        if (Named(catalogName, database.Name))
        {
            var visible = PermissionEnforcement.ObjectVisibility(batch, database);
            foreach (var (_, schema) in database.Schemas)
            {
                if (!Named(owner, schema.Name))
                    continue;
                foreach (var table in BuiltInResources.CatalogTables(schema, batch))
                {
                    if (Named(tableFilter, table.CatalogName) && visible?.Invoke(table) != false)
                        tables[table.ObjectId] = (schema.Name, table);
                }
            }
        }

        var indexes = new Dictionary<(int ObjectId, int IndexId), (string Name, string TypeDesc)>();
        if (tables.Count > 0)
        {
            var view = BuiltInResources.CatalogViews.Value["sys.indexes"];
            var (objectIdAt, nameAt, indexIdAt, typeAt, typeDescAt) = (ColumnAt(view, "object_id"), ColumnAt(view, "name"), ColumnAt(view, "index_id"), ColumnAt(view, "type"), ColumnAt(view, "type_desc"));
            foreach (var row in view.RowGenerator(batch, database))
            {
                if (row[typeAt].AsByte != 0 && tables.ContainsKey(row[objectIdAt].AsInt32) && Named(indexFilter, row[nameAt].AsString))
                    indexes[(row[objectIdAt].AsInt32, row[indexIdAt].AsInt32)] = (row[nameAt].AsString, row[typeDescAt].AsString);
            }
        }

        var catalog = SqlValue.FromNVarchar(RowsetName, database.Name);
        var rows = new List<(string Table, string Index, string Column, SqlValue[] Row)>();
        if (!columns)
        {
            foreach (var ((objectId, _), (name, typeDesc)) in indexes)
            {
                var (schemaName, table) = tables[objectId];
                var schema = SqlValue.FromNVarchar(RowsetName, schemaName);
                var index = SqlValue.FromNVarchar(RowsetName, name);
                rows.Add((table.CatalogName, name, "", [catalog, schema, index, catalog, schema, SqlValue.FromNVarchar(RowsetName, table.CatalogName), index, SqlValue.FromNVarchar(ManagedTypeDesc, typeDesc)]));
            }
        }
        else if (indexes.Count > 0)
        {
            var view = BuiltInResources.CatalogViews.Value["sys.index_columns"];
            var (objectIdAt, indexIdAt, columnIdAt, keyOrdinalAt) = (ColumnAt(view, "object_id"), ColumnAt(view, "index_id"), ColumnAt(view, "column_id"), ColumnAt(view, "key_ordinal"));
            foreach (var row in view.RowGenerator(batch, database))
            {
                var objectId = row[objectIdAt].AsInt32;
                if (!indexes.TryGetValue((objectId, row[indexIdAt].AsInt32), out var index))
                    continue;
                var (schemaName, table) = tables[objectId];
                var columnId = row[columnIdAt].AsInt32;
                if (Array.Find(table.Columns, candidate => candidate.ColumnId == columnId) is not { } column || !Named(columnFilter, column.Name))
                    continue;
                var schema = SqlValue.FromNVarchar(RowsetName, schemaName);
                var indexName = SqlValue.FromNVarchar(RowsetName, index.Name);
                rows.Add((table.CatalogName, index.Name, column.Name,
                [
                    catalog, schema, indexName, catalog, schema, SqlValue.FromNVarchar(RowsetName, table.CatalogName),
                    SqlValue.FromNVarchar(RowsetName, column.Name), SqlValue.FromInt32(row[keyOrdinalAt].AsByte), SqlValue.FromByte(column.SystemTypeId), indexName,
                ]));
            }
        }
        rows.Sort((a, b) =>
        {
            var byTable = collation.Compare(a.Table, b.Table);
            if (byTable != 0)
                return byTable;
            var byIndex = collation.Compare(a.Index, b.Index);
            return byIndex != 0 ? byIndex : collation.Compare(a.Column, b.Column);
        });
        yield return columns
            ? new SimulatedSqlResultSet(ManagedIndexColumnsSchema, ManagedIndexColumnsNames, [.. rows.Select(static r => r.Row)])
            : new SimulatedSqlResultSet(ManagedIndexesSchema, ManagedIndexesNames, [.. rows.Select(static r => r.Row)]);
    }

    private static int ColumnAt(CatalogView view, string name) =>
        Array.FindIndex(view.Columns, column => column.Name == name) is var at and >= 0 ? at : throw new InvalidOperationException($"{view.Name} has no {name} column.");
}
