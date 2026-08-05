using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The ODBC/JDBC catalog stored procedures: sp_tables (SQLTables) and
// sp_columns_100 (SQLColumns). JDBC's DatabaseMetaData.getTables /
// getColumns (Hibernate schema validation, generic tooling) call these on
// connect to enumerate the live catalog. Unlike sp_datatype_info_100's static
// type table, these project the current database's schema objects (user
// HeapTables + Views). Result-set shapes and per-column values are
// probe-confirmed against SQL Server 2025 (2026-07-23). The type facts that
// come from the ODBC type mapping (DATA_TYPE / SQL_DATA_TYPE / SQL_DATETIME_SUB
// / NUM_PREC_RADIX / the parameterless PRECISION) are read out of the shared
// sp_datatype_info_100 raw tables rather than re-derived; PRECISION for
// parameterized types, LENGTH, SCALE, CHAR_OCTET_LENGTH, and the legacy
// SS_DATA_TYPE token are computed per column.
partial class Simulation
{
    private static readonly VarcharSqlType CatalogVarchar32 =
        VarcharSqlType.Get(32, Collation.Baseline, Coercibility.Implicit);

    private static readonly VarcharSqlType CatalogVarchar254 =
        VarcharSqlType.Get(254, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType CatalogNVarchar4000 =
        NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.Implicit);

    private static readonly CharSqlType CatalogChar1 =
        CharSqlType.Get(1, Collation.Baseline, Coercibility.Implicit);

