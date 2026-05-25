using System.Collections.Concurrent;
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
    /// True if <paramref name="name"/> is a recognized SQL Server collation
    /// name — grammatically parseable with a known prefix. Replaces the legacy
    /// whitelist check.
    /// </summary>
    internal static bool IsRecognized(string name) => TryGet(name) is not null;

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
        var storageEncoding = isUtf8 ? Encoding.UTF8 : CharSqlType.Cp1252Encoder;

        if (flags.HasFlag(CollationFlags.Binary) || flags.HasFlag(CollationFlags.Binary2))
        {
            var preBin2 = flags.HasFlag(CollationFlags.Binary);
            Collation varcharBody = isUtf8
                ? new Utf8CodepointBinaryCollation(name, description)
                : new Cp1252BinaryCollation(name, description);
            return new BinaryCollationBody(name, description, preBin2, storageEncoding, varcharBody);
        }

        var caseSensitive = flags.HasFlag(CollationFlags.CaseSensitive);
        var kanaSensitive = flags.HasFlag(CollationFlags.KanaSensitive);
        var widthSensitive = flags.HasFlag(CollationFlags.WidthSensitive);
        // SC behavior is engaged when the _SC_ flag is set explicitly or
        // when the version is v140+ (where SC is implicit / default).
        var scAware = flags.HasFlag(CollationFlags.SupplementaryCharacters) || version is >= 140;
        var cultureBody = new CultureCollation(name, description, prefixInfo.CultureName, caseSensitive, kanaSensitive, widthSensitive, storageEncoding, scAware);

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
