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
                _ => throw SimulatedSqlException.InvalidProcedureParameters("sp_table_privileges"),
            };
            switch (slot)
            {
                case 0: name = CatalogStringArg(arg); break;
                case 1: owner = CatalogStringArg(arg); break;
                case 2: qualifier = CatalogStringArg(arg); break;
                case 3: usePattern = arg.Value.IsNull || arg.Value.CoerceTo(SqlType.Int32).AsInt32 != 0; break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_table_privileges");
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
        List<(string Owner, string Table, string Privilege, string Grantee, SqlValue[] Row)> rows = [];
        foreach (var (schemaName, ownerName, objectId, tableName) in PrivilegeObjects(database))
        {
            if (!NameMatches(ownerPattern, owner, schemaName) || !NameMatches(namePattern, name, tableName))
                continue;
            var ownerValue = SqlValue.FromSystemName(schemaName);
            var tableValue = SqlValue.FromSystemName(tableName);
            void Add(string grantor, string grantee, string privilege, bool grantable) =>
                rows.Add((schemaName, tableName, privilege, grantee, [qualifierValue, ownerValue, tableValue, SqlValue.FromSystemName(grantor), SqlValue.FromSystemName(grantee),
                    SqlValue.FromVarchar(CatalogVarchar32, privilege), SqlValue.FromVarchar(CatalogVarchar3, grantable ? "YES" : "NO")]));
            foreach (var privilege in OwnerTablePrivileges)
                Add(Database.DefaultSchemaName, ownerName, privilege, true);
            var seen = new HashSet<(string, string, string)>();
            foreach (var (grantor, grantee, privilege, grantable, _) in ObjectGrants(database, objectId))
            {
                if (privilege is not null && seen.Add((grantor, grantee, privilege)))
                    Add(grantor, grantee, privilege, grantable);
            }
        }

        // Names sort under the catalog's collation, which puts '_' ahead of
        // the letters.
        rows.Sort((a, b) =>
        {
            var byOwner = collation.Compare(a.Owner, b.Owner);
            if (byOwner != 0)
                return byOwner;
            var byTable = collation.Compare(a.Table, b.Table);
            if (byTable != 0)
                return byTable;
            var byPrivilege = string.CompareOrdinal(a.Privilege, b.Privilege);
            return byPrivilege != 0 ? byPrivilege : collation.Compare(a.Grantee, b.Grantee);
        });
        yield return new SimulatedSqlResultSet(SpTablePrivilegesSchema, SpTablePrivilegesColumnNames, [.. rows.Select(r => r.Row)]);
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
                _ => throw SimulatedSqlException.InvalidProcedureParameters("sp_column_privileges"),
            };
            switch (slot)
            {
                case 0: name = CatalogStringArg(arg); break;
                case 1: owner = CatalogStringArg(arg); break;
                case 2: qualifier = CatalogStringArg(arg); break;
                case 3: column = CatalogStringArg(arg); break;
                default: throw SimulatedSqlException.InvalidProcedureParameters("sp_column_privileges");
            }
        }

        var database = batch.CurrentDatabase;
        var collation = database.Collation;
        if (qualifier is not null && !collation.Equals(qualifier, database.Name))
            throw SimulatedSqlException.HelpObjectNotInCurrentDatabase();
        var columnPattern = CompileCatalogPattern(column ?? "%");
        var qualifierValue = SqlValue.FromSystemName(database.Name);

        List<(string Column, string Privilege, string Grantee, SqlValue[] Row)> rows = [];
        foreach (var (schemaName, ownerName, objectId, tableName) in PrivilegeObjects(database))
        {
            var schemaMatches = owner is null ? collation.Equals(schemaName, Database.DefaultSchemaName) : collation.Equals(owner, schemaName);
            if (!schemaMatches || name is null || !collation.Equals(name, tableName))
                continue;
            var ownerValue = SqlValue.FromSystemName(schemaName);
            var tableValue = SqlValue.FromSystemName(name);
            var grants = ObjectGrants(database, objectId).ToList();
            var columns = Parser.Expressions.ColumnProperty.ColumnsOf(database, objectId) ?? [];
            for (var i = 0; i < columns.Length; i++)
            {
                var columnName = columns[i].Name;
                if (!Matches(columnPattern, columnName))
                    continue;
                var columnId = columns[i].ColumnId == 0 ? i + 1 : columns[i].ColumnId;
                var columnValue = SqlValue.FromSystemName(columnName);
                var seen = new HashSet<(string, string, string)>();
                void Add(string grantor, string grantee, string privilege, bool grantable)
                {
                    if (seen.Add((grantor, grantee, privilege)))
                    {
                        rows.Add((columnName, privilege, grantee, [qualifierValue, ownerValue, tableValue, columnValue, SqlValue.FromSystemName(grantor), SqlValue.FromSystemName(grantee),
                            SqlValue.FromVarchar(CatalogVarchar32, privilege), SqlValue.FromVarchar(CatalogVarchar3, grantable ? "YES" : "NO")]));
                    }
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

        rows.Sort((a, b) =>
        {
            var byColumn = collation.Compare(a.Column, b.Column);
            if (byColumn != 0)
                return byColumn;
            var byPrivilege = string.CompareOrdinal(a.Privilege, b.Privilege);
            return byPrivilege != 0 ? byPrivilege : collation.Compare(a.Grantee, b.Grantee);
        });
        yield return new SimulatedSqlResultSet(SpColumnPrivilegesSchema, SpColumnPrivilegesColumnNames, [.. rows.Select(r => r.Row)]);
    }

    /// <summary>
    /// The tables and views privileges are reported for — the database's own
    /// and its catalog views — with the schema owner who holds them.
    /// </summary>
    private static IEnumerable<(string Schema, string Owner, int ObjectId, string Name)> PrivilegeObjects(Database database)
    {
        foreach (var schema in database.Schemas.Values)
        {
            // The fixed schemas own their catalog views under their own names.
            var ownerName = schema.SchemaId is Database.InformationSchemaId or Database.SysSchemaId
                ? schema.Name
                : PrincipalName(database, schema.PrincipalId) ?? schema.Name;
            foreach (var table in schema.HeapTables.Values)
                yield return (schema.Name, ownerName, table.ObjectId, table.Name);
            foreach (var view in schema.Views.Values)
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
        foreach (var principal in database.Principals.Values)
        {
            if (principal.PrincipalId == principalId)
                return principal.Name;
        }
        return null;
    }
}
