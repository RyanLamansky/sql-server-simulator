namespace SqlServerSimulator;

/// <summary>
/// Matches server principal names — logins and server roles — as real does
/// under its default server collation: without case, kana type or width, as
/// <see cref="BuiltInToken"/> matches, and without trailing spaces, so
/// <c>SUSER_ID('x  ')</c> finds the login named <c>x</c> and a new login
/// named <c>x </c> beside it is a duplicate (probed 2026-10-25 against SQL
/// Server 2025). A principal keeps its name as written. A server whose
/// collation is case-sensitive matches its principals' names with case,
/// which this doesn't follow.
/// </summary>
internal sealed class ServerNameComparer : IEqualityComparer<string>
{
    public static readonly ServerNameComparer Instance = new();

    public bool Equals(string? x, string? y) =>
        x is null ? y is null : y is not null && BuiltInToken.Comparer.Equals(x.TrimEnd(' '), y.TrimEnd(' '));

    public int GetHashCode(string obj) => BuiltInToken.Comparer.GetHashCode(obj.TrimEnd(' '));
}
