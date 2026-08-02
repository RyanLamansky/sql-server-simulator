using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Grammatical decoder for SQL Server collation names. The
/// <c>sys.fn_helpcollations()</c> catalog ships ~5540 entries built from
/// 124 prefixes × ~30 suffix-flag combinations × 5 versions; the parser
/// reads each name as <c>{prefix}_{[version]}_{flags}[_{CP*}]</c> and
/// constructs the corresponding <see cref="Collation"/> instance on
/// demand, interning results so the same name always resolves to the same
/// reference.
/// </summary>
internal abstract partial class Collation
{
    /// <summary>
    /// Decoded suffix-flag bitmask. Each <c>_BIN</c> / <c>_BIN2</c> / <c>_CI</c>
    /// / <c>_CS</c> / <c>_AI</c> / <c>_AS</c> / <c>_KS</c> / <c>_WS</c> / <c>_SC</c>
    /// / <c>_UTF8</c> / <c>_VSS</c> token classified by
    /// <see cref="TryClassifySuffix"/> flips the corresponding bit.
    /// </summary>
    [Flags]
    private enum CollationFlags : ushort
    {
        None = 0,
        CaseInsensitive = 1 << 0,
        CaseSensitive = 1 << 1,
        AccentInsensitive = 1 << 2,
        AccentSensitive = 1 << 3,
        KanaSensitive = 1 << 4,
        WidthSensitive = 1 << 5,
        SupplementaryCharacters = 1 << 6,
        Utf8 = 1 << 7,
        Binary = 1 << 8,
        Binary2 = 1 << 9,
        VariationSelectorSensitive = 1 << 10,
    }

    /// <summary>
    /// Interning cache for parser-derived collation instances. Keyed by the
    /// canonical (uppercase) name; <see cref="TryGet"/> inserts on first
    /// successful parse. Concurrent dictionary because <see cref="TryGet"/>
    /// is callable from any thread.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Collation> interned =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the recognized <see cref="Collation"/> for <paramref name="name"/>,
    /// or <see langword="null"/> if the name isn't grammatically valid +
    /// doesn't carry a known prefix. Subsequent calls with the same name
    /// return the same reference (interned). The default collation name is
    /// special-cased inside <see cref="CreateInstance"/> to wrap its comparer
    /// in the byte-exact <see cref="SqlLatin1Cp1CiAsCollation"/>.
    /// </summary>
    internal static Collation? TryGet(string name) =>
        string.IsNullOrEmpty(name)
            ? null
            : interned.TryGetValue(name, out var hit)
                ? hit
                : TryParse(name, out var parsed) ? interned.GetOrAdd(parsed.Name, parsed) : null;

    /// <summary>
    /// Resolves a collation name guaranteed valid at the call site — a
    /// compile-time constant naming a collation real SQL Server ships. The
    /// <c>Debug.Assert</c> is a refactoring tripwire: a regression in the
    /// parser tables fails loud and early under the (Debug-built) test
    /// suite, where collation resolution is among the hottest paths. Use
    /// <see cref="TryGet"/> for any name that originates from SQL text.
    /// </summary>
    internal static Collation Get(string name)
    {
        var collation = TryGet(name);
        Debug.Assert(collation is not null, $"Collation '{name}' is expected to always resolve but did not.");
        return collation;
    }

    /// <summary>
    /// True if <paramref name="name"/> is a recognized SQL Server collation
    /// name — grammatically parseable with a known prefix. Replaces the legacy
    /// whitelist check.
    /// </summary>
    internal static bool IsRecognized(string name) => TryGet(name) is not null;

    /// <summary>
    /// The <c>COLLATIONPROPERTY(name, property)</c> value set for a recognized
    /// collation: ANSI code page, locale id, comparison-style bitmask, and the
    /// version ordinal. Probe-confirmed against SQL Server 2025 — e.g.
    /// <c>SQL_Latin1_General_CP1_CI_AS</c> → (1252, 1033, 196609, 0),
    /// <c>Latin1_General_100_CI_AS</c> → (1252, 1033, 196609, 2).
    /// </summary>
    internal readonly struct CollationMetrics(int codePage, int lcid, int comparisonStyle, int version, string name)
    {
        public readonly int CodePage = codePage;
        public readonly int Lcid = lcid;
        public readonly int ComparisonStyle = comparisonStyle;
        public readonly int Version = version;
        public readonly string Name = name;
    }

