using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards the <c>IEqualityComparer&lt;string&gt;</c> contract on
/// <see cref="Collation.Latin1GeneralTableCollation"/>: whenever
/// <c>Equals(x, y)</c> is true — including across the repertoire boundary,
/// where a character past the table weighs as its spelling, an accent or a
/// foreign element — <c>GetHashCode</c> must agree, and the order must be one
/// total order, which an index's key order and a sort rely on. Derivation
/// notes live in <c>docs/claude/collations.md</c>.
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
        }
    }

    /// <summary>
    /// A character past the table weighs as the table spelling ICU calls it
    /// equal to, so equality across the boundary is the table's own and
    /// transitive: an ideographic space is a space, which the table holds
    /// apart from a no-break space, so the two are apart too, as on real
    /// (probed 2026-10-24 against SQL Server 2025), where comparing such a
    /// pair by ICU alone would make the ideographic spelling equal to both.
    /// </summary>
    [TestMethod]
    public void CrossBoundarySpellings_WeighAsTheTableSpelling()
    {
        AssertEqualAndHashEqual(Nvarchar, "x2y", "x２y");
        AssertEqualAndHashEqual(Nvarchar, "x²y", "x２y");
        AssertEqualAndHashEqual(Nvarchar, "a b", "a\u3000b");
        IsFalse(Nvarchar.Equals("a\u00A0b", "a\u3000b"));
        IsLessThan(0, Nvarchar.Compare("a\u3000b", "a\u00A0b"));
        AssertEqualAndHashEqual(Nvarchar, "a-b", "a\uFF0Db");
        AssertEqualAndHashEqual(Nvarchar, "a'b", "a\uFF07b");

        // Thai SARA AM: the table holds the composed U+0E33 apart from
        // NIKHAHIT + SARA AA, and a fullwidth x past it changes nothing.
        AssertEqualAndHashEqual(Nvarchar, "กำx", "กำｘ");
        IsFalse(Nvarchar.Equals("กําx", "กำｘ"));
    }

    /// <summary>
    /// The order over a mix of table characters, their spellings past the
    /// table, combining marks and foreign scripts is one total order:
    /// sorting the mix puts every pair in order and equality is
    /// <c>Compare</c> returning 0, which comparing a pair holding a character
    /// past the table by ICU alone doesn't give.
    /// </summary>
    [TestMethod]
    [DataRow(Collation.Latin1GeneralTableCollation.DefaultName)]
    [DataRow("Latin1_General_CS_AS")]
    [DataRow("Latin1_General_100_CI_AI")]
    public void TheOrder_IsOneTotalOrder(string name)
    {
        var collation = Collation.Get(name);
        string[] pieces =
        [
            "a", "A", "ａ", "b", " ", "\u00A0", "\u3000", "\u2007", "-", "\uFF0D", "\u2010", "'", "\uFF07", "\u2019", ",", "\uFF0C",
            "2", "²", "２", "⁴", "4", "⑤", "5", "\u0301", "e", "é", "x\u0301", "α", "Α", "ά", "Ω", "\u2126", "К", "中", "あ", "ア", "→", "€", "§", "ß", "ss", "\uD83D\uDE00",
        ];
        var random = new Random(20261024);
        var strings = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var builder = new StringBuilder();
            for (var length = random.Next(1, 5); length > 0; length--)
                _ = builder.Append(pieces[random.Next(pieces.Length)]);
            strings.Add(builder.ToString());
        }
        strings.Sort(collation);

        var violations = new List<string>();
        for (var i = 0; i < strings.Count && violations.Count < 10; i++)
        {
            for (var j = i + 1; j < strings.Count && violations.Count < 10; j++)
            {
                var order = collation.Compare(strings[i], strings[j]);
                if (order > 0 || order != -collation.Compare(strings[j], strings[i]) || collation.Equals(strings[i], strings[j]) != (order == 0))
                    violations.Add($"{Escape(strings[i])} vs {Escape(strings[j])}: {order}");
            }
        }
        IsEmpty(violations, string.Join("; ", violations));
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
            ["\u0301", "\u0300\u0301", "\u0301\u0300", "\u0301a", "a"],
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
    /// blocks rich in compatibility and decomposed forms of the table's
    /// characters, derive normalization / case variants of a carrier string
    /// and assert the hash contract wherever Equals holds.
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
}