    private static readonly VarcharSqlType CatalogVarchar128 =
        VarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit);

    // sp_stored_procedures reports PROCEDURE_NAME as nvarchar(134): the 128-char
    // sysname plus the trailing ";<group-number>" (probe-confirmed shape).
    private static readonly NVarcharSqlType CatalogNVarchar134 =
        NVarcharSqlType.Get(134, Collation.Baseline, Coercibility.Implicit);

    // sp_tables: TABLE_QUALIFIER / TABLE_OWNER / TABLE_NAME (sysname),
    // TABLE_TYPE (varchar(32)), REMARKS (varchar(254)) — probe-confirmed shape.
    private static readonly SqlType[] SpTablesSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.SystemName,
        CatalogVarchar32, CatalogVarchar254,
    ];

    private static readonly string[] SpTablesColumnNames =
        ["TABLE_QUALIFIER", "TABLE_OWNER", "TABLE_NAME", "TABLE_TYPE", "REMARKS"];

    // sp_columns_100: the 29-column ODBC SQLColumns result set. Probe-confirmed
    // types — sysname for the four name columns + the SS_UDT/XML name columns;
    // smallint for DATA_TYPE / SCALE / RADIX / NULLABLE / SQL_DATA_TYPE /
    // SQL_DATETIME_SUB / the SS_IS_* flags; int for PRECISION / LENGTH /
    // CHAR_OCTET_LENGTH / ORDINAL_POSITION; varchar(254) for REMARKS /
    // IS_NULLABLE; nvarchar(4000) for COLUMN_DEF / SS_UDT_ASSEMBLY_TYPE_NAME;
    // tinyint for SS_DATA_TYPE.
    private static readonly SqlType[] SpColumnsSchema =
    [
        SqlType.SystemName, // TABLE_QUALIFIER
        SqlType.SystemName, // TABLE_OWNER
        SqlType.SystemName, // TABLE_NAME
        SqlType.SystemName, // COLUMN_NAME
        SqlType.SmallInt,   // DATA_TYPE
        SqlType.SystemName, // TYPE_NAME
        SqlType.Int32,      // PRECISION
        SqlType.Int32,      // LENGTH
        SqlType.SmallInt,   // SCALE
        SqlType.SmallInt,   // RADIX
        SqlType.SmallInt,   // NULLABLE
        CatalogVarchar254,  // REMARKS
        CatalogNVarchar4000, // COLUMN_DEF
        SqlType.SmallInt,   // SQL_DATA_TYPE
        SqlType.SmallInt,   // SQL_DATETIME_SUB
        SqlType.Int32,      // CHAR_OCTET_LENGTH
        SqlType.Int32,      // ORDINAL_POSITION
        CatalogVarchar254,  // IS_NULLABLE
        SqlType.SmallInt,   // SS_IS_SPARSE
        SqlType.SmallInt,   // SS_IS_COLUMN_SET
        SqlType.SmallInt,   // SS_IS_COMPUTED
        SqlType.SmallInt,   // SS_IS_IDENTITY
        SqlType.SystemName, // SS_UDT_CATALOG_NAME
        SqlType.SystemName, // SS_UDT_SCHEMA_NAME
        CatalogNVarchar4000, // SS_UDT_ASSEMBLY_TYPE_NAME
        SqlType.SystemName, // SS_XML_SCHEMACOLLECTION_CATALOG_NAME
        SqlType.SystemName, // SS_XML_SCHEMACOLLECTION_SCHEMA_NAME
        SqlType.SystemName, // SS_XML_SCHEMACOLLECTION_NAME
        SqlType.TinyInt,    // SS_DATA_TYPE
    ];

    private static readonly string[] SpColumnsColumnNames =
    [
        "TABLE_QUALIFIER", "TABLE_OWNER", "TABLE_NAME", "COLUMN_NAME", "DATA_TYPE",
        "TYPE_NAME", "PRECISION", "LENGTH", "SCALE", "RADIX", "NULLABLE", "REMARKS",
        "COLUMN_DEF", "SQL_DATA_TYPE", "SQL_DATETIME_SUB", "CHAR_OCTET_LENGTH",
        "ORDINAL_POSITION", "IS_NULLABLE", "SS_IS_SPARSE", "SS_IS_COLUMN_SET",
        "SS_IS_COMPUTED", "SS_IS_IDENTITY", "SS_UDT_CATALOG_NAME", "SS_UDT_SCHEMA_NAME",
        "SS_UDT_ASSEMBLY_TYPE_NAME", "SS_XML_SCHEMACOLLECTION_CATALOG_NAME",
        "SS_XML_SCHEMACOLLECTION_SCHEMA_NAME", "SS_XML_SCHEMACOLLECTION_NAME", "SS_DATA_TYPE",
    ];

    // sp_pkeys: TABLE_QUALIFIER / TABLE_OWNER / TABLE_NAME / COLUMN_NAME /
    // PK_NAME (sysname), KEY_SEQ (smallint) — probe-confirmed shape.
    private static readonly SqlType[] SpPkeysSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.SystemName,
        SqlType.SystemName, SqlType.SmallInt, SqlType.SystemName,
    ];

    private static readonly string[] SpPkeysColumnNames =
        ["TABLE_QUALIFIER", "TABLE_OWNER", "TABLE_NAME", "COLUMN_NAME", "KEY_SEQ", "PK_NAME"];

    // sp_statistics_100: the 13-column ODBC SQLStatistics result set.
    // Probe-confirmed types — sysname for the four name columns; smallint for
    // NON_UNIQUE / TYPE / SEQ_IN_INDEX; char(1) for COLLATION; int for
    // CARDINALITY / PAGES; varchar(128) for FILTER_CONDITION.
    private static readonly SqlType[] SpStatisticsSchema =
    [
        SqlType.SystemName, // TABLE_QUALIFIER
        SqlType.SystemName, // TABLE_OWNER
        SqlType.SystemName, // TABLE_NAME
        SqlType.SmallInt,   // NON_UNIQUE
        SqlType.SystemName, // INDEX_QUALIFIER
        SqlType.SystemName, // INDEX_NAME
        SqlType.SmallInt,   // TYPE
        SqlType.SmallInt,   // SEQ_IN_INDEX
        SqlType.SystemName, // COLUMN_NAME
        CatalogChar1,       // COLLATION
        SqlType.Int32,      // CARDINALITY
        SqlType.Int32,      // PAGES
        CatalogVarchar128,  // FILTER_CONDITION
    ];

    private static readonly string[] SpStatisticsColumnNames =
    [
        "TABLE_QUALIFIER", "TABLE_OWNER", "TABLE_NAME", "NON_UNIQUE", "INDEX_QUALIFIER",
        "INDEX_NAME", "TYPE", "SEQ_IN_INDEX", "COLUMN_NAME", "COLLATION", "CARDINALITY",
        "PAGES", "FILTER_CONDITION",
    ];

    // sp_stored_procedures: the 8-column ODBC SQLProcedures result set.
    // Probe-confirmed types — sysname for the two name columns, nvarchar(134)
    // for PROCEDURE_NAME, int for the three param/result counts, varchar(254)
    // for REMARKS, smallint for PROCEDURE_TYPE.
    private static readonly SqlType[] SpStoredProceduresSchema =
    [
        SqlType.SystemName, SqlType.SystemName, CatalogNVarchar134,
        SqlType.Int32, SqlType.Int32, SqlType.Int32, CatalogVarchar254, SqlType.SmallInt,
    ];

    private static readonly string[] SpStoredProceduresColumnNames =
    [
        "PROCEDURE_QUALIFIER", "PROCEDURE_OWNER", "PROCEDURE_NAME", "NUM_INPUT_PARAMS",
        "NUM_OUTPUT_PARAMS", "NUM_RESULT_SETS", "REMARKS", "PROCEDURE_TYPE",
    ];

    // ODBC type-mapping rows keyed by base type name, one index per version —
    // the same raw tables sp_datatype_info_100 serves, minus the identity /
    // sysname aliases (those carry the same DATA_TYPE as their base but a
    // duplicate name). sp_columns_100 reads DATA_TYPE, SQL_DATA_TYPE,
    // SQL_DATETIME_SUB, NUM_PREC_RADIX, and the parameterless PRECISION out of
    // these, so the two procs can't drift on the shared type facts.
    // Lazy so the build defers past static-init time: the source
    // DatatypeInfo* raw tables live in a sibling partial whose field
    // initializers have no ordering guarantee relative to this file's.
    private static readonly Lazy<FrozenDictionary<string, object?[]>> SpDatatypeInfoByNameV2 =
        new(() => BuildDatatypeInfoNameIndex(DatatypeInfoV2Raw!));

    private static readonly Lazy<FrozenDictionary<string, object?[]>> SpDatatypeInfoByNameV3 =
        new(() => BuildDatatypeInfoNameIndex(DatatypeInfoV3Raw!));

    private static FrozenDictionary<string, object?[]> BuildDatatypeInfoNameIndex(object?[][] raw)
    {
        var index = new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in raw)
        {
            var name = (string)row[0]!;
            if (name.Contains("identity", StringComparison.Ordinal) || name == "sysname")
                continue;
            _ = index.TryAdd(name, row);
        }

        return index.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Handles <c>EXEC sp_tables [@table_name] [, @table_owner]
    /// [, @table_qualifier] [, @table_type]</c> — the proc ODBC's
    /// <c>SQLTables</c> / JDBC's <c>getTables</c> call to enumerate the current
    /// database's tables and views. <c>@table_name</c> / <c>@table_owner</c>
    /// are LIKE patterns (NULL → all); <c>@table_qualifier</c> restricts to a
    /// database name (mismatch → empty); <c>@table_type</c> is a quoted
    /// comma-list (<c>"'TABLE','VIEW'"</c>). Rows sort by TABLE_TYPE,
    /// TABLE_QUALIFIER, TABLE_OWNER, TABLE_NAME (probe-confirmed).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTables(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (tableName, tableOwner, tableQualifier, tableType) = ParseSpTablesArgs(arguments);
        var database = batch.CurrentDatabase;
        var qualifier = SqlValue.FromSystemName(database.Name);
        var nullRemarks = SqlValue.Null(CatalogVarchar254);
        var tableTypeFilter = ParseTableTypeList(tableType);
        var namePattern = CompileCatalogPattern(tableName);
        var ownerPattern = CompileCatalogPattern(tableOwner);

        var rows = new List<SqlValue[]>();
        if (tableQualifier is null || batch.CurrentDatabase.Collation.Equals(tableQualifier, database.Name))
        {
            foreach (var schema in database.Schemas.Values)
            {
                if (!Matches(ownerPattern, schema.Name))
                    continue;
                var owner = SqlValue.FromSystemName(schema.Name);
                foreach (var table in schema.HeapTables.Values)
                    AddTableRow(rows, table.Name, "TABLE");
                foreach (var view in schema.Views.Values)
                    AddTableRow(rows, view.Name, "VIEW");

                void AddTableRow(List<SqlValue[]> into, string name, string type)
                {
                    if (!Matches(namePattern, name)
                        || (tableTypeFilter is not null && !tableTypeFilter.Contains(type)))
                    {
                        return;
                    }

                    into.Add([
                        qualifier, owner, SqlValue.FromSystemName(name),
                        SqlValue.FromString(CatalogVarchar32, type), nullRemarks,
                    ]);
                }
            }
        }

        rows.Sort(CompareSpTablesRows);
        yield return new SimulatedSqlResultSet(SpTablesSchema, SpTablesColumnNames, rows);
    }

    // Sort order: TABLE_TYPE, TABLE_QUALIFIER, TABLE_OWNER, TABLE_NAME — all
    // ordinal-ignore-case string comparisons on the projected cells.
    private static int CompareSpTablesRows(SqlValue[] a, SqlValue[] b)
    {
        for (var i = 3; ; i = i == 3 ? 0 : i + 1)
        {
            var cmp = string.Compare(a[i].AsString, b[i].AsString, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0)
                return cmp;
            if (i == 2)
                return 0;
        }
    }

    private static (string? Name, string? Owner, string? Qualifier, string? Type) ParseSpTablesArgs(
        List<ProcArgument> arguments)
    {
        string? name = null, owner = null, qualifier = null, type = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: name = CatalogStringArg(arg); break;
                    case 1: owner = CatalogStringArg(arg); break;
                    case 2: qualifier = CatalogStringArg(arg); break;
                    case 3: type = CatalogStringArg(arg); break;
                    case 4: break; // @fUsePattern — pattern mode is always on
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_tables");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "table_name"): name = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "table_owner"): owner = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "table_qualifier"): qualifier = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "table_type"): type = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "fUsePattern"): break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_tables");
            }
        }

        return (name, owner, qualifier, type);
    }

    /// <summary>
    /// Handles <c>EXEC sp_columns_100 [@table_name] [, @table_owner]
    /// [, @table_qualifier] [, @column_name] [, @ODBCVer]</c> — the proc ODBC's
    /// <c>SQLColumns</c> / JDBC's <c>getColumns</c> call. One row per column of
    /// every matching table / view, in ORDINAL_POSITION order.
    /// <c>@table_name</c> / <c>@table_owner</c> / <c>@column_name</c> are LIKE
    /// patterns; <c>@ODBCVer</c> (&lt; 3 → 2) selects the temporal DATA_TYPE
    /// codes / float-real precision the same way sp_datatype_info_100 does.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpColumns100(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (tableName, tableOwner, tableQualifier, columnName, odbcVer) = ParseSpColumnsArgs(arguments);
        var database = batch.CurrentDatabase;
        var qualifier = SqlValue.FromSystemName(database.Name);
        var byName = (odbcVer >= 3 ? SpDatatypeInfoByNameV3 : SpDatatypeInfoByNameV2).Value;
        var namePattern = CompileCatalogPattern(tableName);
        var ownerPattern = CompileCatalogPattern(tableOwner);
        var columnPattern = CompileCatalogPattern(columnName);

        var rows = new List<SqlValue[]>();
        if (tableQualifier is null || batch.CurrentDatabase.Collation.Equals(tableQualifier, database.Name))
        {
            foreach (var schema in database.Schemas.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!Matches(ownerPattern, schema.Name))
                    continue;
                var owner = SqlValue.FromSystemName(schema.Name);
                foreach (var table in schema.HeapTables.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (Matches(namePattern, table.Name))
                        AppendColumnRows(rows, qualifier, owner, table.Name, table.Columns, byName, columnPattern);
                }

                foreach (var view in schema.Views.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (Matches(namePattern, view.Name))
                        AppendColumnRows(rows, qualifier, owner, view.Name, view.OutputColumns, byName, columnPattern);
                }
            }
        }

        yield return new SimulatedSqlResultSet(SpColumnsSchema, SpColumnsColumnNames, rows);
    }

    private static void AppendColumnRows(
        List<SqlValue[]> rows, SqlValue qualifier, SqlValue owner, string tableName,
        HeapColumn[] columns, FrozenDictionary<string, object?[]> byName, LikeMatcher? columnPattern)
    {
        var tableNameValue = SqlValue.FromSystemName(tableName);
        for (var i = 0; i < columns.Length; i++)
        {
            var col = columns[i];
            if (!Matches(columnPattern, col.Name))
                continue;
            rows.Add(BuildSpColumnsRow(qualifier, owner, tableNameValue, col, i + 1, byName));
        }
    }

    private static SqlValue[] BuildSpColumnsRow(
        SqlValue qualifier, SqlValue owner, SqlValue tableName,
        HeapColumn col, int ordinal, FrozenDictionary<string, object?[]> byName)
    {
        var baseName = SpColumnsTypeName(col.Type);
        var row = byName[baseName];
        var (typePrecision, length, scale, charOctetLength) = SpColumnGeometry(col);
        var precision = typePrecision ?? (int)row[2]!;
        var isIdentity = col.Identity is not null;
        var isComputed = col.Computed is not null;

        SqlValue Smallint(object? cell) =>
            cell is null ? SqlValue.Null(SqlType.SmallInt) : SqlValue.FromInt16((short)(int)cell);
        SqlValue NullableInt(int? value) =>
            value is { } v ? SqlValue.FromInt32(v) : SqlValue.Null(SqlType.Int32);
        SqlValue Flag(bool value) => SqlValue.FromInt16(value ? (short)1 : (short)0);

        return
        [
            qualifier,
            owner,
            tableName,
            SqlValue.FromSystemName(col.Name),
            SqlValue.FromInt16((short)(int)row[1]!),                              // DATA_TYPE
            SqlValue.FromSystemName(isIdentity ? baseName + " identity" : baseName), // TYPE_NAME
            SqlValue.FromInt32(precision),                                       // PRECISION
            SqlValue.FromInt32(length),                                          // LENGTH
            scale is { } s ? SqlValue.FromInt16((short)s) : SqlValue.Null(SqlType.SmallInt), // SCALE
            Smallint(row[17]),                                                   // RADIX
            Flag(col.Nullable),                                                  // NULLABLE
            SqlValue.Null(CatalogVarchar254),                                    // REMARKS
            SpColumnDefault(col),                                                // COLUMN_DEF
            SqlValue.FromInt16((short)(int)row[15]!),                            // SQL_DATA_TYPE
            Smallint(row[16]),                                                   // SQL_DATETIME_SUB
            NullableInt(charOctetLength),                                        // CHAR_OCTET_LENGTH
            SqlValue.FromInt32(ordinal),                                         // ORDINAL_POSITION
            SqlValue.FromString(CatalogVarchar254, col.Nullable ? "YES" : "NO"), // IS_NULLABLE
            Flag(false),                                                         // SS_IS_SPARSE
            Flag(false),                                                         // SS_IS_COLUMN_SET
            Flag(isComputed),                                                    // SS_IS_COMPUTED
            Flag(isIdentity),                                                    // SS_IS_IDENTITY
            SqlValue.Null(SqlType.SystemName),                                   // SS_UDT_CATALOG_NAME
            SqlValue.Null(SqlType.SystemName),                                   // SS_UDT_SCHEMA_NAME
            SqlValue.Null(CatalogNVarchar4000),                                  // SS_UDT_ASSEMBLY_TYPE_NAME
            SqlValue.Null(SqlType.SystemName),                                   // SS_XML_SCHEMACOLLECTION_CATALOG_NAME
            SqlValue.Null(SqlType.SystemName),                                   // SS_XML_SCHEMACOLLECTION_SCHEMA_NAME
            SqlValue.Null(SqlType.SystemName),                                   // SS_XML_SCHEMACOLLECTION_NAME
            SqlValue.FromByte(SpColumnsSsDataType(col.Type, col.Nullable)),      // SS_DATA_TYPE
        ];
    }

    private static SqlValue SpColumnDefault(HeapColumn col) =>
        col.DefaultConstraint?.Definition is { } def
            ? SqlValue.FromString(CatalogNVarchar4000, def)
            : SqlValue.Null(CatalogNVarchar4000);

    // The ODBC/legacy base type name — the key into the sp_datatype_info raw
    // tables. numeric collapses onto decimal (the simulator's DecimalSqlType
    // does not distinguish the two spellings); sysname reports as nvarchar.
    private static string SpColumnsTypeName(SqlType type) => type switch
    {
        _ when type == SqlType.Bit => "bit",
        _ when type == SqlType.TinyInt => "tinyint",
        _ when type == SqlType.SmallInt => "smallint",
        _ when type == SqlType.Int32 => "int",
        _ when type == SqlType.BigInt => "bigint",
        DecimalSqlType => "decimal",
        _ when type == SqlType.Money => "money",
        _ when type == SqlType.SmallMoney => "smallmoney",
        _ when type == SqlType.Float => "float",
        _ when type == SqlType.Real => "real",
        _ when type == SqlType.Date => "date",
        _ when type == SqlType.SmallDateTime => "smalldatetime",
        _ when type == SqlType.DateTime => "datetime",
        DateTime2SqlType => "datetime2",
        TimeSqlType => "time",
        DateTimeOffsetSqlType => "datetimeoffset",
        _ when type == SqlType.UniqueIdentifier => "uniqueidentifier",
        _ when type == SqlType.RowVersion => "timestamp",
        _ when type == SqlType.Text => "text",
        _ when type == SqlType.NText => "ntext",
        _ when type == SqlType.Image => "image",
        _ when type == SqlType.SystemName => "nvarchar",
        CharSqlType => "char",
        NCharSqlType => "nchar",
        VarcharSqlType => "varchar",
        NVarcharSqlType => "nvarchar",
        BinarySqlType => "binary",
        VarbinarySqlType => "varbinary",
        XmlSqlType => "xml",
        SqlVariantSqlType => "sql_variant",
        _ => throw new NotSupportedException($"sp_columns_100 does not model {type} columns."),
    };

    // Per-column geometry: PRECISION (null → take the parameterless value from
    // the sp_datatype_info row), LENGTH, SCALE, CHAR_OCTET_LENGTH. All
    // probe-confirmed against SQL Server 2025. LENGTH is the ODBC transfer
    // size in bytes (storage bytes for numerics, the *_STRUCT size for
    // date/time, byte width for strings/binary, precision+2 for decimal/money);
    // CHAR_OCTET_LENGTH is set only for string / binary / LOB / xml types
    // (where it equals LENGTH) and NULL otherwise.
    private static (int? Precision, int Length, int? Scale, int? CharOctetLength) SpColumnGeometry(HeapColumn col) =>
        col.Type switch
        {
            _ when col.Type == SqlType.Bit => (null, 1, null, null),
            _ when col.Type == SqlType.TinyInt => (null, 1, 0, null),
            _ when col.Type == SqlType.SmallInt => (null, 2, 0, null),
            _ when col.Type == SqlType.Int32 => (null, 4, 0, null),
            _ when col.Type == SqlType.BigInt => (null, 8, 0, null),
            DecimalSqlType d => (d.precision, d.precision + 2, d.scale, null),
            _ when col.Type == SqlType.Money => (null, 21, 4, null),
            _ when col.Type == SqlType.SmallMoney => (null, 12, 4, null),
            _ when col.Type == SqlType.Float => (null, 8, null, null),
            _ when col.Type == SqlType.Real => (null, 4, null, null),
            _ when col.Type == SqlType.Date => (null, 6, 0, null),
            _ when col.Type == SqlType.SmallDateTime => (null, 16, 0, null),
            _ when col.Type == SqlType.DateTime => (null, 16, 3, null),
            DateTime2SqlType dt2 => (ScaledTemporalPrecision(19, dt2.precision), 16, dt2.precision, null),
            TimeSqlType tm => (ScaledTemporalPrecision(8, tm.precision), 12, tm.precision, null),
            DateTimeOffsetSqlType dto => (ScaledTemporalPrecision(26, dto.precision), 20, dto.precision, null),
            _ when col.Type == SqlType.UniqueIdentifier => (null, 16, null, null),
            _ when col.Type == SqlType.RowVersion => (null, 8, null, 8),
            _ when col.Type == SqlType.Text => (null, 2147483647, null, 2147483647),
            _ when col.Type == SqlType.NText => (null, 2147483646, null, 2147483646),
            _ when col.Type == SqlType.Image => (null, 2147483647, null, 2147483647),
            XmlSqlType => (null, 0, null, 0),
            _ when col.Type == SqlType.SystemName => (128, 256, null, 256),
            CharSqlType c => (c.length, c.length, null, c.length),
            NCharSqlType nc => (nc.length, nc.length * 2, null, nc.length * 2),
            VarcharSqlType vc => vc.length < 1 ? (0, 0, null, 0) : (vc.length, vc.length, null, vc.length),
            NVarcharSqlType nv => nv.length < 1 ? (0, 0, null, 0) : (nv.length, nv.length * 2, null, nv.length * 2),
            BinarySqlType bn => (bn.length, bn.length, null, bn.length),
            VarbinarySqlType vb => vb.length < 1 ? (0, 0, null, 0) : (vb.length, vb.length, null, vb.length),
            SqlVariantSqlType => (null, 8000, null, null),
            _ => throw new NotSupportedException($"sp_columns_100 does not model {col.Type} columns."),
        };

    /// <summary>
    /// The rendered width of a fractional-second temporal type — the ODBC
    /// display precision <c>sp_columns_100</c> and <c>sp_help</c> both report.
    /// A zero scale renders no fractional part, so it stays at the base width;
    /// any other scale adds its digits plus the decimal point.
    /// </summary>
    internal static int ScaledTemporalPrecision(int baseWidth, int scale) =>
        scale == 0 ? baseWidth : baseWidth + scale + 1;

    // SS_DATA_TYPE: the legacy tabular-storage token (old syscolumns.type).
    // Integer / exact-numeric / approximate / money / datetime types switch to
    // their nullable ("N") variant when the column allows NULL; string /
    // binary / uniqueidentifier / bit / the fraction-second temporal types and
    // xml carry one token regardless. Probe-confirmed against SQL Server 2025;
    // bigint's 63 / 108 pairing is a documented sp_columns quirk.
    private static byte SpColumnsSsDataType(SqlType type, bool nullable)
    {
        var (notNull, orNull) = type switch
        {
            _ when type == SqlType.Bit => (50, 50),
            _ when type == SqlType.TinyInt => (48, 38),
            _ when type == SqlType.SmallInt => (52, 38),
            _ when type == SqlType.Int32 => (56, 38),
            _ when type == SqlType.BigInt => (63, 108),
            DecimalSqlType => (55, 106),
            _ when type == SqlType.Money => (60, 110),
            _ when type == SqlType.SmallMoney => (122, 110),
            _ when type == SqlType.Float => (62, 109),
            _ when type == SqlType.Real => (59, 109),
            _ when type == SqlType.DateTime => (61, 111),
            _ when type == SqlType.SmallDateTime => (58, 111),
            _ when type == SqlType.Date => (0, 0),
            TimeSqlType => (0, 0),
            DateTime2SqlType => (0, 0),
            DateTimeOffsetSqlType => (0, 0),
            _ when type == SqlType.UniqueIdentifier => (37, 37),
            _ when type == SqlType.RowVersion => (37, 37),
            _ when type == SqlType.Text => (35, 35),
            _ when type == SqlType.NText => (35, 35),
            _ when type == SqlType.Image => (34, 34),
            XmlSqlType => (0, 0),
            SqlVariantSqlType => (98, 98),
            CharSqlType or NCharSqlType or VarcharSqlType or NVarcharSqlType => (39, 39),
            _ when type == SqlType.SystemName => (39, 39),
            BinarySqlType or VarbinarySqlType => (37, 37),
            _ => (0, 0),
        };

        return (byte)(nullable ? orNull : notNull);
    }

    private static (string? Name, string? Owner, string? Qualifier, string? Column, int OdbcVer) ParseSpColumnsArgs(
        List<ProcArgument> arguments)
    {
        string? name = null, owner = null, qualifier = null, column = null;
        var odbcVer = 2;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: name = CatalogStringArg(arg); break;
                    case 1: owner = CatalogStringArg(arg); break;
                    case 2: qualifier = CatalogStringArg(arg); break;
                    case 3: column = CatalogStringArg(arg); break;
                    case 4: odbcVer = CatalogOdbcVer(arg); break;
                    case 5: break; // @fUsePattern — pattern mode is always on
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_columns_100");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "table_name"): name = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "table_owner"): owner = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "table_qualifier"): qualifier = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "column_name"): column = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "ODBCVer"): odbcVer = CatalogOdbcVer(arg); break;
                case var n when BuiltInToken.Equals(n, "fUsePattern"): break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_columns_100");
            }
        }

        return (name, owner, qualifier, column, odbcVer < 3 ? 2 : 3);
    }

    /// <summary>
    /// Handles <c>EXEC sp_pkeys @table_name [, @table_owner]
    /// [, @table_qualifier]</c> — the proc ODBC's <c>SQLPrimaryKeys</c> /
    /// JDBC's <c>getPrimaryKeys</c> call. One row per primary-key column of the
    /// named table, in key order; <c>KEY_SEQ</c> is the 1-based position within
    /// the key and <c>PK_NAME</c> is the constraint name. <c>@table_name</c> /
    /// <c>@table_owner</c> are exact identifiers (not LIKE patterns —
    /// probe-confirmed: a wildcard matches nothing); a table with no PRIMARY KEY
    /// yields zero rows.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpPkeys(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (tableName, tableOwner, tableQualifier) = ParseSpPkeysArgs(arguments);
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var qualifier = SqlValue.FromSystemName(database.Name);

        var rows = new List<SqlValue[]>();
        if (tableName is not null && (tableQualifier is null || collation.Equals(tableQualifier, database.Name)))
        {
            foreach (var schema in database.Schemas.Values)
            {
                if (tableOwner is not null && !collation.Equals(tableOwner, schema.Name))
                    continue;
                if (!schema.HeapTables.TryGetValue(tableName, out var table) || table.IsTableVariable)
                    continue;

                var pk = FindPrimaryKey(table);
                if (pk is null)
                    continue;

                var owner = SqlValue.FromSystemName(schema.Name);
                var tableNameValue = SqlValue.FromSystemName(table.Name);
                var pkName = SqlValue.FromSystemName(pk.Name);
                for (var i = 0; i < pk.StorageOrdinals.Length; i++)
                {
                    var column = table.StoredColumns[pk.StorageOrdinals[i]];
                    rows.Add([
                        qualifier, owner, tableNameValue,
                        SqlValue.FromSystemName(column.Name),
                        SqlValue.FromInt16((short)(i + 1)), pkName,
                    ]);
                }
            }
        }

        yield return new SimulatedSqlResultSet(SpPkeysSchema, SpPkeysColumnNames, rows);
    }

    private static KeyConstraint? FindPrimaryKey(HeapTable table)
    {
        foreach (var constraint in table.KeyConstraints)
        {
            if (constraint.Kind == KeyConstraintKind.PrimaryKey)
                return constraint;
        }

        return null;
    }

    private static (string? Name, string? Owner, string? Qualifier) ParseSpPkeysArgs(List<ProcArgument> arguments)
    {
        string? name = null, owner = null, qualifier = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: name = CatalogStringArg(arg); break;
                    case 1: owner = CatalogStringArg(arg); break;
                    case 2: qualifier = CatalogStringArg(arg); break;
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_pkeys");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "table_name"): name = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "table_owner"): owner = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "table_qualifier"): qualifier = CatalogStringArg(arg); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_pkeys");
            }
        }

        return (name, owner, qualifier);
    }

    /// <summary>
    /// Handles <c>EXEC sp_statistics_100 @table_name [, @table_owner]
    /// [, @table_qualifier] [, @index_name] [, @is_unique] [, @accuracy]
    /// [, @ODBCVer]</c> — the proc ODBC's <c>SQLStatistics</c> / JDBC's
    /// <c>getIndexInfo</c> call. Emits a table-cardinality summary row first
    /// (<c>TYPE = 0</c>, index columns NULL), then one row per index key column
    /// (<c>TYPE = 1</c> clustered / <c>3</c> nonclustered, <c>COLLATION</c>
    /// 'A'/'D', <c>NON_UNIQUE</c> 0/1). <c>@table_name</c> is an exact
    /// identifier; <c>@index_name</c> is a LIKE pattern over the index rows
    /// (a NULL / omitted value emits the summary row alone — probe-confirmed);
    /// <c>@is_unique = 'Y'</c> restricts to unique indexes (the summary row is
    /// always emitted). <c>CARDINALITY</c> is the
    /// live row count for the table / clustered index (NULL for nonclustered)
    /// and <c>PAGES</c> is the heap's data-page count — an approximation, since
    /// the simulator keeps no separate clustered-index or statistics storage.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpStatistics100(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (tableName, tableOwner, tableQualifier, indexName, uniqueOnly) = ParseSpStatisticsArgs(arguments);
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var qualifier = SqlValue.FromSystemName(database.Name);

        var rows = new List<SqlValue[]>();
        if (tableName is not null && (tableQualifier is null || collation.Equals(tableQualifier, database.Name)))
        {
            foreach (var schema in database.Schemas.Values)
            {
                if (tableOwner is not null && !collation.Equals(tableOwner, schema.Name))
                    continue;
                if (!schema.HeapTables.TryGetValue(tableName, out var table) || table.IsTableVariable)
                    continue;

                AppendStatisticsRows(rows, qualifier, schema.Name, table, indexName, uniqueOnly);
            }
        }

        yield return new SimulatedSqlResultSet(SpStatisticsSchema, SpStatisticsColumnNames, rows);
    }

    private static void AppendStatisticsRows(
        List<SqlValue[]> rows, SqlValue qualifier, string schemaName, HeapTable table,
        string? indexName, bool uniqueOnly)
    {
        var owner = SqlValue.FromSystemName(schemaName);
        var tableNameValue = SqlValue.FromSystemName(table.Name);
        var indexQualifier = SqlValue.FromSystemName(table.Name);
        var cardinality = SqlValue.FromInt32(table.Heap.RowCount);
        var pages = SqlValue.FromInt32(table.Heap.Pages.Count);
        var nullShort = SqlValue.Null(SqlType.SmallInt);
        var nullName = SqlValue.Null(SqlType.SystemName);
        var nullChar = SqlValue.Null(CatalogChar1);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullFilter = SqlValue.Null(CatalogVarchar128);

        // Table-cardinality summary row (SQL_TABLE_STAT): TYPE 0, every index
        // column NULL, CARDINALITY / PAGES from the live heap.
        rows.Add([
            qualifier, owner, tableNameValue, nullShort, nullName, nullName,
            SqlValue.FromInt16(0), nullShort, nullName, nullChar, cardinality, pages, nullFilter,
        ]);

        // @index_name is a LIKE pattern applied to the index rows; a NULL /
        // omitted @index_name emits the summary row alone (probe-confirmed:
        // JDBC getIndexInfo passes '%' to get every index, and no argument
        // yields the summary only). The summary row above is always present.
        if (indexName is null)
            return;
        var indexPattern = CompileCatalogPattern(indexName);

        var indexRows = new List<SqlValue[]>();
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.IsHeap || !Matches(indexPattern, identity.Name!))
                continue;

            var isClustered = identity.Type == 1;
            var (nonUnique, keyColumns) = StatisticsKeyColumns(table, identity);
            if (uniqueOnly && nonUnique != 0)
                continue;

            var type = SqlValue.FromInt16(isClustered ? (short)1 : (short)3);
            var nonUniqueValue = SqlValue.FromInt16((short)nonUnique);
            var indexNameValue = SqlValue.FromSystemName(identity.Name!);
            var rowCardinality = isClustered ? cardinality : nullInt;
            var rowPages = isClustered ? pages : nullInt;
            var filter = identity.Index?.FilterDefinition is { } fd
                ? SqlValue.FromString(CatalogVarchar128, fd)
                : nullFilter;

            for (var i = 0; i < keyColumns.Length; i++)
            {
                var (columnName, descending) = keyColumns[i];
                indexRows.Add([
                    qualifier, owner, tableNameValue, nonUniqueValue, indexQualifier,
                    indexNameValue, type, SqlValue.FromInt16((short)(i + 1)),
                    SqlValue.FromSystemName(columnName),
                    SqlValue.FromString(CatalogChar1, descending ? "D" : "A"),
                    rowCardinality, rowPages, filter,
                ]);
            }
        }

        // ODBC SQLStatistics row order: NON_UNIQUE, TYPE, INDEX_NAME,
        // SEQ_IN_INDEX (all ascending). The summary row stays first.
        indexRows.Sort(CompareSpStatisticsRows);
        rows.AddRange(indexRows);
    }

    // A constraint-backed index (PRIMARY KEY / UNIQUE) is always unique with
    // ascending key columns; a CREATE INDEX-backed one carries its own UNIQUE
    // flag and per-column ASC / DESC direction.
    private static (int NonUnique, (string Name, bool Descending)[] KeyColumns) StatisticsKeyColumns(
        HeapTable table, IndexIdentity identity)
    {
        if (identity.Constraint is { } constraint)
        {
            var columns = new (string, bool)[constraint.StorageOrdinals.Length];
            for (var i = 0; i < columns.Length; i++)
                columns[i] = (table.StoredColumns[constraint.StorageOrdinals[i]].Name, false);
            return (0, columns);
        }

        var index = identity.Index!;
        var keyColumns = new (string, bool)[index.KeyColumns.Length];
        for (var i = 0; i < keyColumns.Length; i++)
        {
            var key = index.KeyColumns[i];
            keyColumns[i] = (table.Columns[key.ColumnOrdinal].Name, key.IsDescending);
        }

        return (index.IsUnique ? 0 : 1, keyColumns);
    }

    // Sort by NON_UNIQUE (cell 3), TYPE (cell 6), INDEX_NAME (cell 5),
    // SEQ_IN_INDEX (cell 7) — the ODBC-documented order; every cell is non-NULL
    // on index rows.
    private static int CompareSpStatisticsRows(SqlValue[] a, SqlValue[] b)
    {
        var cmp = a[3].AsInt16.CompareTo(b[3].AsInt16);
        if (cmp != 0)
            return cmp;
        cmp = a[6].AsInt16.CompareTo(b[6].AsInt16);
        if (cmp != 0)
            return cmp;
        cmp = string.Compare(a[5].AsString, b[5].AsString, StringComparison.OrdinalIgnoreCase);
        return cmp != 0 ? cmp : a[7].AsInt16.CompareTo(b[7].AsInt16);
    }

    private static (string? Name, string? Owner, string? Qualifier, string? IndexName, bool UniqueOnly) ParseSpStatisticsArgs(
        List<ProcArgument> arguments)
    {
        string? name = null, owner = null, qualifier = null, indexName = null;
        var uniqueOnly = false;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: name = CatalogStringArg(arg); break;
                    case 1: owner = CatalogStringArg(arg); break;
                    case 2: qualifier = CatalogStringArg(arg); break;
                    case 3: indexName = CatalogStringArg(arg); break;
                    case 4: uniqueOnly = CatalogIsUnique(arg); break;
                    case 5: break; // @accuracy — no live statistics to tune
                    case 6: break; // @ODBCVer — result shape is version-invariant
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_statistics_100");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "table_name"): name = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "table_owner"): owner = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "table_qualifier"): qualifier = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "index_name"): indexName = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "is_unique"): uniqueOnly = CatalogIsUnique(arg); break;
                case var n when BuiltInToken.Equals(n, "accuracy"): break;
                case var n when BuiltInToken.Equals(n, "ODBCVer"): break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_statistics_100");
            }
        }

        return (name, owner, qualifier, indexName, uniqueOnly);
    }

    // @is_unique = 'Y' restricts to unique indexes; anything else (default 'N')
    // returns all indexes.
    private static bool CatalogIsUnique(ProcArgument arg)
    {
        var value = CatalogStringArg(arg)?.Trim();
        return value is { Length: > 0 } && (value[0] == 'Y' || value[0] == 'y');
    }

    /// <summary>
    /// Handles <c>EXEC sp_stored_procedures [@sp_name] [, @sp_owner]
    /// [, @sp_qualifier] [, @fUsePattern]</c> — the proc ODBC's
    /// <c>SQLProcedures</c> / JDBC's <c>getProcedures</c> call. One row per
    /// user stored procedure in the current database; <c>PROCEDURE_NAME</c>
    /// carries the trailing <c>;1</c> group number, the three param/result
    /// counts report <c>-1</c> (real doesn't compute them), and
    /// <c>PROCEDURE_TYPE</c> is 2 (SQL_PT_PROCEDURE). <c>@sp_name</c> /
    /// <c>@sp_owner</c> are LIKE patterns. The simulator has no system-procedure
    /// catalog, so (unlike real, which also lists the ~1600 <c>sys</c> procs)
    /// only user procedures are projected.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpStoredProcedures(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (spName, spOwner, spQualifier) = ParseSpStoredProceduresArgs(arguments);
        var database = batch.CurrentDatabase;
        var qualifier = SqlValue.FromSystemName(database.Name);
        var namePattern = CompileCatalogPattern(spName);
        var ownerPattern = CompileCatalogPattern(spOwner);
        var negativeOne = SqlValue.FromInt32(-1);
        var nullRemarks = SqlValue.Null(CatalogVarchar254);
        var procedureType = SqlValue.FromInt16(2);

        var rows = new List<SqlValue[]>();
        if (spQualifier is null || database.Collation.Equals(spQualifier, database.Name))
        {
            foreach (var schema in database.Schemas.Values)
            {
                if (!Matches(ownerPattern, schema.Name))
                    continue;
                var owner = SqlValue.FromSystemName(schema.Name);
                foreach (var procedure in schema.Procedures.Values)
                {
                    if (!Matches(namePattern, procedure.Name))
                        continue;
                    rows.Add([
                        qualifier, owner,
                        SqlValue.FromString(CatalogNVarchar134, procedure.Name + ";1"),
                        negativeOne, negativeOne, negativeOne, nullRemarks, procedureType,
                    ]);
                }
            }
        }

        rows.Sort(CompareSpStoredProceduresRows);
        yield return new SimulatedSqlResultSet(SpStoredProceduresSchema, SpStoredProceduresColumnNames, rows);
    }

    // Sort by PROCEDURE_OWNER (cell 1) then PROCEDURE_NAME (cell 2), ordinal
    // case-insensitive.
    private static int CompareSpStoredProceduresRows(SqlValue[] a, SqlValue[] b)
    {
        var cmp = string.Compare(a[1].AsString, b[1].AsString, StringComparison.OrdinalIgnoreCase);
        return cmp != 0 ? cmp : string.Compare(a[2].AsString, b[2].AsString, StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Name, string? Owner, string? Qualifier) ParseSpStoredProceduresArgs(
        List<ProcArgument> arguments)
    {
        string? name = null, owner = null, qualifier = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                switch (positional++)
                {
                    case 0: name = CatalogStringArg(arg); break;
                    case 1: owner = CatalogStringArg(arg); break;
                    case 2: qualifier = CatalogStringArg(arg); break;
                    case 3: break; // @fUsePattern — pattern mode is always on
                    default: throw SimulatedSqlException.InvalidProcedureParameters("sp_stored_procedures");
                }

                continue;
            }

            switch (arg.Name)
            {
                case var n when BuiltInToken.Equals(n, "sp_name"): name = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "sp_owner"): owner = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "sp_qualifier"): qualifier = CatalogStringArg(arg); break;
                case var n when BuiltInToken.Equals(n, "fUsePattern"): break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_stored_procedures");
            }
        }

        return (name, owner, qualifier);
    }

    private static string? CatalogStringArg(ProcArgument arg) =>
        arg.IsDefault || arg.Value.IsNull ? null : arg.Value.CoerceTo(SqlType.NVarchar).AsString;

    private static int CatalogOdbcVer(ProcArgument arg) =>
        arg.IsDefault || arg.Value.IsNull ? 2 : ScalarArguments.CoerceProcedureParameter(arg.Value, SqlType.Int32);

    // A bit-declared system-proc flag (sp_spaceused's @oneresultset /
    // @include_total_xtp_storage): omitted / NULL is the declared default 0.
    private static bool CatalogFlagArg(ProcArgument arg) =>
        !arg.IsDefault && !arg.Value.IsNull && ScalarArguments.CoerceProcedureParameter(arg.Value, SqlType.Bit) != 0;

    // Parses @table_type's quoted comma-list ("'TABLE','VIEW'") into an
    // upper-case set; null / empty means "all types".
    private static HashSet<string>? ParseTableTypeList(string? tableType)
    {
        if (string.IsNullOrWhiteSpace(tableType))
            return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in tableType.Split(','))
        {
            var trimmed = part.Trim().Trim('\'', '"').Trim();
            if (trimmed.Length > 0)
                _ = set.Add(trimmed);
        }

        return set.Count == 0 ? null : set;
    }

    // A null pattern matches everything; otherwise a T-SQL LIKE pattern
    // compiled under the baseline collation (catalog names fold the way
    // sp_tables / sp_columns compare owners and names). The arguments are
    // nvarchar sysname on real, so the match takes no trailing-space slack.
    private static LikeMatcher? CompileCatalogPattern(string? pattern) =>
        pattern is null ? null : LikeMatcher.Compile(pattern, escapeChar: null, Collation.Baseline, forPatIndex: false);

    private static bool Matches(LikeMatcher? pattern, string value) =>
        pattern is null || pattern.IsMatch(value, trailingSpaceSlack: false);
}
