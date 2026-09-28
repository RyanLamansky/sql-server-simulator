using System.Collections.Frozen;

namespace SqlServerSimulator;

partial class Database
{
    /// <summary>
    /// The grants every database starts with, which <c>sys.database_permissions</c>
    /// lists and <c>REVOKE</c> / <c>DENY</c> act on like any other (probed
    /// 2026-09-28 against SQL Server 2025): <c>public</c> holding the two
    /// column-encryption <c>VIEW ANY …</c> permissions, <c>dbo</c> holding
    /// <c>CONNECT</c>, <c>guest</c> holding it in <c>master</c>, <c>tempdb</c>
    /// and <c>msdb</c>, and <c>public</c> holding <c>SELECT</c> on the system
    /// objects <see cref="PublicSelectSeedObjectIds"/> lists. The rows are
    /// immutable, so every database shares the same instances.
    /// </summary>
    private void SeedPermissions(string name)
    {
        this.Permissions.AddRange(DatabaseSeedRows);
        if (BuiltInToken.EqualsAny(name, Simulation.MasterDatabaseName, Simulation.TempdbDatabaseName, Simulation.MsdbDatabaseName))
            this.Permissions.Add(GuestConnectSeedRow);
        this.Permissions.AddRange(PublicSelectSeedRows);
        if (BuiltInToken.EqualsAny(name, Simulation.MasterDatabaseName))
            this.SeedMasterPrincipals();
        else if (BuiltInToken.EqualsAny(name, Simulation.MsdbDatabaseName))
            this.SeedMsdbPrincipals();
    }

    /// <summary>
    /// Whether <c>public</c> starts with <c>SELECT</c> on system object
    /// <paramref name="objectId"/> here: the objects every database grants,
    /// and in <c>master</c> its own further ones.
    /// </summary>
    internal bool SeedsPublicSelect(int objectId) =>
        PublicSelectSeedObjectIdSet.Contains(objectId)
        || (this.seedsMasterGrants && MasterPublicSelectSeedObjectIdSet.Contains(objectId));

    private bool seedsMasterGrants;

    /// <summary>
    /// <c>master</c>'s own principals and grants beyond every database's
    /// (probed 2026-09-28 against SQL Server 2025): <c>public</c> holding
    /// <c>SELECT</c> on further system objects and <c>EXECUTE</c> on the
    /// system procedures and functions, and the policy-engine user and the
    /// agent's certificate-mapped user with their <c>CONNECT</c>, the
    /// latter's database-wide <c>EXECUTE</c> and the former's <c>EXECUTE</c>
    /// on <c>sp_syspolicy_execute_policy</c>.
    /// </summary>
    private void SeedMasterPrincipals()
    {
        this.seedsMasterGrants = true;
        this.Permissions.AddRange(MasterPublicSeedRows);
        var seedDate = DateTime.UtcNow;
        this.AddSeededPrincipal(new DatabasePrincipal(5, "##MS_PolicyEventProcessingLogin##", "S", "SQL_USER", isFixedRole: false, seedDate) { DefaultSchemaName = DefaultSchemaName });
        this.AddSeededPrincipal(new DatabasePrincipal(6, "##MS_AgentSigningCertificate##", "C", "CERTIFICATE_MAPPED_USER", isFixedRole: false, seedDate));
        this.Permissions.AddRange(
        [
            new(PermissionChecker.ClassDatabase, 0, 0, 5, DboPrincipalId, Permission.Connect, PermissionState.Grant),
            new(PermissionChecker.ClassObject, -887594060, 0, 5, DboPrincipalId, Permission.Execute, PermissionState.Grant),
            new(PermissionChecker.ClassDatabase, 0, 0, 6, DboPrincipalId, Permission.Connect, PermissionState.Grant),
            new(PermissionChecker.ClassDatabase, 0, 0, 6, DboPrincipalId, Permission.Execute, PermissionState.Grant),
        ]);
    }

    /// <summary>
    /// <c>msdb</c>'s own principals — the agent, mail, SSIS, data-collector,
    /// policy, server-group and utility roles, and the users those features
    /// run as — with real's ids, memberships and database-level grants
    /// (probed 2026-09-28 against SQL Server 2025). The grants real gives
    /// them on msdb's own tables and procedures aren't seeded, those objects
    /// not being modeled.
    /// </summary>
    private void SeedMsdbPrincipals()
    {
        var seedDate = DateTime.UtcNow;
        foreach (var (id, roleName) in MsdbRoles)
            this.AddSeededPrincipal(new DatabasePrincipal(id, roleName, "R", "DATABASE_ROLE", isFixedRole: false, seedDate));
        foreach (var (id, userName) in MsdbUsers)
        {
            this.AddSeededPrincipal(new DatabasePrincipal(id, userName, "S", "SQL_USER", isFixedRole: false, seedDate) { DefaultSchemaName = DefaultSchemaName });
            this.Permissions.Add(new(PermissionChecker.ClassDatabase, 0, 0, id, DboPrincipalId, Permission.Connect, PermissionState.Grant));
        }
        // dc_admin may impersonate the data collector's internal user.
        this.Permissions.Add(new(PermissionChecker.ClassDatabasePrincipal, 16, 0, 14, DboPrincipalId, Permission.Impersonate, PermissionState.Grant));
        this.RoleMembers.AddRange(MsdbRoleMembers);
    }

