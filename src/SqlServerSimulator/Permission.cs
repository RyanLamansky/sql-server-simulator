namespace SqlServerSimulator;

/// <summary>
/// The canonical permission set the enforcement checker and the
/// <c>sys.database_permissions.type</c> / <c>permission_name</c> projection know
/// by name. <see cref="Other"/> is the sentinel for off-catalog names — the
/// stored-but-never-checked long tail (e.g. <c>CREATE QUEUE</c>) — whose raw text rides on
/// <see cref="DatabasePermission.PermissionName"/>; an <see cref="Other"/> request
/// is never satisfied by any row, and an <see cref="Other"/> row never satisfies
/// any check.
/// </summary>
internal enum Permission : byte
{
    Other = 0,
    Alter,
    AlterAnyDatabase,
    AlterAnyDatabaseDdlTrigger,
    AlterAnyFullTextCatalog,
    AlterAnyLogin,
    AlterAnyRole,
    AlterAnySchema,
    Authenticate,
    Connect,
    Control,
    CreateAggregate,
    CreateAnyDatabase,
    CreateAssembly,
    CreateFullTextCatalog,
    CreateFunction,
    CreateProcedure,
    CreateSequence,
    CreateSynonym,
    CreateTable,
    CreateType,
    CreateView,
    CreateXmlSchemaCollection,
    Delete,
    Execute,
    Impersonate,
    ImpersonateAnyLogin,
    Insert,
    Receive,
    References,
    Select,
    TakeOwnership,
    Unmask,
    Update,
    ViewAnyColumnEncryptionKeyDefinition,
    ViewAnyColumnMasterKeyDefinition,
    ViewAnyDefinition,
    ViewChangeTracking,
    ViewDatabasePerformanceState,
    ViewDatabaseState,
    ViewDefinition,
    ViewServerPerformanceState,
    ViewServerSecurityState,
    ViewServerState,
    // The SERVER-class permissions of sys.fn_builtin_permissions not listed
    // above (probed 2026-09-29 against SQL Server 2025), in its order.
    AdministerBulkOperations,
    AlterAnyAvailabilityGroup,
    AlterAnyConnection,
    AlterAnyCredential,
    AlterAnyEndpoint,
    AlterAnyEventNotification,
    AlterAnyEventSession,
    AlterAnyEventSessionAddEvent,
    AlterAnyEventSessionAddTarget,
    AlterAnyEventSessionDisable,
    AlterAnyEventSessionDropEvent,
    AlterAnyEventSessionDropTarget,
    AlterAnyEventSessionEnable,
    AlterAnyEventSessionOption,
    AlterAnyLinkedServer,
    AlterAnyServerAudit,
    AlterAnyServerRole,
    AlterResources,
    AlterServerState,
    AlterSettings,
    AlterTrace,
    AuthenticateServer,
    ConnectAnyDatabase,
    ConnectSql,
    ControlServer,
    CreateAnyEventSession,
    CreateAvailabilityGroup,
    CreateDdlEventNotification,
    CreateEndpoint,
    CreateLogin,
    CreateServerRole,
    CreateTraceEventNotification,
    DropAnyEventSession,
    ExternalAccessAssembly,
    SelectAllUserSecurables,
    Shutdown,
    UnsafeAssembly,
    ViewAnyCryptographicallySecuredDefinition,
    ViewAnyDatabase,
    ViewAnyErrorLog,
    ViewAnyPerformanceDefinition,
    ViewAnySecurityDefinition,
    ViewServerSecurityAudit,
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
    private readonly struct PermissionInfo(string name, string typeCode, PermissionCategory category, Permission? serverCover = null)
    {
        /// <summary>Canonical uppercase permission name, as real SQL Server stores it in <c>sys.database_permissions</c> regardless of the GRANT's casing.</summary>
        public readonly string Name = name;

        /// <summary>Canonical 4-char type code, space-padded to match real's <c>char(4)</c> column.</summary>
        public readonly string TypeCode = typeCode;

        /// <summary>Read / write / DDL bucket for the fixed-role virtual grants.</summary>
        public readonly PermissionCategory Category = category;

        /// <summary>
        /// For a SERVER-class permission, its <c>covering_permission_name</c>
        /// in <c>sys.fn_builtin_permissions</c> — <see cref="Permission.Other"/>
        /// for <c>CONTROL SERVER</c>, the top — and <see langword="null"/> for
        /// every permission of another class.
        /// </summary>
        public readonly Permission? ServerCover = serverCover;
    }

