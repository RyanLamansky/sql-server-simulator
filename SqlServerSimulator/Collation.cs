using System.Collections.Frozen;
using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// The SQL Server equivalent to .NET's <see cref="IComparer{T}"/> for strings.
/// Three concrete subclasses match the names on the
/// <see cref="Recognized"/> whitelist; each implements its own case /
/// accent / symbol semantics rather than routing through a shared
/// hack-stripped comparer.
/// </summary>
internal abstract class Collation : IComparer<string>, IEqualityComparer<string>
{
    private protected Collation()
    {
    }

    public abstract string Name { get; }

    /// <summary>
    /// The instance returned for "SQL_Latin1_General_CP1_CI_AS" — the
    /// simulator's default database collation. Routed through
    /// <see cref="Database.CollationName"/> when no explicit
    /// <c>ALTER DATABASE COLLATE</c> has been issued, and (for the time
    /// being — see <c>docs/claude/database-options.md</c>) is also the
    /// comparer every string op falls back to regardless of declared
    /// column / database collation.
    /// </summary>
    internal static readonly SQL_Latin1_General_CP1_CI_AS Default = new();

    /// <summary>
    /// "Latin1_General_100_CI_AS" — the Windows-style v100 CI_AS sort
    /// rules. Sort treats primary-weight-zero symbols (apostrophe, hyphen,
    /// and the rest of <see cref="CompareOptions.IgnoreSymbols"/>) as
    /// ignorable; equality keeps them significant — matches probe-confirmed
    /// real-SQL-Server behavior where <c>MIN('Aaronsburg','''Aiea') =
    /// 'Aaronsburg'</c> while <c>'OBrien' = 'O''Brien'</c> evaluates 0.
    /// </summary>
    internal static readonly Latin1_General_100_CI_AS Latin1General100CiAs = new();

    /// <summary>
    /// "Latin1_General_CI_AS" — the older Windows-style CI_AS (pre-v100).
    /// For the Latin-1 + ASCII strings the simulator's regression bar
    /// exercises, semantically identical to <see cref="Latin1General100CiAs"/>
    /// (the v100 update changed Unicode-table ordering for non-Latin
    /// scripts and a handful of newly-added supplementary code points;
    /// none of those reach BACPAC payloads the simulator loads).
    /// </summary>
    internal static readonly Latin1_General_CI_AS Latin1GeneralCiAs = new();

