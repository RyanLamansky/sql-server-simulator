namespace SqlServerSimulator;

// Ownership error factories: ALTER AUTHORIZATION, sp_changedbowner, and the
// refusals to drop a principal that owns something (Msg 15183 / 15184 /
// 15138 / 15421 / 15174).
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
partial class SimulatedSqlException
{
    /// <summary>Msg 15151 for a module's <c>EXECUTE AS</c> naming no user.</summary>
    internal static SimulatedSqlException CannotExecuteAsUser(string name) =>
        new($"Cannot execute as the user '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 in the "Cannot find the &lt;kind&gt;"
    /// wording <c>ALTER AUTHORIZATION</c> uses for every class it resolves —
    /// <paramref name="kind"/> is <c>role</c>, <c>fulltext catalog</c>,
    /// <c>assembly</c>, <c>database</c> or <c>principal</c>, and the name is the
    /// leaf as written. Real answers a principal it can see but won't accept
    /// as an owner (<c>sys</c>, <c>INFORMATION_SCHEMA</c>) at state 2, and one
    /// the caller may not impersonate at state 1 (probed 2026-09-27 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException CannotFindSecurable(string kind, string name, byte state = 1) =>
        new($"Cannot find the {kind} '{name}', because it does not exist or you do not have permission.", 15151, 16, state);

    /// <summary>
    /// Mimics SQL Server error 15344: <c>ALTER AUTHORIZATION</c> on a class that
    /// carries no owner — a <c>USER::</c>, an <c>APPLICATION ROLE::</c>, or a
    /// temporary table (<c>object</c>). Probed 2026-09-27 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException OwnershipChangeNotSupported(string kind) =>
        new($"Ownership change for {kind} is not supported.", 15344, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15346: <c>ALTER AUTHORIZATION</c> named a
    /// trigger or a constraint, each of which is owned through its parent.
    /// Probed 2026-09-27 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException OwnerFollowsParentObject() =>
        new("Cannot change owner for an object that is owned by a parent object. Change the owner of the parent object instead.", 15346, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15150 at state 2: <c>ALTER AUTHORIZATION ON
    /// SCHEMA::</c> of <c>dbo</c>, <c>guest</c>, <c>sys</c> or
    /// <c>INFORMATION_SCHEMA</c> (the fixed-role schemas are movable). Probed
    /// 2026-09-27 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotAlterFixedSchema(string schemaName) =>
        new($"Cannot alter the schema '{schemaName}'.", 15150, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 15109: a new owner for <c>master</c>,
    /// <c>model</c> or <c>tempdb</c>. Probed 2026-09-27 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotChangeSystemDatabaseOwner() =>
        new("Cannot change the owner of the master, model, tempdb or distribution database.", 15109, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15110: the proposed database owner's login is
    /// already mapped to a user in that database. Probed 2026-09-27 against SQL
    /// Server 2025, from <c>ALTER AUTHORIZATION</c> and <c>sp_changedbowner</c>
    /// alike (the latter without procedure attribution).
    /// </summary>
    internal static SimulatedSqlException ProposedDatabaseOwnerIsUser() =>
        new("The proposed new database owner is already a user or aliased in the database.", 15110, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15353: a database's proposed owner is a server
    /// role (<c>sysadmin</c>, <c>[public]</c> …) rather than a login. Probed
    /// 2026-09-27 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException DatabaseCannotBeOwnedByRole() =>
        new("An entity of type database cannot be owned by a role, a group, an approle, or by principals mapped to certificates or asymmetric keys.", 15353, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15174: <c>DROP LOGIN</c> of a login that owns a
    /// database. Probed 2026-09-27 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException LoginOwnsDatabases(string loginName) =>
        new($"Login '{loginName}' owns one or more database(s). Change the owner of the database(s) before dropping the login.", 15174, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15063: <c>CREATE USER … FOR LOGIN</c> naming the
    /// login that owns the database, which is already there as <c>dbo</c>, or
    /// <c>sp_change_users_login</c> mapping a login that already has a user.
    /// Probed 2026-09-27 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException LoginAlreadyHasAccount(string userName) =>
        new($"The login already has an account with the user name '{userName}'.", 15063, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15183: dropping a user or role that owns a
    /// schema-scoped object. Checked ahead of every other ownership refusal
    /// (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException PrincipalOwnsObjects() =>
        new("The database principal owns objects in the database and cannot be dropped.", 15183, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15184: dropping a principal that owns a
    /// user-defined type. Probed 2026-09-27 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException PrincipalOwnsTypes() =>
        new("The database principal owns data types in the database and cannot be dropped.", 15184, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15421: dropping a principal that owns a database
    /// role — itself included. Probed 2026-09-27 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException PrincipalOwnsRole() =>
        new("The database principal owns a database role and cannot be dropped.", 15421, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15136: dropping a user that a module's
    /// <c>WITH EXECUTE AS SELF</c> or <c>WITH EXECUTE AS 'user'</c> names —
    /// raised after every ownership refusal. Probed 2026-09-27 against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException PrincipalIsExecutionContext() =>
        new("The database principal is set as the execution context of one or more procedures, functions, or event notifications and cannot be dropped.", 15136, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15138 for the securables past a schema:
    /// <paramref name="what"/> is <c>a XML namespace</c> (real's words, article
    /// included) or <c>a fulltext catalog</c>. Probed 2026-09-27 against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException PrincipalOwnsA(string what) =>
        new($"The database principal owns {what} in the database, and cannot be dropped.", 15138, 16, 1);
}
