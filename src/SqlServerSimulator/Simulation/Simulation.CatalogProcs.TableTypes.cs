using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The catalog procedures over user-defined table types: the ODBC trio
// sp_table_types / sp_table_type_columns_100 / sp_table_type_pkeys, shaped as
// sp_tables / sp_columns_100 / sp_pkeys are, and the OLE DB rowset trio
// sp_table_types_rowset / sp_table_type_columns_100_rowset /
// sp_table_type_primary_keys_rowset, shaped as the TABLES / COLUMNS /
// PRIMARY_KEYS rowsets. Behavior probed 2026-10-16 against SQL Server 2025 by
// calling each over a fixture of types; see docs/claude/catalog-views.md.
partial class Simulation
{
    // sp_table_type_pkeys reports KEY_SEQ as an int, where sp_pkeys reports a
    // smallint, and TABLE_NAME NOT NULL.
    private static readonly SqlType[] SpTableTypePkeysSchema =
        [SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.Int32, SqlType.SystemName];

    private static readonly bool[] SpTableTypePkeysNullability = [true, true, false, true, true, true];

    // The TABLES and PRIMARY_KEYS rowsets of table types report TABLE_NAME NOT
    // NULL, and the TABLES one DATE_CREATED too.
    private static readonly bool[] TableTypesRowsetNullability = [true, true, false, true, true, true, true, false, true];

    private static readonly bool[] TableTypeKeysRowsetNullability = [true, true, false, true, true, true, true, true];

    // COLUMNS of table types: TABLE_NAME and DATA_TYPE NOT NULL.
    private static readonly bool[] TableTypeColumnsRowsetNullability = [.. Enumerable.Range(0, 42).Select(static i => i is not (2 or 11))];

