namespace SqlServerSimulator;

/// <summary>
/// One entry in <see cref="Database.Permissions"/> — a GRANT / REVOKE /
/// DENY statement that's been parsed and stored but never enforced. The
/// simulator has no permission model; this exists to round-trip the
/// information into <c>sys.database_permissions</c>.
/// </summary>
/// <remarks>
/// Real SQL Server permission rows carry a <c>class</c> tinyint
/// distinguishing database-scope (0) from object/column (1), schema (3),
/// principal (4), etc. AW only emits class-0 GRANT statements, so the
/// simulator's <see cref="Class"/> + <see cref="MajorId"/> + <see cref="MinorId"/>
/// triple mirrors the catalog-view shape: class=0, major_id=0,
/// minor_id=0 for database-scope grants; class=1 + major_id=&lt;object_id&gt;
/// for object-scope grants (when the parser eventually accepts <c>ON
/// OBJECT::name</c>).
/// </remarks>
internal sealed class DatabasePermission(
    byte @class,
    int majorId,
    int minorId,
    int granteePrincipalId,
    int grantorPrincipalId,
    string permissionName,
    string typeCode,
    string state)
{
    /// <summary>
    /// Permission target class: 0=database, 1=object/column, 3=schema,
    /// 4=database principal. The simulator only populates 0 (and 1 once
    /// object-scope grants land).
    /// </summary>
    public readonly byte Class = @class;

    /// <summary>
    /// Target identifier within the class. 0 for class=0 (database scope);
    /// object_id for class=1; schema_id for class=3; principal_id for
    /// class=4.
    /// </summary>
    public readonly int MajorId = majorId;

    /// <summary>Column ordinal for column-level grants; 0 otherwise.</summary>
    public readonly int MinorId = minorId;

    /// <summary>
    /// <see cref="DatabasePrincipal.PrincipalId"/> of the principal
    /// receiving the permission.
    /// </summary>
    public readonly int GranteePrincipalId = granteePrincipalId;

    /// <summary>
    /// <see cref="DatabasePrincipal.PrincipalId"/> of the principal that
    /// issued the GRANT statement. The simulator has no current-user
    /// concept; defaults to <c>dbo</c> (id 1).
    /// </summary>
    public readonly int GrantorPrincipalId = grantorPrincipalId;

    /// <summary>
    /// Long-form permission name as parsed from the GRANT statement
    /// (e.g. <c>VIEW ANY COLUMN ENCRYPTION KEY DEFINITION</c>). Stored
    /// case-preserved; matched case-insensitively at REVOKE/DENY time.
    /// </summary>
    public readonly string PermissionName = permissionName;

    /// <summary>
    /// 4-character SQL Server permission type code
    /// (<c>VWCD</c> for VIEW ANY COLUMN MASTER KEY DEFINITION, etc.).
    /// Derived from the first letter of each word in <see cref="PermissionName"/>,
    /// right-padded with spaces — accurate for most spelled-out permission
    /// names but not all (a small lookup table refinement would be needed
    /// for exact catalog parity).
    /// </summary>
    public readonly string TypeCode = typeCode;

    /// <summary>
    /// State code: <c>G</c>=Grant, <c>R</c>=Revoke, <c>D</c>=Deny,
    /// <c>W</c>=Grant_with_grant. Real SQL Server treats REVOKE as a
    /// row-deletion rather than a stored state; the simulator keeps the
    /// REVOKE row so DENY ⇄ GRANT toggling can be observed in
    /// <c>sys.database_permissions</c> for debugging.
    /// </summary>
    public readonly string State = state;
}
