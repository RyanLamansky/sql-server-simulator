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
    DateTime createDate)
{
    public readonly int PrincipalId = principalId;
    public readonly string Name = name;

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
}
