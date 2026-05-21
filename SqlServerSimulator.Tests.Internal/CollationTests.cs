using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Algorithm-contract tests for the <see cref="Collation"/> implementations
/// that aren't routed through public SQL today. The default collation's
/// behavior (<see cref="Collation.Default"/>) and the parser-driven
/// name-resolution surface (<see cref="Collation.TryGet"/> /
/// <see cref="Collation.IsRecognized"/>) are exercised in the public
/// <c>CollationBehaviorTests</c> / <c>CollationMetadataTests</c> /
/// <c>LikeTests</c>; this file keeps only the dormant comparers
/// (<see cref="Collation.Latin1General100CiAs"/>,
/// <see cref="Collation.Latin1GeneralCiAs"/>,
/// <see cref="Collation.Latin1GeneralCsAs"/>,
/// <see cref="Collation.Latin1GeneralBin"/>) and the internal-only null
/// handling of <see cref="Collation.Default"/>'s
/// <see cref="IComparer{T}"/> / <see cref="IEqualityComparer{T}"/>
/// contracts. The dormant algorithms exist in the code but aren't called
/// from any public SQL site (every string op outside the LIKE
/// case-sensitivity flag still goes through <see cref="Collation.Default"/>
/// — see <c>docs/claude/database-options.md</c>); the tests pin the
/// dormant contract so the algorithms behave correctly once routing
/// lands.
/// </summary>
[TestClass]
public sealed class CollationTests
{
    // ---- SQL_Latin1_General_CP1_CI_AS — internal-only contracts ----

    /// <summary>
    /// Three-valued NULL comparison is handled at the operator layer
    /// (<c>=</c> on a NULL operand returns UNKNOWN before reaching the
    /// comparer), so the comparer's null arms aren't reachable through
    /// public SQL. Pin them anyway — <see cref="IEqualityComparer{T}"/>
    /// / <see cref="IComparer{T}"/> nominally accept null and the
    /// simulator's identifier-resolution sites pass strings that could
    /// in principle be null.
    /// </summary>
    [TestMethod]
    public void Sql_NullHandling()
    {
        IsTrue(Collation.Default.Equals(null, null));
        IsFalse(Collation.Default.Equals(null, ""));
        IsFalse(Collation.Default.Equals("", null));
        AreEqual(0, Collation.Default.Compare(null, null));
        IsLessThan(0, Collation.Default.Compare(null, "x"));
        IsGreaterThan(0, Collation.Default.Compare("x", null));
    }

    // ---- Latin1_General_100_CI_AS (Windows-style v100) ----

    [TestMethod]
    public void Win100_AsciiCaseFolding_EqualAndOrderedSame()
    {
        IsTrue(Collation.Latin1General100CiAs.Equals("abc", "ABC"));
        AreEqual(0, Collation.Latin1General100CiAs.Compare("abc", "ABC"));
        AreEqual(Collation.Latin1General100CiAs.GetHashCode("abc"), Collation.Latin1General100CiAs.GetHashCode("ABC"));
    }

    [TestMethod]
    public void Win100_AccentSensitive()
    {
        IsFalse(Collation.Latin1General100CiAs.Equals("e", "é"));
        AreNotEqual(0, Collation.Latin1General100CiAs.Compare("e", "é"));
    }

    [TestMethod]
    public void Win100_ApostropheIsPrimaryWeightZero_InSort()
    {
        // Probe-confirmed against WideWorldImporters.Application.Cities:
        // MIN of ('Aaronsburg', "'Aiea") is 'Aaronsburg' under
        // Latin1_General_100_CI_AS because the apostrophe drops out of
        // the primary sort key, leaving "Aiea" > "Aaronsburg".
        IsLessThan(0, Collation.Latin1General100CiAs.Compare("Aaronsburg", "'Aiea"));
        AreEqual(0, Collation.Latin1General100CiAs.Compare("'A", "A"));
        AreEqual(0, Collation.Latin1General100CiAs.Compare("O'Brien", "OBrien"));
    }