    private void AddSeededPrincipal(DatabasePrincipal principal)
    {
        this.Principals[principal.Name] = principal;
        this.nextPrincipalId = Math.Max(this.nextPrincipalId, principal.PrincipalId);
    }

    private static readonly (int Id, string Name)[] MsdbRoles =
    [
        (5, "TargetServersRole"),
        (6, "SQLAgentUserRole"),
        (7, "SQLAgentReaderRole"),
        (8, "SQLAgentOperatorRole"),
        (9, "DatabaseMailUserRole"),
        (10, "db_ssisadmin"),
        (11, "db_ssisltduser"),
        (12, "db_ssisoperator"),
        (13, "dc_operator"),
        (14, "dc_admin"),
        (15, "dc_proxy"),
        (17, "PolicyAdministratorRole"),
        (18, "ServerGroupAdministratorRole"),
        (19, "ServerGroupReaderRole"),
        (22, "UtilityCMRReader"),
        (23, "UtilityIMRWriter"),
        (24, "UtilityIMRReader"),
    ];

    private static readonly (int Id, string Name)[] MsdbUsers =
    [
        (16, "MS_DataCollectorInternalUser"),
        (20, "##MS_PolicyEventProcessingLogin##"),
        (21, "##MS_PolicyTsqlExecutionLogin##"),
    ];

    // (role, member), as real lists them in msdb.
    private static readonly (int RoleId, int MemberId)[] MsdbRoleMembers =
    [
        (11, 13), (11, 15), (12, 13), (12, 15), (12, 16), (14, 16), (13, 14), (17, 20), (17, 21),
        (19, 18), (8, 17), (7, 8), (6, 13), (6, 16), (6, 7), (24, 23),
    ];

    private static readonly DatabasePermission[] DatabaseSeedRows =
    [
        new(PermissionChecker.ClassDatabase, 0, 0, PublicPrincipalId, DboPrincipalId, Permission.ViewAnyColumnEncryptionKeyDefinition, PermissionState.Grant),
        new(PermissionChecker.ClassDatabase, 0, 0, PublicPrincipalId, DboPrincipalId, Permission.ViewAnyColumnMasterKeyDefinition, PermissionState.Grant),
        new(PermissionChecker.ClassDatabase, 0, 0, DboPrincipalId, DboPrincipalId, Permission.Connect, PermissionState.Grant),
    ];

    private static readonly DatabasePermission GuestConnectSeedRow =
        new(PermissionChecker.ClassDatabase, 0, 0, GuestPrincipalId, DboPrincipalId, Permission.Connect, PermissionState.Grant);

    /// <summary>
    /// The system objects whose <c>SELECT</c> a fresh database grants
    /// <c>public</c>, by real's fixed object ids (a user database, <c>model</c>
    /// and <c>tempdb</c> all list these 232, probed 2026-09-28 against SQL
    /// Server 2025). Among them are the catalog views whose reads the grant
    /// gates; the rest name system objects the simulator has no catalog for.
    /// </summary>
    internal static readonly int[] PublicSelectSeedObjectIds =
    [
        -101, -102, -103, -104, -105, -106, -107, -129, -130, -131, -132, -133, -134, -135, -136, -137,
        -138, -139, -140, -141, -142, -143, -385, -386, -387, -388, -389, -390, -391, -392, -393, -394,
        -395, -396, -397, -398, -399, -400, -401, -402, -403, -404, -405, -406, -407, -408, -409, -410,
        -411, -412, -413, -414, -415, -416, -417, -418, -419, -420, -421, -422, -423, -424, -425, -426,
        -428, -429, -430, -431, -432, -433, -434, -435, -436, -437, -438, -439, -440, -441, -442, -443,
        -444, -445, -446, -447, -448, -449, -450, -451, -452, -453, -454, -455, -456, -457, -458, -459,
        -460, -461, -462, -463, -464, -465, -466, -467, -468, -469, -476, -477, -478, -479, -480, -481,
        -482, -483, -484, -485, -489, -494, -496, -497, -498, -499, -500, -501, -502, -503, -504, -505,
        -513, -514, -515, -516, -517, -518, -519, -535, -536, -537, -538, -539, -540, -541, -549, -550,
        -551, -557, -558, -559, -560, -561, -562, -563, -564, -565, -566, -567, -568, -569, -570, -571,
        -572, -580, -582, -586, -587, -588, -589, -590, -591, -592, -593, -596, -598, -599, -600, -601,
        -602, -603, -605, -606, -610, -611, -612, -613, -614, -615, -617, -620, -621, -622, -623, -624,
        -625, -627, -628, -629, -630, -631, -632, -635, -636, -637, -638, -640, -641, -642, -647, -648,
        -649, -652, -654, -656, -657, -660, -662, -664, -665, -666, -667, -669, -670, -671, -672, -673,
        -674, -679, -680, -682, -683, -684, -685, -686,
    ];

    /// <summary>
    /// <see cref="PublicSelectSeedObjectIds"/> as a set: a catalog view it
    /// names needs <c>SELECT</c> to be read, which <c>public</c> holds until a
    /// <c>REVOKE</c> takes it (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static readonly FrozenSet<int> PublicSelectSeedObjectIdSet = PublicSelectSeedObjectIds.ToFrozenSet();

    private static readonly DatabasePermission[] PublicSelectSeedRows = Array.ConvertAll(
        PublicSelectSeedObjectIds,
        id => new DatabasePermission(PermissionChecker.ClassObject, id, 0, PublicPrincipalId, DboPrincipalId, Permission.Select, PermissionState.Grant));
}
