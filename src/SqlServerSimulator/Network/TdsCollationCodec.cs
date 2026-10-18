using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

/// <summary>
/// The COLMETADATA 5-byte collation structure and the code page a client
/// will use to decode <c>varchar</c> values described by it. Flags, version,
/// and sort id derive from the collation name; LCID and code page come from
/// <see cref="Collation.LcidAndCodePageByPrefix"/> (probed per prefix). Instances cache by
/// collation reference, which is stable because collation lookups intern.
/// </summary>
internal sealed class TdsCollationCodec
{
    private const uint IgnoreCase = 1u << 20;
    private const uint IgnoreAccent = 1u << 21;
    private const uint IgnoreWidth = 1u << 22;
    private const uint IgnoreKana = 1u << 23;
    private const uint Binary = 1u << 24;
    private const uint Binary2 = 1u << 25;
    private const uint Utf8 = 1u << 26;

    /// <summary>LCID, flags, and version packed as the wire's first four bytes.</summary>
    public readonly uint Info;

    /// <summary>Nonzero only for the SQL_* family.</summary>
    public readonly byte SortId;

    /// <summary>The encoding matching the code page a client derives from the structure.</summary>
    public readonly Encoding WireEncoding;

    private static readonly ConcurrentDictionary<Collation, TdsCollationCodec> Cache = new(ReferenceEqualityComparer.Instance);

    public static TdsCollationCodec For(Collation? collation) =>
        Cache.GetOrAdd(collation ?? Collation.Baseline, static c => new TdsCollationCodec(c));

    public void Write(TdsTokenWriter writer)
    {
        writer.WriteUInt32(this.Info);
        writer.WriteByte(this.SortId);
    }

    /// <summary>
    /// <c>COLLATIONPROPERTY(name, 'TDSCollation')</c>, which is not quite the
    /// wire structure but for a UTF-8 collation: it swaps the ignore-width and
    /// ignore-kana bits and gives the two EBCDIC sort variants their base
    /// collation's sort id (probed 2026-09-26 against SQL Server 2025, all
    /// 5540 collations).
    /// </summary>
    public byte[] PropertyBytes()
    {
        var info = this.Info;
        if ((info & Utf8) == 0)
        {
            info &= ~(IgnoreWidth | IgnoreKana);
            if ((this.Info & IgnoreWidth) != 0)
                info |= IgnoreKana;
            if ((this.Info & IgnoreKana) != 0)
                info |= IgnoreWidth;
        }
        var sortId = this.SortId switch { 218 => (byte)212, 219 => (byte)211, var own => own };
        return [(byte)info, (byte)(info >> 8), (byte)(info >> 16), (byte)(info >> 24), sortId];
    }

    /// <summary>
    /// The collation whose <see cref="PropertyBytes"/> are
    /// <paramref name="bytes"/> — of several sharing them, the one of smallest
    /// collation id — or null for bytes no collation has.
    /// </summary>
    public static string? NameForPropertyBytes(byte[] bytes) =>
        bytes.Length == 5 && ByPropertyBytes.Value.TryGetValue(PropertyKey(bytes), out var name) ? name : null;

    private static readonly Lazy<FrozenDictionary<ulong, string>> ByPropertyBytes = new(static () =>
    {
        var byKey = new Dictionary<ulong, (string Name, int Id)>();
        foreach (var (name, _) in Collation.EnumerateRecognized())
        {
            if (!Collation.TryGetMetrics(name, out var metrics))
                continue;
            var key = PropertyKey(For(Collation.Get(name)).PropertyBytes());
            if (!byKey.TryGetValue(key, out var held) || metrics.CollationId < held.Id)
                byKey[key] = (name, metrics.CollationId);
        }
        return byKey.ToDictionary(static entry => entry.Key, static entry => entry.Value.Name).ToFrozenDictionary();
    });

    private static ulong PropertyKey(byte[] bytes)
    {
        ulong key = 0;
        foreach (var b in bytes)
            key = (key << 8) | b;
        return key;
    }