    /// <summary>
    /// Computes the <c>COLLATIONPROPERTY</c> metrics for <paramref name="name"/>,
    /// or returns <see langword="false"/> when the name isn't a recognized
    /// collation (real SQL Server's <c>COLLATIONPROPERTY</c> returns NULL for
    /// an unrecognized name). The comparison style is derived from the suffix
    /// flags (binary collations report 0, otherwise the ignore-case /
    /// ignore-accent / ignore-kana / ignore-width bitmask), the version ordinal
    /// from the numeric name token (unversioned/SQL → 0, 90 → 1, 100 → 2,
    /// 140 → 3, 160 → 4), and the LCID plus ANSI code page from the probe-built
    /// collation registry keyed by prefix. SQL_* names carry their code page in
    /// the <c>CPnnn</c> name token, and a <c>_UTF8</c> name overrides it to
    /// 65001; the LCID defaults to <c>0x0409</c> and the code page to 1252 when
    /// a recognized prefix isn't tabulated.
    /// </summary>
    internal static bool TryGetMetrics(string name, out CollationMetrics metrics)
    {
        metrics = default;
        if (TryGet(name) is not { } collation)
            return false;

        // The name is recognized, so the suffix walk that TryParse ran is
        // guaranteed to succeed again; re-walk it here to recover the prefix,
        // flags, version, and code-page token the metrics derive from.
        var parts = name.Split('_');
        var splitAt = parts.Length;
        var flags = CollationFlags.None;
        int? version = null;
        int? codePage = null;
        while (splitAt > 0 && TryClassifySuffix(parts[splitAt - 1], ref flags, ref version, ref codePage))
            splitAt--;
        var prefix = string.Join("_", parts.Take(splitAt));

        var isBinary = flags.HasFlag(CollationFlags.Binary) || flags.HasFlag(CollationFlags.Binary2);
        var comparisonStyle = isBinary
            ? 0
            : (flags.HasFlag(CollationFlags.CaseInsensitive) ? 0x1 : 0)
                + (flags.HasFlag(CollationFlags.AccentInsensitive) ? 0x2 : 0)
                + (flags.HasFlag(CollationFlags.KanaSensitive) ? 0 : 0x10000)
                + (flags.HasFlag(CollationFlags.WidthSensitive) ? 0 : 0x20000);
        var versionOrdinal = version switch { 90 => 1, 100 => 2, 140 => 3, 160 => 4, _ => 0 };

        var lcid = LcidAndCodePageByPrefix.TryGetValue(prefix, out var registered) ? registered.Lcid : 0x0409;

        metrics = new CollationMetrics(ResolveAnsiCodePage(prefix, codePage, flags), lcid, comparisonStyle, versionOrdinal, collation.Name);
        return true;
    }

    /// <summary>
    /// The ANSI code page a collation stores <c>varchar</c> / <c>char</c> data
    /// in, and reports through <c>COLLATIONPROPERTY(name, 'CodePage')</c>.
    /// A <c>_UTF8</c> name overrides everything to 65001; otherwise a
    /// <c>CPnnn</c> name token wins (the SQL_* family carries its code page
    /// there — <c>CP1</c> = 1252, <c>CP850</c> = 850), falling back to the
    /// prefix registry.
    /// <para>Returns <c>0</c> for the twelve Windows prefixes real SQL Server
    /// supports on Unicode data types only (probe-confirmed: their
    /// <c>COLLATIONPROPERTY</c> code page is 0, and pinning one on a char
    /// family type raises Msg 459). An unregistered prefix defaults to 1252
    /// rather than 0, so an unrecognized name stays storable.</para>
    /// </summary>
    private static int ResolveAnsiCodePage(string prefix, int? codePage, CollationFlags flags) =>
        flags.HasFlag(CollationFlags.Utf8) ? 65001
        : codePage ?? (LcidAndCodePageByPrefix.TryGetValue(prefix, out var registered) ? registered.CodePage : 1252);

