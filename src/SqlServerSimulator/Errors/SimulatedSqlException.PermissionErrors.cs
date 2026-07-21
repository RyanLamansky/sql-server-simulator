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
