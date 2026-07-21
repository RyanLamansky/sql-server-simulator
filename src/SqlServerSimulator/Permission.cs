namespace SqlServerSimulator;

/// <summary>
/// The canonical permission set the enforcement checker and the
/// <c>sys.database_permissions.type</c> / <c>permission_name</c> projection know
/// by name. <see cref="Other"/> is the sentinel for off-catalog names — the
/// stored-but-never-checked long tail (e.g. <c>VIEW ANY COLUMN MASTER KEY
/// DEFINITION</c>) — whose raw text rides on
/// <see cref="DatabasePermission.PermissionName"/>; an <see cref="Other"/> request
/// is never satisfied by any row, and an <see cref="Other"/> row never satisfies
/// any check.
/// </summary>
internal enum Permission : byte
{
    Other = 0,
    Alter,
    Connect,
    Control,
    CreateFunction,
    CreateProcedure,
    CreateSequence,
    CreateTable,
    CreateView,
    Delete,
    Execute,
    Impersonate,
    Insert,
    Receive,
    References,
    Select,
    TakeOwnership,
    Unmask,
    Update,
    ViewChangeTracking,
    ViewDefinition,
}

/// <summary>
/// State of a <see cref="DatabasePermission"/> row — the
/// <c>sys.database_permissions.state</c> code (<c>G</c> / <c>W</c> / <c>D</c> /
/// <c>R</c>) in its typed form. The state-code / state-desc strings materialize
/// only at the catalog-view boundary via <see cref="PermissionCatalog"/>.
/// </summary>
internal enum PermissionState : byte
{
    Grant,
    GrantWithGrantOption,
    Deny,
    Revoke,
}

/// <summary>
/// The read / write / DDL bucket a permission falls in, driving the fixed-role
/// virtual grants (<c>db_datareader</c> → read, <c>db_datawriter</c> → write,
/// <c>db_ddladmin</c> → DDL) and their deny counterparts.
/// </summary>
internal enum PermissionCategory : byte
{
    None,
    Read,
    Write,
    Ddl,
}

/// <summary>
/// The single source of truth for the permission catalog — the canonical name and
/// 4-char <c>sys.database_permissions.type</c> code per <see cref="Permission"/>,
/// the read/write/DDL classification, the covering graph, the name→enum resolver,
/// and the state-code / state-desc materialization — surfaced as extension
/// members on <see cref="Permission"/> / <see cref="PermissionState"/>. The names
/// and type codes are imported from <c>sys.fn_builtin_permissions</c> for the
/// OBJECT / SCHEMA / DATABASE / DATABASE_PRINCIPAL classes; off-catalog names
/// project their raw text plus a first-letter-of-each-word type-code heuristic
/// (<see cref="DatabasePermission.DisplayTypeCode"/>).
/// </summary>
internal static class PermissionCatalog
{
    private readonly struct PermissionInfo(string name, string typeCode, PermissionCategory category)
    {
        /// <summary>Canonical uppercase permission name, as real SQL Server stores it in <c>sys.database_permissions</c> regardless of the GRANT's casing.</summary>
        public readonly string Name = name;

        /// <summary>Canonical 4-char type code, space-padded to match real's <c>char(4)</c> column.</summary>
        public readonly string TypeCode = typeCode;

        /// <summary>Read / write / DDL bucket for the fixed-role virtual grants.</summary>
        public readonly PermissionCategory Category = category;
    }

    // Indexed by (byte)Permission — the array order MUST track the enum.
    private static readonly PermissionInfo[] Table =
    [
        new("", "    ", PermissionCategory.None),                    // Other (name/code come from the row's raw text)
        new("ALTER", "AL  ", PermissionCategory.Ddl),               // Alter
        new("CONNECT", "CO  ", PermissionCategory.None),            // Connect
        new("CONTROL", "CL  ", PermissionCategory.None),            // Control
        new("CREATE FUNCTION", "CRFN", PermissionCategory.Ddl),     // CreateFunction
        new("CREATE PROCEDURE", "CRPR", PermissionCategory.Ddl),    // CreateProcedure
        new("CREATE SEQUENCE", "CRSO", PermissionCategory.Ddl),     // CreateSequence
        new("CREATE TABLE", "CRTB", PermissionCategory.Ddl),        // CreateTable
        new("CREATE VIEW", "CRVW", PermissionCategory.Ddl),         // CreateView
        new("DELETE", "DL  ", PermissionCategory.Write),            // Delete
        new("EXECUTE", "EX  ", PermissionCategory.None),            // Execute
        new("IMPERSONATE", "IM  ", PermissionCategory.None),        // Impersonate
        new("INSERT", "IN  ", PermissionCategory.Write),            // Insert
        new("RECEIVE", "RC  ", PermissionCategory.None),            // Receive
        new("REFERENCES", "RF  ", PermissionCategory.None),         // References
        new("SELECT", "SL  ", PermissionCategory.Read),             // Select
        new("TAKE OWNERSHIP", "TO  ", PermissionCategory.None),     // TakeOwnership
        new("UNMASK", "UMSK", PermissionCategory.None),             // Unmask
        new("UPDATE", "UP  ", PermissionCategory.Write),            // Update
        new("VIEW CHANGE TRACKING", "VWCT", PermissionCategory.None), // ViewChangeTracking
        new("VIEW DEFINITION", "VW  ", PermissionCategory.None),    // ViewDefinition
    ];