    /// <summary>
    /// Enumerates every recognized collation name with its description
    /// (the form <c>sys.fn_helpcollations()</c> exposes). Crosses the
    /// SQL_* sort-order table and the per-prefix tail-set patterns. Ordering
    /// is the caller's responsibility.
    /// </summary>
    internal static IEnumerable<(string Name, string Description)> EnumerateRecognized()
    {
        // SQL_* slice — 78 baked names.
        foreach (var name in SqlServerSortOrders.Keys)
        {
            if (TryGet(name) is { } c)
                yield return (name, c.Description);
        }

        // Non-SQL_* slice — exactly the (prefix, tail) pairs that real SQL
        // Server ships (5463 names probed against SQL Server 2025).
        foreach (var (prefix, patternIdx) in PrefixToPattern)
        {
            foreach (var tail in GetPatternTails(patternIdx))
            {
                var name = prefix + tail;
                if (TryGet(name) is { } c)
                    yield return (name, c.Description);
            }
        }
    }

    /// <summary>
    /// Parses <paramref name="name"/> into (prefix, version, codePage, flags)
    /// and constructs the appropriate concrete <see cref="Collation"/>. The
    /// suffix walk starts at the rightmost token and consumes recognized
    /// tokens until it hits a token that isn't a known suffix marker — the
    /// remaining left segments form the prefix.
    /// </summary>
    private static bool TryParse(string name, [NotNullWhen(true)] out Collation? collation)
    {
        collation = null;
        if (name.Length == 0 || !validNameChars.IsMatch(name)) return false;

        var parts = name.Split('_');
        if (parts.Length < 2) return false;

        var splitAt = parts.Length;
        var flags = CollationFlags.None;
        int? version = null;
        int? codePage = null;

        while (splitAt > 0)
        {
            var token = parts[splitAt - 1];
            if (!TryClassifySuffix(token, ref flags, ref version, ref codePage))
                break;
            splitAt--;
        }

        // Must consume at least one suffix token; otherwise the input isn't
        // a collation name (it's just a prefix).
        if (splitAt == parts.Length) return false;

        var prefix = string.Join("_", parts.Take(splitAt));
        if (!KnownPrefixes.TryGetValue(prefix, out var prefixInfo)) return false;

        // Recognition gate: SQL_* names must be in the per-name sort-order
        // table; non-SQL_* names must be in the prefix's tail-set pattern.
        // Without this check the parser is grammatically permissive — it
        // would accept any well-formed name, including combinations that
        // real SQL Server never ships (e.g., Latin1_General_140_BIN). Names
        // missing from the catalog are phantom; reject them so the
        // simulator's recognized set matches sys.fn_helpcollations()
        // (probe-confirmed 2026-05-21: 78 SQL_* + 5463 non-SQL_* = 5541).
        var isSqlPrefix = prefix.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase);
        if (isSqlPrefix)
        {
            if (!SqlServerSortOrders.ContainsKey(name)) return false;
        }
        else
        {
            if (!PrefixToPattern.TryGetValue(prefix, out var patternIdx)) return false;
            var tail = name[prefix.Length..];
            if (!GetPatternTails(patternIdx).Contains(tail)) return false;
        }

        if (!IsCoherent(flags)) return false;

