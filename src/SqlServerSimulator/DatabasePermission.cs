namespace SqlServerSimulator;

/// <summary>
/// One entry in <see cref="Database.Permissions"/> — a stored GRANT / DENY
/// (WITH GRANT OPTION) row. Canonical permissions store their identity as a
/// <see cref="SqlServerSimulator.Permission"/> enum and draw their name / type
/// code from <see cref="PermissionCatalog"/> at projection time; off-catalog
/// names store <see cref="SqlServerSimulator.Permission.Other"/> plus their raw
/// text on <see cref="PermissionName"/>.
/// </summary>
/// <remarks>
/// The <see cref="Class"/> + <see cref="MajorId"/> + <see cref="MinorId"/> triple
/// mirrors the catalog-view shape: class=0, major_id=0 for database-scope grants;
/// class=1 + major_id=&lt;object_id&gt; for object-scope; class=3 + schema_id for
/// schema-scope; class=4 + principal_id for the IMPERSONATE gate.
/// </remarks>
internal sealed class DatabasePermission(
    byte @class,
    int majorId,
    int minorId,
    int granteePrincipalId,
    int grantorPrincipalId,
    Permission permission,
    PermissionState state,
    string? permissionName = null)
{
    /// <summary>
    /// Permission target class: 0=database, 1=object/column, 3=schema,
    /// 4=database principal.
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
    /// issued the GRANT statement (the granting session's effective principal;
    /// <c>dbo</c> = id 1 for an unimpersonated session).
    /// </summary>
    public readonly int GrantorPrincipalId = grantorPrincipalId;

    /// <summary>
    /// The canonical permission, or <see cref="SqlServerSimulator.Permission.Other"/>
    /// for an off-catalog name carried on <see cref="PermissionName"/>.
    /// </summary>
    public readonly Permission Permission = permission;

    /// <summary>
    /// Raw permission text for an <see cref="SqlServerSimulator.Permission.Other"/>
    /// row (e.g. <c>VIEW ANY COLUMN MASTER KEY DEFINITION</c>), stored
    /// case-preserved and matched case-insensitively; <see langword="null"/> for a
    /// canonical row, whose name / type code come from <see cref="PermissionCatalog"/>.
    /// </summary>
    public readonly string? PermissionName = permissionName;

    /// <summary>
    /// State: Grant / GrantWithGrantOption / Deny / Revoke. Real SQL Server treats
    /// REVOKE as a row-deletion; the simulator likewise removes rows on REVOKE and
    /// keeps only live Grant / GrantWithGrantOption / Deny rows.
    /// </summary>
    public readonly PermissionState State = state;

    /// <summary>The catalog-view <c>permission_name</c> — the canonical catalog spelling, or the raw stored text for an off-catalog (<see cref="SqlServerSimulator.Permission.Other"/>) name.</summary>
    public string DisplayName => Permission == Permission.Other ? PermissionName! : Permission.CanonicalName;

    /// <summary>The catalog-view <c>type</c> code — the canonical 4-char code, or the first-letter heuristic for an off-catalog name.</summary>
    public string DisplayTypeCode => Permission == Permission.Other ? DeriveTypeCode(PermissionName!) : Permission.CanonicalTypeCode;

    /// <summary>
    /// Whether this row names <paramref name="permission"/> on the
    /// (<paramref name="securableClass"/>, <paramref name="majorId"/>) securable.
    /// Enum equality except for <see cref="SqlServerSimulator.Permission.Other"/>,
    /// where collation name-equality against <paramref name="permissionName"/>
    /// keeps two distinct off-catalog names from colliding.
    /// </summary>
    public bool IsFor(byte securableClass, int majorId, Permission permission, string permissionName, Database database) =>
        Class == securableClass
        && MajorId == majorId
        && Permission == permission
        && (permission != Permission.Other || database.Collation.Equals(PermissionName, permissionName));

    /// <summary>
    /// First-letter-of-each-word type-code heuristic for off-catalog permission
    /// names (e.g. <c>VIEW ANY COLUMN MASTER KEY DEFINITION</c> → <c>VACM</c>),
    /// right-padded with spaces to 4 chars. Accurate for most spelled-out names
    /// but won't byte-match real for every long name.
    /// </summary>
    private static string DeriveTypeCode(string permissionName)
    {
        var words = permissionName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Span<char> code = stackalloc char[4];
        var idx = 0;
        foreach (var w in words)
        {
            if (idx >= 4)
                break;
            code[idx++] = char.ToUpperInvariant(w[0]);
        }
        while (idx < 4)
            code[idx++] = ' ';
        return new string(code);
    }
}