    /// <summary>
    /// Closed accept-list of collation names the simulator recognizes as
    /// metadata. ALTER DATABASE COLLATE / CREATE TABLE column COLLATE accept
    /// these names without raising; the loader records them on the
    /// database / column for catalog-view round-trip. The comparison /
    /// sort / LIKE pipeline still routes every op through
    /// <see cref="Default"/> regardless of declared column / database
    /// collation (the per-collation algorithms below ARE correct on their
    /// own terms — the gap is the routing, not the algorithms; see
    /// <c>docs/claude/database-options.md</c>).
    /// Names outside this set surface as <see cref="NotSupportedException"/>
    /// in direct SQL; the BACPAC loader catches and records on
    /// <c>BacpacImportResult.Warnings</c>. Each entry's value is the
    /// human-readable description that <c>sys.fn_helpcollations()</c>
    /// exposes verbatim (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal static readonly FrozenDictionary<string, string> Recognized =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Default.Name] = "Latin1-General, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive for Unicode Data, SQL Server Sort Order 52 on Code Page 1252 for non-Unicode Data",
            [Latin1General100CiAs.Name] = "Latin1-General-100, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
            [Latin1GeneralCiAs.Name] = "Latin1-General, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Name-keyed lookup of the recognized <see cref="Collation"/>
    /// instances. Case-insensitive (SQL Server collation names are
    /// themselves case-insensitive identifiers). Keys and entries are
    /// kept in sync with <see cref="Recognized"/>.
    /// </summary>
    internal static readonly FrozenDictionary<string, Collation> ByName =
        new Dictionary<string, Collation>(StringComparer.OrdinalIgnoreCase)
        {
            [Default.Name] = Default,
            [Latin1General100CiAs.Name] = Latin1General100CiAs,
            [Latin1GeneralCiAs.Name] = Latin1GeneralCiAs,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if <paramref name="name"/> is on the
    /// <see cref="Recognized"/> whitelist. Case-insensitive (SQL Server
    /// collation names are themselves case-insensitive identifiers).
    /// </summary>
    internal static bool IsRecognized(string name) => Recognized.ContainsKey(name);

    public abstract int Compare(string? x, string? y);

    public abstract bool Equals(string? x, string? y);

    public abstract int GetHashCode(string obj);

    /// <summary>
    /// Shared host for the Windows-flavored CI_AS pair
    /// (<see cref="Latin1_General_100_CI_AS"/> /
    /// <see cref="Latin1_General_CI_AS"/>). Both pin to the invariant
    /// culture's <see cref="CompareInfo"/> with
    /// <see cref="CompareOptions.IgnoreCase"/> +
    /// <see cref="CompareOptions.IgnoreKanaType"/> +
    /// <see cref="CompareOptions.IgnoreWidth"/> — the same option set the
    /// collation name advertises. <see cref="Compare"/> additionally
    /// ignores <see cref="CompareOptions.IgnoreSymbols"/> so primary-
    /// weight-zero punctuation (apostrophe, hyphen, …) sorts as if it
    /// weren't there; <see cref="Equals"/> / <see cref="GetHashCode"/>
    /// keep symbols significant, mirroring real SQL Server's
    /// sort-only ignore-symbols treatment.
    /// </summary>
    internal abstract class WindowsCiAs : Collation
    {
        private static readonly CompareInfo Compare_Info = CultureInfo.InvariantCulture.CompareInfo;

        private const CompareOptions EqualityOptions =
            CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        private const CompareOptions SortOptions = EqualityOptions | CompareOptions.IgnoreSymbols;

        public override int Compare(string? x, string? y) =>
            x is null
                ? (y is null ? 0 : -1)
                : y is null ? 1 : Compare_Info.Compare(x, y, SortOptions);

        public override bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null && Compare_Info.Compare(x, y, EqualityOptions) == 0;

        public override int GetHashCode(string obj) =>
            Compare_Info.GetHashCode(obj, EqualityOptions);
    }

    /// <summary>
    /// SQL Server's "SQL_Latin1_General_CP1_CI_AS" — Sort Order 52 on
    /// CP1252 for non-Unicode and Latin1-General CI_AS rules for Unicode.
    /// Case-insensitive, accent-sensitive, and (unlike the Windows-style
    /// CI_AS family) does NOT treat apostrophe / hyphen as primary-
    /// weight-zero in sort. Modeled with the invariant culture's
    /// <see cref="CompareInfo"/> + <see cref="CompareOptions.IgnoreCase"/>
    /// for both <see cref="Compare"/> and <see cref="Equals"/>; this
    /// adds the linguistic-comparison fidelity (Unicode-normalization
    /// equivalence between NFD and NFC forms, halfwidth/fullwidth
    /// equivalence) that <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// lacks — both fold precomposed Latin-1 accented letters
    /// (<c>é = É</c>) identically.
    /// </summary>
    internal sealed class SQL_Latin1_General_CP1_CI_AS : Collation
    {
        private static readonly CompareInfo Compare_Info = CultureInfo.InvariantCulture.CompareInfo;

        private const CompareOptions Options =
            CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        public override string Name => "SQL_Latin1_General_CP1_CI_AS";

        public override int Compare(string? x, string? y) =>
            x is null
                ? (y is null ? 0 : -1)
                : y is null ? 1 : Compare_Info.Compare(x, y, Options);

        public override bool Equals(string? x, string? y) =>
            x is null
                ? y is null
                : y is not null && Compare_Info.Compare(x, y, Options) == 0;

        public override int GetHashCode(string obj) =>
            Compare_Info.GetHashCode(obj, Options);
    }

    /// <summary>
    /// "Latin1_General_100_CI_AS" — Windows-style v100 sort rules.
    /// Behavior body lives on <see cref="WindowsCiAs"/>; this class
    /// supplies the metadata <see cref="Name"/> only.
    /// </summary>
    internal sealed class Latin1_General_100_CI_AS : WindowsCiAs
    {
        public override string Name => "Latin1_General_100_CI_AS";
    }

    /// <summary>
    /// "Latin1_General_CI_AS" — Windows-style pre-v100 sort rules.
    /// Behavior body lives on <see cref="WindowsCiAs"/>; this class
    /// supplies the metadata <see cref="Name"/> only.
    /// </summary>
    internal sealed class Latin1_General_CI_AS : WindowsCiAs
    {
        public override string Name => "Latin1_General_CI_AS";
    }
}
