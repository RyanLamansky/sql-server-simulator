using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Hash-key wrapper around a <see cref="SqlValue"/> tuple. Used as a dictionary
/// key for GROUP BY buckets and as the per-<see cref="Heap"/> seek
/// cache's bucket key. Two NULL slots compare equal (matching SQL Server: NULL is
/// a valid group key with one bucket). Top-level in the Parser namespace so the
/// query planner (<see cref="Selection"/>), the seek cache
/// (<see cref="HeapSeekCache"/>), and foreign-key enforcement all share it.
/// </summary>
internal readonly struct SqlValueKey(SqlValue[] values) : IEquatable<SqlValueKey>
{
    public static readonly SqlValueKey Empty = new([]);

    private readonly SqlValue[] values = values;

    /// <summary>The number of tuple components (key columns) in this key.</summary>
    internal int ComponentCount => this.values.Length;

    /// <summary>The component at <paramref name="index"/> — used by the range-seek ordered comparer.</summary>
    internal SqlValue ComponentAt(int index) => this.values[index];

    public bool Equals(SqlValueKey other)
    {
        if (this.values.Length != other.values.Length)
            return false;
        for (var i = 0; i < this.values.Length; i++)
        {
            var a = this.values[i];
            var b = other.values[i];
            if (a.IsNull != b.IsNull)
                return false;
            if (a.IsNull)
                continue;
            if (!a.Equals(b))
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is SqlValueKey other && Equals(other);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var v in this.values)
            h.Add(v.IsNull ? 0 : v.GetHashCode());
        return h.ToHashCode();
    }
}
