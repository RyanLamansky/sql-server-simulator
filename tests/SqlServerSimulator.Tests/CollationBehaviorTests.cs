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
    /// The default collation's varchar sort weighs a control character below
    /// the space, <c>CHAR(0)</c> lowest of all, and compares a shorter string
    /// as if space-padded — so the control sorts a string before its own
    /// prefix — while the nvarchar sort ignores <c>CHAR(0)</c>.
    /// Probed 2026-09-26 against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("'a' + char(0) + 'b' = 'ab'", 0)]
    [DataRow("'a' + char(0) + 'b' < 'ab'", 1)]
    [DataRow("'a' + char(0) + 'b' < 'a b'", 1)]
    [DataRow("'a' + char(0) = 'a'", 0)]
    [DataRow("'a' + char(0) < 'a'", 1)]
    [DataRow("'a' + char(0) < 'a '", 1)]
    [DataRow("'a' + char(0) + ' ' = 'a' + char(0)", 1)]
    [DataRow("char(0) = ''", 0)]
    [DataRow("char(0) < char(1)", 1)]
    [DataRow("'a' + char(1) < 'a'", 1)]
    [DataRow("'a' + char(9) < 'a'", 1)]
    [DataRow("N'a' + nchar(0) + N'b' = N'ab'", 1)]
    [DataRow("'a' + char(0) + 'b' = 'ab' collate Latin1_General_CI_AS", 1)]
    public void DefaultCollation_VarcharControlCharacters_WeighBelowSpace(string condition, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select case when {condition} then 1 else 0 end"));

    /// <summary>
    /// Where the varchar sort weighs <c>CHAR(0)</c>, a pattern's literal run
    /// that ends in one takes exactly as many of the subject's, whatever
    /// wildcard follows (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("'a' + char(0) + 'b' like 'a' + char(0) + '_'", 1)]
    [DataRow("'a' + char(0) + 'b' like 'a' + char(0) + '%'", 1)]
    [DataRow("'a' + char(0) + char(0) + 'b' like 'a' + char(0) + '%'", 1)]
    [DataRow("'a' + char(0) like 'a' + char(0) + '%'", 1)]
    [DataRow("char(0) + 'b' like char(0) + '%'", 1)]
    [DataRow("char(0) like char(0) + '%'", 1)]
    [DataRow("char(0) like '_'", 1)]
    [DataRow("char(0) like ''", 0)]
    [DataRow("'a' + char(0) + 'b' like 'a_b'", 1)]
    [DataRow("'a' + char(0) + 'b' like 'ab'", 0)]
    public void DefaultCollation_VarcharNul_InALikePattern(string condition, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select case when {condition} then 1 else 0 end"));

    [TestMethod]
    public void DefaultCollation_VarcharNul_KeepsDistinctValuesApart()
        => AreEqual(2, new Simulation().ExecuteScalar("select count(distinct v) from (values ('a' + char(0) + 'b'), ('ab')) t(v)"));

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
    /// Thai (out-of-CP1252) data sorts through the Latin1-General weight
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

    /// <summary>
    /// Apostrophe and hyphen are both minimal-weight marks but remain
    /// distinct from each other, including when a fullwidth character
    /// pushes the comparison onto the non-CP1252 fallback path —
    /// probe-confirmed (2026-07-13): real SQL Server returns 'neq' for
    /// both forms and keeps the values in separate GROUP BY buckets.
    /// </summary>
    [TestMethod]
    [DataRow("N'ab''c' = N'ab-c'", 0)]
    [DataRow("N'ab''cＸ' = N'ab-cＸ'", 0)]
    [DataRow("N'coopＸ' = N'co-opＸ'", 0)]
    public void DefaultCollation_MinimalMarks_StayDistinct(string condition, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select case when {condition} then 1 else 0 end"));

    [TestMethod]
    public void DefaultCollation_MinimalMarks_GroupSeparately()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table dbo.marks (v nvarchar(10));
            insert dbo.marks values (N'ab''cＸ'), (N'ab-cＸ');
            select count(*) from (select v from dbo.marks group by v) g
            """));

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

    /// <summary>
    /// <c>CHAR(0)</c> in <c>varchar</c> data is the lowest-weight character
    /// under every non-binary <c>SQL_</c> collation — for comparison, LIKE,
    /// CHARINDEX and REPLACE alike — where Unicode data and a Windows
    /// collation ignore it (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("SQL_Latin1_General_CP1_CI_AS", "0 0 1 0 1 1 2 2 1")]
    [DataRow("SQL_Latin1_General_CP1_CS_AS", "0 0 1 0 1 1 2 2 2")]
    [DataRow("SQL_Latin1_General_CP1253_CI_AS", "0 0 1 0 1 1 2 2 1")]
    [DataRow("SQL_Latin1_General_CP850_CI_AS", "0 0 1 0 1 1 2 2 1")]
    [DataRow("Latin1_General_100_CI_AS", "1 1 0 1 1 1 0 3 1")]
    public void Nul_IsWeightedInVarcharUnderSqlCollations(string collation, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            declare @s varchar(10) = 'a' + char(0) + 'b';
            select concat_ws(' ',
                case when @s collate {collation} = 'ab' then 1 else 0 end,
                case when @s collate {collation} like 'ab' then 1 else 0 end,
                case when @s collate {collation} < 'ab' then 1 else 0 end,
                case when ('a' + char(0)) collate {collation} = 'a' then 1 else 0 end,
                case when @s collate {collation} like 'a%' then 1 else 0 end,
                case when (N'a' + nchar(0) + N'b') collate {collation} = N'ab' then 1 else 0 end,
                charindex(char(0), @s collate {collation}),
                len(replace(@s collate {collation}, char(0), '')),
                (select count(distinct v) from (values (@s collate {collation}), ('a' + char(0) + 'B')) d(v)))
            """));

    /// <summary>
    /// Sort orders 51 and 41 order a case pair uppercase first in <c>varchar</c>
    /// data, where the same names' <c>nvarchar</c> data and the CP1250 sibling
    /// sort lowercase first (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("varchar", "SQL_Latin1_General_CP1_CS_AS", "A a B b", "A|a")]
    [DataRow("varchar", "SQL_Latin1_General_CP850_CS_AS", "A a B b", "A|a")]
    [DataRow("nvarchar", "SQL_Latin1_General_CP1_CS_AS", "a A b B", "a|A")]
    [DataRow("varchar", "SQL_Latin1_General_CP1250_CS_AS", "a A b B", "a|A")]
    public void CaseSensitiveLegacySortOrders_OrderACasePair(string type, string collation, string ordered, string minMax)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t (v {type}(5) collate {collation}); insert t values ('a'), ('A'), ('b'), ('B')");
        AreEqual(ordered, sim.ExecuteScalar("select string_agg(v, ' ') within group (order by v) from t"));
        AreEqual(minMax, sim.ExecuteScalar("select concat(min(v), '|', max(v)) from t where v in ('a', 'A')"));
    }

    /// <summary>
    /// The <c>Pref</c> names' <c>varchar</c> data compares equal but sorts a
    /// case pair uppercase first once every key ties, at the first character
    /// the spellings differ in; <c>nvarchar</c> data doesn't (probed 2026-09-29
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("varchar", "SQL_Latin1_General_Pref_CP1_CI_AS", "A a B b")]
    [DataRow("varchar", "SQL_Latin1_General_Pref_CP437_CI_AS", "A a B b")]
    [DataRow("varchar", "SQL_Danish_Pref_CP1_CI_AS", "A a B b")]
    public void PrefCollations_OrderACasePair_UppercaseFirst(string type, string collation, string ordered)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t (id int identity, v {type}(5) collate {collation}); insert t (v) values ('a'), ('A'), ('b'), ('B')");
        AreEqual(ordered, sim.ExecuteScalar("select string_agg(v, ' ') within group (order by v) from t"));
    }

    /// <summary>A later key outranks the preference, the pair stays equal, and the preference reverses under <c>DESC</c> and moves a <c>TOP … WITH TIES</c> boundary.</summary>
    [TestMethod]
    public void PrefCollation_PreferenceIsTheFinalTiebreak()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int identity, v varchar(5) collate SQL_Latin1_General_Pref_CP1_CI_AS, w int); insert t (v, w) values ('a', 2), ('A', 3), ('AB', 1), ('ab', 1), ('Ab', 1)");
        AreEqual("1 2", sim.ExecuteScalar("select string_agg(id, ' ') within group (order by v, w) from t where id <= 2"));
        AreEqual("2 1", sim.ExecuteScalar("select string_agg(id, ' ') within group (order by v) from t where id <= 2"));
        AreEqual("1 2", sim.ExecuteScalar("select string_agg(id, ' ') within group (order by v desc) from t where id <= 2"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from t where v = 'A'"));
        AreEqual("3 5 4", sim.ExecuteScalar("select string_agg(id, ' ') within group (order by v) from t where id > 2"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from (select top (1) with ties v from t order by v) d"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from (select distinct v from t where id <= 2) d"));
    }

    /// <summary>
    /// The Latin1-General weight tables under the names they serve: ligatures
    /// equal to their letters, the minimal-weight hyphen against sort order
    /// 52's per-character one, sort order 51's uppercase-first case level, the
    /// code page 850 and 437 orders, the version split over U+202F, the binary
    /// names' space padding and a Thai tone mark as an accent (probed
    /// 2026-10-02 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("N'æ'", "N'ae'", "Latin1_General_CI_AS", "=")]
    [DataRow("N'ß'", "N'ss'", "Latin1_General_100_CS_AS", "=")]
    [DataRow("N'ǅ'", "N'Dž'", "Latin1_General_CS_AS", "=")]
    [DataRow("N'coop'", "N'co-op'", "Latin1_General_CI_AS", "<")]
    [DataRow("'coop'", "'co-op'", "SQL_Latin1_General_CP1_CI_AS", ">")]
    [DataRow("'ss'", "'ß'", "SQL_Latin1_General_CP1_CI_AS", "<")]
    [DataRow("'a'", "'A'", "SQL_Latin1_General_CP1_CS_AS", ">")]
    [DataRow("N'a'", "N'A'", "Latin1_General_CS_AS", "<")]
    [DataRow("'à'", "'Ao'", "SQL_Latin1_General_CP850_CI_AS", "<")]
    [DataRow("'Çm'", "'cn'", "SQL_Latin1_General_CP437_CI_AS", "<")]
    [DataRow("'é'", "'f'", "SQL_Latin1_General_CP850_CI_AI", "<")]
    [DataRow("N'x' + NCHAR(8239)", "N'x'", "Latin1_General_100_CI_AS", ">")]
    [DataRow("N'x' + NCHAR(8239)", "N'x'", "Latin1_General_CI_AS", "=")]
    [DataRow("'a' + CHAR(9)", "'a'", "Latin1_General_BIN2", "<")]
    [DataRow("N'a' + NCHAR(9)", "N'a'", "Latin1_General_BIN", ">")]
    [DataRow("N'a' + NCHAR(256)", "N'a' + NCHAR(511)", "Latin1_General_BIN", "<")]
    [DataRow("N'cafe'", "N'café'", "Latin1_General_CI_AS", "<")]
    [DataRow("N'ร่'", "N'ร'", "Latin1_General_CI_AS", ">")]
    public void Latin1GeneralTables_CompareAsRealDoes(string left, string right, string collation, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar(
            $"select case when {left} collate {collation} < {right} then '<' when {left} collate {collation} = {right} then '=' else '>' end"));
}
