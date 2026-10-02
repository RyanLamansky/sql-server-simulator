using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards the <c>IEqualityComparer&lt;string&gt;</c> hash contract on
/// <see cref="Collation.Latin1GeneralTableCollation"/>: whenever
/// <c>Equals(x, y)</c> is true — including across the repertoire boundary,
/// where equality routes through the inner <see cref="CompareInfo"/> —
/// <c>GetHashCode</c> must agree. The hybrid hashes in-repertoire strings
/// by SQL sort-weight runs and everything else through canonicalization +
/// the inner hash, so the boundary is where inconsistency would hide.
/// Derivation notes live in <c>docs/claude/collations.md</c>.
/// </summary>
[TestClass]
public sealed class CollationHashConsistencyTests
{
    private static readonly Collation Nvarchar = Collation.Get(Collation.Latin1GeneralTableCollation.DefaultName);

    private static readonly Collation Varchar = Nvarchar.ForVarcharStorage();

    private static void CollectHashMismatch(Collation collation, string x, string y, List<string> mismatches)
    {
        if (collation.Equals(x, y) && collation.GetHashCode(x) != collation.GetHashCode(y))
            mismatches.Add($"{Escape(x)} vs {Escape(y)} ({(ReferenceEquals(collation, Varchar) ? "varchar" : collation.Name)})");
    }

    private static string Escape(string s) =>
        string.Concat(s.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}"));

    private static void AssertEqualAndHashEqual(Collation collation, string x, string y)
    {
        IsTrue(collation.Equals(x, y), $"expected Equals: '{x}' vs '{y}'");
        AreEqual(collation.GetHashCode(x), collation.GetHashCode(y), $"hash differs: '{x}' vs '{y}'");
    }

    [TestMethod]
    public void FullwidthSpelling_HashesLikeAsciiTarget()
    {
        foreach (var collation in (Collation[])[Nvarchar, Varchar])
        {
            AssertEqualAndHashEqual(collation, "sp_executesql", "ｓp_executesql");
            AssertEqualAndHashEqual(collation, "table1", "ｔable１");
            AssertEqualAndHashEqual(collation, "ABC", "ａｂｃ");
        }
    }

    [TestMethod]
    public void DecomposedAccent_HashesLikeComposed()
    {
        foreach (var collation in (Collation[])[Nvarchar, Varchar])
            AssertEqualAndHashEqual(collation, "café", "cafe\u0301");
    }

    // U+1E9E postdates the default collation's weight table, so it weighs
    // nothing there (probed 2026-09-29 against SQL Server 2025).
    [TestMethod]
    public void CapitalSharpS_HashesLikeAbsent()
    {
        foreach (var collation in (Collation[])[Nvarchar, Varchar])
            AssertEqualAndHashEqual(collation, "strae", "stra\u1E9Ee");
    }

    [TestMethod]
    public void WeightlessCharacters_HashLikeAbsent()
    {
        foreach (var collation in (Collation[])[Nvarchar, Varchar])
        {
            AssertEqualAndHashEqual(collation, "ab", "a\uFEFFb");
            AssertEqualAndHashEqual(collation, "ab", "a\u0378b");
            AssertEqualAndHashEqual(collation, "ab", "a\uD83D\uDE00b");
            AssertEqualAndHashEqual(collation, "aｂ", "a\u0378ｂ");
            // ICU ignores these; real weighs them.
            IsFalse(collation.Equals("ab", "a\u200Bb"));
            IsFalse(collation.Equals("aｂ", "a\u0001ｂ"));
            AreEqual(collation.GetHashCode("a\u0001b"), collation.GetHashCode("ab"));
        }
    }

