using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-surface coverage for the default collation's
/// (<c>SQL_Latin1_General_CP1_CI_AS</c>) case-folding, accent-sensitivity,
/// and minimal-weight (hyphen / apostrophe) sort/equality rules — exercised through
/// <c>=</c>, <c>ORDER BY</c>, <c>DISTINCT</c>, and <c>COLLATE</c>-name
/// case-insensitive resolution. Counterpart to the internal-only
/// <c>CollationTests</c>, which retains the algorithm-contract tests for
/// the dormant non-default collations (their algorithms exist but aren't
/// routed through public SQL — see <c>docs/claude/database-options.md</c>).
/// </summary>
[TestClass]
public sealed class CollationBehaviorTests
{
    [TestMethod]
    [DataRow("'abc' = 'ABC'", 1)]
    [DataRow("'AbC' = 'aBc'", 1)]
    public void DefaultCollation_AsciiEquality_IsCaseInsensitive(string condition, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select case when {condition} then 1 else 0 end"));

    [TestMethod]
    [DataRow("'é' = 'É'", 1)]
    [DataRow("'café' = 'CAFÉ'", 1)]
    public void DefaultCollation_Latin1Equality_FoldsAccentedLetters(string condition, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select case when {condition} then 1 else 0 end"));

    [TestMethod]
    [DataRow("'e' = 'é'", 0)]
    [DataRow("'a' = 'ä'", 0)]
    public void DefaultCollation_IsAccentSensitive(string condition, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select case when {condition} then 1 else 0 end"));

    /// <summary>
    /// Real <c>SQL_Latin1_General_CP1_CI_AS</c> keeps apostrophe and hyphen
    /// meaningful for both sort and equality — unlike the Windows-style
    /// CI_AS family which treats them as primary-weight-zero (sort-ignorable).
    /// </summary>
    [TestMethod]
    [DataRow("'co-op' = 'coop'", 0)]
    [DataRow("'''A' = 'A'", 0)]
    [DataRow("'O''Brien' = 'OBrien'", 0)]
    public void DefaultCollation_SymbolsAreSignificant(string condition, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select case when {condition} then 1 else 0 end"));

    /// <summary>
    /// ORDER BY routes through the default collation. <c>'a' &lt; 'B'</c>
    /// because case-fold yields <c>'A' &lt; 'B'</c>.
    /// </summary>
    [TestMethod]
    public void DefaultCollation_OrderBy_AsciiLowerVsUpper_IsCaseInsensitive()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v nvarchar(20)); insert t values ('B'), ('a')");
        using var reader = sim.CreateCommand("select v from t order by v").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "a", "B" }, rows);
    }

