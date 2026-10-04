using System.Collections.Frozen;

namespace SqlServerSimulator;

/// <summary>
/// The implication graph of <c>sys.fn_builtin_permissions</c>: for a permission
/// on a securable class, every (class, permission) pair whose grant implies it —
/// the class's own covering permission, transitively, and each link's parent
/// covering permission on the enclosing class (an object's on its schema, a
/// schema's on its database). The permission checker builds its satisfier and
/// deny sets from this, so a request is answered exactly the way the built-in
/// table says real answers it: a schema's <c>TAKE OWNERSHIP</c> does not reach
/// the objects in it (their parent is the schema's <c>CONTROL</c>), and a
/// database's <c>ALTER ANY SCHEMA</c> reaches every schema's <c>ALTER</c> and
/// through it every object's.
/// </summary>
/// <remarks>
/// The server-side classes are left out: a database request that reaches no
/// grant asks the login's server permissions separately
/// (<see cref="ServerLoginRights"/>).
/// </remarks>
internal static class PermissionGraph
{
    /// <summary>One (securable class, permission) pair a request can be satisfied by; <see cref="Class"/> is a <c>sys.database_permissions.class</c> value.</summary>
    internal readonly struct Link(byte securableClass, string name)
    {
        public readonly byte Class = securableClass;
        public readonly string Name = name;
    }

    /// <summary>
    /// Per (class description, permission name), the pairs implying it,
    /// itself first. Keyed by the class descriptions <c>sys.fn_builtin_permissions</c>
    /// uses, so the three database-principal kinds (<c>USER</c>, <c>ROLE</c>,
    /// <c>APPLICATION ROLE</c>) keep their own graphs.
    /// </summary>
    private static readonly FrozenDictionary<(string ClassDescription, string Name), Link[]> Closures = Build();

    /// <summary>
    /// The pairs that imply <paramref name="name"/> on a
    /// <paramref name="classDescription"/> securable, itself first; just the
    /// permission itself when the built-in table doesn't list it for that class.
    /// </summary>
    internal static Link[] Implying(string classDescription, string name) =>
        Closures.TryGetValue((classDescription, name), out var links) ? links : [new(ClassOf(classDescription), name)];

    /// <summary>Whether <paramref name="name"/> is a permission of <paramref name="classDescription"/> securables.</summary>
    internal static bool IsPermissionOf(string classDescription, string name) =>
        AllPermissions.Contains((classDescription, name));

    /// <summary>The 4-char <c>type</c> code the built-in table lists for <paramref name="name"/>, or null for a name it doesn't carry.</summary>
    internal static string? TypeCodeOf(string name) =>
        TypeCodes.TryGetValue(name, out var code) ? code : null;

    private static readonly FrozenDictionary<string, string> TypeCodes = BuildTypeCodes();

    private static FrozenDictionary<string, string> BuildTypeCodes()
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in BuiltinPermissionRows.All)
            _ = codes.TryAdd(row.PermissionName, row.Type.PadRight(4));
        return codes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every (class description, permission name) pair the built-in table lists, the server-side classes included.</summary>
    private static readonly FrozenSet<(string ClassDescription, string Name)> AllPermissions =
        BuiltinPermissionRows.All.Select(row => (row.ClassDescription, row.PermissionName)).ToFrozenSet();

    /// <summary>
    /// The <c>sys.database_permissions.class</c> value for a
    /// <c>sys.fn_builtin_permissions</c> class description, or
    /// <see cref="byte.MaxValue"/> for a server-side class.
    /// </summary>
    internal static byte ClassOf(string classDescription) => classDescription switch
    {
        "APPLICATION ROLE" or "ROLE" or "USER" => PermissionChecker.ClassDatabasePrincipal,
        "ASSEMBLY" => 5,
        "ASYMMETRIC KEY" => 26,
        "CERTIFICATE" => 25,
        "CONTRACT" => 16,
        "DATABASE" => PermissionChecker.ClassDatabase,
        "DATABASE SCOPED CREDENTIAL" => 28,
        "EXTERNAL LANGUAGE" => 34,
        "EXTERNAL MODEL" => 35,
        "FULLTEXT CATALOG" => PermissionChecker.ClassFulltextCatalog,
        "FULLTEXT STOPLIST" => 29,
        "MESSAGE TYPE" => 15,
        "OBJECT" => PermissionChecker.ClassObject,
        "REMOTE SERVICE BINDING" => 18,
        "ROUTE" => 19,
        "SCHEMA" => PermissionChecker.ClassSchema,
        "SEARCH PROPERTY LIST" => 31,
        "SERVICE" => 17,
        "SYMMETRIC KEY" => 24,
        "TYPE" => PermissionChecker.ClassType,
        "XML SCHEMA COLLECTION" => PermissionChecker.ClassXmlSchemaCollection,
        _ => byte.MaxValue,
    };

    private static FrozenDictionary<(string, string), Link[]> Build()
    {
        var direct = new Dictionary<(string, string), List<(string, string)>>();
        foreach (var row in BuiltinPermissionRows.All)
        {
            if (ClassOf(row.ClassDescription) == byte.MaxValue)
                continue;
            var implying = new List<(string, string)>(2);
            if (row.Covering.Length != 0)
                implying.Add((row.ClassDescription, row.Covering));
            if (row.ParentCovering.Length != 0 && ClassOf(row.ParentClass) != byte.MaxValue)
                implying.Add((row.ParentClass, row.ParentCovering));
            direct[(row.ClassDescription, row.PermissionName)] = implying;
        }

        var closures = new Dictionary<(string, string), Link[]>(direct.Count);
        var seen = new HashSet<(string, string)>();
        var queue = new Queue<(string, string)>();
        var links = new List<Link>();
        foreach (var (key, _) in direct)
        {
            seen.Clear();
            links.Clear();
            queue.Enqueue(key);
            _ = seen.Add(key);
            while (queue.TryDequeue(out var current))
            {
                links.Add(new(ClassOf(current.Item1), current.Item2));
                if (!direct.TryGetValue(current, out var next))
                    continue;
                foreach (var pair in next)
                {
                    if (seen.Add(pair))
                        queue.Enqueue(pair);
                }
            }
            closures[key] = [.. links];
        }
        return closures.ToFrozenDictionary();
    }
}
