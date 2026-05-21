using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-surface coverage for the default collation's
/// (<c>SQL_Latin1_General_CP1_CI_AS</c>) case-folding, accent-sensitivity,
/// and primary-weight-zero sort/equality rules — exercised through
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
    /// Probe-confirmed against SQL Server 2025: the default collation
    /// applies <c>IgnoreSymbols</c> in sort — apostrophe drops out of the
    /// primary sort key, so "'Aiea" sorts as "Aiea" which is greater than
    /// "Aaronsburg". Equality keeps symbols significant — the direct
    /// asymmetry probe lives in <c>CollationTests</c>.
    /// </summary>
    [TestMethod]
    public void DefaultCollation_OrderBy_ApostropheIsIgnored()
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
