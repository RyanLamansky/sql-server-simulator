using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly VarcharSqlType CatalogVarchar3 =
        VarcharSqlType.Get(3, Collation.Catalog, Coercibility.Implicit);

    private static readonly SqlType[] SpTablePrivilegesSchema =
        [SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, CatalogVarchar32, CatalogVarchar3];

    private static readonly string[] SpTablePrivilegesColumnNames =
        ["TABLE_QUALIFIER", "TABLE_OWNER", "TABLE_NAME", "GRANTOR", "GRANTEE", "PRIVILEGE", "IS_GRANTABLE"];

    private static readonly SqlType[] SpColumnPrivilegesSchema =
        [SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, CatalogVarchar32, CatalogVarchar3];

    private static readonly string[] SpColumnPrivilegesColumnNames =
        ["TABLE_QUALIFIER", "TABLE_OWNER", "TABLE_NAME", "COLUMN_NAME", "GRANTOR", "GRANTEE", "PRIVILEGE", "IS_GRANTABLE"];

    /// <summary>
    /// The privileges an object's owner holds implicitly, which both
    /// procedures report as granted by <c>dbo</c> and grantable; a column
    /// carries no DELETE (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static readonly string[] OwnerTablePrivileges = ["DELETE", "INSERT", "REFERENCES", "SELECT", "UPDATE"];

    private static readonly string[] OwnerColumnPrivileges = ["INSERT", "REFERENCES", "SELECT", "UPDATE"];

    /// <summary>
    /// Handles <c>EXEC sp_table_privileges @table_name [, @table_owner]
    /// [, @table_qualifier] [, @fUsePattern]</c> — ODBC's
    /// <c>SQLTablePrivileges</c>. For each table or view the LIKE patterns
    /// match (exact names under <c>@fUsePattern = 0</c>): the owner's five
    /// privileges granted by <c>dbo</c>, then every standing GRANT on the
    /// object or one of its columns, grantable when given WITH GRANT OPTION;
    /// sorted by owner, table, privilege and grantee (probed 2026-09-26
    /// against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTablePrivileges(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        RequireFirstParameter(arguments, "sp_table_privileges", "table_name");
        string? name = null, owner = null, qualifier = null;
        var usePattern = true;
        var positional = 0;
        foreach (var arg in arguments)
        {
            var slot = arg.Name switch
            {
                null => positional++,
                var n when BuiltInToken.Equals(n, "table_name") => 0,
                var n when BuiltInToken.Equals(n, "table_owner") => 1,
                var n when BuiltInToken.Equals(n, "table_qualifier") => 2,
                var n when BuiltInToken.Equals(n, "fUsePattern") => 3,
                _ => throw CatalogBindingError(SimulatedSqlException.NotAParameterForProcedure(arg.Name!, "sp_table_privileges")),
            };
            switch (slot)
            {
                case 0: name = CatalogStringArg(arg); break;
                case 1: owner = CatalogStringArg(arg); break;
                case 2: qualifier = CatalogStringArg(arg); break;
                case 3: usePattern = arg.Value.IsNull || arg.Value.CoerceTo(SqlType.Int32).AsInt32 != 0; break;
                default: throw CatalogBindingError(SimulatedSqlException.TooManyArgumentsToFunction("sp_table_privileges"));
            }
        }

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        if (qualifier is not null && !collation.Equals(qualifier, database.Name))
            throw SimulatedSqlException.HelpObjectNotInCurrentDatabase();
        var namePattern = CompileCatalogPattern(name ?? "%");
        var ownerPattern = CompileCatalogPattern(owner ?? "%");
        bool NameMatches(LikeMatcher? pattern, string? written, string value) =>
            usePattern ? Matches(pattern, value) : written is null || collation.Equals(written, value);

        var qualifierValue = SqlValue.FromSystemName(database.Name);
        var rows = TablePrivilegeRows(database, (schemaName, tableName) => NameMatches(ownerPattern, owner, schemaName) && NameMatches(namePattern, name, tableName))
            .ConvertAll(row => new SqlValue[]
            {
                qualifierValue, SqlValue.FromSystemName(row.Schema), SqlValue.FromSystemName(row.Table), SqlValue.FromSystemName(row.Grantor),
                SqlValue.FromSystemName(row.Grantee), SqlValue.FromVarchar(CatalogVarchar32, row.Privilege), SqlValue.FromVarchar(CatalogVarchar3, row.Grantable ? "YES" : "NO"),
            });
        foreach (var step in ProcedureSteps(batch, SpTablePrivilegesSteps(owner, qualifier, usePattern)))
            yield return step;
        yield return new SimulatedSqlResultSet(SpTablePrivilegesSchema, SpTablePrivilegesColumnNames, rows);
    }

    /// <summary>
    /// Handles <c>EXEC sp_column_privileges @table_name [, @table_owner]
    /// [, @table_qualifier] [, @column_name]</c> — ODBC's
    /// <c>SQLColumnPrivileges</c>. The table name is exact and the column
    /// name a LIKE pattern: per column the owner's INSERT / REFERENCES /
    /// SELECT / UPDATE granted by <c>dbo</c>, then each standing GRANT on the
    /// object or that column, a permission other than the four DML ones
    /// reading as REFERENCES as real's own procedure maps it; sorted by
    /// column, privilege and grantee (probed 2026-09-26 against SQL Server
    /// 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpColumnPrivileges(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        RequireFirstParameter(arguments, "sp_column_privileges", "table_name");
        string? name = null, owner = null, qualifier = null, column = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            var slot = arg.Name switch
            {
                null => positional++,
                var n when BuiltInToken.Equals(n, "table_name") => 0,
                var n when BuiltInToken.Equals(n, "table_owner") => 1,
                var n when BuiltInToken.Equals(n, "table_qualifier") => 2,
                var n when BuiltInToken.Equals(n, "column_name") => 3,
                _ => throw CatalogBindingError(SimulatedSqlException.NotAParameterForProcedure(arg.Name!, "sp_column_privileges")),
            };
            switch (slot)
            {
                case 0: name = CatalogStringArg(arg); break;
                case 1: owner = CatalogStringArg(arg); break;
                case 2: qualifier = CatalogStringArg(arg); break;
                case 3: column = CatalogStringArg(arg); break;
                default: throw CatalogBindingError(SimulatedSqlException.TooManyArgumentsToFunction("sp_column_privileges"));
            }
        }

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        if (qualifier is not null && !collation.Equals(qualifier, database.Name))
            throw SimulatedSqlException.HelpObjectNotInCurrentDatabase();
        var columnPattern = CompileCatalogPattern(column ?? "%");
        var qualifierValue = SqlValue.FromSystemName(database.Name);
        var rows = ColumnPrivilegeRows(
            database,
            (schemaName, tableName) => (owner is null ? collation.Equals(schemaName, Database.DefaultSchemaName) : collation.Equals(owner, schemaName))
                && name is not null && collation.Equals(name, tableName),
            columnPattern)
            .ConvertAll(row => new SqlValue[]
            {
                qualifierValue, SqlValue.FromSystemName(row.Schema), SqlValue.FromSystemName(row.Table), SqlValue.FromSystemName(row.Column),
                SqlValue.FromSystemName(row.Grantor), SqlValue.FromSystemName(row.Grantee), SqlValue.FromVarchar(CatalogVarchar32, row.Privilege),
                SqlValue.FromVarchar(CatalogVarchar3, row.Grantable ? "YES" : "NO"),
            });
        foreach (var step in ProcedureSteps(batch, SpColumnPrivilegesSteps(owner, qualifier, column)))
            yield return step;
        yield return new SimulatedSqlResultSet(SpColumnPrivilegesSchema, SpColumnPrivilegesColumnNames, rows);
    }

    /// <summary>One row <c>sp_table_privileges</c> or <c>sp_column_privileges</c> (and their linked-server pair) reports; <see cref="Column"/> is empty for a table's.</summary>
    private readonly struct PrivilegeRow(string schema, string table, string column, string grantor, string grantee, string privilege, bool grantable)
    {
        public readonly string Schema = schema;
        public readonly string Table = table;
        public readonly string Column = column;
        public readonly string Grantor = grantor;
        public readonly string Grantee = grantee;
        public readonly string Privilege = privilege;
        public readonly bool Grantable = grantable;
    }

    /// <summary>
    /// The table privileges of every table and view <paramref name="include"/>
    /// takes (by schema and name): the owner's five granted by <c>dbo</c>, then
    /// every standing GRANT on the object or one of its columns, grantable when
    /// given WITH GRANT OPTION; sorted by schema, table, privilege and grantee
    /// under the database's collation, which puts '_' ahead of the letters.
    /// </summary>
    private static List<PrivilegeRow> TablePrivilegeRows(Database database, Func<string, string, bool> include)
    {
        var rows = new List<PrivilegeRow>();
        foreach (var (schemaName, ownerName, objectId, tableName) in PrivilegeObjects(database))
        {
            if (!include(schemaName, tableName))
                continue;
            foreach (var privilege in OwnerTablePrivileges)
                rows.Add(new(schemaName, tableName, "", Database.DefaultSchemaName, ownerName, privilege, true));
            var seen = new HashSet<(string, string, string)>();
            foreach (var (grantor, grantee, privilege, grantable, _) in ObjectGrants(database, objectId))
            {
                if (privilege is not null && seen.Add((grantor, grantee, privilege)))
                    rows.Add(new(schemaName, tableName, "", grantor, grantee, privilege, grantable));
            }
        }
        var collation = database.Collation;
        rows.Sort((a, b) =>
        {
            var order = collation.Compare(a.Schema, b.Schema);
            if (order == 0)
                order = collation.Compare(a.Table, b.Table);
            if (order == 0)
                order = string.CompareOrdinal(a.Privilege, b.Privilege);
            return order != 0 ? order : collation.Compare(a.Grantee, b.Grantee);
        });
        return rows;
    }

    /// <summary>
    /// The column privileges of every table and view <paramref name="include"/>
    /// takes, for each column <paramref name="columnPattern"/> matches: the
    /// owner's INSERT / REFERENCES / SELECT / UPDATE granted by <c>dbo</c>, then
    /// each standing GRANT on the object or that column, a permission other
    /// than the four DML ones reading as REFERENCES; sorted by schema, table,
    /// column, privilege and grantee.
    /// </summary>
    private static List<PrivilegeRow> ColumnPrivilegeRows(Database database, Func<string, string, bool> include, LikeMatcher? columnPattern)
    {
        var rows = new List<PrivilegeRow>();
        foreach (var (schemaName, ownerName, objectId, tableName) in PrivilegeObjects(database))
        {
            if (!include(schemaName, tableName))
                continue;
            var grants = ObjectGrants(database, objectId).ToList();
            var columns = Parser.Expressions.ColumnProperty.ColumnsOf(database, objectId) ?? [];
            for (var i = 0; i < columns.Length; i++)
            {
                var columnName = columns[i].Name;
                if (!Matches(columnPattern, columnName))
                    continue;
                var columnId = columns[i].ColumnId == 0 ? i + 1 : columns[i].ColumnId;
                var seen = new HashSet<(string, string, string)>();
                void Add(string grantor, string grantee, string privilege, bool grantable)
                {
                    if (seen.Add((grantor, grantee, privilege)))
                        rows.Add(new(schemaName, tableName, columnName, grantor, grantee, privilege, grantable));
                }
                foreach (var privilege in OwnerColumnPrivileges)
                    Add(Database.DefaultSchemaName, ownerName, privilege, true);
                foreach (var (grantor, grantee, privilege, grantable, minorId) in grants)
                {
                    if (minorId == 0 || minorId == columnId)
                        Add(grantor, grantee, privilege ?? "REFERENCES", grantable);
                }
            }
        }
        var collation = database.Collation;
        rows.Sort((a, b) =>
        {
            var order = collation.Compare(a.Schema, b.Schema);
            if (order == 0)
                order = collation.Compare(a.Table, b.Table);
            if (order == 0)
                order = collation.Compare(a.Column, b.Column);
            if (order == 0)
                order = string.CompareOrdinal(a.Privilege, b.Privilege);
            return order != 0 ? order : collation.Compare(a.Grantee, b.Grantee);
        });
        return rows;
    }

    /// <summary>
    /// The tables and views privileges are reported for — the database's own
    /// and its catalog views — with the schema owner who holds them.
    /// </summary>
    private static IEnumerable<(string Schema, string Owner, int ObjectId, string Name)> PrivilegeObjects(Database database)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            // The fixed schemas own their catalog views under their own names.
            var ownerName = schema.SchemaId is Database.InformationSchemaId or Database.SysSchemaId
                ? schema.Name
                : PrincipalName(database, schema.PrincipalId) ?? schema.Name;
            foreach (var (_, table) in schema.HeapTables)
                yield return (schema.Name, ownerName, table.ObjectId, table.Name);
            foreach (var (_, view) in schema.Views)
                yield return (schema.Name, ownerName, view.ObjectId, view.Name);
            foreach (var (systemView, schemaId) in BuiltInResources.SystemViews.Value)
            {
                if (schemaId == schema.SchemaId)
                    yield return (schema.Name, ownerName, systemView.ObjectId, systemView.Name);
            }
        }
    }

    /// <summary>
    /// The standing object-class GRANTs on <paramref name="objectId"/>, a
    /// DENY or REVOKE never listing; the privilege is null for a permission
    /// other than SELECT / INSERT / DELETE / UPDATE / REFERENCES.
    /// </summary>
    private static IEnumerable<(string Grantor, string Grantee, string? Privilege, bool Grantable, int MinorId)> ObjectGrants(Database database, int objectId)
    {
        foreach (var permission in database.Permissions)
        {
            if (permission.Class != 1 || permission.MajorId != objectId
                || permission.State is not (PermissionState.Grant or PermissionState.GrantWithGrantOption))
            {
                continue;
            }
            var privilege = permission.Permission switch
            {
                Permission.Delete => "DELETE",
                Permission.Insert => "INSERT",
                Permission.References => "REFERENCES",
                Permission.Select => "SELECT",
                Permission.Update => "UPDATE",
                _ => null,
            };
            yield return (
                PrincipalName(database, permission.GrantorPrincipalId) ?? Database.DefaultSchemaName,
                PrincipalName(database, permission.GranteePrincipalId) ?? string.Empty,
                privilege,
                permission.State == PermissionState.GrantWithGrantOption,
                permission.MinorId);
        }
    }

    private static string? PrincipalName(Database database, int principalId)
    {
        foreach (var (_, principal) in database.Principals)
        {
            if (principal.PrincipalId == principalId)
                return principal.Name;
        }
        return null;
    }
}
