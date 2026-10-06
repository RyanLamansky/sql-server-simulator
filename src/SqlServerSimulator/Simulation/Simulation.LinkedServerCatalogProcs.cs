using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The catalog procedures that describe linked servers rather than reach
// through one: sp_linkedservers, sp_testlinkedserver and sp_catalogs, and the
// provider's schema rowsets sp_tables_ex and sp_columns_ex (probed 2026-10-06
// against SQL Server 2025).
partial class Simulation
{
    private static readonly SystemProcedureParameter[] CatalogsParameters =
    [
        new("server_name", SqlType.NVarchar, 128),
    ];

    private static readonly SystemProcedureParameter[] TablesExParameters =
    [
        new("table_server", SqlType.NVarchar, 128),
        new("table_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_schema", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_catalog", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_type", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("fUsePattern", SqlType.Bit, 0, SqlValue.FromBoolean(true)),
    ];

    private static readonly SystemProcedureParameter[] ColumnsExParameters =
    [
        new("table_server", SqlType.NVarchar, 128),
        new("table_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_schema", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_catalog", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("column_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("ODBCVer", SqlType.Int32, 0, SqlValue.FromInt32(2)),
    ];

    /// <summary>
    /// <c>sp_linkedservers</c> lists every server <c>sys.servers</c> holds, the
    /// instance's own row included, in its order, as <c>SRV_NAME</c>,
    /// <c>SRV_PROVIDERNAME</c>, <c>SRV_PRODUCT</c>, <c>SRV_DATASOURCE</c>,
    /// <c>SRV_PROVIDERSTRING</c>, <c>SRV_LOCATION</c> and <c>SRV_CAT</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpLinkedServers(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        _ = BindSystemProcedureArguments("sp_linkedservers", calledAs, arguments, []);
        var rows = new List<SqlValue[]>();
        foreach (var server in BuiltInResources.EnumerateSysServers(batch, batch.CurrentDatabase))
            rows.Add([server[1], server[3], server[2], server[4], server[6], server[5], server[7]]);
        var name = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        var text = NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit);
        yield return new SimulatedSqlResultSet(
            [name, name, name, text, text, text, name],
            ["SRV_NAME", "SRV_PROVIDERNAME", "SRV_PRODUCT", "SRV_DATASOURCE", "SRV_PROVIDERSTRING", "SRV_LOCATION", "SRV_CAT"],
            rows);
    }