    // Indexed by (byte)Permission — the array order MUST track the enum.
    private static readonly PermissionInfo[] Table =
    [
        new("", "    ", PermissionCategory.None),                    // Other (name/code come from the row's raw text)
        new("ALTER", "AL  ", PermissionCategory.Ddl),               // Alter
        new("ALTER ANY DATABASE", "ALDB", PermissionCategory.None, Permission.ControlServer), // AlterAnyDatabase (server scope)
        new("ALTER ANY DATABASE DDL TRIGGER", "ALTG", PermissionCategory.Ddl), // AlterAnyDatabaseDdlTrigger
        new("ALTER ANY FULLTEXT CATALOG", "ALFT", PermissionCategory.Ddl), // AlterAnyFullTextCatalog
        new("ALTER ANY LOGIN", "ALLG", PermissionCategory.None, Permission.ControlServer),    // AlterAnyLogin (server scope)
        // ALTER ANY ROLE is deliberately not Ddl: db_ddladmin does NOT confer
        // role DDL (probe-confirmed — DROP ROLE stays Msg 15151 for a member).
        new("ALTER ANY ROLE", "ALRL", PermissionCategory.None),     // AlterAnyRole
        new("ALTER ANY SCHEMA", "ALSM", PermissionCategory.Ddl),    // AlterAnySchema
        new("AUTHENTICATE", "AUTH", PermissionCategory.None),       // Authenticate
        new("CONNECT", "CO  ", PermissionCategory.None),            // Connect
        new("CONTROL", "CL  ", PermissionCategory.None),            // Control
        new("CREATE AGGREGATE", "CRAG", PermissionCategory.Ddl),    // CreateAggregate
        new("CREATE ANY DATABASE", "CRDB", PermissionCategory.None, Permission.AlterAnyDatabase), // CreateAnyDatabase (server scope)
        new("CREATE ASSEMBLY", "CRAS", PermissionCategory.Ddl),     // CreateAssembly
        new("CREATE FULLTEXT CATALOG", "CRFT", PermissionCategory.Ddl), // CreateFullTextCatalog
        new("CREATE FUNCTION", "CRFN", PermissionCategory.Ddl),     // CreateFunction
        new("CREATE PROCEDURE", "CRPR", PermissionCategory.Ddl),    // CreateProcedure
        new("CREATE SEQUENCE", "CRSO", PermissionCategory.Ddl),     // CreateSequence
        new("CREATE SYNONYM", "CRSN", PermissionCategory.Ddl),      // CreateSynonym
        new("CREATE TABLE", "CRTB", PermissionCategory.Ddl),        // CreateTable
        new("CREATE TYPE", "CRTY", PermissionCategory.Ddl),         // CreateType
        new("CREATE VIEW", "CRVW", PermissionCategory.Ddl),         // CreateView
        new("CREATE XML SCHEMA COLLECTION", "CRXS", PermissionCategory.Ddl), // CreateXmlSchemaCollection
        new("DELETE", "DL  ", PermissionCategory.Write),            // Delete
        new("EXECUTE", "EX  ", PermissionCategory.None),            // Execute
        new("IMPERSONATE", "IM  ", PermissionCategory.None),        // Impersonate
        new("IMPERSONATE ANY LOGIN", "IAL ", PermissionCategory.None, Permission.ControlServer), // ImpersonateAnyLogin (server scope)
        new("INSERT", "IN  ", PermissionCategory.Write),            // Insert
        new("RECEIVE", "RC  ", PermissionCategory.None),            // Receive
        new("REFERENCES", "RF  ", PermissionCategory.None),         // References
        new("SELECT", "SL  ", PermissionCategory.Read),             // Select
        new("TAKE OWNERSHIP", "TO  ", PermissionCategory.None),     // TakeOwnership
        new("UNMASK", "UMSK", PermissionCategory.None),             // Unmask
        new("UPDATE", "UP  ", PermissionCategory.Write),            // Update
        new("VIEW ANY COLUMN ENCRYPTION KEY DEFINITION", "VWCK", PermissionCategory.None), // ViewAnyColumnEncryptionKeyDefinition
        new("VIEW ANY COLUMN MASTER KEY DEFINITION", "VWCM", PermissionCategory.None), // ViewAnyColumnMasterKeyDefinition
        new("VIEW ANY DEFINITION", "VWAD", PermissionCategory.None, Permission.ControlServer), // ViewAnyDefinition (server scope)
        new("VIEW CHANGE TRACKING", "VWCT", PermissionCategory.None), // ViewChangeTracking
        new("VIEW DATABASE PERFORMANCE STATE", "VDP ", PermissionCategory.None), // ViewDatabasePerformanceState
        new("VIEW DATABASE STATE", "VWDS", PermissionCategory.None), // ViewDatabaseState
        new("VIEW DEFINITION", "VW  ", PermissionCategory.None),    // ViewDefinition
        new("VIEW SERVER PERFORMANCE STATE", "VSP ", PermissionCategory.None, Permission.ViewServerState), // ViewServerPerformanceState
        new("VIEW SERVER SECURITY STATE", "VSS ", PermissionCategory.None, Permission.ViewServerState), // ViewServerSecurityState
        new("VIEW SERVER STATE", "VWSS", PermissionCategory.None, Permission.AlterServerState),  // ViewServerState
        new("ADMINISTER BULK OPERATIONS", "ADBO", PermissionCategory.None, Permission.ControlServer), // AdministerBulkOperations
        new("ALTER ANY AVAILABILITY GROUP", "ALAG", PermissionCategory.None, Permission.ControlServer), // AlterAnyAvailabilityGroup
        new("ALTER ANY CONNECTION", "ALCO", PermissionCategory.None, Permission.ControlServer), // AlterAnyConnection
        new("ALTER ANY CREDENTIAL", "ALCD", PermissionCategory.None, Permission.ControlServer), // AlterAnyCredential
        new("ALTER ANY ENDPOINT", "ALHE", PermissionCategory.None, Permission.ControlServer), // AlterAnyEndpoint
        new("ALTER ANY EVENT NOTIFICATION", "ALES", PermissionCategory.None, Permission.ControlServer), // AlterAnyEventNotification
        new("ALTER ANY EVENT SESSION", "AAES", PermissionCategory.None, Permission.ControlServer), // AlterAnyEventSession
        new("ALTER ANY EVENT SESSION ADD EVENT", "LSAE", PermissionCategory.None, Permission.AlterAnyEventSession), // AlterAnyEventSessionAddEvent
        new("ALTER ANY EVENT SESSION ADD TARGET", "LSAT", PermissionCategory.None, Permission.AlterAnyEventSession), // AlterAnyEventSessionAddTarget
        new("ALTER ANY EVENT SESSION DISABLE", "DES ", PermissionCategory.None, Permission.AlterAnyEventSession), // AlterAnyEventSessionDisable
        new("ALTER ANY EVENT SESSION DROP EVENT", "LSDE", PermissionCategory.None, Permission.AlterAnyEventSession), // AlterAnyEventSessionDropEvent
        new("ALTER ANY EVENT SESSION DROP TARGET", "LSDT", PermissionCategory.None, Permission.AlterAnyEventSession), // AlterAnyEventSessionDropTarget
        new("ALTER ANY EVENT SESSION ENABLE", "EES ", PermissionCategory.None, Permission.AlterAnyEventSession), // AlterAnyEventSessionEnable
        new("ALTER ANY EVENT SESSION OPTION", "LESO", PermissionCategory.None, Permission.AlterAnyEventSession), // AlterAnyEventSessionOption
        new("ALTER ANY LINKED SERVER", "ALLS", PermissionCategory.None, Permission.ControlServer), // AlterAnyLinkedServer
        new("ALTER ANY SERVER AUDIT", "ALAA", PermissionCategory.None, Permission.ControlServer), // AlterAnyServerAudit
        new("ALTER ANY SERVER ROLE", "ALSR", PermissionCategory.None, Permission.ControlServer), // AlterAnyServerRole
        new("ALTER RESOURCES", "ALRS", PermissionCategory.None, Permission.ControlServer), // AlterResources
        new("ALTER SERVER STATE", "ALSS", PermissionCategory.None, Permission.ControlServer), // AlterServerState
        new("ALTER SETTINGS", "ALST", PermissionCategory.None, Permission.ControlServer), // AlterSettings
        new("ALTER TRACE", "ALTR", PermissionCategory.None, Permission.ControlServer), // AlterTrace
        new("AUTHENTICATE SERVER", "AUTH", PermissionCategory.None, Permission.ControlServer), // AuthenticateServer
        new("CONNECT ANY DATABASE", "CADB", PermissionCategory.None, Permission.ControlServer), // ConnectAnyDatabase
        new("CONNECT SQL", "COSQ", PermissionCategory.None, Permission.ControlServer), // ConnectSql
        new("CONTROL SERVER", "CL  ", PermissionCategory.None, Permission.Other), // ControlServer
        new("CREATE ANY EVENT SESSION", "CRES", PermissionCategory.None, Permission.AlterAnyEventSession), // CreateAnyEventSession
        new("CREATE AVAILABILITY GROUP", "CRAC", PermissionCategory.None, Permission.AlterAnyAvailabilityGroup), // CreateAvailabilityGroup
        new("CREATE DDL EVENT NOTIFICATION", "CRDE", PermissionCategory.None, Permission.AlterAnyEventNotification), // CreateDdlEventNotification
        new("CREATE ENDPOINT", "CRHE", PermissionCategory.None, Permission.AlterAnyEndpoint), // CreateEndpoint
        new("CREATE LOGIN", "CRLG", PermissionCategory.None, Permission.AlterAnyLogin), // CreateLogin
        new("CREATE SERVER ROLE", "CRSR", PermissionCategory.None, Permission.AlterAnyServerRole), // CreateServerRole
        new("CREATE TRACE EVENT NOTIFICATION", "CRTE", PermissionCategory.None, Permission.AlterAnyEventNotification), // CreateTraceEventNotification
        new("DROP ANY EVENT SESSION", "DRES", PermissionCategory.None, Permission.AlterAnyEventSession), // DropAnyEventSession
        new("EXTERNAL ACCESS ASSEMBLY", "XA  ", PermissionCategory.None, Permission.UnsafeAssembly), // ExternalAccessAssembly
        new("SELECT ALL USER SECURABLES", "SUS ", PermissionCategory.None, Permission.ControlServer), // SelectAllUserSecurables
        new("SHUTDOWN", "SHDN", PermissionCategory.None, Permission.ControlServer), // Shutdown
        new("UNSAFE ASSEMBLY", "XU  ", PermissionCategory.None, Permission.ControlServer), // UnsafeAssembly
        new("VIEW ANY CRYPTOGRAPHICALLY SECURED DEFINITION", "VACD", PermissionCategory.None, Permission.ControlServer), // ViewAnyCryptographicallySecuredDefinition
        new("VIEW ANY DATABASE", "VWDB", PermissionCategory.None, Permission.ViewAnyDefinition), // ViewAnyDatabase
        new("VIEW ANY ERROR LOG", "VEL ", PermissionCategory.None, Permission.ControlServer), // ViewAnyErrorLog
        new("VIEW ANY PERFORMANCE DEFINITION", "VAP ", PermissionCategory.None, Permission.ViewAnyDefinition), // ViewAnyPerformanceDefinition
        new("VIEW ANY SECURITY DEFINITION", "VAS ", PermissionCategory.None, Permission.ViewAnyDefinition), // ViewAnySecurityDefinition
        new("VIEW SERVER SECURITY AUDIT", "VSSA", PermissionCategory.None, Permission.ControlServer), // ViewServerSecurityAudit
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
                "ADMINISTER BULK OPERATIONS" => Permission.AdministerBulkOperations,
                "ALTER" => Permission.Alter,
                "ALTER ANY AVAILABILITY GROUP" => Permission.AlterAnyAvailabilityGroup,
                "ALTER ANY CONNECTION" => Permission.AlterAnyConnection,
                "ALTER ANY CREDENTIAL" => Permission.AlterAnyCredential,
                "ALTER ANY DATABASE" => Permission.AlterAnyDatabase,
                "ALTER ANY DATABASE DDL TRIGGER" => Permission.AlterAnyDatabaseDdlTrigger,
                "ALTER ANY ENDPOINT" => Permission.AlterAnyEndpoint,
                "ALTER ANY EVENT NOTIFICATION" => Permission.AlterAnyEventNotification,
                "ALTER ANY EVENT SESSION" => Permission.AlterAnyEventSession,
                "ALTER ANY EVENT SESSION ADD EVENT" => Permission.AlterAnyEventSessionAddEvent,
                "ALTER ANY EVENT SESSION ADD TARGET" => Permission.AlterAnyEventSessionAddTarget,
                "ALTER ANY EVENT SESSION DISABLE" => Permission.AlterAnyEventSessionDisable,
                "ALTER ANY EVENT SESSION DROP EVENT" => Permission.AlterAnyEventSessionDropEvent,
                "ALTER ANY EVENT SESSION DROP TARGET" => Permission.AlterAnyEventSessionDropTarget,
                "ALTER ANY EVENT SESSION ENABLE" => Permission.AlterAnyEventSessionEnable,
                "ALTER ANY EVENT SESSION OPTION" => Permission.AlterAnyEventSessionOption,
                "ALTER ANY FULLTEXT CATALOG" => Permission.AlterAnyFullTextCatalog,
                "ALTER ANY LINKED SERVER" => Permission.AlterAnyLinkedServer,
                "ALTER ANY LOGIN" => Permission.AlterAnyLogin,
                "ALTER ANY ROLE" => Permission.AlterAnyRole,
                "ALTER ANY SCHEMA" => Permission.AlterAnySchema,
                "ALTER ANY SERVER AUDIT" => Permission.AlterAnyServerAudit,
                "ALTER ANY SERVER ROLE" => Permission.AlterAnyServerRole,
                "ALTER RESOURCES" => Permission.AlterResources,
                "ALTER SERVER STATE" => Permission.AlterServerState,
                "ALTER SETTINGS" => Permission.AlterSettings,
                "ALTER TRACE" => Permission.AlterTrace,
                "AUTHENTICATE" => Permission.Authenticate,
                "AUTHENTICATE SERVER" => Permission.AuthenticateServer,
                "CONNECT" => Permission.Connect,
                "CONNECT ANY DATABASE" => Permission.ConnectAnyDatabase,
                "CONNECT SQL" => Permission.ConnectSql,
                "CONTROL" => Permission.Control,
                "CONTROL SERVER" => Permission.ControlServer,
                "CREATE AGGREGATE" => Permission.CreateAggregate,
                "CREATE ANY DATABASE" => Permission.CreateAnyDatabase,
                "CREATE ANY EVENT SESSION" => Permission.CreateAnyEventSession,
                "CREATE ASSEMBLY" => Permission.CreateAssembly,
                "CREATE AVAILABILITY GROUP" => Permission.CreateAvailabilityGroup,
                "CREATE DDL EVENT NOTIFICATION" => Permission.CreateDdlEventNotification,
                "CREATE ENDPOINT" => Permission.CreateEndpoint,
                "CREATE FULLTEXT CATALOG" => Permission.CreateFullTextCatalog,
                "CREATE FUNCTION" => Permission.CreateFunction,
                "CREATE LOGIN" => Permission.CreateLogin,
                "CREATE PROCEDURE" => Permission.CreateProcedure,
                "CREATE SEQUENCE" => Permission.CreateSequence,
                "CREATE SERVER ROLE" => Permission.CreateServerRole,
                "CREATE SYNONYM" => Permission.CreateSynonym,
                "CREATE TABLE" => Permission.CreateTable,
                "CREATE TRACE EVENT NOTIFICATION" => Permission.CreateTraceEventNotification,
                "CREATE TYPE" => Permission.CreateType,
                "CREATE VIEW" => Permission.CreateView,
                "CREATE XML SCHEMA COLLECTION" => Permission.CreateXmlSchemaCollection,
                "DELETE" => Permission.Delete,
                "DROP ANY EVENT SESSION" => Permission.DropAnyEventSession,
                // GRANT EXEC is the same permission, stored and reported as EXECUTE
                // (probed 2026-09-27 against SQL Server 2025).
                "EXEC" => Permission.Execute,
                "EXECUTE" => Permission.Execute,
                "EXTERNAL ACCESS ASSEMBLY" => Permission.ExternalAccessAssembly,
                "IMPERSONATE" => Permission.Impersonate,
                "IMPERSONATE ANY LOGIN" => Permission.ImpersonateAnyLogin,
                "INSERT" => Permission.Insert,
                "RECEIVE" => Permission.Receive,
                "REFERENCES" => Permission.References,
                "SELECT" => Permission.Select,
                "SELECT ALL USER SECURABLES" => Permission.SelectAllUserSecurables,
                "SHUTDOWN" => Permission.Shutdown,
                "TAKE OWNERSHIP" => Permission.TakeOwnership,
                "UNMASK" => Permission.Unmask,
                "UNSAFE ASSEMBLY" => Permission.UnsafeAssembly,
                "UPDATE" => Permission.Update,
                "VIEW ANY COLUMN ENCRYPTION KEY DEFINITION" => Permission.ViewAnyColumnEncryptionKeyDefinition,
                "VIEW ANY COLUMN MASTER KEY DEFINITION" => Permission.ViewAnyColumnMasterKeyDefinition,
                "VIEW ANY CRYPTOGRAPHICALLY SECURED DEFINITION" => Permission.ViewAnyCryptographicallySecuredDefinition,
                "VIEW ANY DATABASE" => Permission.ViewAnyDatabase,
                "VIEW ANY DEFINITION" => Permission.ViewAnyDefinition,
                "VIEW ANY ERROR LOG" => Permission.ViewAnyErrorLog,
                "VIEW ANY PERFORMANCE DEFINITION" => Permission.ViewAnyPerformanceDefinition,
                "VIEW ANY SECURITY DEFINITION" => Permission.ViewAnySecurityDefinition,
                "VIEW CHANGE TRACKING" => Permission.ViewChangeTracking,
                "VIEW DATABASE PERFORMANCE STATE" => Permission.ViewDatabasePerformanceState,
                "VIEW DATABASE STATE" => Permission.ViewDatabaseState,
                "VIEW DEFINITION" => Permission.ViewDefinition,
                "VIEW SERVER PERFORMANCE STATE" => Permission.ViewServerPerformanceState,
                "VIEW SERVER SECURITY AUDIT" => Permission.ViewServerSecurityAudit,
                "VIEW SERVER SECURITY STATE" => Permission.ViewServerSecurityState,
                "VIEW SERVER STATE" => Permission.ViewServerState,
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

        /// <summary>Whether this is a SERVER-class permission — one a grant stores in <c>sys.server_permissions</c> at class 100.</summary>
        internal bool IsServerClass => Table[(byte)permission].ServerCover is not null;

        /// <summary>
        /// The immediate covering permission for this permission at
        /// <paramref name="securableClass"/> — the permission that, when granted,
        /// implies this one — or <see langword="null"/> at the top (<c>CONTROL</c>,
        /// or <c>CONTROL SERVER</c> at server scope). Chains to <c>CONTROL</c>
        /// for most; the class-specific exceptions (OBJECT SELECT ← RECEIVE ←
        /// CONTROL, DATABASE CREATE TABLE ← ALTER ← CONTROL, the VIEW …STATE
        /// granular graph) and the whole SERVER class come straight from
        /// <c>sys.fn_builtin_permissions</c> (probed 2026-09-29 against SQL
        /// Server 2025).
        /// </summary>
        internal Permission? Covering(byte securableClass) => (securableClass, permission) switch
        {
            (PermissionChecker.ClassServer, _) => Table[(byte)permission].ServerCover is { } cover && cover != Permission.Other ? cover : null,
            (_, Permission.Control) => null,
            (PermissionChecker.ClassObject, Permission.Select) => Permission.Receive,
            (PermissionChecker.ClassObject, Permission.Receive) => Permission.Control,
            // Database-scope ALTER covers the granular DDL permissions that
            // gate the statement kinds (imported from sys.fn_builtin_permissions:
            // covering_permission_name = ALTER for each). CREATE ASSEMBLY covers
            // through ALTER ANY ASSEMBLY on real, which isn't modeled, so it
            // falls to CONTROL.
            (PermissionChecker.ClassDatabase, Permission.AlterAnyDatabaseDdlTrigger) => Permission.Alter,
            (PermissionChecker.ClassDatabase, Permission.AlterAnyFullTextCatalog) => Permission.Alter,
            (PermissionChecker.ClassDatabase, Permission.AlterAnyRole) => Permission.Alter,
            (PermissionChecker.ClassDatabase, Permission.AlterAnySchema) => Permission.Alter,
            (PermissionChecker.ClassDatabase, Permission.CreateFullTextCatalog) => Permission.AlterAnyFullTextCatalog,
            (PermissionChecker.ClassDatabase, Permission.CreateSynonym) => Permission.Alter,
            (PermissionChecker.ClassDatabase, Permission.CreateTable) => Permission.Alter,
            (PermissionChecker.ClassDatabase, Permission.CreateType) => Permission.Alter,
            (PermissionChecker.ClassDatabase, Permission.CreateXmlSchemaCollection) => Permission.Alter,
            // VIEW DATABASE STATE covers VIEW DATABASE PERFORMANCE STATE at
            // database scope; the cross-scope server → database satisfaction is
            // ServerParent's.
            (PermissionChecker.ClassDatabase, Permission.ViewDatabasePerformanceState) => Permission.ViewDatabaseState,
            _ => Permission.Control,
        };

        /// <summary>
        /// The SERVER-class permission that implies this DATABASE-class one in
        /// every database — <c>parent_covering_permission_name</c> in
        /// <c>sys.fn_builtin_permissions('DATABASE')</c>: <c>ALTER</c> ←
        /// <c>ALTER ANY DATABASE</c>, <c>CONNECT</c> ← <c>CONNECT ANY
        /// DATABASE</c>, <c>VIEW DEFINITION</c> ← <c>VIEW ANY DEFINITION</c>,
        /// the state pair ← their server siblings, and <c>CONTROL SERVER</c>
        /// for the rest. A request walks its database covering chain and asks
        /// each link's parent, so <c>CREATE TABLE</c> is implied by <c>ALTER
        /// ANY DATABASE</c> through <c>ALTER</c>.
        /// </summary>
        internal Permission ServerParent => permission switch
        {
            Permission.Alter => Permission.AlterAnyDatabase,
            Permission.Authenticate => Permission.AuthenticateServer,
            Permission.Connect => Permission.ConnectAnyDatabase,
            Permission.ViewAnyColumnEncryptionKeyDefinition or Permission.ViewAnyColumnMasterKeyDefinition or Permission.ViewDefinition => Permission.ViewAnyDefinition,
            Permission.ViewDatabasePerformanceState => Permission.ViewServerPerformanceState,
            Permission.ViewDatabaseState => Permission.ViewServerState,
            _ => Permission.ControlServer,
        };

        /// <summary>
        /// This permission's covering chain at <paramref name="securableClass"/> —
        /// the permission itself, then each broader covering permission up to the
        /// top (<c>CONTROL</c> / <c>CONTROL SERVER</c>). The single covering
        /// walk shared by the database checker's satisfier build-out
        /// (<see cref="PermissionChecker"/>) and the server-permission check
        /// (<see cref="Simulation.HoldsServerPermission"/>).
        /// </summary>
        internal IEnumerable<Permission> CoveringChain(byte securableClass)
        {
            Permission? current = permission;
            while (current is Permission p)
            {
                yield return p;
                current = p.Covering(securableClass);
            }
        }

        /// <summary>
        /// Whether this (granted) permission satisfies <paramref name="required"/>
        /// at <paramref name="securableClass"/> — it is <paramref name="required"/>
        /// itself or one of its covering permissions.
        /// </summary>
        internal bool Covers(Permission required, byte securableClass)
        {
            foreach (var p in required.CoveringChain(securableClass))
            {
                if (p == permission)
                    return true;
            }
            return false;
        }
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
