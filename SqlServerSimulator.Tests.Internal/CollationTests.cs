using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Direct exercises of the three concrete <see cref="Collation"/>
/// implementations — case folding, accent-sensitivity, the
/// sort-vs-equality asymmetry on primary-weight-zero symbols, and the
/// name-keyed lookup surfaces (<see cref="Collation.Recognized"/> /
/// <see cref="Collation.ByName"/>). Lives under the internal-tests
/// project because the <see cref="Collation"/> class is internal and
/// the algorithm contracts are an implementation concern that doesn't
/// thread through public SQL (data still routes through
/// <see cref="Collation.Default"/> regardless of declared column /
/// database collation — see <c>docs/claude/database-options.md</c>).
/// </summary>
[TestClass]
public sealed class CollationTests
{
    [TestMethod]
    public void Default_IsSqlLatin1()
        => AreEqual("SQL_Latin1_General_CP1_CI_AS", Collation.Default.Name);

    [TestMethod]
    public void Names_RoundTripVerbatim()
    {
        AreEqual("SQL_Latin1_General_CP1_CI_AS", Collation.Default.Name);
        AreEqual("Latin1_General_100_CI_AS", Collation.Latin1General100CiAs.Name);
        AreEqual("Latin1_General_CI_AS", Collation.Latin1GeneralCiAs.Name);
    }

    [TestMethod]
    public void Recognized_ListsExactlyThree()
        => HasCount(3, Collation.Recognized);

    [TestMethod]
    public void IsRecognized_KnownNames_ReturnTrue()
    {
        IsTrue(Collation.IsRecognized("SQL_Latin1_General_CP1_CI_AS"));
        IsTrue(Collation.IsRecognized("Latin1_General_100_CI_AS"));
        IsTrue(Collation.IsRecognized("Latin1_General_CI_AS"));
    }

    [TestMethod]
    public void IsRecognized_CaseInsensitive()
        => IsTrue(Collation.IsRecognized("sql_latin1_general_cp1_ci_as"));

    [TestMethod]
    public void IsRecognized_UnknownName_ReturnsFalse()
        => IsFalse(Collation.IsRecognized("Japanese_CI_AS"));

    [TestMethod]
    public void ByName_ResolvesEachInstance()
    {
        AreSame(Collation.Default, Collation.ByName["SQL_Latin1_General_CP1_CI_AS"]);
        AreSame(Collation.Latin1General100CiAs, Collation.ByName["Latin1_General_100_CI_AS"]);
        AreSame(Collation.Latin1GeneralCiAs, Collation.ByName["Latin1_General_CI_AS"]);
    }

    [TestMethod]
    public void ByName_CaseInsensitive()
        => AreSame(Collation.Default, Collation.ByName["sql_LATIN1_general_cp1_ci_AS"]);

    // ---- SQL_Latin1_General_CP1_CI_AS ----

    [TestMethod]
    public void Sql_AsciiCaseFolding_EqualAndOrderedSame()
    {
        IsTrue(Collation.Default.Equals("abc", "ABC"));
        AreEqual(0, Collation.Default.Compare("abc", "ABC"));
        AreEqual(Collation.Default.GetHashCode("abc"), Collation.Default.GetHashCode("ABC"));
    }

    [TestMethod]
    public void Sql_LatinOneCaseFolding_FoldsAccentedLetters()
    {
        // OrdinalIgnoreCase folds these too — what CompareInfo with
        // IgnoreCase adds over Ordinal is NFD/NFC equivalence and
        // halfwidth/fullwidth folding, not Latin-1 case folding.
        IsTrue(Collation.Default.Equals("é", "É"));
        IsTrue(Collation.Default.Equals("àÀáÁ", "ÀàÁá"));
        AreEqual(0, Collation.Default.Compare("café", "CAFÉ"));
    }

    [TestMethod]
    public void Sql_AccentSensitive_DistinguishesBaseAndAccent()
    {
        IsFalse(Collation.Default.Equals("e", "é"));
        IsFalse(Collation.Default.Equals("a", "ä"));
        AreNotEqual(0, Collation.Default.Compare("e", "é"));
    }

    [TestMethod]
    public void Sql_ApostropheIsNotSortIgnorable_DiffersFromWindowsCiAs()
    {
        // Real SQL_Latin1_General_CP1_CI_AS keeps apostrophe meaningful for
        // both sort and equality — this is the behavior the pre-existing
        // StripSortIgnorable hack got wrong.
        IsLessThan(0, Collation.Default.Compare("'Aiea", "Aaronsburg"));
        AreNotEqual(0, Collation.Default.Compare("'A", "A"));
        IsFalse(Collation.Default.Equals("'A", "A"));
    }

    [TestMethod]
    public void Sql_HyphenIsNotSortIgnorable()
    {
        AreNotEqual(0, Collation.Default.Compare("co-op", "coop"));
        IsFalse(Collation.Default.Equals("co-op", "coop"));
    }

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

    [TestMethod]
    public void Sql_OrdersAsciiLowerVsUpperCaseInsensitively()
    {
        // 'a' < 'B' because case folds 'a' → 'A' and 'A' < 'B'.
        IsLessThan(0, Collation.Default.Compare("a", "B"));
        IsGreaterThan(0, Collation.Default.Compare("B", "a"));
    }

    [TestMethod]
    public void Sql_HashCode_AgreesWithEquals()
    {
        // Hash contract: Equals(a,b) → GetHashCode(a) == GetHashCode(b).
        AreEqual(Collation.Default.GetHashCode("AbC"), Collation.Default.GetHashCode("abc"));
        AreEqual(Collation.Default.GetHashCode("café"), Collation.Default.GetHashCode("CAFÉ"));
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
}
