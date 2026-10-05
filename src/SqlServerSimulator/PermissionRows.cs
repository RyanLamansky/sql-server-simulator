namespace SqlServerSimulator;

/// <summary>
/// A database's stored permission rows (<see cref="Database.Permissions"/>),
/// in the order <c>sys.database_permissions</c> lists them, with an index by
/// securable the permission checker reads instead of the whole list.
/// </summary>
/// <remarks>
/// Every database carries a few hundred seeded rows — <c>public</c>'s
/// <c>SELECT</c> on the system objects — so a check that walked the list paid
/// for all of them on each statement a restricted principal ran, several
/// times over when it checked columns. The index is rebuilt on the first read
/// after a change, and every change goes through this class, which is what
/// keeps it exact: a mutation bumps <see cref="version"/>, and a lookup that
/// finds the built index naming an older one builds it again.
/// </remarks>
internal sealed class PermissionRows
{
    private readonly List<DatabasePermission> rows = [];

    /// <summary>Bumped by every mutation; the index records the one it was built from.</summary>
    private int version;

    private Index? index;

    private sealed class Index(int version, Dictionary<long, DatabasePermission[]> bySecurable)
    {
        public readonly int Version = version;
        public readonly Dictionary<long, DatabasePermission[]> BySecurable = bySecurable;
    }

    public List<DatabasePermission>.Enumerator GetEnumerator() => this.rows.GetEnumerator();

    /// <summary>
    /// The rows stored on the (<paramref name="securableClass"/>,
    /// <paramref name="majorId"/>) securable, columns included, in stored order.
    /// </summary>
    public DatabasePermission[] On(byte securableClass, int majorId)
    {
        var current = this.index;
        if (current is null || current.Version != Volatile.Read(ref this.version))
            this.index = current = this.Build();
        return current.BySecurable.TryGetValue(Key(securableClass, majorId), out var found) ? found : [];
    }

    private static long Key(byte securableClass, int majorId) => ((long)securableClass << 32) | (uint)majorId;

    private Index Build()
    {
        // The version is read ahead of the rows, so a change racing the build
        // leaves an index naming the older version, which the next read rebuilds.
        var builtFrom = Volatile.Read(ref this.version);
        var lists = new Dictionary<long, List<DatabasePermission>>();
        foreach (var row in this.rows)
        {
            var key = Key(row.Class, row.MajorId);
            if (!lists.TryGetValue(key, out var list))
                lists[key] = list = [];
            list.Add(row);
        }
        var bySecurable = new Dictionary<long, DatabasePermission[]>(lists.Count);
        foreach (var (key, list) in lists)
            bySecurable[key] = [.. list];
        return new(builtFrom, bySecurable);
    }

    private void Changed() => Interlocked.Increment(ref this.version);

    public void Add(DatabasePermission row)
    {
        this.rows.Add(row);
        this.Changed();
    }

    public void AddRange(DatabasePermission[] added)
    {
        this.rows.AddRange(added);
        this.Changed();
    }

    public bool Remove(DatabasePermission row)
    {
        var removed = this.rows.Remove(row);
        this.Changed();
        return removed;
    }

    public int RemoveAll(Predicate<DatabasePermission> match)
    {
        var removed = this.rows.RemoveAll(match);
        this.Changed();
        return removed;
    }

    public void Clear()
    {
        this.rows.Clear();
        this.Changed();
    }

    public bool Contains(DatabasePermission row) => this.rows.Contains(row);

    public bool Exists(Predicate<DatabasePermission> match) => this.rows.Exists(match);

    public DatabasePermission? Find(Predicate<DatabasePermission> match) => this.rows.Find(match);

    public List<DatabasePermission> FindAll(Predicate<DatabasePermission> match) => this.rows.FindAll(match);
}
