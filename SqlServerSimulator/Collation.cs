using System.Collections.Frozen;

namespace SqlServerSimulator;

/// <summary>
/// The SQL Server equivalent to .NET's <see cref="IComparer{T}"/> for strings.
/// </summary>
internal abstract class Collation : IComparer<string>, IEqualityComparer<string>
{
    private protected Collation()
    {
    }

    public abstract string Name { get; }

    internal static readonly SQL_Latin1_General_CP1_CI_AS Default = new();

    /// <summary>
    /// Closed accept-list of collation names the simulator recognizes as
    /// metadata. ALTER DATABASE COLLATE / CREATE TABLE column COLLATE accept
    /// these names without raising; the loader records them on the
    /// database / column for catalog-view round-trip but every comparison
    /// continues to route through <see cref="Default"/> (the actual
    /// collation algorithms aren't modeled — see
    /// <c>docs/claude/bacpac-prerequisites.md</c> step 13 collation entry).
    /// Names outside this set surface as <see cref="NotSupportedException"/>
    /// in direct SQL; the BACPAC loader catches and records on
    /// <c>BacpacLoadResult.Warnings</c>. Each entry carries a human-readable
    /// description that <c>sys.fn_helpcollations()</c> exposes verbatim
    /// (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal static readonly FrozenDictionary<string, string> Recognized =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SQL_Latin1_General_CP1_CI_AS"] = "Latin1-General, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive for Unicode Data, SQL Server Sort Order 52 on Code Page 1252 for non-Unicode Data",
            ["Latin1_General_100_CI_AS"] = "Latin1-General-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if <paramref name="name"/> is on the
    /// <see cref="Recognized"/> whitelist. Case-insensitive (SQL Server
    /// collation names are themselves case-insensitive identifiers).
    /// </summary>
    internal static bool IsRecognized(string name) => Recognized.ContainsKey(name);

    public abstract int Compare(string? x, string? y);

    public virtual bool Equals(string? x, string? y) => this.Compare(x, y) == 0;

    public abstract int GetHashCode(string obj);

    internal sealed class SQL_Latin1_General_CP1_CI_AS : Collation
    {
        public override string Name => "SQL_Latin1_General_CP1_CI_AS";

        public override int Compare(string? x, string? y) => StringComparer.InvariantCultureIgnoreCase.Compare(x, y);

        public override int GetHashCode(string obj) => StringComparer.InvariantCultureIgnoreCase.GetHashCode(obj);
    }
}
