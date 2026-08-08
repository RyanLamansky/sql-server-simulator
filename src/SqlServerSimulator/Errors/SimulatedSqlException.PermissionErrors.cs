namespace SqlServerSimulator;

// Permission-enforcement error factories (Msg 229 / 262 / 1088 / 4606 / 4611).
// The 15151 object-variant and the impersonation errors (15517 / 15406) live
// in SimulatedSqlException.SchemaErrors.cs alongside the principal-resolution
// factories.
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
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
    /// Mimics SQL Server error 230: a SELECT / UPDATE (/ REFERENCES) permission
    /// was denied on a specific <em>column</em> of an object — the column-level
    /// grant model's denial, naming the first inaccessible column. Severity 14,
    /// state 1, probe-confirmed wording. Fires only when the principal has
    /// <em>partial</em> access to the object (a column grant, or a table grant
    /// with a column DENY); with no access at all the object-level Msg 229 fires
    /// instead.
    /// </summary>
    internal static SimulatedSqlException ColumnPermissionDenied(string permission, string columnName, string objectName, string databaseName, string schemaName) =>
        new($"The {permission} permission was denied on the column '{columnName}' of the object '{objectName}', database '{databaseName}', schema '{schemaName}'.",
            new SimulatedError(@class: 14, lineNumber: 0,
                message: $"The {permission} permission was denied on the column '{columnName}' of the object '{objectName}', database '{databaseName}', schema '{schemaName}'.",
                number: 230, procedure: "", server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 1));

    /// <summary>
    /// Mimics SQL Server error 4615: a <c>GRANT</c> / <c>DENY</c> / <c>REVOKE</c>
    /// column list named a column the object doesn't have. Severity 16, state 1,
    /// probe-confirmed wording (distinct from the query-time Msg 207
    /// <c>InvalidColumnName</c>).
    /// </summary>
    internal static SimulatedSqlException GrantInvalidColumnName(string columnName) =>
        new($"Invalid column name '{columnName}'.", 4615, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1019: a <c>GRANT</c> / <c>REVOKE</c> named a column
    /// list both after a permission and after the object name
    /// (<c>GRANT SELECT (a) ON t (b)</c>). Severity 15, state 1, probe-confirmed
    /// wording.
    /// </summary>
    internal static SimulatedSqlException GrantInvalidColumnListAfterObject() =>
        new("Invalid column list after object name in GRANT/REVOKE statement.", 1019, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 1020: a column list was given for a permission
    /// whose securable isn't an object (<c>GRANT SELECT ON SCHEMA::s (c)</c>).
    /// Severity 15, state 1, probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException GrantSubEntityListNotAllowed() =>
        new("Sub-entity lists (such as column or security expressions) cannot be specified for entity-level permissions.", 1020, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 1020 for a column list on a <em>synonym</em>
    /// securable. Same wording as <see cref="GrantSubEntityListNotAllowed"/> but
    /// severity 16 state 3, because real raises it after the securable resolves
    /// (so it is a catchable runtime error, and it beats the Msg 4615
    /// unknown-column check) rather than as the compile-time class-15 rejection
    /// an entity-level <em>permission</em> gets. Probe-confirmed against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException GrantSubEntityListNotAllowedOnSynonym() =>
        new("Sub-entity lists (such as column or security expressions) cannot be specified for entity-level permissions.", 1020, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 262: a database-scope permission the statement
    /// needs is missing. Severity 14, state 1, probe-confirmed wording, shared by
    /// the <c>CREATE TABLE</c> / <c>CREATE SYNONYM</c> / <c>CREATE TYPE</c> /
    /// <c>CREATE XML SCHEMA COLLECTION</c> / <c>CREATE ASSEMBLY</c> gates, the
    /// server-scope <c>CREATE DATABASE</c> gate (which names <c>master</c>), and
    /// the database-scope DMV read denied by a missing <c>VIEW DATABASE
    /// PERFORMANCE STATE</c>. The <c>CREATE VIEW</c> / <c>PROCEDURE</c> /
    /// <c>FUNCTION</c> family takes the state-18 variant instead — see
    /// <see cref="CreateModulePermissionDenied"/>. Real also raises a trailing
    /// Msg 297 on the DMV path; the simulator surfaces the single Msg 262.
    /// </summary>
    internal static SimulatedSqlException DatabasePermissionDenied(string permission, string databaseName) =>
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
    /// Mimics SQL Server error 3701 for a <c>DROP</c> denied by the missing
    /// schema-ALTER / object-CONTROL pair. Same wording as the not-found 3701 but
    /// <strong>severity 14, state 20</strong> (the not-found form is sev 11 state
    /// 5) — probe-confirmed for every object kind, each naming its own noun
    /// (<c>table</c> / <c>view</c> / <c>procedure</c> / <c>function</c> /
    /// <c>trigger</c> / <c>sequence</c> / <c>synonym</c>) and the object's leaf.
    /// </summary>
    internal static SimulatedSqlException DropObjectPermissionDenied(string objectKind, string name) =>
        new($"Cannot drop the {objectKind} '{name}', because it does not exist or you do not have permission.", 3701, 14, 20);

    /// <summary>
    /// Mimics SQL Server error 3701 for an <c>ALTER</c> / <c>CREATE OR ALTER</c>
    /// of an existing module denied by a missing ALTER permission on it. The
    /// <c>Cannot alter the …</c> sibling of <see cref="DropObjectPermissionDenied"/>,
    /// same severity 14 / state 20, probe-confirmed for <c>procedure</c> /
    /// <c>view</c> / <c>function</c> / <c>trigger</c>.
    /// </summary>
    internal static SimulatedSqlException AlterObjectPermissionDenied(string objectKind, string name) =>
        new($"Cannot alter the {objectKind} '{name}', because it does not exist or you do not have permission.", 3701, 14, 20);

    /// <summary>
    /// Mimics SQL Server error 3701 for a <c>DROP DATABASE</c> denied by a
    /// missing server-scope authority. Distinct from every object drop:
    /// <strong>severity 11, state 2</strong> (probe-confirmed).
    /// </summary>
    internal static SimulatedSqlException DropDatabasePermissionDenied(string name) =>
        new($"Cannot drop the database '{name}', because it does not exist or you do not have permission.", 3701, 11, 2);

    /// <summary>
    /// Mimics SQL Server error 2104: <c>CREATE TRIGGER</c> denied by a missing
    /// ALTER permission on the parent object (a DML trigger) or the missing
    /// <c>ALTER ANY DATABASE DDL TRIGGER</c> (a database-scope one). Severity 14,
    /// state 1, probe-confirmed wording — the name is echoed as written, so a
    /// two-part <c>dbo.tr</c> reports both parts.
    /// </summary>
    internal static SimulatedSqlException CreateTriggerPermissionDenied(string name) =>
        new($"Cannot create the trigger '{name}', because you do not have permission.", 2104, 14, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 for an <c>ALTER SEQUENCE</c> denied by a
    /// missing ALTER permission on the sequence. Severity 16, state 1,
    /// probe-confirmed wording (the same record a missing sequence earns).
    /// </summary>
    internal static SimulatedSqlException CannotAlterSequence(string name) =>
        new($"Cannot alter the sequence '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 for a <c>DROP XML SCHEMA COLLECTION</c>
    /// denied by the missing schema-ALTER / collection-CONTROL pair. Severity 16,
    /// state 1, probe-confirmed wording (lowercase object noun).
    /// </summary>
    internal static SimulatedSqlException CannotDropXmlSchemaCollection(string name) =>
        new($"Cannot drop the xml schema collection '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 for a <c>DROP ROLE</c> denied by a missing
    /// <c>ALTER ANY ROLE</c>. Severity 16, state 1, probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException CannotDropRole(string name) =>
        new($"Cannot drop the role '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 for an <c>ALTER ROLE</c> denied by a missing
    /// <c>ALTER ANY ROLE</c>. Severity 16, <strong>state 2</strong> — probe-confirmed
    /// distinct from the DROP ROLE state 1.
    /// </summary>
    internal static SimulatedSqlException CannotAlterRole(string name) =>
        new($"Cannot alter the role '{name}', because it does not exist or you do not have permission.", 15151, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 15151 for the <c>ALTER SCHEMA … TRANSFER</c> half
    /// that checks the moved object: real requires CONTROL on it, over and above
    /// ALTER on the destination schema (which is checked first and reports
    /// <see cref="CannotAlterSchemaDoesNotExist"/>). Severity 16, state 1,
    /// probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException CannotTransferObject(string name) =>
        new($"Cannot transfer the object '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 5011 for an <c>ALTER DATABASE</c> denied by a
    /// missing ALTER permission on the database. Same wording as the
    /// unknown-database <see cref="CannotAlterDatabase"/> but
    /// <strong>state 9</strong> — probe-confirmed. Real follows it with a
    /// terminating Msg 5069 (<c>ALTER DATABASE statement failed.</c>); the
    /// simulator surfaces the single 5011, matching how the other paired
    /// diagnostics are modeled.
    /// </summary>
    internal static SimulatedSqlException AlterDatabasePermissionDenied(string databaseName) =>
        new($"User does not have permission to alter database '{databaseName}', the database does not exist, or the database is not in a state that allows access checks.", 5011, 14, 9);

    /// <summary>
    /// Mimics SQL Server error 7666: <c>CREATE FULLTEXT CATALOG</c> denied by a
    /// missing <c>CREATE FULLTEXT CATALOG</c> permission. Severity 16, state 2,
    /// probe-confirmed wording (the same sentence Msg 15247 carries, at a
    /// different number).
    /// </summary>
    internal static SimulatedSqlException FullTextUserDoesNotHavePermission() =>
        new("User does not have permission to perform this action.", 7666, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 7641 for a <c>DROP FULLTEXT CATALOG</c> denied by
    /// a missing database-scope ALTER. Severity 16, state 5, probe-confirmed
    /// wording.
    /// </summary>
    internal static SimulatedSqlException FullTextCatalogNotFoundOrDenied(string catalogName, string databaseName) =>
        new($"Full-Text catalog '{catalogName}' does not exist in database '{databaseName}' or user does not have permission to perform this action.", 7641, 16, 5);

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

    /// <summary>Mimics SQL Server error 4621: a server-scope permission granted outside the <c>master</c> database. Severity 16, state 10, probe-confirmed wording (no trailing period) for both the ON-less and <c>ON LOGIN::</c> forms.</summary>
    internal static SimulatedSqlException ServerPermissionsMasterOnly() =>
        new("Permissions at the server scope can only be granted when the current database is master", 4621, 16, 10);

    /// <summary>
    /// Mimics SQL Server error 15161: <c>sp_setapprole</c> naming an
    /// application role that doesn't exist, or supplying the wrong password.
    /// Real leaks no distinction between the two. Severity 16, state 1,
    /// probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException CannotSetApplicationRole(string roleName) =>
        new($"Cannot set application role '{roleName}' because it does not exist or the password is incorrect.", 15161, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2762: <c>sp_setapprole</c> called on a session
    /// that already has an application role set. Severity 16, state 1,
    /// probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException SetApplicationRoleNotInvokedCorrectly() =>
        new("sp_setapprole was not invoked correctly. Refer to the documentation for more information.", 2762, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15592: <c>sp_unsetapprole</c> with no role set,
    /// or with a cookie that doesn't match the one <c>sp_setapprole …
    /// @fCreateCookie = 1</c> issued. Severity 16, state 1, probe-confirmed
    /// wording.
    /// </summary>
    internal static SimulatedSqlException CannotUnsetApplicationRole() =>
        new("Cannot unset application role because none was set or the cookie is invalid.", 15592, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 505: a <c>USE</c> / <c>ChangeDatabase</c> attempt
    /// while an application role is active — the activation pins the session to
    /// the database that set it. Severity 16, state 1, probe-confirmed wording
    /// (real names SETUSER alongside sp_setapprole).
    /// </summary>
    internal static SimulatedSqlException CannotChangeDatabaseUnderApplicationRole() =>
        new("The current user account was invoked with SETUSER or SP_SETAPPROLE. Changing databases is not allowed.", 505, 16, 1);

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
