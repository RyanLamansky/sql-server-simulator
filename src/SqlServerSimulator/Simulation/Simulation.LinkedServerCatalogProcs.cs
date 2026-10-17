using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The catalog procedures that describe linked servers rather than reach
// through one: sp_linkedservers, sp_testlinkedserver and sp_catalogs, and the
// provider's schema rowsets sp_tables_ex, sp_columns_ex, sp_primarykeys,
// sp_foreignkeys, sp_table_privileges_ex, sp_column_privileges_ex and
// sp_indexes (probed 2026-10-06, 2026-10-16 and 2026-10-17 against SQL Server
// 2025).
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

    private static readonly SystemProcedureParameter[] PrimaryKeysParameters =
    [
        new("table_server", SqlType.NVarchar, 128),
        new("table_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_schema", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_catalog", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] ForeignKeysParameters =
    [
        new("table_server", SqlType.NVarchar, 128),
        new("pktab_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("pktab_schema", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("pktab_catalog", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("fktab_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("fktab_schema", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("fktab_catalog", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] TablePrivilegesExParameters =
    [
        new("table_server", SqlType.NVarchar, 128),
        new("table_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_schema", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_catalog", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("fUsePattern", SqlType.Bit, 0, SqlValue.FromBoolean(true)),
    ];

    private static readonly SystemProcedureParameter[] ColumnPrivilegesExParameters =
    [
        new("table_server", SqlType.NVarchar, 128),
        new("table_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_schema", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_catalog", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("column_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] IndexesParameters =
    [
        new("table_server", SqlType.NVarchar, 128),
        new("table_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_schema", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("table_catalog", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("index_name", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("is_unique", SqlType.Bit, 0, SqlValue.Null(SqlType.Bit)),
    ];

    /// <summary>
    /// <c>sp_table_privileges_ex</c> lists the table privileges of a linked
    /// server's tables and views in one of its databases — its catalog views
    /// beside the user's objects — as that server's own
    /// <c>sp_table_privileges</c> reports them, under the provider's
    /// <c>TABLE_CAT</c> / <c>TABLE_SCHEM</c> names and with a
    /// <c>nvarchar(128)</c> <c>PRIVILEGE</c>. The name and schema match as
    /// <c>LIKE</c> patterns unless <c>@fUsePattern</c> is 0, a database the
    /// server lacks lists nothing, and an unknown server is Msg 7202 after an
    /// empty listing, returning -1.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTablePrivilegesEx(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_table_privileges_ex", calledAs, arguments, TablePrivilegesExParameters);
        var name = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        SqlType[] schema = [name, name, name, name, name, name, CatalogVarchar3];
        string[] names = ["TABLE_CAT", "TABLE_SCHEM", "TABLE_NAME", "GRANTOR", "GRANTEE", "PRIVILEGE", "IS_GRANTABLE"];
        if (SchemaRowsetServer(batch, values[0]) is not { } server)
        {
            foreach (var step in ProcedureSteps(batch, MissingServerSteps(values[4])))
                yield return step;
            var missing = MissingSchemaRowsetServer(calledAs, values[0], line: 34);
            yield return FailedListing(batch, new SimulatedSqlResultSet(schema, names, []), missing);
            throw missing;
        }
        var rows = new List<SqlValue[]>();
        if (SchemaRowsetDatabase(server, values[3]) is { } database)
        {
            var usePattern = values[4].IsNull || values[4].CoerceTo(SqlType.Bit).AsBoolean;
            var tableMatches = SchemaRowsetNameMatch(database, values[1], usePattern);
            var schemaMatches = SchemaRowsetNameMatch(database, values[2], usePattern);
            var catalog = SqlValue.FromString(name, database.Name);
            foreach (var row in TablePrivilegeRows(database, (schemaName, tableName) => schemaMatches(schemaName) && tableMatches(tableName)))
            {
                rows.Add([
                    catalog, SqlValue.FromString(name, row.Schema), SqlValue.FromString(name, row.Table), SqlValue.FromString(name, row.Grantor),
                    SqlValue.FromString(name, row.Grantee), SqlValue.FromString(name, row.Privilege), SqlValue.FromVarchar(CatalogVarchar3, row.Grantable ? "YES" : "NO"),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(schema, names, rows);
    }

    /// <summary>
    /// <c>sp_column_privileges_ex</c> lists the column privileges of a linked
    /// server's tables and views as that server's <c>sp_column_privileges</c>
    /// reports them, under the provider's names: the table and schema are exact
    /// names (every one when NULL) and the column a <c>LIKE</c> pattern. A
    /// database the server lacks lists nothing, and an unknown server is Msg
    /// 7202 after an empty listing, returning -1.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpColumnPrivilegesEx(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_column_privileges_ex", calledAs, arguments, ColumnPrivilegesExParameters);
        var name = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        SqlType[] schema = [name, name, name, name, name, name, name, CatalogVarchar3];
        string[] names = ["TABLE_CAT", "TABLE_SCHEM", "TABLE_NAME", "COLUMN_NAME", "GRANTOR", "GRANTEE", "PRIVILEGE", "IS_GRANTABLE"];
        if (SchemaRowsetServer(batch, values[0]) is not { } server)
        {
            var missing = MissingSchemaRowsetServer(calledAs, values[0], line: 11);
            yield return FailedListing(batch, new SimulatedSqlResultSet(schema, names, []), missing);
            throw missing;
        }
        var rows = new List<SqlValue[]>();
        if (SchemaRowsetDatabase(server, values[3]) is { } database)
        {
            var tableMatches = SchemaRowsetNameMatch(database, values[1], usePattern: false);
            var schemaMatches = SchemaRowsetNameMatch(database, values[2], usePattern: false);
            var catalog = SqlValue.FromString(name, database.Name);
            foreach (var row in ColumnPrivilegeRows(database, (schemaName, tableName) => schemaMatches(schemaName) && tableMatches(tableName), CompileCatalogPattern(values[4].IsNull ? "%" : values[4].AsString)))
            {
                rows.Add([
                    catalog, SqlValue.FromString(name, row.Schema), SqlValue.FromString(name, row.Table), SqlValue.FromString(name, row.Column),
                    SqlValue.FromString(name, row.Grantor), SqlValue.FromString(name, row.Grantee), SqlValue.FromString(name, row.Privilege),
                    SqlValue.FromVarchar(CatalogVarchar3, row.Grantable ? "YES" : "NO"),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(schema, names, rows);
    }

    /// <summary>
    /// <c>sp_indexes</c> asks a linked server's provider for its INDEXES rowset,
    /// which SQL Server's own provider can't hand back: whenever the rowset
    /// would hold a row — an index of a table or view the exact, case-blind
    /// name, schema and index arguments take (a unique one under
    /// <c>@is_unique = 1</c>), or a system base table's, which every database
    /// carries in <c>sys</c> — the procedure sends its empty listing, the
    /// provider's Msg 7412 (<c>Binding information is invalid.</c>) and Msg
    /// 7346, which ends the batch and rolls the transaction back as under
    /// <c>XACT_ABORT</c>. With nothing to report the listing is empty. An
    /// unknown server is Msg 7202 after an empty listing, returning -1.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpIndexes(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_indexes", calledAs, arguments, IndexesParameters);
        var name = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        SqlType[] schema =
        [
            name, name, name, SqlType.SmallInt, name, name, SqlType.Int32, SqlType.Int32, name,
            VarcharSqlType.Get(1, Collation.Catalog, Coercibility.Implicit), SqlType.Int32, SqlType.Int32,
            NVarcharSqlType.Get(4000, Collation.Catalog, Coercibility.Implicit),
        ];
        string[] names =
        [
            "TABLE_CAT", "TABLE_SCHEM", "TABLE_NAME", "NON_UNIQUE", "INDEX_QUALIFIER", "INDEX_NAME", "TYPE", "ORDINAL_POSITION", "COLUMN_NAME",
            "ASC_OR_DESC", "CARDINALITY", "PAGES", "FILTER_CONDITION",
        ];
        var listing = new SimulatedSqlResultSet(schema, names, [])
        {
            ColumnNullability = [true, true, true, true, true, true, false, true, true, true, true, true, true],
        };
        if (SchemaRowsetServer(batch, values[0]) is not { } server)
        {
            var missing = MissingSchemaRowsetServer(calledAs, values[0], line: 12);
            yield return FailedListing(batch, listing, missing);
            throw missing;
        }
        yield return listing;
        var uniqueOnly = !values[5].IsNull && values[5].CoerceTo(SqlType.Bit).AsBoolean;
        var query = $"""
            SELECT TOP (1) 1 FROM sys.indexes AS i JOIN sys.objects AS o ON o.object_id = i.object_id JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE i.index_id > 0 AND o.type IN ('U', 'V'){(uniqueOnly ? " AND i.is_unique = 1" : "")}{SchemaRowsetFilter("o.name", values[1], usePattern: false)}{SchemaRowsetFilter("s.name", values[2], usePattern: false)}{SchemaRowsetFilter("i.name", values[4], usePattern: false)}
            UNION ALL
            SELECT TOP (1) 1 FROM sys.all_objects AS o
            WHERE o.type = 'S' AND o.name <> N'sysfiles1'{(values[4].IsNull ? "" : " AND 1 = 0")}{SchemaRowsetFilter("o.name", values[1], usePattern: false)}{SchemaRowsetFilter("SCHEMA_NAME(o.schema_id)", values[2], usePattern: false)}
            """;
        if (SchemaRowsetQuery(server, values[3], query) is { } found && found.RowValues.Any())
        {
            var provider = batch.InfoMessage(@class: 0, state: 2, number: 7412, $"OLE DB provider \"{server.ProviderInMessages}\" for linked server \"(null)\" returned message \"Binding information is invalid.\".");
            provider.LineNumber = 12;
            provider.Procedure = calledAs;
            yield return new SimulatedInfoOutcome(provider);
            throw AtProcedureLine(SimulatedSqlException.IndexesRowsetUnavailable(server), calledAs, 12);
        }
    }

    /// <summary>
    /// <c>sp_primarykeys</c> lists the primary-key columns of a linked
    /// server's tables in one of its databases, by schema, table and key
    /// position, each name an exact one under the server's collation; the key's
    /// name isn't reported. The database defaults to the one a session of the
    /// server starts in.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpPrimaryKeys(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_primarykeys", calledAs, arguments, PrimaryKeysParameters);
        var name = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        SqlType[] schema = [name, name, name, name, SqlType.Int32, name];
        string[] names = ["TABLE_CAT", "TABLE_SCHEM", "TABLE_NAME", "COLUMN_NAME", "KEY_SEQ", "PK_NAME"];
        if (SchemaRowsetServer(batch, values[0]) is not { } server)
        {
            var missing = MissingSchemaRowsetServer(calledAs, values[0], line: 10);
            yield return FailedListing(batch, new SimulatedSqlResultSet(schema, names, []), missing);
            throw missing;
        }
        var query = $"""
            SELECT DB_NAME(), s.name, o.name, c.name, ic.key_ordinal
            FROM sys.key_constraints AS k JOIN sys.objects AS o ON o.object_id = k.parent_object_id
                JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                JOIN sys.index_columns AS ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
                JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE k.type = 'PK'{SchemaRowsetFilter("o.name", values[1], usePattern: false)}{SchemaRowsetFilter("s.name", values[2], usePattern: false)}
            ORDER BY s.name, o.name, ic.key_ordinal
            """;
        var rows = new List<SqlValue[]>();
        if (SchemaRowsetQuery(server, values[3], query) is { } result)
        {
            foreach (var row in result.RowValues)
            {
                rows.Add([
                    SqlValue.FromString(name, row[0].AsString), SqlValue.FromString(name, row[1].AsString), SqlValue.FromString(name, row[2].AsString),
                    SqlValue.FromString(name, row[3].AsString), SqlValue.FromInt32(row[4].CoerceTo(SqlType.Int32).AsInt32), SqlValue.Null(name),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(schema, names, rows);
    }

    /// <summary>
    /// <c>sp_foreignkeys</c> lists the foreign-key columns between a linked
    /// server's tables — those referencing the primary-key table named, those
    /// of the foreign-key table named, or every one — by primary-key table,
    /// foreign-key table and key position, in the database either catalog
    /// argument names. A rule reads in ODBC's terms (cascade 0, no action 1,
    /// set NULL 2, set default 3) and neither key's name is reported.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpForeignKeys(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_foreignkeys", calledAs, arguments, ForeignKeysParameters);
        var name = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        SqlType[] schema = [name, name, name, name, name, name, name, name, SqlType.Int32, SqlType.Int32, SqlType.Int32, name, name, SqlType.SmallInt];
        string[] names =
        [
            "PKTABLE_CAT", "PKTABLE_SCHEM", "PKTABLE_NAME", "PKCOLUMN_NAME", "FKTABLE_CAT", "FKTABLE_SCHEM", "FKTABLE_NAME", "FKCOLUMN_NAME",
            "KEY_SEQ", "UPDATE_RULE", "DELETE_RULE", "FK_NAME", "PK_NAME", "DEFERRABILITY",
        ];
        if (SchemaRowsetServer(batch, values[0]) is not { } server)
        {
            var missing = MissingSchemaRowsetServer(calledAs, values[0], line: 13);
            yield return FailedListing(batch, new SimulatedSqlResultSet(schema, names, []), missing);
            throw missing;
        }
        var query = $"""
            SELECT DB_NAME(), ps.name, po.name, pc.name, fs.name, fo.name, fc.name, k.constraint_column_id,
                f.update_referential_action, f.delete_referential_action
            FROM sys.foreign_keys AS f JOIN sys.foreign_key_columns AS k ON k.constraint_object_id = f.object_id
                JOIN sys.objects AS po ON po.object_id = f.referenced_object_id JOIN sys.schemas AS ps ON ps.schema_id = po.schema_id
                JOIN sys.columns AS pc ON pc.object_id = k.referenced_object_id AND pc.column_id = k.referenced_column_id
                JOIN sys.objects AS fo ON fo.object_id = f.parent_object_id JOIN sys.schemas AS fs ON fs.schema_id = fo.schema_id
                JOIN sys.columns AS fc ON fc.object_id = k.parent_object_id AND fc.column_id = k.parent_column_id
            WHERE 1 = 1{SchemaRowsetFilter("po.name", values[1], usePattern: false)}{SchemaRowsetFilter("ps.name", values[2], usePattern: false)}{SchemaRowsetFilter("fo.name", values[4], usePattern: false)}{SchemaRowsetFilter("fs.name", values[5], usePattern: false)}
            ORDER BY ps.name, po.name, fs.name, fo.name, k.constraint_column_id
            """;
        static SqlValue Rule(SqlValue action) => SqlValue.FromInt32(action.CoerceTo(SqlType.Int32).AsInt32 switch { 0 => 1, 1 => 0, var other => other });
        var rows = new List<SqlValue[]>();
        if (SchemaRowsetQuery(server, values[3].IsNull ? values[6] : values[3], query) is { } result)
        {
            foreach (var row in result.RowValues)
            {
                SqlValue Text(int at) => SqlValue.FromString(name, row[at].AsString);
                rows.Add([
                    Text(0), Text(1), Text(2), Text(3), Text(0), Text(4), Text(5), Text(6), SqlValue.FromInt32(row[7].CoerceTo(SqlType.Int32).AsInt32),
                    Rule(row[8]), Rule(row[9]), SqlValue.Null(name), SqlValue.Null(name), SqlValue.Null(SqlType.SmallInt),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(schema, names, rows);
    }

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
        SqlType[] schema = [NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit), NVarcharSqlType.Get(255, Collation.Catalog, Coercibility.Implicit)];
        string[] columnNames = ["CATALOG_NAME", "DESCRIPTION"];
        if (SchemaRowsetServer(batch, values[0]) is not { } server)
        {
            var missing = MissingSchemaRowsetServer(calledAs, values[0], line: 7);
            yield return FailedListing(batch, new SimulatedSqlResultSet(schema, columnNames, []), missing);
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
        yield return new SimulatedSqlResultSet(schema, columnNames, rows);
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
            foreach (var step in ProcedureSteps(batch, MissingServerSteps(values[5])))
                yield return step;
            var missing = MissingSchemaRowsetServer(calledAs, values[0], line: 41);
            yield return FailedListing(batch, new SimulatedSqlResultSet(schema, names, []), missing);
            throw missing;
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
        // A server sys.servers doesn't hold is Msg 7202 ending the INSERT
        // that fills the procedure's work table, which it then lists empty
        // with no count on its DONE, returning -1; a TRY catching the error
        // ends the procedure there (captured 2026-10-17 against SQL Server
        // 2025).
        if (SchemaRowsetServer(batch, values[0]) is not { } server)
        {
            var missing = MissingSchemaRowsetServer(calledAs, values[0], line: 177);
            if (CaughtByTryFrame(batch, missing))
                throw missing;
            yield return new SimulatedErrorOutcome(missing) { DoneKind = StatementDoneKind.Insert, InModule = true };
            var terminated = SimulatedSqlException.StatementTerminatedMessage(batch);
            terminated.LineNumber = 177;
            terminated.Procedure = calledAs;
            yield return new SimulatedInfoOutcome(terminated, followsRows: true);
            batch.SystemProcedureStatus = -1;
            yield return new SimulatedSqlResultSet(schema, names, []) { ColumnNullability = nullability, CountSuppressed = true };
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
    /// <remarks>
    /// The schema-rowset procedures return -1 after it, and a NULL server is
    /// state 1 where a name is state 2 (captured 2026-10-17 against SQL Server
    /// 2025).
    /// </remarks>
    private static SimulatedSqlException MissingSchemaRowsetServer(string calledAs, SqlValue serverName, int line)
    {
        var missing = AtSystemProcedureLine(calledAs, SimulatedSqlException.LinkedServerNotFound(serverName.IsNull ? "" : serverName.AsString, serverName.IsNull ? (byte)1 : (byte)2), line, returnCode: -1);
        missing.EndedCalledBatch = false;
        return missing;
    }

    /// <summary>
    /// The statements <c>sp_tables_ex</c> and <c>sp_table_privileges_ex</c>
    /// run ahead of a missing server's failed query (see
    /// <see cref="ProcedureSteps"/>): two conditions, then an assignment and a
    /// condition unless <c>@fUsePattern</c> is 0, whatever else they were
    /// given (captured 2026-10-17 against SQL Server 2025).
    /// </summary>
    private static string MissingServerSteps(SqlValue usePattern) =>
        usePattern.IsNull || usePattern.CoerceTo(SqlType.Bit).AsBoolean ? "IISI" : "II";

    /// <summary>
    /// <paramref name="listing"/> as a schema-rowset procedure's query that
    /// <paramref name="error"/> fails before its first row: its metadata and
    /// ORDER go out, then the error ahead of its DONE, as real's remote-rowset
    /// query sends a missing server's Msg 7202 (captured 2026-10-17 against
    /// SQL Server 2025); the caller throws the error after it.
    /// </summary>
    private static SimulatedSqlResultSet FailedListing(BatchContext batch, SimulatedSqlResultSet listing, SimulatedSqlException error)
    {
        listing.EndedByError = true;
        listing.ErrorCaught = CaughtByTryFrame(batch, error);
        return listing;
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

    /// <summary>The database of a linked server a schema rowset reads — the one its sessions start in when <paramref name="catalog"/> is NULL — or null for one it lacks.</summary>
    private static Database? SchemaRowsetDatabase(LinkedServer server, SqlValue catalog) =>
        server.Target.Databases.TryGetValue(catalog.IsNull ? server.SessionDatabaseName : catalog.AsString, out var database) ? database : null;

    /// <summary>A schema-rowset name restriction: everything for NULL, else a <c>LIKE</c> pattern or an exact name under the database's collation.</summary>
    private static Func<string, bool> SchemaRowsetNameMatch(Database database, SqlValue restriction, bool usePattern)
    {
        if (restriction.IsNull)
            return static _ => true;
        var written = restriction.AsString;
        if (usePattern)
        {
            var pattern = CompileCatalogPattern(written);
            return value => Matches(pattern, value);
        }
        return value => database.Collation.Equals(written, value);
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
