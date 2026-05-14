using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Table hints (<c>WITH (NOLOCK [, …])</c>) on FROM sources / JOIN-RHS /
/// UPDATE / DELETE targets, plus statement-level <c>OPTION (…)</c> hints —
/// parsed and discarded for grammar compatibility (no locking / isolation
/// modeling). Probe-confirmed rejection wording: unknown table-hint → Msg
/// 321 verbatim; unknown OPTION hint → generic Msg 102 (matches probe
/// surprise that the OPTION clause has no dedicated unknown-hint code).
/// </summary>
[TestClass]
public sealed class QueryHintTests
{
    [TestMethod]
    public void Select_WithNoLock_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (nolock)
            """));

    [TestMethod]
    public void Select_WithMultipleHints_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (nolock, holdlock, readpast)
            """));

    [TestMethod]
    public void Select_LegacyParenForm_NoWith_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t (nolock)
            """));

    [TestMethod]
    public void Select_HintAfterAlias_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t as x with (nolock)
            """));

    [TestMethod]
    public void Select_HintAfterBareAlias_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t x with (nolock)
            """));

    [TestMethod]
    public void Select_IndexHint_NumericArg_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (index(0))
            """));

    /// <summary>
    /// Simulator doesn't model indexes, so the named-index reference parses
    /// and is discarded. Real SQL Server raises Msg 308 on a wrong name;
    /// the parse-and-ignore stance is the consistent posture for hints.
    /// </summary>
    [TestMethod]
    public void Select_IndexHint_NamedArg_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (index(IX_does_not_exist))
            """));

    [TestMethod]
    public void Select_ForceSeek_BareForm_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (forceseek)
            """));

    [TestMethod]
    public void Select_SpatialWindowMaxCells_EqForm_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t with (SPATIAL_WINDOW_MAX_CELLS = 1024)
            """));

    [TestMethod]
    public void Select_UnknownHint_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            select * from t with (banana)
            """, 321, "\"banana\" is not a recognized table hints option.");

    [TestMethod]
    public void Select_UnknownHint_AmongValidOnes_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            select * from t with (nolock, foobar, readpast)
            """, 321, "\"foobar\" is not a recognized table hints option.");

    [TestMethod]
    public void Join_RhsHint_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*)
            from t a
            inner join t b with (nolock) on a.id = b.id
            """));

    [TestMethod]
    public void Join_LegacyParenRhs_NoWith_ReturnsRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*)
            from t a
            left join t b (nolock) on a.id = b.id
            """));

    [TestMethod]
    public void Update_WithHint_AppliesChange()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100), (2, 200);
            update t with (rowlock) set v = v + 1
            """);
        AreEqual(101, sim.ExecuteScalar("select v from t where id = 1"));
        AreEqual(201, sim.ExecuteScalar("select v from t where id = 2"));
    }

    [TestMethod]
    public void Update_LegacyParenForm_AppliesChange()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100);
            update t (tablock) set v = 999
            """);
        AreEqual(999, sim.ExecuteScalar("select v from t where id = 1"));
    }

    [TestMethod]
    public void Update_UnknownHint_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key, v int);
            insert t values (1, 100);
            update t with (banana) set v = 999
            """, 321, "\"banana\" is not a recognized table hints option.");

    [TestMethod]
    public void Delete_BareForm_WithHint_RemovesRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            delete from t with (tablock) where id = 2
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Delete_AliasForm_WithHint_RemovesRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            delete a from t a with (tablock) where a.id = 2
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Delete_UnknownHint_RaisesMsg321()
        => new Simulation().AssertSqlError("""
            create table t (id int primary key);
            delete from t with (banana)
            """, 321, "\"banana\" is not a recognized table hints option.");

    [TestMethod]
    public void Option_Recompile_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (recompile)"));

    [TestMethod]
    public void Option_MaxDop_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (maxdop 4)"));

    [TestMethod]
    public void Option_Fast_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (fast 100)"));

    [TestMethod]
    public void Option_LoopJoin_AcceptedAsNoop()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t a inner join t b on a.id = b.id option (loop join)
            """));

    [TestMethod]
    public void Option_HashJoin_AcceptedAsNoop()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int primary key);
            insert t values (1), (2), (3);
            select count(*) from t a inner join t b on a.id = b.id option (hash join)
            """));

    [TestMethod]
    public void Option_ForceOrder_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (force order)"));

    [TestMethod]
    public void Option_KeepFixedPlan_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (keepfixed plan)"));

    [TestMethod]
    public void Option_OptimizeForUnknown_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (optimize for unknown)"));

    [TestMethod]
    public void Option_UseHint_QuotedArg_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select 1 option (use hint('FORCE_LEGACY_CARDINALITY_ESTIMATION'))
            """));

    [TestMethod]
    public void Option_MultipleHints_AcceptedAsNoop()
        => AreEqual(1, new Simulation().ExecuteScalar("select 1 option (recompile, maxdop 4)"));

    [TestMethod]
    public void Option_UnknownHint_RaisesMsg102()
        => new Simulation().AssertSqlError("select 1 option (banana)", 102);

    [TestMethod]
    public void Option_MaxRecursionStillWorks()
    {
        // Closed-list parser preserves MAXRECURSION's runtime effect — the
        // CTE recursion limit override path is the only OPTION hint with
        // observable behavior.
        var ex = new Simulation().AssertSqlError("""
            with c as (
                select 1 as n
                union all
                select n + 1 from c where n < 200
            )
            select count(*) from c option (maxrecursion 50)
            """, 530);
        Contains("50", ex.Message);
    }

    [TestMethod]
    public void Select_BareIdentifier_NotHint_ParsesAsAlias()
    {
        // `FROM t nolock` (no WITH, no parens) is alias parsing, not a
        // deprecated hint shape — nolock / readpast / etc. aren't reserved
        // keywords, so they're valid bare aliases. Same path as `FROM t a`.
        // Real SQL Server treats this identically; the simulator's bare-alias
        // branch in ConsumeOptionalAlias matches by Name token, which
        // UnquotedString satisfies.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table h1 (id int primary key, v int);
            insert h1 values (1, 100), (2, 200)
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from h1 nolock"));
        AreEqual(100, sim.ExecuteScalar("select nolock.v from h1 nolock where nolock.id = 1"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from h1 banana"));
        AreEqual(100, sim.ExecuteScalar("select banana.v from h1 banana where banana.id = 1"));
    }

    [TestMethod]
    public void Update_BareAlias_HintAfter_AppliesChange()
    {
        // UPDATE target-hint position is between target name and SET, not
        // between alias and SET — the alias form `UPDATE t a WITH (...)`
        // isn't probe-confirmed and not modeled here. This test pins the
        // unambiguous shape.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v int);
            insert t values (1, 100);
            update t with (tablock) set v = 999
            """);
        AreEqual(999, sim.ExecuteScalar("select v from t where id = 1"));
    }
}
