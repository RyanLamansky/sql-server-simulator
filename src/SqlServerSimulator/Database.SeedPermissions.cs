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
    }

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