    [TestMethod]
    public void Win100_HyphenIsPrimaryWeightZero_InSort()
    {
        AreEqual(0, Collation.Latin1General100CiAs.Compare("co-op", "coop"));
        AreEqual(0, Collation.Latin1General100CiAs.Compare("re-do", "redo"));
    }

    [TestMethod]
    public void Win100_Equality_IsStrictAboutSymbols_NotJustSort()
    {
        // Probe-confirmed: 'OBrien' = 'O''Brien' returns 0 even under
        // Windows-100 CI_AS. The primary-weight-zero treatment only
        // applies to sort/MIN/MAX, not to '='.
        IsFalse(Collation.Latin1General100CiAs.Equals("O'Brien", "OBrien"));
        IsFalse(Collation.Latin1General100CiAs.Equals("'A", "A"));
        IsFalse(Collation.Latin1General100CiAs.Equals("co-op", "coop"));
    }

    [TestMethod]
    public void Win100_HashCode_AgreesWithEquals()
    {
        AreEqual(Collation.Latin1General100CiAs.GetHashCode("AbC"), Collation.Latin1General100CiAs.GetHashCode("abc"));
        // And differs for strings the equality side distinguishes.
        AreNotEqual(Collation.Latin1General100CiAs.GetHashCode("O'Brien"), Collation.Latin1General100CiAs.GetHashCode("OBrien"));
    }

    [TestMethod]
    public void Win100_NullHandling()
    {
        IsTrue(Collation.Latin1General100CiAs.Equals(null, null));
        IsFalse(Collation.Latin1General100CiAs.Equals(null, "x"));
        AreEqual(0, Collation.Latin1General100CiAs.Compare(null, null));
        IsLessThan(0, Collation.Latin1General100CiAs.Compare(null, "x"));
        IsGreaterThan(0, Collation.Latin1General100CiAs.Compare("x", null));
    }

    // ---- Latin1_General_CI_AS (Windows-style pre-v100) ----

    [TestMethod]
    public void Win80_BehavesLikeWin100_ForAsciiAndLatin1()
    {
        // For the Latin-1 / ASCII strings the simulator exercises, v80 and
        // v100 are identical (the v100 update changed ordering for non-
        // Latin scripts and a handful of newly-added supplementary code
        // points; none reach the regression bar).
        IsTrue(Collation.Latin1GeneralCiAs.Equals("abc", "ABC"));
        AreEqual(0, Collation.Latin1GeneralCiAs.Compare("'A", "A"));
        IsFalse(Collation.Latin1GeneralCiAs.Equals("'A", "A"));
        IsLessThan(0, Collation.Latin1GeneralCiAs.Compare("Aaronsburg", "'Aiea"));
    }

    [TestMethod]
    public void Win80_HashCode_AgreesWithEquals()
        => AreEqual(Collation.Latin1GeneralCiAs.GetHashCode("AbC"), Collation.Latin1GeneralCiAs.GetHashCode("abc"));

    // ---- Latin1_General_CS_AS ----

    [TestMethod]
    public void CsAs_AccentSensitive()
        => IsFalse(Collation.Latin1GeneralCsAs.Equals("e", "é"));

    // ---- Latin1_General_BIN ----

    [TestMethod]
    public void Bin_StrictCodepoint_DistinguishesNfdAndNfc()
    {
        // NFC = single precomposed U+00E9; NFD = base 'e' + combining
        // U+0301. Ordinal compare treats them as distinct byte sequences;
        // CompareInfo-based comparers (Sql, Win100, CsAs) fold them.
        const string nfc = "é";
        const string nfd = "é";
        IsFalse(Collation.Latin1GeneralBin.Equals(nfc, nfd));
        IsTrue(Collation.Default.Equals(nfc, nfd));
    }
}
