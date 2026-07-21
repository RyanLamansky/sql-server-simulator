namespace SqlServerSimulator;

/// <summary>
/// Permission-enforcement error factories (Msg 229 / 262 / 1088 / 4606 /
/// 4611). The 15151 object-variant and the impersonation errors (15517 /
/// 15406) live in <c>SimulatedSqlException.SchemaErrors.cs</c> alongside the
/// principal-resolution factories.
/// </summary>
public sealed partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 229: a DML / EXECUTE permission was denied on an
    /// object. Severity 14, state 5, probe-confirmed wording. For an EXEC-proc
    /// denial <paramref name="procedure"/> carries the schema-qualified proc
    /// name (surfaces through <c>ERROR_PROCEDURE()</c>), matching real; empty
    /// for table / view / TVF denials.
    /// </summary>
    internal static SimulatedSqlException PermissionDenied(string permission, string objectName, string databaseName, string schemaName, string procedure = "") =>
        new($"The {permission} permission was denied on the object '{objectName}', database '{databaseName}', schema '{schemaName}'.",
            new SimulatedError(@class: 14, lineNumber: 0,
                message: $"The {permission} permission was denied on the object '{objectName}', database '{databaseName}', schema '{schemaName}'.",
                number: 229, procedure: procedure, server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 5));

    /// <summary>
    /// Mimics SQL Server error 262: <c>CREATE TABLE</c> attempted by a principal
    /// lacking <c>db_ddladmin</c> / <c>db_owner</c> membership (or an explicit
    /// CREATE TABLE grant). Severity 14, state 1, probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException CreateTablePermissionDenied(string databaseName) =>
        new($"CREATE TABLE permission denied in database '{databaseName}'.", 262, 14, 1);

    /// <summary>
    /// Mimics SQL Server error 262 for a database-scope DMV read denied by a
    /// missing <c>VIEW DATABASE PERFORMANCE STATE</c> (or covering) permission.
    /// Same shape as the CREATE TABLE 262 (severity 14, state 1), with the
    /// permission name parameterized — probe-confirmed wording. Real also raises a
    /// trailing Msg 297; the simulator surfaces the single Msg 262.
    /// </summary>
    internal static SimulatedSqlException DatabaseStatePermissionDenied(string permission, string databaseName) =>
        new($"{permission} permission denied in database '{databaseName}'.", 262, 14, 1);

    /// <summary>
    /// Mimics SQL Server error 300 for a server-scope DMV read denied by a missing
    /// <c>VIEW SERVER PERFORMANCE STATE</c> (or covering <c>VIEW SERVER STATE</c>)
    /// permission. Severity 14, state 1, probe-confirmed wording. Real also raises
    /// a trailing Msg 297; the simulator surfaces the single Msg 300.
    /// </summary>
    internal static SimulatedSqlException ServerStatePermissionDenied(string permission, string databaseName) =>
        new($"{permission} permission was denied on object 'server', database '{databaseName}'.", 300, 14, 1);

    /// <summary>
    /// Mimics SQL Server error 262 for a <c>CREATE VIEW</c> / <c>PROCEDURE</c> /
    /// <c>FUNCTION</c> denied by a missing database-scope CREATE-of-that-kind
    /// permission. Severity 14, <strong>state 18</strong>, with the object being
    /// created carried as the <c>Procedure</c> attribution (surfaces through
    /// <c>ERROR_PROCEDURE()</c>) — probe-confirmed distinct from CREATE TABLE's
    /// state 1 / no-attribution shape.
    /// </summary>
    internal static SimulatedSqlException CreateModulePermissionDenied(string permission, string databaseName, string moduleName) =>
        new($"{permission} permission denied in database '{databaseName}'.",
            new SimulatedError(@class: 14, lineNumber: 0,
                message: $"{permission} permission denied in database '{databaseName}'.",
                number: 262, procedure: moduleName, server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 18));

    /// <summary>
    /// Mimics SQL Server error 15247: a DDL statement the simulator doesn't model
    /// as a named permission (<c>CREATE SEQUENCE</c> / <c>CREATE ROLE</c> /
    /// <c>CREATE USER</c> / <c>CREATE SCHEMA</c>) attempted by a non-privileged
    /// principal. Severity 16, state 1, probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException UserDoesNotHavePermission() =>
        new("User does not have permission to perform this action.", 15247, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1088 for an <c>ALTER TABLE</c> denied by a missing
    /// ALTER permission on the object. Same double-quoted wording as TRUNCATE's
    /// 1088 but <strong>state 13</strong> (TRUNCATE uses state 7) — probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException AlterTablePermissionDenied(string qualifiedName) =>
        new($"Cannot find the object \"{qualifiedName}\" because it does not exist or you do not have permissions.", 1088, 16, 13);

    /// <summary>
    /// Mimics SQL Server error 3701 for a <c>DROP TABLE</c> denied by a missing
    /// ALTER permission on the schema. Same wording as the not-found 3701 but
    /// <strong>severity 14, state 20</strong> (the not-found form is sev 11 state
    /// 5) — probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException DropTablePermissionDenied(string name) =>
        new($"Cannot drop the table '{name}', because it does not exist or you do not have permission.", 3701, 14, 20);

    /// <summary>
    /// Mimics SQL Server error 15151 for a <c>DROP USER</c> denied to a principal
    /// that isn't <c>dbo</c> / a <c>db_owner</c> member (the simulator has no
    /// ALTER ANY USER model). Severity 16, state 1 (state approximate — the
    /// reference login can't reach the check).
    /// </summary>
    internal static SimulatedSqlException DropUserPermissionDenied(string name) =>
        new($"Cannot drop the user '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Mimics SQL Server error 15151: <c>ALTER SERVER ROLE</c> naming a role that doesn't exist. Probe-confirmed wording (probe6 N6).</summary>
    internal static SimulatedSqlException CannotAlterServerRole(string roleName) =>
        new($"Cannot alter the server role '{roleName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Mimics SQL Server error 15151: <c>ALTER SERVER ROLE … ADD MEMBER</c> naming a server principal that doesn't exist. Probe-confirmed wording (probe6 N6).</summary>
    internal static SimulatedSqlException CannotAddServerPrincipal(string loginName) =>
        new($"Cannot add the server principal '{loginName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Mimics SQL Server error 15151: server-scope GRANT / DENY / REVOKE naming a login that doesn't exist. Probe-confirmed wording (probe6 N6).</summary>
    internal static SimulatedSqlException CannotFindLogin(string loginName) =>
        new($"Cannot find the login '{loginName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Mimics SQL Server error 15151: <c>DROP SERVER ROLE</c> naming a role that doesn't exist. Probe-confirmed 15151 wording family.</summary>
    internal static SimulatedSqlException CannotDropServerRole(string roleName) =>
        new($"Cannot drop the server role '{roleName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Mimics SQL Server error 15150: attempt to <c>DROP SERVER ROLE</c> a fixed server role. Probe-confirmed wording (probe6 N6).</summary>
    internal static SimulatedSqlException CannotDropFixedServerRole(string roleName) =>
        new($"Cannot drop the server role '{roleName}'.", 15150, 16, 1);

    /// <summary>Mimics SQL Server error 4621: a server-scope permission granted outside the <c>master</c> database. Severity 16, state 1, probe-confirmed wording (probe6 N7).</summary>
    internal static SimulatedSqlException ServerPermissionsMasterOnly() =>
        new("Permissions at the server scope can only be granted when the current database is master", 4621, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1088: a TRUNCATE (which requires ALTER on the
    /// object) was denied. Distinct shape from Msg 229 — double-quoted name,
    /// severity 16, state 7, probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException CannotFindObjectForAlter(string objectLeafName) =>
        new($"Cannot find the object \"{objectLeafName}\" because it does not exist or you do not have permissions.", 1088, 16, 7);

    /// <summary>
    /// Mimics SQL Server error 4606: a permission is incompatible with the
    /// securable's object kind (SELECT on a procedure, EXECUTE on a table /
    /// view / TVF). Severity 16, probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException PermissionIncompatibleWithObject(string permission) =>
        new($"Granted or revoked privilege {permission} is not compatible with object.", 4606, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 4611: a plain <c>REVOKE</c> (or REVOKE GRANT
    /// OPTION FOR) of a grantable (<c>WITH GRANT OPTION</c>) permission that has
    /// live delegations, without the CASCADE option. Severity 16, catchable,
    /// probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException RevokeRequiresCascade() =>
        new("To revoke or deny grantable privileges, specify the CASCADE option.", 4611, 16, 1);
}