        collation = CreateInstance(name, prefix, prefixInfo, version, codePage, flags);
        return true;
    }

    private static readonly Regex validNameChars =
        new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Maps one suffix token to its bit / numeric slot. Returns
    /// <see langword="false"/> when the token isn't a recognized suffix —
    /// the caller takes that as the signal to stop the suffix-walk and
    /// treat the remaining left segments as the prefix.
    /// </summary>
    private static bool TryClassifySuffix(string token, ref CollationFlags flags, ref int? version, ref int? codePage)
    {
        Span<char> upper = stackalloc char[token.Length];
        var len = token.AsSpan().ToUpperInvariant(upper);
        switch (len)
        {
            case 2:
                switch (upper)
                {
                    case "AI": flags |= CollationFlags.AccentInsensitive; return true;
                    case "AS": flags |= CollationFlags.AccentSensitive; return true;
                    case "CI": flags |= CollationFlags.CaseInsensitive; return true;
                    case "CS": flags |= CollationFlags.CaseSensitive; return true;
                    case "KS": flags |= CollationFlags.KanaSensitive; return true;
                    case "SC": flags |= CollationFlags.SupplementaryCharacters; return true;
                    case "WS": flags |= CollationFlags.WidthSensitive; return true;
                }
                break;
            case 3:
                switch (upper)
                {
                    case "BIN": flags |= CollationFlags.Binary; return true;
                    case "CP1":
                        codePage = 1252;
                        return true;
                    case "VSS": flags |= CollationFlags.VariationSelectorSensitive; return true;
                }
                break;
            case 4:
                switch (upper)
                {
                    case "BIN2": flags |= CollationFlags.Binary2; return true;
                    case "UTF8": flags |= CollationFlags.Utf8; return true;
                }
                break;
        }

        // Numeric version tag (90, 100, 140, 160).
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            version = v;
            return true;
        }

        // CP<digits> code-page tag — accepted at length >= 3 to cover CP1.
        if (token.Length >= 3
            && upper[0] == 'C'
            && upper[1] == 'P'
            && int.TryParse(token.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var cp))
        {
            codePage = cp;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sanity checks on the flag combination. A collation name must carry
    /// exactly one of {BIN, BIN2, CI, CS}; CI/CS must pair with AI/AS;
    /// BIN/BIN2 reject other compare modifiers (CI/CS/AI/AS/KS/WS/SC/VSS).
    /// </summary>
    private static bool IsCoherent(CollationFlags flags)
    {
        var isBin = flags.HasFlag(CollationFlags.Binary);
        var isBin2 = flags.HasFlag(CollationFlags.Binary2);
        var isCi = flags.HasFlag(CollationFlags.CaseInsensitive);
        var isCs = flags.HasFlag(CollationFlags.CaseSensitive);

        if (isBin && isBin2) return false;
        if (isCi && isCs) return false;

        if (isBin || isBin2)
        {
            // Binary collations reject all compare modifiers.
            const CollationFlags Rejected =
                CollationFlags.CaseInsensitive | CollationFlags.CaseSensitive
                | CollationFlags.AccentInsensitive | CollationFlags.AccentSensitive
                | CollationFlags.KanaSensitive | CollationFlags.WidthSensitive
                | CollationFlags.SupplementaryCharacters | CollationFlags.VariationSelectorSensitive;
            return (flags & Rejected) == 0;
        }

        // Non-binary must have one of CI/CS and one of AI/AS, but not both.
        if (!isCi && !isCs) return false;
        var isAi = flags.HasFlag(CollationFlags.AccentInsensitive);
        var isAs = flags.HasFlag(CollationFlags.AccentSensitive);
        return (isAi || isAs) && !(isAi && isAs);
    }

    /// <summary>
    /// Constructs the concrete <see cref="Collation"/> for a fully-parsed
    /// name. Binary collations get a <see cref="BinaryCollationBody"/>
    /// instance with the matching varchar-storage substitution; non-binary
    /// names get a <see cref="CultureCollation"/> instance pinned to the
    /// prefix's culture with the flag-derived option set.
    /// </summary>
    private static Collation CreateInstance(string name, string prefix, PrefixInfo prefixInfo, int? version, int? codePage, CollationFlags flags)
    {
        var description = BuildDescription(name, prefix, prefixInfo, version, codePage, flags);
        var isUtf8 = flags.HasFlag(CollationFlags.Utf8);
        var ansiCodePage = ResolveAnsiCodePage(prefix, codePage, flags);
        // A Unicode-only collation has no ANSI encoding to pin; the char-family
        // type factories reject it with Msg 459 before storage is reached, so
        // the placeholder here is never consulted.
        var storageEncoding = ansiCodePage == 0 ? CharSqlType.Cp1252Encoder : AnsiEncoding(ansiCodePage);

        if (flags.HasFlag(CollationFlags.Binary) || flags.HasFlag(CollationFlags.Binary2))
        {
            var preBin2 = flags.HasFlag(CollationFlags.Binary);
            Collation varcharBody = isUtf8
                ? new Utf8CodepointBinaryCollation(name, description)
                : new AnsiBinaryCollation(name, description, storageEncoding, ansiCodePage);
            return new BinaryCollationBody(name, description, preBin2, storageEncoding, ansiCodePage, varcharBody);
        }

        var caseSensitive = flags.HasFlag(CollationFlags.CaseSensitive);
        var kanaSensitive = flags.HasFlag(CollationFlags.KanaSensitive);
        var widthSensitive = flags.HasFlag(CollationFlags.WidthSensitive);
        // SC behavior is engaged when the _SC_ flag is set explicitly or
        // when the version is v140+ (where SC is implicit / default).
        var scAware = flags.HasFlag(CollationFlags.SupplementaryCharacters) || version is >= 140;
        var cultureBody = new CultureCollation(name, description, prefixInfo.CultureName, caseSensitive, kanaSensitive, widthSensitive, storageEncoding, ansiCodePage, scAware);

        // The default collation gets a byte-exact sort body wrapping the
        // generic culture comparer (which still supplies metadata + the
        // non-CP1252 fallback). See Collation.SqlLatin1Sort.cs.
        return name.Equals(SqlLatin1Cp1CiAsCollation.CollationName, StringComparison.OrdinalIgnoreCase)
            ? new SqlLatin1Cp1CiAsCollation(cultureBody)
            : cultureBody;
    }

    /// <summary>
    /// Generates the human-readable description that
    /// <c>sys.fn_helpcollations()</c> exposes. Two grammars: SQL_* names
    /// suffix the comparison clauses with "for Unicode Data, SQL Server
    /// Sort Order N on Code Page M for non-Unicode Data"; everything else
    /// is just the comparison clauses with optional ", supplementary
    /// characters", ", variation selector …", ", UTF8" tails.
    /// </summary>
    private static string BuildDescription(string name, string prefix, PrefixInfo prefixInfo, int? version, int? codePage, CollationFlags flags)
    {
        var isSql = prefix.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase);
        var humanPrefix = prefixInfo.HumanPrefix;
        if (isSql && SqlServerSortOrders.TryGetValue(name, out var sortOrder))
            humanPrefix = sortOrder.HumanPrefix;

        var sb = new StringBuilder().Append(humanPrefix);
        if (version is { } v)
            _ = sb.Append('-').Append(v);

        if (flags.HasFlag(CollationFlags.Binary))
        {
            _ = sb.Append(", binary sort");
        }
        else if (flags.HasFlag(CollationFlags.Binary2))
        {
            _ = sb.Append(", binary code point comparison sort");
        }
        else
        {
            _ = sb
                .Append(flags.HasFlag(CollationFlags.CaseSensitive) ? ", case-sensitive" : ", case-insensitive")
                .Append(flags.HasFlag(CollationFlags.AccentSensitive) ? ", accent-sensitive" : ", accent-insensitive")
                .Append(flags.HasFlag(CollationFlags.KanaSensitive) ? ", kanatype-sensitive" : ", kanatype-insensitive")
                .Append(flags.HasFlag(CollationFlags.WidthSensitive) ? ", width-sensitive" : ", width-insensitive");

            // v140+ implicitly carries SC + a variation-selector clause; explicit _SC_
            // is needed for v90/v100.
            var scImplicit = version is >= 140;
            if (flags.HasFlag(CollationFlags.SupplementaryCharacters) || scImplicit)
                _ = sb.Append(", supplementary characters");
            if (scImplicit)
            {
                _ = sb.Append(flags.HasFlag(CollationFlags.VariationSelectorSensitive)
                    ? ", variation selector sensitive"
                    : ", variation selector insensitive");
            }
        }

        if (isSql)
        {
            var sortOrderNumber = SqlServerSortOrders.TryGetValue(name, out var so) ? so.OrderNumber : 0;
            _ = sb
                .Append(" for Unicode Data, SQL Server Sort Order ")
                .Append(sortOrderNumber)
                .Append(" on Code Page ")
                .Append(codePage ?? 0)
                .Append(" for non-Unicode Data");
        }
        else if (flags.HasFlag(CollationFlags.Utf8))
        {
            _ = sb.Append(", UTF8");
        }

        return sb.ToString();
    }
}