    /// <summary>
    /// The current database's table types the session may see — or, with
    /// <paramref name="definitionOnly"/>, whose definition it may see — by
    /// schema then name under the database's collation.
    /// </summary>
    private static List<TableType> VisibleTableTypes(BatchContext batch, bool definitionOnly = false)
    {
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var types = new List<TableType>();
        var hasPrincipal = PermissionEnforcement.TryMetadataVisibilityPrincipal(batch, database, out var principalId);
        if (!hasPrincipal)
            return types;
        var server = ServerLoginRights.For(batch.Connection);
        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var (_, type) in schema.TableTypes)
            {
                var ownerId = type.OwnerPrincipalId ?? schema.PrincipalId;
                if (principalId is not int filter
                    || (definitionOnly
                        ? PermissionChecker.CanViewTypeDefinition(database, filter, type.UserTypeId, schema.SchemaId, ownerId, server)
                        : PermissionChecker.CanViewTypeMetadata(database, filter, type.UserTypeId, schema.SchemaId, ownerId, server)))
                {
                    types.Add(type);
                }
            }
        }
        types.Sort((a, b) =>
        {
            var bySchema = collation.Compare(a.Schema.Name, b.Schema.Name);
            return bySchema != 0 ? bySchema : collation.Compare(a.Name, b.Name);
        });
        return types;
    }

    /// <summary>
    /// The qualifier check the three ODBC table-type procedures make: a
    /// qualifier naming another database than the current one, patterns
    /// included, is Msg 15250, after which the procedure returns -6;
    /// <c>sp_table_types</c> passes an empty one.
    /// </summary>
    private static void RequireCurrentDatabaseQualifier(BatchContext batch, SqlValue qualifier, bool emptyPasses = false)
    {
        if (qualifier.IsNull || batch.CurrentDatabase.Collation.Equals(qualifier.AsString, batch.CurrentDatabase.Name) || (emptyPasses && qualifier.AsString.Length == 0))
            return;
        var refusal = SimulatedSqlException.HelpObjectNotInCurrentDatabase();
        refusal.SystemProcedureReturnCode = -6;
        throw refusal;
    }

    /// <summary>
    /// <c>sp_table_types [@table_name] [, @table_owner] [, @table_qualifier]
    /// [, @table_type] [, @fUsePattern]</c>: ODBC's <c>SQLTables</c> over the
    /// table types, one <c>TABLE TYPE</c> row per type the session may see,
    /// by owner and name. The name and owner are always LIKE patterns, and
    /// <c>@table_type</c> and <c>@fUsePattern</c> change nothing; an empty
    /// qualifier lists nothing.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTableTypes(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_table_types", calledAs, arguments,
        [
            new("table_name", SqlType.NVarchar, 384, SqlValue.FromNVarchar("%")),
            new("table_owner", SqlType.NVarchar, 384, SqlValue.Null(SqlType.NVarchar)),
            new("table_qualifier", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
            new("table_type", SqlType.NVarchar, 100, SqlValue.Null(SqlType.NVarchar)),
            new("fUsePattern", SqlType.Bit, 0, SqlValue.FromInt32(1)),
        ]);
        RequireCurrentDatabaseQualifier(batch, values[2], emptyPasses: true);
        var namePattern = CompileCatalogPattern(RowsetText(values[0]));
        // An empty qualifier passes the check and lists nothing.
        var listsNothing = values[2] is { IsNull: false, AsString: "" };
        var ownerPattern = CompileCatalogPattern(RowsetText(values[1]));

        var qualifier = SqlValue.FromSystemName(batch.CurrentDatabase.Name);
        var rows = new List<SqlValue[]>();
        foreach (var type in listsNothing ? [] : VisibleTableTypes(batch))
        {
            if (Matches(namePattern, type.Name) && Matches(ownerPattern, type.Schema.Name))
            {
                rows.Add([
                    qualifier, SqlValue.FromSystemName(type.Schema.Name), SqlValue.FromSystemName(type.Name),
                    SqlValue.FromString(CatalogVarchar32, "TABLE TYPE"), SqlValue.Null(CatalogVarchar254),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(SpTablesSchema, SpTablesColumnNames, rows);
    }

    /// <summary>
    /// <c>sp_table_type_columns_100 @table_name [, @table_owner]
    /// [, @table_qualifier] [, @column_name] [, @ODBCVer] [, @fUsePattern]</c>:
    /// ODBC's <c>SQLColumns</c> over the table types, each column as
    /// <c>sp_columns_100</c> describes a table's. A NULL name lists every
    /// type; the names are LIKE patterns unless <c>@fUsePattern</c> is 0,
    /// which makes them exact; <c>@ODBCVer</c> 3 reports ODBC 3's date and
    /// time codes, any other value ODBC 2's.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTableTypeColumns(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_table_type_columns_100", calledAs, arguments,
        [
            new("table_name", SqlType.NVarchar, 384),
            new("table_owner", SqlType.NVarchar, 384, SqlValue.Null(SqlType.NVarchar)),
            new("table_qualifier", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
            new("column_name", SqlType.NVarchar, 384, SqlValue.Null(SqlType.NVarchar)),
            new("ODBCVer", SqlType.Int32, 0, SqlValue.FromInt32(2)),
            new("fUsePattern", SqlType.Bit, 0, SqlValue.FromInt32(1)),
        ]);
        RequireCurrentDatabaseQualifier(batch, values[2]);
        var database = batch.CurrentDatabase;
        var exact = values[5] is { IsNull: false } usePattern && usePattern.AsInt32 == 0;
        var (tableName, owner, column) = (RowsetText(values[0]), RowsetText(values[1]), RowsetText(values[3]));
        Func<string, bool> NameTest(string? text)
        {
            if (text is null)
                return static _ => true;
            if (exact)
                return candidate => database.Collation.Equals(text, candidate);
            var pattern = CompileCatalogPattern(text);
            return candidate => Matches(pattern, candidate);
        }
        var (nameTest, ownerTest, columnTest) = (NameTest(tableName), NameTest(owner), NameTest(column));
        var byName = (values[4] is { IsNull: false } odbcVer && odbcVer.AsInt32 == 3 ? SpDatatypeInfoByNameV3 : SpDatatypeInfoByNameV2).Value;

        var qualifier = SqlValue.FromSystemName(database.Name);
        var rows = new List<SqlValue[]>();
        foreach (var type in VisibleTableTypes(batch))
        {
            if (!nameTest(type.Name) || !ownerTest(type.Schema.Name))
                continue;
            var ownerValue = SqlValue.FromSystemName(type.Schema.Name);
            var typeName = SqlValue.FromSystemName(type.Name);
            for (var i = 0; i < type.Columns.Length; i++)
            {
                var col = type.Columns[i];
                if (columnTest(col.Name))
                    rows.Add(BuildSpColumnsRow(qualifier, ownerValue, typeName, col, i + 1, byName));
            }
        }
        yield return new SimulatedSqlResultSet(SpColumnsSchema, SpColumnsColumnNames, rows) { ColumnNullability = SpColumnsNullability };
    }

    /// <summary>
    /// <c>sp_table_type_pkeys @table_name [, @table_owner] [, @table_qualifier]</c>:
    /// ODBC's <c>SQLPrimaryKeys</c> over the table types, one row per key
    /// column in key order. The names are exact, a NULL one matching every
    /// type.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTableTypePkeys(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_table_type_pkeys", calledAs, arguments,
        [
            new("table_name", SqlType.NVarchar, 128),
            new("table_owner", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
            new("table_qualifier", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        ]);
        RequireCurrentDatabaseQualifier(batch, values[2]);
        var qualifier = SqlValue.FromSystemName(batch.CurrentDatabase.Name);
        var rows = new List<SqlValue[]>();
        foreach (var (type, key) in TableTypeKeys(batch, RowsetText(values[0]), RowsetText(values[1])))
        {
            var owner = SqlValue.FromSystemName(type.Schema.Name);
            var typeName = SqlValue.FromSystemName(type.Name);
            var keyName = SqlValue.FromSystemName(key.Name);
            for (var i = 0; i < key.StorageOrdinals.Length; i++)
            {
                rows.Add([
                    qualifier, owner, typeName, SqlValue.FromSystemName(type.CatalogShape.StoredColumns[key.StorageOrdinals[i]].Name),
                    SqlValue.FromInt32(i + 1), keyName,
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(SpTableTypePkeysSchema, SpPkeysColumnNames, rows) { ColumnNullability = SpTableTypePkeysNullability };
    }

    /// <summary>The visible table types an exact name and schema pick, each with its primary key.</summary>
    private static List<(TableType Type, KeyConstraint Key)> TableTypeKeys(BatchContext batch, string? name, string? schema)
    {
        var collation = batch.CurrentDatabase.Collation;
        var keys = new List<(TableType, KeyConstraint)>();
        foreach (var type in VisibleTableTypes(batch))
        {
            if ((name is null || collation.Equals(name, type.Name)) && (schema is null || collation.Equals(schema, type.Schema.Name))
                && FindPrimaryKey(type.CatalogShape) is { } key)
            {
                keys.Add((type, key));
            }
        }
        return keys;
    }

    /// <summary>
    /// <c>sp_table_types_rowset [@table_name] [, @table_schema]</c>: the OLE DB
    /// <c>TABLES</c> rowset over the table types, each a <c>TABLE TYPE</c> with
    /// its creation date; the names are exact, a NULL one matching every type.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeTableTypesRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, [RowsetNameParameter("table_name"), RowsetNameParameter("table_schema")]);
        var (name, schema) = (RowsetText(values[0]), RowsetText(values[1]));
        var collation = batch.CurrentDatabase.Collation;
        var catalog = SqlValue.FromNVarchar(RowsetName, batch.CurrentDatabase.Name);
        var rows = new List<SqlValue[]>();
        foreach (var type in VisibleTableTypes(batch))
        {
            if ((name is null || collation.Equals(name, type.Name)) && (schema is null || collation.Equals(schema, type.Schema.Name)))
            {
                rows.Add([
                    catalog, SqlValue.FromNVarchar(RowsetName, type.Schema.Name), SqlValue.FromNVarchar(RowsetName, type.Name),
                    SqlValue.FromNVarchar(RowsetType, "TABLE TYPE"), SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(RowsetDescription),
                    SqlValue.Null(SqlType.Int32), SqlValue.FromDateTime(type.CreateDate), SqlValue.Null(SqlType.DateTime),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(RowsetTablesSchema, RowsetTablesNames, rows) { ColumnNullability = TableTypesRowsetNullability };
    }

    /// <summary>
    /// <c>sp_table_type_columns_100_rowset [@table_name] [, @table_schema]
    /// [, @column_name]</c>: the OLE DB <c>COLUMNS</c> rowset over the table
    /// types, each column as <c>sp_columns_100_rowset</c> describes a
    /// table's, of the types whose definition the session may see; the names
    /// are exact, a NULL one matching everything.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeTableTypeColumnsRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments,
            [RowsetNameParameter("table_name"), RowsetNameParameter("table_schema"), RowsetNameParameter("column_name")]);
        var (name, schema, column) = (RowsetText(values[0]), RowsetText(values[1]), RowsetText(values[2]));
        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        var rows = new List<SqlValue[]>();
        foreach (var type in VisibleTableTypes(batch, definitionOnly: true))
        {
            if ((name is not null && !collation.Equals(name, type.Name)) || (schema is not null && !collation.Equals(schema, type.Schema.Name)))
                continue;
            var item = new RowsetObject(type.Schema.Name, type.Name, "TABLE TYPE", type, type.CatalogShape, type.Columns, type.CreateDate, type.CreateDate);
            for (var i = 0; i < type.Columns.Length; i++)
            {
                var col = type.Columns[i];
                if ((column is null || collation.Equals(column, col.Name)) && RowsetColumnRow(database, item, col, i, RowsetVersion.V100) is { } row)
                    rows.Add(row);
            }
        }
        yield return new SimulatedSqlResultSet(RowsetColumnsSchema, RowsetColumnsNames, rows) { ColumnNullability = TableTypeColumnsRowsetNullability };
    }

    /// <summary>
    /// <c>sp_table_type_primary_keys_rowset [@table_name] [, @table_schema]</c>:
    /// the OLE DB <c>PRIMARY_KEYS</c> rowset over the table types, each key's
    /// columns in name order with their key positions; the names are exact, a
    /// NULL one matching every type.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeTableTypePrimaryKeysRowset(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, [RowsetNameParameter("table_name"), RowsetNameParameter("table_schema")]);
        var collation = batch.CurrentDatabase.Collation;
        var catalog = SqlValue.FromNVarchar(RowsetName, batch.CurrentDatabase.Name);
        var rows = new List<SqlValue[]>();
        foreach (var (type, key) in TableTypeKeys(batch, RowsetText(values[0]), RowsetText(values[1])))
        {
            var columns = new List<(string Name, int Ordinal)>(key.StorageOrdinals.Length);
            for (var i = 0; i < key.StorageOrdinals.Length; i++)
                columns.Add((type.CatalogShape.StoredColumns[key.StorageOrdinals[i]].Name, i + 1));
            columns.Sort((a, b) => collation.Compare(a.Name, b.Name));
            foreach (var (columnName, ordinal) in columns)
            {
                rows.Add([
                    catalog, SqlValue.FromNVarchar(RowsetName, type.Schema.Name), SqlValue.FromNVarchar(RowsetName, type.Name),
                    SqlValue.FromNVarchar(RowsetName, columnName), SqlValue.Null(SqlType.UniqueIdentifier), SqlValue.Null(SqlType.Int32),
                    SqlValue.FromInt32(ordinal), SqlValue.FromNVarchar(RowsetName, key.Name),
                ]);
            }
        }
        yield return new SimulatedSqlResultSet(RowsetPrimaryKeysSchema, RowsetPrimaryKeysNames, rows) { ColumnNullability = TableTypeKeysRowsetNullability };
    }
}