    extension(Permission)
    {
        /// <summary>Resolves a permission-name string (any casing, surrounding whitespace trimmed) to its <see cref="Permission"/>, or <see cref="Permission.Other"/> for an off-catalog name. Zero-alloc on the span switch.</summary>
        internal static Permission Resolve(string name)
        {
            var trimmed = name.AsSpan().Trim();
            Span<char> upper = stackalloc char[trimmed.Length];
            _ = trimmed.ToUpperInvariant(upper);
            return upper switch
            {
                "ALTER" => Permission.Alter,
                "CONNECT" => Permission.Connect,
                "CONTROL" => Permission.Control,
                "CREATE FUNCTION" => Permission.CreateFunction,
                "CREATE PROCEDURE" => Permission.CreateProcedure,
                "CREATE SEQUENCE" => Permission.CreateSequence,
                "CREATE TABLE" => Permission.CreateTable,
                "CREATE VIEW" => Permission.CreateView,
                "DELETE" => Permission.Delete,
                "EXECUTE" => Permission.Execute,
                "IMPERSONATE" => Permission.Impersonate,
                "INSERT" => Permission.Insert,
                "RECEIVE" => Permission.Receive,
                "REFERENCES" => Permission.References,
                "SELECT" => Permission.Select,
                "TAKE OWNERSHIP" => Permission.TakeOwnership,
                "UNMASK" => Permission.Unmask,
                "UPDATE" => Permission.Update,
                "VIEW CHANGE TRACKING" => Permission.ViewChangeTracking,
                "VIEW DEFINITION" => Permission.ViewDefinition,
                _ => Permission.Other,
            };
        }
    }

    extension(Permission permission)
    {
        /// <summary>Canonical uppercase permission name, as real SQL Server stores it in <c>sys.database_permissions.permission_name</c> regardless of the GRANT's casing.</summary>
        internal string CanonicalName => Table[(byte)permission].Name;

        /// <summary>Canonical 4-char type code, space-padded to match real's <c>char(4)</c> column.</summary>
        internal string CanonicalTypeCode => Table[(byte)permission].TypeCode;

        /// <summary>The read / write / DDL bucket, driving the fixed-role virtual grants.</summary>
        internal PermissionCategory Category => Table[(byte)permission].Category;

        /// <summary>
        /// The immediate covering permission for this permission at
        /// <paramref name="securableClass"/> — the permission that, when granted,
        /// implies this one — or <see langword="null"/> at the top (<c>CONTROL</c>).
        /// Chains to <c>CONTROL</c> for most; the class-specific exceptions
        /// (OBJECT SELECT ← RECEIVE ← CONTROL, DATABASE CREATE TABLE ← ALTER ←
        /// CONTROL) come straight from <c>sys.fn_builtin_permissions</c>.
        /// </summary>
        internal Permission? Covering(byte securableClass) => (securableClass, permission) switch
        {
            (_, Permission.Control) => null,
            (PermissionChecker.ClassObject, Permission.Select) => Permission.Receive,
            (PermissionChecker.ClassObject, Permission.Receive) => Permission.Control,
            (PermissionChecker.ClassDatabase, Permission.CreateTable) => Permission.Alter,
            _ => Permission.Control,
        };
    }

    extension(PermissionState state)
    {
        /// <summary>The <c>sys.database_permissions.state</c> 1-char code.</summary>
        internal string Code => state switch
        {
            PermissionState.Deny => "D",
            PermissionState.Grant => "G",
            PermissionState.GrantWithGrantOption => "W",
            PermissionState.Revoke => "R",
            _ => "G",
        };

        /// <summary>The <c>sys.database_permissions.state_desc</c> spelling.</summary>
        internal string Description => state switch
        {
            PermissionState.Deny => "DENY",
            PermissionState.Grant => "GRANT",
            PermissionState.GrantWithGrantOption => "GRANT_WITH_GRANT_OPTION",
            PermissionState.Revoke => "REVOKE",
            _ => "GRANT",
        };
    }
}
