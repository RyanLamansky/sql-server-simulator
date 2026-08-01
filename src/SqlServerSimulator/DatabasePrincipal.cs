namespace SqlServerSimulator;

/// <summary>
/// A database-level principal: a user, role, or built-in. Stored on
/// <see cref="Database.Principals"/> keyed by name (case-insensitive).
/// Created via <c>CREATE USER name</c> / <c>CREATE ROLE name</c>;
/// well-known principals (<c>public</c>, <c>dbo</c>, <c>guest</c>,
/// <c>INFORMATION_SCHEMA</c>, <c>sys</c>) are pre-seeded so AW's
/// <c>GRANT … TO public</c> succeeds without explicit principal DDL.
/// </summary>
/// <remarks>
/// The simulator <strong>does not</strong> enforce permissions. This type
/// exists so principal DDL can round-trip through <c>sys.database_principals</c>
/// and so <c>GRANT/REVOKE/DENY</c> can resolve its <c>TO</c> / <c>FROM</c>
/// principal name to a real id at parse time. Fixed-principal ids match
/// the real-SQL-Server convention (probe-confirmed against
/// <c>sys.database_principals</c> on 2026-05-14): public=0, dbo=1,
/// guest=2, INFORMATION_SCHEMA=3, sys=4. User principals start at 5.
/// </remarks>
internal sealed class DatabasePrincipal(
    int principalId,
    string name,
    string typeCode,
    string typeDescription,
    bool isFixedRole,
    DateTime createDate,
    string? loginName = null,
    string? securityIdentifierString = null)
{
    public readonly int PrincipalId = principalId;
    public readonly string Name = name;

    /// <summary>
    /// The server login this database user is mapped to
    /// (<c>CREATE USER name FOR LOGIN login</c>), or <c>null</c> when the user
    /// carries no login link (WITHOUT LOGIN users, the fixed principals, and
    /// the parse-and-discard <c>CREATE USER</c> forms). Drives login →
    /// database-user resolution at connect time and the
    /// <c>SYSTEM_USER</c> / <c>SUSER_SNAME()</c> value while impersonating this
    /// user.
    /// </summary>
    public readonly string? LoginName = loginName;

    /// <summary>
    /// The synthetic <c>S-1-9-3-…</c> security-identifier string a
    /// <c>CREATE USER name WITHOUT LOGIN</c> user reports through
    /// <c>SYSTEM_USER</c> / <c>SUSER_SNAME()</c> and the Msg 916 "server
    /// principal" wording (real SQL Server has no login name for these users,
    /// only a SID). Deterministically derived from the user name;
    /// <c>null</c> for every other principal.
    /// </summary>
    public readonly string? SecurityIdentifierString = securityIdentifierString;

    /// <summary>
    /// The identity string a session impersonating this database user reports
    /// through <c>SYSTEM_USER</c> / <c>SUSER_SNAME()</c>: the mapped login when
    /// one exists, the synthetic SID for WITHOUT LOGIN users, else the user
    /// name itself.
    /// </summary>
    public string EffectiveLoginIdentity => this.LoginName ?? this.SecurityIdentifierString ?? this.Name;

    /// <summary>
    /// One- or two-character <c>sys.database_principals.type</c> code.
    /// <c>S</c> = SQL_USER, <c>U</c> = WINDOWS_USER, <c>G</c> = WINDOWS_GROUP,
    /// <c>R</c> = DATABASE_ROLE, <c>C</c> = CERTIFICATE_MAPPED_USER,
    /// <c>K</c> = ASYMMETRIC_KEY_MAPPED_USER, <c>X</c> = EXTERNAL_GROUPS,
    /// <c>E</c> = EXTERNAL_USER. Matches real SQL Server codes verbatim.
    /// </summary>
    public readonly string TypeCode = typeCode;

    /// <summary>
    /// Long-form description matching real SQL Server's
    /// <c>sys.database_principals.type_desc</c> column
    /// (<c>SQL_USER</c> / <c>DATABASE_ROLE</c> / …).
    /// </summary>
    public readonly string TypeDescription = typeDescription;

    /// <summary>
    /// True for the fixed database roles (<c>public</c>, <c>db_owner</c>,
    /// etc.). Surfaces in <c>sys.database_principals.is_fixed_role</c>.
    /// The simulator pre-seeds <c>public</c> only; the other fixed roles
    /// are deferred.
    /// </summary>
    public readonly bool IsFixedRole = isFixedRole;

    public readonly DateTime CreateDate = createDate;

    /// <summary>
    /// The <c>sys.database_principals.default_schema_name</c> value, non-null
    /// only for an application role (<c>CREATE APPLICATION ROLE … [DEFAULT_SCHEMA
    /// = s]</c>, defaulting to <c>dbo</c>). Every other principal projects NULL,
    /// the shape the catalog view has always had.
    /// </summary>
    public string? DefaultSchemaName;

    /// <summary>
    /// The application role's password hash (<c>CREATE / ALTER APPLICATION ROLE
    /// … WITH PASSWORD</c>), verified by <c>sp_setapprole</c>. Null for every
    /// other principal kind. Uses the same legacy <c>0x0200</c> single-pass
    /// format as <see cref="ServerLogin"/> — never persisted, so PBKDF2
    /// hardening would only bill activation.
    /// </summary>
    public byte[]? PasswordHash;
}