    /// <summary>
    /// Probe-confirmed against SQL Server 2025: apostrophe and hyphen carry
    /// only a secondary sort weight, so they drop out of the *primary* key —
    /// "'Aiea" sorts as "Aiea", which is greater than "Aaronsburg". (Other
    /// symbols keep a real primary weight and sort ahead of letters; the
    /// minimal-weight asymmetry and the secondary tie-break live in
    /// <c>CollationTests</c>.) Equality keeps every symbol significant.
    /// </summary>
    [TestMethod]
    public void DefaultCollation_OrderBy_ApostropheHasMinimalWeight()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (v nvarchar(20)); insert t values ('Aaronsburg'), ('''Aiea')");
        using var reader = sim.CreateCommand("select v from t order by v").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "Aaronsburg", "'Aiea" }, rows);
    }

    /// <summary>
    /// Thai (out-of-CP1252) data sorts through the unified SqlLatin1 weight
    /// table baked from SQL Server's NLS Unicode weights, not .NET's code-point
    /// order: every Thai letter ranks above all Latin, and the leading vowel
    /// เ (U+0E40) sorts low — so เบญจศร &lt; คณาพล &lt; บางสุขศรี. This is the
    /// exact AdventureWorks <c>vJobCandidate.[Name.Last]</c> ordering;
    /// probe-confirmed against SQL Server 2025 (.NET's invariant and th-TH
    /// CompareInfo both order these the opposite way).
    /// </summary>
    [TestMethod]
    public void DefaultCollation_OrderBy_ThaiUsesSqlServerNlsWeights()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (v nvarchar(20)); insert t values (N'บางสุขศรี'), (N'Yee'), (N'เบญจศร'), (N'คณาพล')");
        using var reader = sim.CreateCommand("select v from t order by v").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "Yee", "เบญจศร", "คณาพล", "บางสุขศรี" }, rows);
    }

    /// <summary>MAX over a Latin/Thai mix returns the Thai extreme — Thai letters outrank Latin.</summary>
    [TestMethod]
    public void DefaultCollation_Max_ThaiOutranksLatin()
        => AreEqual("บางสุขศรี", new Simulation().ExecuteScalar(
            "create table t (v nvarchar(20)); insert t values (N'Yee'), (N'เบญจศร'), (N'บางสุขศรี'); select max(v) from t"));

    /// <summary>
    /// Probe-confirmed against SQL Server 2025: symbols other than hyphen /
    /// apostrophe keep a real primary weight that sorts them ahead of digits
    /// and letters, so MIN of ('#500-75', '00,', 'abc') is '#500-75'. (An
    /// earlier ignore-all-symbols sort stripped the '#' and mis-ranked
    /// "#500-75" among the digits as "50075" — the divergence this guards.)
    /// </summary>
    [TestMethod]
    public void DefaultCollation_OrderBy_NonMinimalSymbolsSortFirst()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (v nvarchar(20)); insert t values ('00,'), ('abc'), ('#500-75')");
        AreEqual("#500-75", sim.ExecuteScalar("select min(v) from t"));
        using var reader = sim.CreateCommand("select v from t order by v").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "#500-75", "00,", "abc" }, rows);
    }

    /// <summary>
    /// The minimal-weight marks break ties only against an otherwise-identical
    /// neighbor: "coop" sorts before "co-op" (probe-confirmed). MIN therefore
    /// picks the mark-free spelling.
    /// </summary>
    [TestMethod]
    public void DefaultCollation_OrderBy_MinimalWeightBreaksTieAfterPlainSpelling()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (v nvarchar(20)); insert t values ('co-op'), ('coop')");
        AreEqual("coop", sim.ExecuteScalar("select min(v) from t"));
        using var reader = sim.CreateCommand("select v from t order by v").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "coop", "co-op" }, rows);
    }

    /// <summary>
    /// DISTINCT relies on the comparer's hash/equality contract: case-
    /// folded equivalents collapse to a single bucket, accent-distinct
    /// strings stay separate.
    /// </summary>
    [TestMethod]
    public void DefaultCollation_Distinct_HashContractAgreesWithEquals()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table caseFold (v nvarchar(20));
            insert caseFold values ('AbC'), ('abc');
            create table accent (v nvarchar(20));
            insert accent values ('café'), ('cafe');
            """);
        // 'AbC' and 'abc' collapse (case fold); 'café' and 'cafe' don't (accent).
        AreEqual(1, sim.ExecuteScalar("select count(*) from (select distinct v from caseFold) d"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from (select distinct v from accent) d"));
    }

    /// <summary>
    /// The default collation sorts at two levels: primary (accent-folded base
    /// letter) then a secondary accent tie-break. So <c>'à'</c> orders before
    /// <c>'Ao'</c> (base <c>a</c> precedes <c>Ao</c>) even though the accented
    /// letter sorts after its plain form within a tie (<c>'az'</c> &lt;
    /// <c>'àz'</c>). Probe-confirmed against SQL Server 2025; this is the level
    /// a single-rank table can't express.
    /// </summary>
    [TestMethod]
    public void DefaultCollation_OrderBy_AccentIsSecondaryWeight()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (v nvarchar(20)); insert t values ('Ao'), ('à'), ('az'), ('àz')");
        using var reader = sim.CreateCommand("select v from t order by v").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "à", "Ao", "az", "àz" }, rows);
    }

    /// <summary>
    /// nvarchar expands the Latin ligatures to their base letters (probe-
    /// confirmed <c>'æ' = 'ae'</c>, <c>'ß' = 'ss'</c>); varchar's legacy sort
    /// order expands only <c>æ</c>/<c>Æ</c> and treats <c>œ</c>/<c>ß</c> as
    /// distinct single-weight letters. Here MIN under nvarchar collapses
    /// <c>'æ'</c> against <c>'ae'</c> and orders it between <c>'ad'</c> and
    /// <c>'af'</c>.
    /// </summary>
    [TestMethod]
    public void DefaultCollation_OrderBy_NvarcharExpandsLigatures()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (v nvarchar(20)); insert t values ('ad'), ('af'), ('æx')");
        using var reader = sim.CreateCommand("select v from t order by v").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        // 'æx' expands to 'aex', which sorts between 'ad' and 'af'.
        CollectionAssert.AreEqual(new[] { "ad", "æx", "af" }, rows);
    }

    [TestMethod]
    public void CollationName_LookupIsCaseInsensitive()
    {
        // CREATE TABLE accepts a lowercase collation name — verifies the
        // recognized-collation lookup is case-insensitive (collation names
        // are themselves case-insensitive identifiers in SQL Server). The
        // stored / round-tripped form reflects what was typed.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("CREATE TABLE t (c nvarchar(50) COLLATE sql_latin1_general_cp1_ci_as)");
        AreEqual("sql_latin1_general_cp1_ci_as", sim.ExecuteScalar(
            "SELECT collation_name FROM sys.columns WHERE name = 'c'"));
    }
}
