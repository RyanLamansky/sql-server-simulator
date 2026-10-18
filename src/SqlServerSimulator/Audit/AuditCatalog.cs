using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;

namespace SqlServerSimulator.Audit;

/// <summary>
/// SQL Server Audit's own catalog: <c>sys.dm_audit_actions</c>' 663 rows and
/// <c>sys.dm_audit_class_type_map</c>'s 110, dumped from SQL Server 2025
/// (17.0.4065.4, 2026-10-17) into <c>Audit/Metadata/</c> and zipped into the
/// assembly, plus the bit each permission takes in an audit record's
/// <c>permission_bitmask</c>, read off real's records of a <c>GRANT</c> of
/// every permission of the four classes. Read once per process on first use.
/// </summary>
/// <remarks>
/// <c>Actions.tsv</c> and <c>ClassTypes.tsv</c> are the views' rows in real's
/// order, tab-separated, <c>\N</c> for NULL; <c>PermissionBits.tsv</c> is
/// class, bit and permission name.
/// </remarks>
internal sealed class AuditCatalog
{
    /// <summary>Every row of <c>sys.dm_audit_actions</c>, in real's order.</summary>
    public readonly AuditAction[] Actions;

    /// <summary>Every row of <c>sys.dm_audit_class_type_map</c>, in real's order.</summary>
    public readonly (string? ClassType, string? ClassTypeDesc, string? SecurableClassDesc)[] ClassTypes;

    /// <summary>The action groups a server audit specification takes, by name.</summary>
    private readonly FrozenDictionary<string, AuditAction> serverGroups;

    /// <summary>The action groups and the actions a database audit specification takes, by name.</summary>
    private readonly FrozenDictionary<string, AuditAction> databaseGroupsAndActions;

    /// <summary>The group covering each action on each class, keyed by <c>action_id</c> and <c>class_desc</c>.</summary>
    private readonly FrozenDictionary<string, string> groupOf;

    /// <summary>Each permission's bit, keyed by class and name.</summary>
    private readonly FrozenDictionary<string, int> permissionBits;

    private AuditCatalog()
    {
        using var stream = typeof(AuditCatalog).Assembly.GetManifestResourceStream("SqlServerSimulator.AuditMetadata.zip")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.AuditMetadata.zip is missing.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        this.Actions = [.. ReadRows(archive, "Actions.tsv").Select(static fields => new AuditAction(
            fields[0]!, fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7], fields[8] == "1"))];
        this.ClassTypes = [.. ReadRows(archive, "ClassTypes.tsv").Select(static fields => (fields[0], fields[1], fields[2]))];

        var server = new Dictionary<string, AuditAction>(StringComparer.OrdinalIgnoreCase);
        var database = new Dictionary<string, AuditAction>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in this.Actions)
        {
            if (action.Name is { } name)
            {
                // A database specification names its groups and the seven
                // actions SCHEMA_OBJECT_ACCESS_GROUP covers on the database
                // (EXECUTE among them, though real lists no level for it).
                if (action.ConfigurationLevel == "Group" && action.ClassDesc == "SERVER")
                    _ = server.TryAdd(name, action);
                else if (action.ClassDesc == "DATABASE" && (action.ConfigurationLevel == "Group" || action.CoveringActionName == "SCHEMA_OBJECT_ACCESS_GROUP"))
                    _ = database.TryAdd(name, action);
            }
            if (action.ContainingGroupName is { } group && action.ClassDesc is { } classDesc)
                _ = groups.TryAdd(action.Id + "|" + classDesc, group);
        }
        this.serverGroups = server.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        this.databaseGroupsAndActions = database.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        this.groupOf = groups.ToFrozenDictionary(StringComparer.Ordinal);
        this.permissionBits = ReadRows(archive, "PermissionBits.tsv").ToFrozenDictionary(
            static fields => fields[0] + "|" + fields[2], static fields => int.Parse(fields[1]!, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The process-wide catalog, read on first use.</summary>
    public static AuditCatalog Instance => Holder.Value;

    private static class Holder
    {
        public static readonly AuditCatalog Value = new();
    }

    /// <summary>The action group <paramref name="name"/> a server audit specification may add, or null.</summary>
    public AuditAction? ServerGroup(string name) => this.serverGroups.GetValueOrDefault(name);

    /// <summary>The action group or action <paramref name="name"/> a database audit specification may add, or null.</summary>
    public AuditAction? DatabaseGroupOrAction(string name) => this.databaseGroupsAndActions.GetValueOrDefault(name);

    /// <summary>The group that covers <paramref name="actionId"/> on securables of <paramref name="classDesc"/>, null for one no group covers.</summary>
    public string? GroupOf(string actionId, string classDesc) => this.groupOf.GetValueOrDefault(actionId + "|" + classDesc);

    /// <summary>
    /// <c>permission_bitmask</c> for <paramref name="permission"/> on a
    /// securable of <paramref name="classDesc"/> (<c>SERVER</c>,
    /// <c>DATABASE</c>, <c>SCHEMA</c> or <c>OBJECT</c>): sixteen bytes holding
    /// the permission's bit, big-endian; all zeros for a permission the table
    /// doesn't list.
    /// </summary>
    public byte[] PermissionBitmask(string classDesc, string permission)
    {
        var mask = new byte[16];
        if (this.permissionBits.TryGetValue(classDesc + "|" + permission, out var bit))
            mask[15 - (bit / 8)] = (byte)(1 << (bit % 8));
        return mask;
    }

    private static IEnumerable<string?[]> ReadRows(ZipArchive archive, string entry)
    {
        using var reader = new StreamReader((archive.GetEntry(entry) ?? throw new InvalidOperationException($"{entry} is missing from the audit metadata.")).Open());
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            var fields = line.Split('\t');
            yield return Array.ConvertAll(fields, static field => field == "\\N" ? null : field);
        }
    }
}

/// <summary>One row of <c>sys.dm_audit_actions</c>.</summary>
internal sealed class AuditAction(string id, string? name, string? classDesc, string? coveringActionName, string? parentClassDesc, string? coveringParentActionName,
    string? configurationLevel, string? containingGroupName, bool actionInLog)
{
    /// <summary>The four-character id, trailing blanks kept (<c>SL  </c>).</summary>
    public readonly string Id = id;
    public readonly string? Name = name;
    public readonly string? ClassDesc = classDesc;
    public readonly string? CoveringActionName = coveringActionName;
    public readonly string? ParentClassDesc = parentClassDesc;
    public readonly string? CoveringParentActionName = coveringParentActionName;

    /// <summary><c>Group</c> for an action group, <c>Action</c> for an action a database specification names on a securable, else NULL.</summary>
    public readonly string? ConfigurationLevel = configurationLevel;
    public readonly string? ContainingGroupName = containingGroupName;
    public readonly bool ActionInLog = actionInLog;

    public bool IsGroup => this.ConfigurationLevel == "Group";
}