    /// <summary>
    /// An out-of-repertoire spelling can be Equals-equal to two
    /// in-repertoire strings that are unequal to each other (fullwidth
    /// <c>２</c> equals both <c>2</c> and superscript <c>²</c> through the
    /// inner collation), so all three must share a hash — the
    /// in-repertoire pair's shared hash is a legal collision of unequal
    /// strings.
    /// </summary>
    [TestMethod]
    public void CrossBoundaryTriangles_ShareOneHash()
    {
        AssertEqualAndHashEqual(Nvarchar, "x2y", "x２y");
        AssertEqualAndHashEqual(Nvarchar, "x²y", "x２y");
        AreEqual(Nvarchar.GetHashCode("x2y"), Nvarchar.GetHashCode("x²y"));

        AssertEqualAndHashEqual(Nvarchar, "a b", "a\u3000b");
        AssertEqualAndHashEqual(Nvarchar, "a\u00A0b", "a\u3000b");
        AreEqual(Nvarchar.GetHashCode("a b"), Nvarchar.GetHashCode("a\u00A0b"));

        // Thai SARA AM: ICU equates the composed U+0E33 with NIKHAHIT +
        // SARA AA while the weight tables keep the spellings unequal.
        AssertEqualAndHashEqual(Nvarchar, "กําx", "กำｘ");
        AssertEqualAndHashEqual(Nvarchar, "กำx", "กำｘ");
        AreEqual(Nvarchar.GetHashCode("กำx"), Nvarchar.GetHashCode("กําx"));
    }

    /// <summary>
    /// Completeness sweep: groups every repertoire character by its inner
    /// (invariant, CI + width/kana-insensitive) sort key and asserts each
    /// group's members hash identically in context — the behavioral form
    /// of "the in-repertoire hash-fold table covers every ICU-equal
    /// class" (case mates, superscripts, ordinal indicators, NBSP, Thai
    /// digits, ignorable controls).
    /// </summary>
    [TestMethod]
    public void InRepertoireIcuEqualClasses_ShareHashes()
    {
        const CompareOptions Options =
            CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
        var compareInfo = CultureInfo.InvariantCulture.CompareInfo;

        var groups = new Dictionary<string, List<char>>(StringComparer.Ordinal);
        foreach (var ch in Repertoire())
        {
            var key = Convert.ToBase64String(compareInfo.GetSortKey(ch.ToString(), Options).KeyData);
            if (!groups.TryGetValue(key, out var members))
                groups[key] = members = [];
            members.Add(ch);
        }

        var mismatches = new List<string>();
        foreach (var members in groups.Values)
        {
            for (var i = 1; i < members.Count; i++)
            {
                foreach (var collation in (Collation[])[Nvarchar, Varchar])
                {
                    if (collation.GetHashCode($"x{members[0]}z") != collation.GetHashCode($"x{members[i]}z"))
                        mismatches.Add($"U+{(int)members[0]:X4} vs U+{(int)members[i]:X4} ({(ReferenceEquals(collation, Varchar) ? "varchar" : "nvarchar")})");
                }
            }
        }

        IsEmpty(mismatches, string.Join("; ", mismatches));
    }

    /// <summary>
    /// Seeded fuzz over ICU-equivalence substitution pools: every generated
    /// pair that Equals reports equal must hash equal, under both storage
    /// bodies. Pools mix case flips, fullwidth forms, ignorables,
    /// decomposition, ligature case-mates, digit homoglyphs, and Thai.
    /// </summary>
    [TestMethod]
    public void SubstitutionFuzz_EqualsImpliesHashEqual()
    {
        var mismatches = new List<string>();
        Fuzz(Nvarchar, mismatches);
        Fuzz(Varchar, mismatches);
        IsEmpty(mismatches, string.Join("; ", mismatches));
    }

    /// <summary>
    /// The same contract over the other names the Latin1-General tables serve,
    /// each strength and both versions.
    /// </summary>
    [TestMethod]
    [DataRow("Latin1_General_CI_AS")]
    [DataRow("Latin1_General_CS_AS")]
    [DataRow("Latin1_General_CI_AI")]
    [DataRow("Latin1_General_CS_AI")]
    [DataRow("SQL_Latin1_General_CP1_CS_AS")]
    [DataRow("Latin1_General_100_CI_AS")]
    [DataRow("Latin1_General_100_CS_AS")]
    [DataRow("Latin1_General_100_CI_AI")]
    [DataRow("Latin1_General_100_CI_AS_SC_UTF8")]
    public void OtherTableBodies_EqualsImpliesHashEqual(string name)
    {
        var collation = Collation.Get(name);
        var mismatches = new List<string>();
        Fuzz(collation, mismatches);
        Sweep(collation, mismatches);
        IsEmpty(mismatches, string.Join("; ", mismatches));
    }