    /// <summary>
    /// <c>sp_testlinkedserver @servername</c>, an extended procedure: connects
    /// to the server and returns 0 quietly when it answers. Its one parameter
    /// takes only a Unicode string — a bare word, an <c>N''</c> literal or an
    /// <c>nvarchar</c> variable — and anything else, NULL included, is Msg 214;
    /// a server <c>sys.servers</c> doesn't hold is Msg 7202, which ends the
    /// batch.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTestLinkedServer(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        if (arguments.Count > 1)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.TooManyArgumentsToFunction("sp_testlinkedserver", state: 90), 1);
        if (arguments.Count == 0 || arguments[0].IsDefault)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ProcedureExpectsParameter("sp_testlinkedserver", "servername", state: 95), 1);
        var value = arguments[0].Value;
        if (arguments[0].IsUntypedNull || value.Type is not (NVarcharSqlType or SystemNameSqlType) || value.IsNull)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.SystemProcedureParameterType("servername", "sysname", 90), 1);
        var serverName = value.AsString;
        if (!batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(serverName, out var server))
        {
            var missing = AtSystemProcedureLine(calledAs, SimulatedSqlException.LinkedServerNotFound(serverName), 1);
            missing.EndedCalledBatch = false;
            throw missing;
        }
        // Opening a session is the test: a server that can't be reached
        // fails here as any use of it would.
        using var session = server.OpenSession(null);
    }

    /// <summary>
    /// <c>sp_catalogs @server_name</c> lists the databases of a linked server
    /// as <c>CATALOG_NAME</c> and a NULL <c>DESCRIPTION</c>, in the remote
    /// server's collation order.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpCatalogs(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_catalogs", calledAs, arguments, CatalogsParameters);
        var serverName = values[0].IsNull ? "" : values[0].AsString;
        if (!batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(serverName, out var server))
        {
            var missing = AtSystemProcedureLine(calledAs, SimulatedSqlException.LinkedServerNotFound(serverName), 1);
            missing.EndedCalledBatch = false;
            throw missing;
        }
        var target = server.Target;
        var names = new List<string>();
        foreach (var (name, _) in target.Databases)
            names.Add(name);
        names.Sort(target.ServerCollation);
        var description = SqlValue.Null(NVarcharSqlType.Get(255, Collation.Catalog, Coercibility.Implicit));
        var rows = new List<SqlValue[]>(names.Count);
        foreach (var name in names)
            rows.Add([SqlValue.FromNVarchar(name), description]);
        yield return new SimulatedSqlResultSet(
            [NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), NVarcharSqlType.Get(255, Collation.Catalog, Coercibility.Implicit)],
            ["CATALOG_NAME", "DESCRIPTION"],
            rows);
    }

    /// <summary>
    /// <c>sp_tables_ex</c> lists a linked server's tables, views and synonyms
    /// in one of its databases — the server's own system tables and views
    /// beside the user's, as <c>SYSTEM TABLE</c> / <c>SYSTEM VIEW</c> — by
    /// type, schema and name. The name and schema match as <c>LIKE</c>
    /// patterns unless <c>@fUsePattern</c> is 0, and <c>@table_type</c> takes a
    /// comma-separated list. The database defaults to the one a session of
    /// the server starts in, and a database it doesn't have lists nothing.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTablesEx(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_tables_ex", calledAs, arguments, TablesExParameters);
        var name = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        var schema = new SqlType[] { name, name, name, name, NVarcharSqlType.Get(255, Collation.Catalog, Coercibility.Implicit) };
        string[] names = ["TABLE_CAT", "TABLE_SCHEM", "TABLE_NAME", "TABLE_TYPE", "REMARKS"];
        // A server sys.servers doesn't hold lists nothing, then is Msg 7202.
        if (SchemaRowsetServer(batch, values[0]) is not { } server)
        {
            yield return new SimulatedSqlResultSet(schema, names, []);
            throw MissingSchemaRowsetServer(calledAs, values[0], line: 41);
        }
        var rows = new List<SqlValue[]>();
        var usePattern = values[5].IsNull || values[5].CoerceTo(SqlType.Bit).AsBoolean;
        var types = values[4].IsNull ? null
            : values[4].AsString.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(static type => type.Trim('\'')).ToArray();
        var query = $"""
            SELECT DB_NAME(), s.name, o.name,
                CASE WHEN o.is_ms_shipped = 1 THEN CASE WHEN o.type = 'V' THEN N'SYSTEM VIEW' ELSE N'SYSTEM TABLE' END
                    WHEN o.type = 'U' THEN N'TABLE' WHEN o.type = 'V' THEN N'VIEW' ELSE N'SYNONYM' END AS table_type
            FROM sys.all_objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U', 'V', 'S', 'SN'){SchemaRowsetFilter("o.name", values[1], usePattern)}{SchemaRowsetFilter("s.name", values[2], usePattern)}
            ORDER BY table_type, s.name, o.name
            """;
        if (SchemaRowsetQuery(server, values[3], query) is { } result)
        {
            var remarks = SqlValue.Null(schema[4]);
            foreach (var row in result.RowValues)
            {
                if (types is null || Array.Exists(types, type => string.Equals(type, row[3].AsString, StringComparison.OrdinalIgnoreCase)))
                    rows.Add([row[0], row[1], row[2], row[3], remarks]);
            }
        }
        yield return new SimulatedSqlResultSet(schema, names, rows);
    }

    /// <summary>
    /// <c>sp_columns_ex</c> describes the columns of a linked server's tables
    /// and views as its OLE DB provider reports them: <c>DATA_TYPE</c> and
    /// <c>SS_DATA_TYPE</c> in that provider's down-level terms (see
    /// <see cref="ProviderColumnRow"/>), columns of a type it has no mapping
    /// for left out, by schema, table and column id. Names match as
    /// <c>LIKE</c> patterns.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpColumnsEx(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_columns_ex", calledAs, arguments, ColumnsExParameters);
        var name = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        var text = NVarcharSqlType.Get(254, Collation.Catalog, Coercibility.Implicit);
        SqlType[] schema =
        [
            name, name, name, name, SqlType.SmallInt, name, SqlType.Int32, SqlType.Int32, SqlType.SmallInt, SqlType.SmallInt, SqlType.SmallInt,
            text, text, SqlType.SmallInt, SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt,
            VarcharSqlType.Get(254, Collation.Catalog, Coercibility.Implicit), SqlType.TinyInt,
        ];
        string[] names =
        [
            "TABLE_CAT", "TABLE_SCHEM", "TABLE_NAME", "COLUMN_NAME", "DATA_TYPE", "TYPE_NAME", "COLUMN_SIZE", "BUFFER_LENGTH",
            "DECIMAL_DIGITS", "NUM_PREC_RADIX", "NULLABLE", "REMARKS", "COLUMN_DEF", "SQL_DATA_TYPE", "SQL_DATETIME_SUB",
            "CHAR_OCTET_LENGTH", "ORDINAL_POSITION", "IS_NULLABLE", "SS_DATA_TYPE",
        ];
        var nullability = new bool[] { true, true, false, true, false, true, true, true, true, true, false, true, true, true, true, true, true, false, true };
        // A server sys.servers doesn't hold is Msg 7202 ending the statement
        // that fills the procedure's work table, which it then lists empty.
        if (SchemaRowsetServer(batch, values[0]) is not { } server)
        {
            yield return new SimulatedErrorOutcome(MissingSchemaRowsetServer(calledAs, values[0], line: 177));
            var terminated = SimulatedSqlException.StatementTerminatedMessage(batch);
            terminated.LineNumber = 177;
            terminated.Procedure = calledAs;
            yield return new SimulatedInfoOutcome(terminated);
            yield return new SimulatedSqlResultSet(schema, names, []) { ColumnNullability = nullability };
            yield break;
        }
        var query = $"""
            SELECT DB_NAME(), s.name, o.name, c.name, TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale,
                c.is_nullable, d.definition, c.column_id
            FROM sys.all_columns AS c JOIN sys.all_objects AS o ON o.object_id = c.object_id
                JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                LEFT JOIN sys.default_constraints AS d ON d.object_id = c.default_object_id
            WHERE o.type IN ('U', 'V'){SchemaRowsetFilter("o.name", values[1], usePattern: true)}{SchemaRowsetFilter("s.name", values[2], usePattern: true)}{SchemaRowsetFilter("c.name", values[4], usePattern: true)}
            ORDER BY s.name, o.name, c.column_id
            """;
        var rows = new List<SqlValue[]>();
        if (SchemaRowsetQuery(server, values[3], query) is { } result)
        {
            foreach (var row in result.RowValues)
            {
                if (ProviderColumnRow(row, schema) is { } described)
                    rows.Add(described);
            }
        }
        yield return new SimulatedSqlResultSet(schema, names, rows) { ColumnNullability = nullability };
    }

    /// <summary>The linked server a schema-rowset procedure names; null for one sys.servers doesn't hold.</summary>
    private static LinkedServer? SchemaRowsetServer(BatchContext batch, SqlValue serverName) =>
        batch.Connection.Simulation.ActiveLinkedServers.TryGetValue(serverName.IsNull ? "" : serverName.AsString, out var server) ? server : null;

    /// <summary>A schema-rowset procedure's Msg 7202, at its own line, leaving the calling batch running.</summary>
    private static SimulatedSqlException MissingSchemaRowsetServer(string calledAs, SqlValue serverName, int line)
    {
        var missing = AtSystemProcedureLine(calledAs, SimulatedSqlException.LinkedServerNotFound(serverName.IsNull ? "" : serverName.AsString), line);
        missing.EndedCalledBatch = false;
        return missing;
    }

    /// <summary>
    /// A schema rowset's catalog query run in <paramref name="catalog"/> on the
    /// server — the database its sessions start in when null — or null for a
    /// database the server doesn't have.
    /// </summary>
    private static SimulatedSqlResultSet? SchemaRowsetQuery(LinkedServer server, SqlValue catalog, string query)
    {
        var database = catalog.IsNull ? server.SessionDatabaseName : catalog.AsString;
        return server.Target.Databases.ContainsKey(database)
            ? RemoteWrite.RunRemoteQuery(server, query, database, browse: false)
            : null;
    }

    /// <summary>A catalog query's condition on <paramref name="column"/>, empty when <paramref name="value"/> is NULL.</summary>
    private static string SchemaRowsetFilter(string column, SqlValue value, bool usePattern) =>
        value.IsNull ? "" : $" AND {column} {(usePattern ? "LIKE" : "=")} N'{value.AsString.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    /// One <c>sp_columns_ex</c> row for a catalog row (database, schema, table,
    /// column, system type name, max_length, precision, scale, nullability,
    /// default, column id), described as SQL Server's OLE DB provider reports
    /// it (probed 2026-10-06 against SQL Server 2025 for every system type): a
    /// <c>bigint</c> as <c>NUMERIC</c>, the date and time types after
    /// <c>datetime</c> as <c>WVARCHAR</c> strings of their text length with
    /// <c>SS_DATA_TYPE</c> 0, <c>nchar</c> named <c>nvarchar</c>, a
    /// <c>vector</c> as the <c>varbinary</c> it is stored as, and
    /// <c>SS_DATA_TYPE</c> the TDS type code — its nullable form for a
    /// nullable fixed-length column. A <c>hierarchyid</c>, spatial, <c>json</c>
    /// or CLR type's column is left out (null).
    /// </summary>
    private static SqlValue[]? ProviderColumnRow(SqlValue[] row, SqlType[] schema)
    {
        var typeName = row[4].IsNull ? "" : row[4].AsString;
        var maxLength = row[5].IsNull ? 0 : row[5].CoerceTo(SqlType.Int32).AsInt32;
        var precision = row[6].IsNull ? 0 : row[6].CoerceTo(SqlType.Int32).AsInt32;
        var scale = row[7].IsNull ? 0 : row[7].CoerceTo(SqlType.Int32).AsInt32;
        var nullable = !row[8].IsNull && row[8].CoerceTo(SqlType.Bit).AsBoolean;
        var isMax = maxLength == -1;

        // DATA_TYPE, TYPE_NAME, COLUMN_SIZE, BUFFER_LENGTH, DECIMAL_DIGITS,
        // NUM_PREC_RADIX, SQL_DATA_TYPE, SQL_DATETIME_SUB, CHAR_OCTET_LENGTH,
        // SS_DATA_TYPE (NOT NULL, NULL).
        (short Type, string Name, int? Size, int? Buffer, short? Digits, short? Radix, short SqlType, short? Sub, int? Octet, byte NotNull, byte Null)? described = typeName switch
        {
            "bigint" => (2, "bigint", 19, 21, 0, 10, 2, null, null, 108, 108),
            "binary" => (-2, "binary", maxLength, maxLength, null, null, -2, null, maxLength, 45, 37),
            "bit" => (-7, "bit", 1, 1, 0, null, -7, null, null, 50, 50),
            "char" => (1, "char", maxLength, maxLength, null, null, 1, null, maxLength, 47, 39),
            "date" => (-9, "date", 10, 20, null, null, -9, null, null, 0, 0),
            "datetime" => (11, "datetime", 23, 16, 3, null, 9, 3, null, 61, 111),
            "datetime2" => (-9, "datetime2", scale == 0 ? 19 : 20 + scale, 2 * (scale == 0 ? 19 : 20 + scale), null, null, -9, null, null, 0, 0),
            "datetimeoffset" => (-9, "datetimeoffset", scale == 0 ? 26 : 27 + scale, 2 * (scale == 0 ? 26 : 27 + scale), null, null, -9, null, null, 0, 0),
            "decimal" or "numeric" => (2, "numeric", precision, precision + 2, (short)scale, 10, 2, null, null, 108, 108),
            "float" => (6, "float", 15, 8, null, 10, 6, null, null, 62, 109),
            "image" => (-4, "image", int.MaxValue, int.MaxValue, null, null, -4, null, int.MaxValue, 34, 34),
            "int" => (4, "int", 10, 4, 0, 10, 4, null, null, 56, 38),
            "money" => (3, "money", 19, 21, 4, 10, 3, null, null, 60, 110),
            "nchar" => (-8, "nvarchar", maxLength / 2, maxLength, null, null, -8, null, maxLength, 47, 39),
            "ntext" => (-10, "ntext", 1073741823, 2147483646, null, null, -10, null, 2147483646, 35, 35),
            "nvarchar" => (-9, "nvarchar", isMax ? 0 : maxLength / 2, isMax ? null : maxLength, null, null, -9, null, isMax ? 0 : maxLength, 39, 39),
            "real" => (7, "real", 7, 4, null, 10, 7, null, null, 59, 109),
            "smalldatetime" => (11, "smalldatetime", 16, 16, 0, null, 9, 3, null, 61, 111),
            "smallint" => (5, "smallint", 5, 2, 0, 10, 5, null, null, 52, 38),
            "smallmoney" => (3, "smallmoney", 10, 12, 4, 10, 3, null, null, 60, 110),
            "sql_variant" => (-9, "sql_variant", 16, null, null, null, -9, null, null, 39, 39),
            "text" => (-1, "text", int.MaxValue, int.MaxValue, null, null, -1, null, int.MaxValue, 35, 35),
            "time" => (-9, "time", scale == 0 ? 8 : 9 + scale, 2 * (scale == 0 ? 8 : 9 + scale), null, null, -9, null, null, 0, 0),
            "timestamp" => (-2, "timestamp", 8, 8, null, null, -2, null, 8, 45, 45),
            "tinyint" => (-6, "tinyint", 3, 1, 0, 10, -6, null, null, 48, 38),
            "uniqueidentifier" => (-11, "uniqueidentifier", 16, 36, null, null, -11, null, null, 36, 36),
            "varbinary" or "vector" => (-3, "varbinary", isMax ? 0 : maxLength, isMax ? null : maxLength, null, null, -3, null, isMax ? 0 : maxLength, 37, 37),
            "varchar" => (12, "varchar", isMax ? 0 : maxLength, isMax ? null : maxLength, null, null, 12, null, isMax ? 0 : maxLength, 39, 39),
            "xml" => (-9, "xml", 0, null, null, null, -9, null, null, 39, 39),
            _ => null,
        };
        if (described is not { } d)
            return null;

        static SqlValue Small(short? value) => value is { } v ? SqlValue.FromInt16(v) : SqlValue.Null(SqlType.SmallInt);
        static SqlValue Int(int? value) => value is { } v ? SqlValue.FromInt32(v) : SqlValue.Null(SqlType.Int32);
        var definition = row[9].IsNull ? SqlValue.Null(schema[12])
            : SqlValue.FromString(schema[12], row[9].AsString.Length > 254 ? row[9].AsString[..254] : row[9].AsString);
        return
        [
            SqlValue.FromString(schema[0], row[0].AsString), SqlValue.FromString(schema[1], row[1].AsString),
            SqlValue.FromString(schema[2], row[2].AsString), SqlValue.FromString(schema[3], row[3].AsString),
            SqlValue.FromInt16(d.Type), SqlValue.FromString(schema[5], d.Name), Int(d.Size), Int(d.Buffer), Small(d.Digits), Small(d.Radix),
            SqlValue.FromInt16((short)(nullable ? 1 : 0)), SqlValue.Null(schema[11]), definition,
            SqlValue.FromInt16(d.SqlType), Small(d.Sub), Int(d.Octet), SqlValue.FromInt16((short)row[10].CoerceTo(SqlType.Int32).AsInt32),
            SqlValue.FromString(schema[17], nullable ? "YES" : "NO"), SqlValue.FromByte(nullable ? d.Null : d.NotNull),
        ];
    }
}
