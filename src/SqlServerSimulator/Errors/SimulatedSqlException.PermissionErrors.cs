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
    /// Mimics SQL Server error 4610: a <c>DATABASE::</c> securable naming a
    /// database other than the current one (probed 2026-09-28 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException GrantOnAnotherDatabase() =>
        new("You can only grant or revoke permissions on objects in the current database.", 4610, 16, 1);

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
    /// A denied CREATE ends the batch — through an <c>EXEC</c> too — and rolls
    /// the transaction back as under <c>SET XACT_ABORT ON</c>, catchable by a
    /// TRY (probed 2026-09-27 against SQL Server 2025); the DMV path's scope is
    /// unprobed and keeps a plain error.
    /// </summary>
    internal static SimulatedSqlException DatabasePermissionDenied(string permission, string databaseName, bool aborts = true) =>
        new($"{permission} permission denied in database '{databaseName}'.", 262, 14, 1) { AbortsAsUnderXactAbort = aborts };

    /// <summary>
    /// Mimics SQL Server error 371 — <c>sys.dm_exec_connections</c> read without
    /// <c>VIEW SERVER PERFORMANCE STATE</c>, where the other server DMVs raise
    /// Msg 300. Severity 14, state 3, probed 2026-09-25 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ServerStatePolicyDenied() =>
        new("The user does not have the external policy action 'Microsoft.Sql/Sqlservers/SystemViewsAndFunctions/ServerPerformanceState/Rows/Select' or permission 'VIEW SERVER PERFORMANCE STATE' to perform this action.", 371, 14, 3);

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
                number: 262, procedure: moduleName, server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 18))
        {
            AbortsAsUnderXactAbort = true,
        };

    /// <summary>
    /// Mimics SQL Server error 15247: a DDL statement the simulator doesn't model
    /// as a named permission (<c>CREATE SEQUENCE</c> / <c>CREATE ROLE</c> /
    /// <c>CREATE USER</c> / <c>CREATE SCHEMA</c>) attempted by a non-privileged
    /// principal, and the server-scope refusals real words the same way —
    /// <c>CREATE LOGIN</c>, <c>CREATE SERVER ROLE</c>, an <c>sp_configure</c>
    /// write and the linked-server procedures. Severity 16, state 1,
    /// probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException UserDoesNotHavePermission(byte state = 1) =>
        new("User does not have permission to perform this action.", 15247, 16, state);

    /// <summary>
    /// Mimics SQL Server error 5812: <c>RECONFIGURE</c> by a session without
    /// <c>ALTER SETTINGS</c> (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ReconfigurePermissionDenied() =>
        new("You do not have permission to run the RECONFIGURE statement.", 5812, 14, 1);

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
    internal static SimulatedSqlException CannotAlterRole(string name, byte state = 2) =>
        new($"Cannot alter the role '{name}', because it does not exist or you do not have permission.", 15151, 16, state);

    /// <summary>
    /// Mimics SQL Server error 15151 as a membership change words a member
    /// that doesn't exist, naming the verb (<c>add</c> or <c>drop</c>) —
    /// probed 2026-09-25 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotChangeMembershipOfPrincipal(string verb, string name) =>
        new($"Cannot {verb} the principal '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15081: <c>ALTER ROLE [public]</c> or
    /// <c>sp_addrolemember 'public'</c> (probed 2026-09-25).
    /// </summary>
    internal static SimulatedSqlException PublicRoleMembershipFixed() =>
        new("Membership of the public role cannot be changed.", 15081, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15410, class 11: <c>sp_addrolemember</c> naming a
    /// member that doesn't exist, where <c>ALTER ROLE</c> reports Msg 15151
    /// (probed 2026-09-25).
    /// </summary>
    internal static SimulatedSqlException UserOrRoleDoesNotExist(string name) =>
        new($"User or role '{name}' does not exist in this database.", 15410, 11, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 state 1 for <c>ALTER USER</c> of a user
    /// that doesn't exist or can't be altered (probed 2026-09-25).
    /// </summary>
    internal static SimulatedSqlException CannotAlterUser(string name) =>
        new($"Cannot alter the user '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15405: <c>ALTER ROLE … ADD MEMBER dbo</c>, the
    /// special principal no role may hold (probed 2026-09-25).
    /// </summary>
    internal static SimulatedSqlException CannotUseSpecialPrincipal(string name) =>
        new($"Cannot use the special principal '{name}'.", 15405, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15144: <c>DROP ROLE</c> of a role that still has
    /// members (probed 2026-09-25).
    /// </summary>
    internal static SimulatedSqlException RoleHasMembers() =>
        new("The role has members. It must be empty before it can be dropped.", 15144, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15413: <c>ALTER ROLE r ADD MEMBER r</c> (probed
    /// 2026-09-25).
    /// </summary>
    internal static SimulatedSqlException RoleMemberOfItself() =>
        new("Cannot make a role a member of itself.", 15413, 16, 1);

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
    internal static SimulatedSqlException FullTextUserDoesNotHavePermission(byte state = 2) =>
        new("User does not have permission to perform this action.", 7666, 16, state);

    /// <summary>
    /// Mimics SQL Server error 7641 for a <c>DROP FULLTEXT CATALOG</c> (state
    /// 5) or <c>ALTER FULLTEXT CATALOG</c> (state 2) of a catalog that's
    /// missing or denied. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException FullTextCatalogNotFoundOrDenied(string catalogName, string databaseName, byte state = 5) =>
        new($"Full-Text catalog '{catalogName}' does not exist in database '{databaseName}' or user does not have permission to perform this action.", 7641, 16, state);

    /// <summary>Mimics SQL Server error 15151: <c>ALTER APPLICATION ROLE</c> naming one that doesn't exist (probed 2026-09-29 against SQL Server 2025).</summary>
    internal static SimulatedSqlException CannotAlterApplicationRole(string name) =>
        new($"Cannot alter the application role '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Mimics SQL Server error 15151: <c>DROP APPLICATION ROLE</c> naming one that doesn't exist (probed 2026-09-29 against SQL Server 2025).</summary>
    internal static SimulatedSqlException CannotDropApplicationRole(string name) =>
        new($"Cannot drop the application role '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Mimics SQL Server error 15151: <c>ALTER SERVER ROLE</c> naming a role that doesn't exist. Probe-confirmed wording (probe6 N6).</summary>
    internal static SimulatedSqlException CannotAlterServerRole(string roleName) =>
        new($"Cannot alter the server role '{roleName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Mimics SQL Server error 15151: <c>ALTER SERVER ROLE … DROP MEMBER</c> naming a server principal that doesn't exist (probed 2026-09-30).</summary>
    internal static SimulatedSqlException CannotDropServerPrincipal(string loginName) =>
        new($"Cannot drop the server principal '{loginName}', because it does not exist or you do not have permission.", 15151, 16, 1);

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
    internal static SimulatedSqlException CannotUnsetApplicationRole(byte state = 1) =>
        new("Cannot unset application role because none was set or the cookie is invalid.", 15592, 16, state);

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

    /// <summary>
    /// A foreign key whose referenced columns the creator may not reference:
    /// the REFERENCES denial (Msg 229 or one Msg 230 per column), then Msg
    /// 1088 state 20 naming the table as written, then Msg 1750 (probed
    /// 2026-10-04 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ForeignKeyReferencesDenied(SimulatedSqlException denied, string referencedTableAsWritten) =>
        FollowedByConstraintNotCreated(Aggregate([denied, CannotFindObjectForCreateIndex(referencedTableAsWritten, 20)]));

    /// <summary>Mimics SQL Server error 15199: a <c>REVERT</c> run in a database other than the one its <c>EXECUTE AS</c> ran in (probed 2026-10-04 against SQL Server 2025). This and the two REVERT refusals below end the batch.</summary>
    internal static SimulatedSqlException RevertInAnotherDatabase() =>
        new("The current security context cannot be reverted. Please switch to the original database where 'Execute As' was called and try it again.", 15199, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 15196: a <c>REVERT</c> of an <c>EXECUTE AS … WITH NO REVERT</c> context.</summary>
    internal static SimulatedSqlException RevertOfNonRevertibleContext() =>
        new("The current security context is non-revertible. The \"Revert\" statement failed.", 15196, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 15591: a <c>REVERT</c> without the cookie its <c>EXECUTE AS … WITH COOKIE INTO</c> issued, or with one it didn't.</summary>
    internal static SimulatedSqlException RevertNeedsMatchingCookie() =>
        new("The current security context cannot be reverted using this statement. A cookie may or may not be needed with 'Revert' statement depending on how the context was set with 'Execute As' statement.", 15591, 16, 1) { TerminatesBatch = true };

    /// <summary>Mimics SQL Server error 15533: a <c>REVERT WITH COOKIE</c> whose cookie isn't a <c>varbinary(100)</c> variable.</summary>
    internal static SimulatedSqlException RevertCookieWrongType() =>
        new("Invalid data type is supplied in the 'Revert' statement.", 15533, 16, 2);

    /// <summary>Mimics SQL Server error 4617: a GRANT, DENY or REVOKE naming a fixed database role as grantee (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException GrantToSpecialRole() =>
        new("Cannot grant, deny or revoke permissions to or from special roles.", 4617, 16, 1);

    /// <summary>Mimics SQL Server error 4613: a database-scope GRANT, DENY or REVOKE by a principal without the authority to make it (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException GrantorLacksGrantPermission() =>
        new("Grantor does not have GRANT permission.", 4613, 16, 1);

    /// <summary>Mimics SQL Server error 4629: a permission on an <c>INFORMATION_SCHEMA</c> view or a system procedure outside <c>master</c> (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException GrantOnServerScopedObjectOutsideMaster() =>
        new("Permissions on server scoped catalog views or system stored procedures or extended stored procedures can be granted only when the current database is master.", 4629, 16, 10);

    /// <summary>Mimics SQL Server error 15539: <c>DROP USER guest</c> (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException GuestCannotBeDropped() =>
        new("User 'guest' cannot be dropped, it can only be disabled. The user is already disabled in the current database.", 15539, 16, 1);

    /// <summary>Mimics SQL Server error 15284: dropping a principal that is the grantor of a permission (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException PrincipalHasGrantedPermissions() =>
        new("The database principal has granted or denied permissions to objects in the database and cannot be dropped.", 15284, 16, 1);

    /// <summary>Mimics SQL Server error 15150: renaming a fixed database role (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException CannotAlterFixedRole(string roleName) =>
        new($"Cannot alter the role '{roleName}'.", 15150, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1088 state 11 for a <c>SET IDENTITY_INSERT</c>
    /// the caller holds no <c>ALTER</c> for: the missing-object wording, and
    /// it ends the batch — a procedure stops at it (probed 2026-10-04 against
    /// SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException IdentityInsertDenied(string writtenName) =>
        new($"Cannot find the object \"{writtenName}\" because it does not exist or you do not have permissions.", 1088, 16, 11) { TerminatesBatch = true };

    /// <summary>
    /// A schema-bound module's creator lacks REFERENCES on an object the body
    /// binds to: Msg 229, then Msg 1088 state 18 naming the object's leaf, both
    /// attributed to the module (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException SchemaBoundReferencesDenied(string objectName, string databaseName, string schemaName, string moduleName)
    {
        var denied = $"The REFERENCES permission was denied on the object '{objectName}', database '{databaseName}', schema '{schemaName}'.";
        var notFound = $"Cannot find the object \"{objectName}\" because it does not exist or you do not have permissions.";
        return Aggregate(
        [
            new(denied, new SimulatedError(@class: 14, lineNumber: 0, message: denied, number: 229, procedure: moduleName, server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 5)),
            new(notFound, new SimulatedError(@class: 16, lineNumber: 0, message: notFound, number: 1088, procedure: moduleName, server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 18)),
        ]);
    }

    /// <summary>Mimics SQL Server error 15062: <c>CREATE USER guest</c> (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException GuestCannotBeMapped() =>
        new("The guest user cannot be mapped to a login name.", 15062, 16, 1);

    /// <summary>Mimics SQL Server error 15431: <c>sp_setapprole</c> with no role name (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException RoleNameParameterRequired() =>
        new("You must specify the @rolename parameter.", 15431, 16, 1);

    /// <summary>Mimics SQL Server error 15422: <c>sp_setapprole</c> from a procedure or dynamic SQL (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException ApplicationRoleOnlyAtAdHocLevel() =>
        new("Application roles can only be activated at the ad hoc level.", 15422, 16, 1);

    /// <summary>Mimics SQL Server error 2710: <c>ALTER SCHEMA sys | INFORMATION_SCHEMA TRANSFER …</c> (probed 2026-10-04 against SQL Server 2025).</summary>
    internal static SimulatedSqlException NotTheSpecifiedOwner(string schemaName) =>
        new($"You are not the owner specified for the object '{schemaName}' in this statement (CREATE, ALTER, TRUNCATE, UPDATE STATISTICS or BULK INSERT).", 2710, 16, 1);
}
