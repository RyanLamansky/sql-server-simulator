namespace SqlServerSimulator;

/// <summary>
/// How <c>sp_set_session_context</c> and <c>SESSION_CONTEXT</c> match keys,
/// as probed 2026-10-04 against SQL Server 2025: trailing spaces aside, the
/// first character compares without case and every other character exactly —
/// a key set as <c>Key</c> reads back as <c>key</c> but not as <c>KEY</c>, and
/// <c>key</c> and <c>KEY</c> are two keys.
/// </summary>
internal sealed class SessionContextKeyComparer : IEqualityComparer<string>
{
    public static readonly SessionContextKeyComparer Instance = new();

    private SessionContextKeyComparer()
    {
    }

    public bool Equals(string? x, string? y)
    {
        if (x is null || y is null)
            return x is null && y is null;
        var left = x.AsSpan().TrimEnd(' ');
        var right = y.AsSpan().TrimEnd(' ');
        return left.Length == right.Length
            && (left.Length == 0 || char.ToLowerInvariant(left[0]) == char.ToLowerInvariant(right[0]))
            && left[Math.Min(1, left.Length)..].SequenceEqual(right[Math.Min(1, right.Length)..]);
    }

    public int GetHashCode(string obj)
    {
        var key = obj.AsSpan().TrimEnd(' ');
        if (key.Length == 0)
            return 0;
        var hash = new HashCode();
        hash.Add(char.ToLowerInvariant(key[0]));
        hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(key[1..]));
        return hash.ToHashCode();
    }
}