    private TdsCollationCodec(Collation collation)
    {
        var name = collation.Name;
        var tokens = name.Split('_');

        uint flags = 0;
        var version = 0u;
        var utf8 = false;
        var binary = false;
        bool kanaSensitive = false, widthSensitive = false;
        Span<char> upper = stackalloc char[4];
        foreach (var token in tokens)
        {
            if (token.Length > upper.Length)
                continue;

            var upperLength = token.AsSpan().ToUpperInvariant(upper);
            switch (upper[..upperLength])
            {
                case "100":
                    version = 2;
                    break;
                case "140":
                    version = 3;
                    break;
                case "160":
                    version = 4;
                    break;
                case "90":
                    version = 1;
                    break;
                case "AI":
                    flags |= IgnoreAccent;
                    break;
                case "BIN":
                    flags |= Binary;
                    binary = true;
                    break;
                case "BIN2":
                    flags |= Binary2;
                    binary = true;
                    break;
                case "CI":
                    flags |= IgnoreCase;
                    break;
                case "KS":
                    kanaSensitive = true;
                    break;
                case "UTF8":
                    utf8 = true;
                    break;
                case "WS":
                    widthSensitive = true;
                    break;
            }
        }

        if (binary)
        {
            flags &= Binary | Binary2;
        }
        else
        {
            if (!kanaSensitive)
                flags |= IgnoreKana;
            if (!widthSensitive)
                flags |= IgnoreWidth;
        }

        var (lcid, codePage) = ResolvePrefix(name);
        if (name.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase))
        {
            version = 0;
            foreach (var token in tokens)
            {
                if (token.Length > 2 && token.StartsWith("CP", StringComparison.OrdinalIgnoreCase))
                {
                    var digits = token.AsSpan(2);
                    codePage = digits is "1" ? 1252 : int.Parse(digits, CultureInfo.InvariantCulture);
                    break;
                }
            }

            // Probe anomaly: the two SQL_Latin1_General_CP1254_* collations
            // report the Turkish LCID while every sibling reports 0x0409.
            if (codePage == 1254)
                lcid = 0x041F;

            if (Collation.SqlServerSortOrders.TryGetValue(name, out var sortOrder))
                this.SortId = checked((byte)sortOrder.OrderNumber);
        }

        // A UTF-8 collation's wire form is its COLLATIONPROPERTY form: the
        // fUTF8 bit beside a _BIN2's binary-sort bit, and the ignore-width and
        // ignore-kana bits the other way round from every other collation's
        // (captured 2026-10-18 against SQL Server 2025 for each of 1400 UTF-8
        // collations, the client having negotiated UTF-8 support).
        if (utf8)
        {
            var swapped = flags & ~(IgnoreWidth | IgnoreKana);
            if ((flags & IgnoreWidth) != 0)
                swapped |= IgnoreKana;
            if ((flags & IgnoreKana) != 0)
                swapped |= IgnoreWidth;
            flags = swapped | Utf8;
            codePage = 65001;
        }

        this.Info = (uint)(lcid & 0xFFFFF) | flags | (version << 28);
        this.WireEncoding = codePage switch
        {
            65001 => Encoding.UTF8,
            0 or 1252 => CharSqlType.Cp1252Encoder,
            _ => Encoding.GetEncoding(codePage),
        };
    }

    /// <summary>
    /// Longest-prefix match of the collation name against the probed
    /// registry, stripping one underscore-delimited token at a time.
    /// </summary>
    private static (int Lcid, int CodePage) ResolvePrefix(string name)
    {
        var candidate = name;
        while (true)
        {
            if (Collation.LcidAndCodePageByPrefix.TryGetValue(candidate, out var entry))
                return entry;

            var cut = candidate.LastIndexOf('_');
            if (cut < 0)
                return (0x0409, 1252);

            candidate = candidate[..cut];
        }
    }
}