    private static void Fuzz(Collation collation, List<string> mismatches)
    {
        string[][] pools =
        [
            ["s", "S", "ｓ", "Ｓ"],
            ["2", "²", "๒", "２"],
            ["ß", "ẞ", "ss"],
            ["æ", "Æ", "ae"],
            ["é", "e\u0301", "É"],
            ["", "\u200B", "\u0001", "\u00AD"],
            [" ", "\u00A0", "\u3000"],
            ["ำ", "ํา"],
            ["-", "'", "_", "9", "ก", "z"],
            ["µ", "μ"],
            ["Ł", "ł", "ő", "Ő", "ǆ", "Ǆ", "ǅ", "dž", "ȸ", "ɓ", "Ɓ"],
            ["\u2010", "\u202F", "\u200D", "\u0E49"],
        ];

        var random = new Random(20260713);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var length = random.Next(0, 6);
            var builderX = new StringBuilder();
            var builderY = new StringBuilder();
            for (var i = 0; i < length; i++)
            {
                var pool = pools[random.Next(pools.Length)];
                _ = builderX.Append(pool[random.Next(pool.Length)]);
                _ = builderY.Append(pool[random.Next(pool.Length)]);
            }

            CollectHashMismatch(collation, builderX.ToString(), builderY.ToString(), mismatches);
        }
    }

    /// <summary>
    /// Systematic single-character sweep: for each BMP character in the
    /// blocks the fold machinery covers, derive normalization / case
    /// variants of a carrier string and assert the hash contract wherever
    /// Equals holds.
    /// </summary>
    [TestMethod]
    public void NormalizationVariantSweep_EqualsImpliesHashEqual()
    {
        var mismatches = new List<string>();
        Sweep(Nvarchar, mismatches);
        Sweep(Varchar, mismatches);
        IsEmpty(mismatches, string.Join("; ", mismatches));
    }

    private static void Sweep(Collation collation, List<string> mismatches)
    {
        (int Start, int End)[] blocks =
        [
            (0x0020, 0x02FF), (0x0E00, 0x0E7F), (0x1E00, 0x1EFF),
            (0x2000, 0x20AF), (0x2100, 0x214F), (0xFB00, 0xFB06), (0xFF00, 0xFFEF),
        ];
        foreach (var (start, end) in blocks)
        {
            for (var cp = start; cp <= end; cp++)
            {
                var carrier = $"x{(char)cp}z";
                foreach (var variant in Variants(carrier))
                {
                    if (variant != carrier)
                        CollectHashMismatch(collation, carrier, variant, mismatches);
                }
            }
        }
    }

    private static IEnumerable<string> Variants(string s)
    {
        yield return s.ToUpperInvariant();
        string[] normalized;
        try
        {
            normalized =
            [
                s.Normalize(NormalizationForm.FormC),
                s.Normalize(NormalizationForm.FormD),
                s.Normalize(NormalizationForm.FormKC),
                s.Normalize(NormalizationForm.FormKD),
            ];
        }
        catch (ArgumentException)
        {
            yield break;
        }

        foreach (var form in normalized)
            yield return form;
    }

    private static IEnumerable<char> Repertoire()
    {
        var encoding = CharSqlType.Cp1252Encoder;
        var buffer = new byte[1];
        for (var b = 1; b <= 255; b++)
        {
            buffer[0] = (byte)b;
            var decoded = encoding.GetString(buffer);
            if (decoded.Length == 1)
                yield return decoded[0];
        }

        for (var cp = 0x0E00; cp <= 0x0E7F; cp++)
            yield return (char)cp;
    }
}
