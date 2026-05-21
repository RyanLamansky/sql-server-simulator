using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-surface coverage for per-column declared collation: ORDER BY /
/// equality / LIKE / GROUP BY honor the column's pinned collation; cross-
/// collation operand pairs raise Msg 468 (comparison / set ops / LIKE) and
/// Msg 457 (concat); the <c>COLLATE</c> postfix overrides; ALTER COLUMN
/// preserves declared collation when no clause is specified; and <c>#temp</c>
/// inherits the active database's default. Counterpart to
/// <see cref="CollationBehaviorTests"/> which covers only the
/// <c>SQL_Latin1_General_CP1_CI_AS</c> default.
/// </summary>
[TestClass]
public sealed class CollationDeclaredColumnTests
{
    /// <summary>
    /// <c>Latin1_General_BIN</c> sorts by codepoint, so uppercase precedes
    /// lowercase. Demonstrates that ORDER BY routes through the column's
    /// declared collation rather than the database default's case-insensitive
    /// rules.
    /// </summary>
    [TestMethod]
    public void OrderBy_BinaryColumn_SortsByCodepoint()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s varchar(20) collate Latin1_General_BIN);
            insert t values ('Apple'), ('apple'), ('Banana'), ('banana')
            """);
        using var reader = sim.CreateCommand("select s from t order by s").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "Apple", "Banana", "apple", "banana" }, rows);
    }

    /// <summary>
    /// <c>Latin1_General_CS_AS</c> is case-sensitive, so <c>'A' = 'a'</c>
    /// returns false (column-vs-literal goes through coercibility resolution:
    /// the Implicit column wins, literal coerces to the column's collation).
    /// </summary>
    [TestMethod]
    public void Equality_CaseSensitiveColumn_DistinguishesCase()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s varchar(20) collate Latin1_General_CS_AS);
            insert t values ('Apple'), ('apple')
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s = 'Apple'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s = 'apple'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from t where s = 'APPLE'"));
    }

    /// <summary>
    /// <c>Latin1_General_BIN2</c> equality is pure codepoint: <c>'é' = 'e'</c>
    /// returns false (no accent folding), unlike the database default which
    /// folds accents.
    /// </summary>
    [TestMethod]
    public void Equality_BinaryColumn_DoesNotFoldAccents()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s nvarchar(20) collate Latin1_General_BIN2);
            insert t values (N'café')
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s = N'café'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from t where s = N'cafe'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from t where s = N'CAFÉ'"));
    }

    /// <summary>
    /// GROUP BY and DISTINCT bucket through the column's collation. A
    /// case-sensitive column produces distinct buckets per case; the
    /// default (case-insensitive) column collapses them.
    /// </summary>
    [TestMethod]
    public void GroupBy_CaseSensitiveColumn_PreservesCaseDistinctions()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table cs (v varchar(20) collate Latin1_General_CS_AS);
            insert cs values ('A'), ('a'), ('A'), ('a');
            create table ci (v varchar(20));
            insert ci values ('A'), ('a'), ('A'), ('a')
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from (select distinct v from cs) d"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from (select distinct v from ci) d"));
    }

    /// <summary>
    /// LIKE consults the operand types' collation: a CS_AS column rejects
    /// case-mismatched patterns even when the literal would coerce to the
    /// column's collation (Implicit beats CoercibleDefault).
    /// </summary>
    [TestMethod]
    public void Like_CaseSensitiveColumn_RejectsCaseMismatch()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s varchar(20) collate Latin1_General_CS_AS);
            insert t values ('Apple')
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s like 'App%'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from t where s like 'app%'"));
    }

    /// <summary>
    /// Cross-collation column-to-column comparison (both Implicit, different
    /// collations) raises Msg 468 with the verbatim wording probed against
    /// SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void Equality_CrossCollation_RaisesMsg468()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (s varchar(20) collate Latin1_General_CI_AS);
            create table b (s varchar(20) collate Latin1_General_CS_AS);
            insert a values ('x'); insert b values ('x')
            """);
        var ex = sim.AssertSqlError("select count(*) from a join b on a.s = b.s", 468);
        AreEqual((byte)9, ex.State);
        AreEqual(
            "Cannot resolve the collation conflict between \"Latin1_General_CS_AS\" and \"Latin1_General_CI_AS\" in the equal to operation.",
            ex.Message);
    }

    /// <summary>
    /// Msg 468 also fires for <c>&lt;&gt;</c>, <c>&lt;</c>, etc. — the operator
    /// name in the message reflects the actual operator, not just "equal to".
    /// </summary>
    [TestMethod]
    [DataRow("=", "equal to")]
    [DataRow("<>", "not equal to")]
    [DataRow("<", "less than")]
    [DataRow(">", "greater than")]
    public void CrossCollation_OperatorNameInMsg468(string op, string opName)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (s varchar(20) collate Latin1_General_CI_AS);
            create table b (s varchar(20) collate Latin1_General_CS_AS);
            insert a values ('x'); insert b values ('x')
            """);
        var ex = sim.AssertSqlError($"select count(*) from a, b where a.s {op} b.s", 468);
        Contains($"in the {opName} operation", ex.Message);
    }

    /// <summary>
    /// LIKE across cross-collation operands raises Msg 468 with operator
    /// name "like" (lowercase, matching probed wording).
    /// </summary>
    [TestMethod]
    public void Like_CrossCollation_RaisesMsg468()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (s varchar(20) collate Latin1_General_CI_AS);
            create table b (s varchar(20) collate Latin1_General_CS_AS);
            insert a values ('x'); insert b values ('x')
            """);
        var ex = sim.AssertSqlError("select count(*) from a, b where a.s like b.s", 468);
        Contains("in the like operation", ex.Message);
    }

    /// <summary>
    /// String concat (<c>+</c>) across cross-collation operands raises
    /// Msg 457 ("Implicit conversion ... unresolved collation conflict").
    /// Distinct error path from comparison's Msg 468.
    /// </summary>
    [TestMethod]
    public void Concat_CrossCollation_RaisesMsg457()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (s varchar(20) collate Latin1_General_CI_AS);
            create table b (s varchar(20) collate Latin1_General_CS_AS);
            insert a values ('x'); insert b values ('y')
            """);
        var ex = sim.AssertSqlError("select a.s + b.s from a, b", 457);
        AreEqual(
            "Implicit conversion of varchar value to varchar cannot be performed because the collation of the value is unresolved due to a collation conflict.",
            ex.Message);
    }

    /// <summary>
    /// Coercibility precedence: a column reference (Implicit) compared to a
    /// literal (CoercibleDefault) coerces the literal to the column's
    /// collation. No 468 raised even though the column's collation differs
    /// from the simulator's database default.
    /// </summary>
    [TestMethod]
    public void Coercibility_LiteralCoercesToColumnCollation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s varchar(20) collate Latin1_General_BIN);
            insert t values ('Apple'), ('apple')
            """);
        // No Msg 468: literal coerces to column's BIN collation. BIN compare
        // is codepoint-strict, so only the exact-case literal matches.
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s = 'Apple'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from t where s = 'APPLE'"));
    }

    /// <summary>
    /// <c>COLLATE</c> postfix on the operand overrides the column's
    /// declared collation (Explicit beats Implicit). The override applies
    /// the new collation at Explicit rank — overriding one side with the
    /// same name as the other resolves the conflict.
    /// </summary>
    [TestMethod]
    public void CollatePostfix_ResolvesCrossCollationConflict()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (s varchar(20) collate Latin1_General_CI_AS);
            create table b (s varchar(20) collate Latin1_General_CS_AS);
            insert a values ('Apple'); insert b values ('apple')
            """);
        // Force both to CI_AS via explicit COLLATE; case-insensitive match.
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from a, b where a.s = b.s collate Latin1_General_CI_AS"));
        // Force both to CS_AS; case-sensitive mismatch → 0 rows.
        AreEqual(0, sim.ExecuteScalar(
            "select count(*) from a, b where a.s collate Latin1_General_CS_AS = b.s"));
    }

    /// <summary>
    /// <c>COLLATE</c> postfix on a non-string operand raises Msg 447. The
    /// simulator surfaces this at runtime rather than at bind time (lazy-plan
    /// parity gap; same Msg + wording as real SQL Server).
    /// </summary>
    [TestMethod]
    public void CollatePostfix_OnNonString_RaisesMsg447()
        => _ = new Simulation().AssertSqlError("select 5 collate Latin1_General_CI_AS", 447);

    /// <summary>
    /// <c>COLLATE</c> postfix on a NULL operand returns NULL of the
    /// rewrapped type — no Msg 447 since the inner is NULL-of-string.
    /// </summary>
    [TestMethod]
    public void CollatePostfix_OnTypedNullString_ReturnsNullNotError()
        => IsNull(new Simulation().ExecuteScalar(
            "select cast(null as varchar(10)) collate Latin1_General_BIN"));

    /// <summary>
    /// ALTER COLUMN with no COLLATE clause preserves the column's existing
    /// declared collation — width change shouldn't reset the comparer.
    /// </summary>
    [TestMethod]
    public void AlterColumn_WithoutCollateClause_PreservesDeclaredCollation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s varchar(20) collate Latin1_General_CS_AS);
            insert t values ('Apple');
            alter table t alter column s varchar(50);
            insert t values ('apple')
            """);
        // CS_AS preserved: 'Apple' = 'Apple' true, 'Apple' = 'apple' false.
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s = 'Apple'"));
        AreEqual("Latin1_General_CS_AS", sim.ExecuteScalar(
            "select collation_name from sys.columns where name = 's'"));
    }

    /// <summary>
    /// ALTER COLUMN with an explicit COLLATE clause changes the column's
    /// pinned collation — subsequent compares use the new rules.
    /// </summary>
    [TestMethod]
    public void AlterColumn_WithCollateClause_ChangesPinnedCollation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s varchar(20) collate Latin1_General_CS_AS);
            insert t values ('Apple');
            alter table t alter column s varchar(20) collate Latin1_General_CI_AS
            """);
        // Was CS_AS, now CI_AS — case-insensitive match returns 1.
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s = 'APPLE'"));
        AreEqual("Latin1_General_CI_AS", sim.ExecuteScalar(
            "select collation_name from sys.columns where name = 's'"));
    }

    /// <summary>
    /// Hash agrees with equality under the declared collation: a
    /// case-sensitive column's DISTINCT preserves case distinctions, which
    /// requires the hash code path to also respect collation (matching the
    /// equality path).
    /// </summary>
    [TestMethod]
    public void DistinctHashContract_AgreesWithCaseSensitiveEquality()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v varchar(20) collate Latin1_General_CS_AS);
            insert t values ('Apple'), ('apple'), ('APPLE'), ('Apple')
            """);
        // CS_AS distinct: 'Apple', 'apple', 'APPLE' → 3 buckets.
        AreEqual(3, sim.ExecuteScalar("select count(*) from (select distinct v from t) d"));
    }

    /// <summary>
    /// IN-list cross-collation raises Msg 468 — InExpression flows through
    /// the same CompareValuesPromoted entry point.
    /// </summary>
    [TestMethod]
    public void InList_CrossCollation_RaisesMsg468()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table a (s varchar(20) collate Latin1_General_CI_AS);
            create table b (s varchar(20) collate Latin1_General_CS_AS);
            insert a values ('x'); insert b values ('x')
            """);
        _ = sim.AssertSqlError("select count(*) from a where s in (select b.s from b)", 468);
    }

    /// <summary>
    /// Each non-Latin1 locale collation registered in the catalog is
    /// recognized at parse time (CREATE TABLE accepts the COLLATE clause)
    /// and round-trips through <c>sys.columns.collation_name</c>.
    /// </summary>
    [TestMethod]
    [DataRow("Japanese_XJIS_140_CI_AS")]
    [DataRow("Chinese_PRC_CI_AS")]
    [DataRow("Turkish_CI_AS")]
    [DataRow("Latin1_General_CI_AS_KS_WS")]
    [DataRow("SQL_Latin1_General_CP437_CS_AS")]
    [DataRow("UNICODE_CODEPOINT")]
    public void NewCollation_AcceptedAndRoundTripsThroughSysColumns(string collationName)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t (s nvarchar(20) collate {collationName})");
        AreEqual(collationName, sim.ExecuteScalar(
            "select collation_name from sys.columns where name = 's'"));
    }

    /// <summary>
    /// Turkish collation honors the dotted/dotless i distinction: ASCII 'i'
    /// uppercases to Turkish 'İ' (dotted, U+0130), not 'I' (dotless). Under
    /// the Turkish case-fold, <c>'istanbul' = 'ISTANBUL'</c> is FALSE (the
    /// i's don't fold to each other). Same query under the database default
    /// (invariant culture) returns TRUE — demonstrating the comparer is
    /// actually wired through the Turkish culture, not silently falling
    /// back to the default.
    /// </summary>
    [TestMethod]
    public void TurkishCollation_HonorsDottedDotlessIRule()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table tr (s nvarchar(20) collate Turkish_CI_AS);
            create table dflt (s nvarchar(20));
            insert tr values (N'istanbul');
            insert dflt values (N'istanbul')
            """);
        // Turkish: i ≠ I under case-fold → 0 rows.
        AreEqual(0, sim.ExecuteScalar("select count(*) from tr where s = N'ISTANBUL'"));
        // Default (invariant CI_AS): i = I under case-fold → 1 row.
        AreEqual(1, sim.ExecuteScalar("select count(*) from dflt where s = N'ISTANBUL'"));
        // Turkish: 'istanbul' = 'istanbul' still works (same dotted-i on both sides).
        AreEqual(1, sim.ExecuteScalar("select count(*) from tr where s = N'istanbul'"));
    }

    /// <summary>
    /// Unknown collation name raises a clear NotSupportedException (it's
    /// not on the simulator's recognized whitelist). Probes confirm this
    /// is the simulator's "honest about what's modeled" stance, distinct
    /// from real SQL Server's Msg 448.
    /// </summary>
    [TestMethod]
    public void UnknownCollationName_RaisesNotSupported()
    {
        var ex = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            "create table t (s varchar(20) collate Hungarian_CI_AS)"));
        Contains("Hungarian_CI_AS", ex.Message);
    }

    /// <summary>
    /// Cross-database-default collation: when the active database has a
    /// non-default declared collation, newly created columns without an
    /// explicit COLLATE clause inherit that database default (rather than
    /// always defaulting to <c>SQL_Latin1_General_CP1_CI_AS</c>).
    /// </summary>
    [TestMethod]
    public void NewColumn_InheritsDatabaseDefaultCollation()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated collate Latin1_General_CS_AS");
        _ = sim.ExecuteNonQuery("create table t (s varchar(20))");
        AreEqual("Latin1_General_CS_AS", sim.ExecuteScalar(
            "select collation_name from sys.columns where name = 's'"));
        // Behavior matches metadata: column is now case-sensitive.
        _ = sim.ExecuteNonQuery("insert t values ('Apple')");
        AreEqual(0, sim.ExecuteScalar("select count(*) from t where s = 'apple'"));
    }

    /// <summary>
    /// Temp table inherits the active database's default collation —
    /// solves the EF Core join footgun where a BACPAC-loaded non-default
    /// database would otherwise cross-collation-conflict against #temp.
    /// </summary>
    [TestMethod]
    public void TempTable_InheritsActiveDatabaseDefaultCollation()
    {
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("alter database simulated collate Latin1_General_CS_AS").ExecuteNonQuery();
        _ = conn.CreateCommand("create table u (s varchar(20)); insert u values ('Apple')").ExecuteNonQuery();
        _ = conn.CreateCommand("create table #t (s varchar(20)); insert #t values ('Apple')").ExecuteNonQuery();
        // Both inherit CS_AS — join works without Msg 468.
        var n = conn.CreateCommand("select count(*) from u join #t on u.s = #t.s").ExecuteScalar();
        AreEqual(1, n);
    }

    /// <summary>
    /// LIKE consults the column's collation's <c>CaseSensitive</c> flag —
    /// covers the <c>CultureCollation.CaseSensitive</c> property path the
    /// locale collations expose. The locale set is all case-insensitive
    /// (Japanese / Chinese / Turkish CI_AS), so LIKE matches a
    /// case-mismatched pattern.
    /// </summary>
    [TestMethod]
    public void Like_ThroughLocaleCollation_HonorsCaseInsensitivity()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s nvarchar(20) collate Chinese_PRC_CI_AS);
            insert t values (N'Hello')
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s like N'HEL%'"));
    }

    /// <summary>
    /// Exercises the Compare path through each non-default collation via
    /// ORDER BY — picks up the comparer wiring that wasn't reachable when
    /// columns silently fell back to the database default. Each collation's
    /// per-name sort parity with real SQL Server isn't asserted (deferred
    /// to per-name probes); the test only proves the comparer runs without
    /// throwing.
    /// </summary>
    [TestMethod]
    [DataRow("Latin1_General_100_CI_AS")]
    [DataRow("Latin1_General_CI_AS")]
    [DataRow("Latin1_General_CS_AS")]
    [DataRow("Latin1_General_BIN")]
    [DataRow("Latin1_General_BIN2")]
    [DataRow("Latin1_General_CI_AS_KS_WS")]
    [DataRow("SQL_Latin1_General_CP437_CS_AS")]
    [DataRow("UNICODE_CODEPOINT")]
    [DataRow("Japanese_XJIS_140_CI_AS")]
    [DataRow("Chinese_PRC_CI_AS")]
    [DataRow("Turkish_CI_AS")]
    public void OrderBy_AnyDeclaredCollation_ExecutesWithoutError(string collationName)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table t (s nvarchar(20) collate {collationName});
            insert t values (N'banana'), (N'apple'), (N'cherry')
            """);
        using var reader = sim.CreateCommand("select s from t order by s").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        HasCount(3, rows);
    }

    /// <summary>
    /// Same coverage motivation but for the hash path (DISTINCT bucketing).
    /// Three identical rows under any case-insensitive collation should
    /// collapse to one bucket; case-sensitive / binary collations preserve
    /// distinct case variants.
    /// </summary>
    [TestMethod]
    [DataRow("Latin1_General_100_CI_AS", 1)]
    [DataRow("Latin1_General_CI_AS", 1)]
    [DataRow("Latin1_General_CS_AS", 3)]
    [DataRow("Latin1_General_BIN", 3)]
    [DataRow("Latin1_General_BIN2", 3)]
    [DataRow("Latin1_General_CI_AS_KS_WS", 1)]
    [DataRow("SQL_Latin1_General_CP437_CS_AS", 3)]
    [DataRow("UNICODE_CODEPOINT", 3)]
    [DataRow("Japanese_XJIS_140_CI_AS", 1)]
    [DataRow("Chinese_PRC_CI_AS", 1)]
    [DataRow("Turkish_CI_AS", 1)]
    public void Distinct_ExercisesHashContractPerCollation(string collationName, int expectedDistinctCount)
    {
        var sim = new Simulation();
        // Pure-ASCII inputs avoid the Turkish-i edge case while still
        // exercising case-fold semantics for the CI collations. Three rows
        // differ only in case, so CI collapses to 1 bucket and CS / binary
        // keeps all 3.
        _ = sim.ExecuteNonQuery($"""
            create table t (s nvarchar(20) collate {collationName});
            insert t values (N'Apple'), (N'apple'), (N'APPLE')
            """);
        AreEqual(expectedDistinctCount, sim.ExecuteScalar(
            "select count(*) from (select distinct s from t) d"));
    }

    /// <summary>
    /// <c>varchar Latin1_General_BIN2</c> sorts by CP1252 byte order — which
    /// diverges from Unicode codepoint order in the 0x80-0x9F window. The
    /// euro sign U+20AC encodes to CP1252 byte 0x80, so it sorts BEFORE
    /// NBSP (U+00A0, CP1252 byte 0xA0). Probe-confirmed against SQL Server
    /// 2025 — order matches byte order across the test fixture.
    /// </summary>
    [TestMethod]
    public void OrderBy_VarcharBin2_UsesCp1252ByteOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s varchar(2) collate Latin1_General_BIN2);
            insert t values (nchar(161)), (nchar(8364)), (nchar(402)), (nchar(376)), (nchar(160))
            """);
        using var reader = sim.CreateCommand("select ascii(s) from t order by s").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 0x80, 0x83, 0x9F, 0xA0, 0xA1 }, rows);
    }

    /// <summary>
    /// <c>nvarchar Latin1_General_BIN2</c> sorts by UTF-16 code unit
    /// (16-bit big-endian value), <em>not</em> code point. The Microsoft
    /// docs say "BIN2 = code point sort" but the empirical behavior on
    /// SQL Server 2025 is code-unit — probe-confirmed with the
    /// supplementary char U+1F600 (emoji, encoded as surrogate pair
    /// D83D DE00) sorting BEFORE U+E000, which can only happen if the
    /// compare is code-unit (D83D &lt; E000) and not code-point
    /// (U+1F600 &gt; U+E000). For the BMP-only inputs here the two
    /// orderings happen to agree, so the assertion is the same either
    /// way — what's exercised is that nvarchar BIN2 sorts by something
    /// other than the varchar BIN2 CP1252 byte order. See
    /// <c>docs/claude/collations.md</c> "Microsoft-docs-vs-real-behavior
    /// gap" for the contrast and citations.
    /// </summary>
    [TestMethod]
    public void OrderBy_NvarcharBin2_UsesCodeUnitOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s nvarchar(2) collate Latin1_General_BIN2);
            insert t values (nchar(161)), (nchar(8364)), (nchar(402)), (nchar(376)), (nchar(160))
            """);
        using var reader = sim.CreateCommand("select unicode(s) from t order by s").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 160, 161, 376, 402, 8364 }, rows);
    }

    /// <summary>
    /// <c>nvarchar Latin1_General_BIN2</c> with a supplementary character
    /// (U+1F600, 😀, encoded as surrogate pair D83D DE00) sorts BEFORE
    /// the high BMP char U+E000 because D83D &lt; E000 as 16-bit code
    /// units. Under a hypothetical code-point compare (which the docs
    /// claim) the supplementary U+1F600 would sort AFTER U+E000.
    /// Probe-confirmed against SQL Server 2025. This test catches any
    /// regression that would introduce "code-point fixing" logic into
    /// the binary collation body.
    /// </summary>
    [TestMethod]
    public void OrderBy_NvarcharBin2_SupplementaryCharSortsByCodeUnit()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int identity primary key, s nvarchar(4) collate Latin1_General_BIN2);
            insert t (s) values
                (nchar(57344)),                      -- U+E000
                (nchar(55357) + nchar(56832))        -- surrogate pair for U+1F600 (😀)
            """);
        // ORDER BY s should give: emoji (D83D…) before U+E000.
        // We project the first code unit's UNICODE() value to make the
        // assertion source-encoding-independent.
        using var reader = sim.CreateCommand("select unicode(s) from t order by s").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add(reader.GetInt32(0));
        // 55357 = 0xD83D (high surrogate of emoji, sorts first).
        // 57344 = 0xE000 (sorts second).
        CollectionAssert.AreEqual(new[] { 55357, 57344 }, rows);
    }

    /// <summary>
    /// <c>varchar Latin1_General_BIN</c> uses the same CP1252 byte body as
    /// BIN2 at the simulator's value layer — the BIN-vs-BIN2 code-page-
    /// prefix asymmetry isn't observable through a single-codepage value
    /// stack. Confirms the substitution wires up symmetrically for BIN.
    /// </summary>
    [TestMethod]
    public void OrderBy_VarcharBin_UsesCp1252ByteOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s varchar(2) collate Latin1_General_BIN);
            insert t values (nchar(8364)), (nchar(160)), (nchar(402))
            """);
        using var reader = sim.CreateCommand("select ascii(s) from t order by s").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 0x80, 0x83, 0xA0 }, rows);
    }

    /// <summary>
    /// DISTINCT hash on a varchar BIN2 column agrees with the CP1252
    /// byte-equality contract — duplicates collapse and CP1252-distinct
    /// values stay in separate buckets. Covers the GetHashCode path on
    /// <c>Cp1252BinaryCollation</c>.
    /// </summary>
    [TestMethod]
    public void Distinct_VarcharBin2_HashRespectsCp1252Bytes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s varchar(2) collate Latin1_General_BIN2);
            insert t values (N'€'), (N'€'), (N'ƒ')
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from (select distinct s from t) d"));
    }

    /// <summary>
    /// <c>char(N) Latin1_General_BIN2</c> picks up the CP1252 substitution
    /// through the same <c>CharSqlType.WithCollation</c> hook as varchar.
    /// Same input characters produce the same byte-order ranking.
    /// </summary>
    [TestMethod]
    public void OrderBy_CharBin2_UsesCp1252ByteOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s char(2) collate Latin1_General_BIN2);
            insert t values (nchar(8364)), (nchar(160)), (nchar(402))
            """);
        using var reader = sim.CreateCommand("select ascii(s) from t order by s").ExecuteReader();
        var rows = new List<int>();
        while (reader.Read())
            rows.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 0x80, 0x83, 0xA0 }, rows);
    }

    /// <summary>
    /// Pre-SQL-Server-2005 <c>Latin1_General_BIN</c> on nvarchar has an
    /// asymmetric sort rule: position 0 is a 16-bit code-unit compare
    /// (matches BIN2), but position 1+ combines surrogate pairs into
    /// 32-bit scalars and code-point-compares. So <c>'Z' + N'😀'</c>
    /// (after the shared <c>'Z'</c>, the supplementary U+1F600 vs the
    /// high BMP U+E000) sorts as <c>0x1F600 &gt; 0xE000</c> — emoji-row
    /// LAST. Under BIN2 with the same data the comparison is code-unit
    /// (0xD83D &lt; 0xE000), so emoji-row sorts FIRST — see
    /// <see cref="OrderBy_NvarcharBin2_Position1Plus_KeepsCodeUnitOrder"/>
    /// for the companion contrast.
    /// </summary>
    [TestMethod]
    public void OrderBy_NvarcharBin_Position1Plus_UsesCodePointOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int identity primary key, s nvarchar(4) collate Latin1_General_BIN);
            insert t (s) values
                (N'Z' + nchar(55357) + nchar(56832)),  -- 'Z' + emoji U+1F600
                (N'Z' + nchar(57344))                  -- 'Z' + U+E000
            """);
        using var reader = sim.CreateCommand("select id from t order by s").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        // 'Z'+U+E000 (id=2) sorts first; 'Z'+emoji (id=1) sorts second.
        CollectionAssert.AreEqual(new[] { 2, 1 }, ids);
    }

    /// <summary>
    /// Contrast to <see cref="OrderBy_NvarcharBin_Position1Plus_UsesCodePointOrder"/>:
    /// the same data under BIN2 sorts the opposite way because BIN2 is
    /// code-unit throughout (0xD83D &lt; 0xE000 at position 1).
    /// </summary>
    [TestMethod]
    public void OrderBy_NvarcharBin2_Position1Plus_KeepsCodeUnitOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int identity primary key, s nvarchar(4) collate Latin1_General_BIN2);
            insert t (s) values
                (N'Z' + nchar(55357) + nchar(56832)),
                (N'Z' + nchar(57344))
            """);
        using var reader = sim.CreateCommand("select id from t order by s").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        // 'Z'+emoji (id=1) sorts first under code-unit; 'Z'+U+E000 (id=2) second.
        CollectionAssert.AreEqual(new[] { 1, 2 }, ids);
    }

    /// <summary>
    /// <c>Latin1_General_CI_AS_KS_WS</c> with <em>KS</em> (kanatype-
    /// sensitive) and <em>WS</em> (width-sensitive) flips active: the
    /// full-width katakana ア, hiragana あ, and half-width katakana ｱ
    /// all compare distinct. Under plain CI_AS (no KS/WS) the same trio
    /// folds together. Probe-confirmed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void Equality_Latin1KsWs_DistinguishesKanaTypeAndWidth()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s nvarchar(2) collate Latin1_General_CI_AS_KS_WS);
            insert t values (N'ア'), (N'あ'), (N'ｱ')
            """);
        // Each value is distinct under KS+WS — DISTINCT preserves all three.
        AreEqual(3, sim.ExecuteScalar("select count(*) from (select distinct s from t) d"));
        // Equality also distinguishes: each value only matches itself.
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s = N'ア'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s = N'あ'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where s = N'ｱ'"));
    }

    /// <summary>
    /// Contrast to <see cref="Equality_Latin1KsWs_DistinguishesKanaTypeAndWidth"/>:
    /// under a plain CI_AS collation (no KS/WS), the same kana trio
    /// folds together — kanatype-insensitive + width-insensitive are
    /// the SQL Server defaults for "*_CI_AS" / "*_CS_AS" variants
    /// without explicit KS/WS markers.
    /// </summary>
    [TestMethod]
    public void Equality_PlainCiAs_FoldsKanaTypeAndWidth()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (s nvarchar(2) collate Latin1_General_100_CI_AS);
            insert t values (N'ア'), (N'あ'), (N'ｱ')
            """);
        // All three fold together — DISTINCT collapses to one bucket.
        AreEqual(1, sim.ExecuteScalar("select count(*) from (select distinct s from t) d"));
        // Equality picks up all three regardless of which kana variant is on the right side.
        AreEqual(3, sim.ExecuteScalar("select count(*) from t where s = N'ア'"));
    }

    private static void IsNull(object? value) => Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNull(value is DBNull ? null : value);
}
