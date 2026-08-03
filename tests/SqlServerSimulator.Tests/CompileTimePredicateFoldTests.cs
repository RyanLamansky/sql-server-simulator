using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The predicate SQL Server settles while compiling, and the operand it
/// therefore never evaluates — nor even checks against the GROUP BY. Two rules
/// carry it: a comparison against a NULL constant is UNKNOWN whatever the other
/// side holds, and an <c>AND</c> / <c>OR</c> chain carrying an absorbing written
/// constant is that constant. A filter position adds one more, since it keeps
/// only the rows a predicate answers TRUE for.
/// <para>
/// Every expectation probe-confirmed against SQL Server 2025 (17.0.4065.4) on
/// 2026-08-03, one batch per claim. Each case is the over-permissive direction
/// inverted: the simulator used to raise Msg 8115 / 8134 / 8121 where real
/// answers rows, so a working generated query broke here.
/// </para>
/// </summary>
[TestClass]
public sealed class CompileTimePredicateFoldTests
{
    /// <summary>
    /// Three rows whose <c>a</c> makes both of the arithmetic errors reachable:
    /// <c>a * 2000000000</c> overflows <c>int</c> (Msg 8115) and <c>a / 0</c>
    /// divides by zero (Msg 8134), so an operand that runs is unmistakable.
    /// </summary>
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, s varchar(20));
            insert t values (2, 20, 'x'), (3, 30, 'y'), (0, 0, 'z')
            """);
        return simulation;
    }

    private static int RowCount(Simulation simulation, string sql) =>
        simulation.ExecuteScalar<int>($"select count(*) from ({sql}) q(c1)");

    // === The other side of a NULL comparison never runs ===

    [TestMethod]
    [DataRow("select b from t where null > a * 2000000000")]
    [DataRow("select b from t where a * 2000000000 < null")]
    [DataRow("select b from t where null = a * 2000000000")]
    [DataRow("select b from t where null <> a * 2000000000")]
    [DataRow("select b from t where null >= a * 2000000000")]
    [DataRow("select b from t where null <= a * 2000000000")]
    [DataRow("select b from t where null != a * 2000000000")]
    [DataRow("select b from t where null > a / 0")]
    [DataRow("select b from t where null > a + cast(s as int)")]
    [DataRow("select b from t where not (null > a * 2000000000)")]
    // The NULL constant, seen through the wrappers real folds through.
    [DataRow("select b from t where (null) > a * 2000000000")]
    [DataRow("select b from t where cast(null as int) > a * 2000000000")]
    [DataRow("select b from t where convert(int, null) > a * 2000000000")]
    [DataRow("select b from t where -cast(null as int) > a * 2000000000")]
    // The IN / BETWEEN shapes that reduce to a comparison against NULL.
    [DataRow("select b from t where null between a / 0 and a * 2000000000")]
    [DataRow("select b from t where a * 2000000000 in (null)")]
    [DataRow("select b from t where a * 2000000000 not in (null)")]
    [DataRow("select b from t where null in (a / 0, 1)")]
    // The subquery a folded comparison names never executes either.
    [DataRow("select b from t where null > (select max(a * 2000000000) from t)")]
    [DataRow("select a from t where exists (select 1 from t t2 where null > t2.a * 2000000000)")]
    public void NullComparison_DoesNotEvaluateOtherSide(string sql) =>
        AreEqual(0, RowCount(Seeded(), sql));

    [TestMethod]
    public void NullComparisonInSelectList_DoesNotEvaluateOtherSide() =>
        AreEqual(0, Seeded().ExecuteScalar<int>(
            "select count(*) from t where (select 1 where null > a * 2000000000) is not null"));

    [TestMethod]
    public void NullComparisonInJoinOn_LeavesOuterSideNullExtended() =>
        AreEqual(3, RowCount(Seeded(), "select t1.b from t t1 left join t t2 on null > t2.a * 2000000000"));

    [TestMethod]
    public void NullComparisonInJoinOn_MatchesNoRow() =>
        AreEqual(0, RowCount(Seeded(), "select t1.b from t t1 join t t2 on null > t2.a * 2000000000"));

    [TestMethod]
    [DataRow("delete t where null > a * 2000000000")]
    [DataRow("update t set b = b where null > a * 2000000000")]
    public void NullComparisonInDmlWhere_DoesNotEvaluateOtherSide(string sql) =>
        AreEqual(0, Seeded().ExecuteNonQuery(sql));

    /// <summary>
    /// A CHECK constraint passes UNKNOWN, so the folded comparison admits the
    /// row rather than raising the operand's Msg 8115 (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void NullComparisonInCheckConstraint_AdmitsRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(
            "create table ck (x int, constraint c1 check (null > x * 2000000000)); insert ck values (2)");
        AreEqual(1, simulation.ExecuteScalar<int>("select count(*) from ck"));
    }

    // === Real evaluates these, so the simulator must too ===

    [TestMethod]
    // Not a NULL constant: real folds neither an arithmetic NULL nor NULLIF's,
    // and raises the other side's error.
    [DataRow("select b from t where null + 1 > a * 2000000000", 8115)]
    [DataRow("select b from t where cast(null as int) + 1 > a * 2000000000", 8115)]
    [DataRow("select b from t where nullif(1, 1) > a * 2000000000", 8115)]
    // A NULL-valued variable is not a NULL constant — its value isn't known
    // while compiling.
    [DataRow("declare @v int = null; select b from t where @v > a * 2000000000", 8115)]
    // LIKE takes no such fold in either operand position.
    [DataRow("select b from t where null like cast(a / 0 as varchar(10))", 8134)]
    [DataRow("select b from t where cast(a * 2000000000 as varchar(20)) like null", 8115)]
    // One non-NULL element leaves an equality real evaluates.
    [DataRow("select b from t where a * 2000000000 in (null, 1)", 8115)]
    // IS NULL resolves UNKNOWN rather than propagating it, so nothing folds.
    [DataRow("select b from t where null is null and a * 2000000000 > 1", 8115)]
    // A constant TRUE absorbs nothing.
    [DataRow("select b from t where 1 = 1 and a * 2000000000 > 1", 8115)]
    [DataRow("select b from t where 1 = 0 or a * 2000000000 > 1", 8115)]
    public void NotFolded_StillRaisesTheOperandsError(string sql, int errorNumber) =>
        _ = Seeded().AssertSqlError(sql, errorNumber);

    /// <summary>
    /// Name resolution runs ahead of the fold, so an unknown column inside the
    /// dropped operand still reports (probe-confirmed in both positions).
    /// </summary>
    [TestMethod]
    [DataRow("select b from t where null > zzz")]
    [DataRow("select b from t where 1 = 0 and zzz > 1")]
    [DataRow("select t1.a from t t1 join t t2 on null > t2.zzz")]
    public void FoldedOperand_StillResolvesNames(string sql) =>
        _ = Seeded().AssertSqlError(sql, 207);

    // === An absorbing constant collapses the chain, wherever it sits ===

    [TestMethod]
    [DataRow("select b from t where 1 = 0 and a * 2000000000 > 1", 0)]
    [DataRow("select b from t where a * 2000000000 > 1 and 1 = 0", 0)]
    [DataRow("select b from t where a / 0 > 1 and 1 = 0", 0)]
    [DataRow("select b from t where null = null and a * 2000000000 > 1", 0)]
    [DataRow("select b from t where 'a' = 'b' and a * 2000000000 > 1", 0)]
    [DataRow("select b from t where abs(-1) = 0 and a * 2000000000 > 1", 0)]
    [DataRow("select b from t where not (1 = 1) and a * 2000000000 > 1", 0)]
    [DataRow("select b from t where null is not null and a * 2000000000 > 1", 0)]
    // A fold that raises leaves the chain standing for runtime, but the
    // absorbing constant beside it still collapses the whole thing.
    [DataRow("select b from t where 1 / 0 = 1 and 1 = 0", 0)]
    // OR's absorbing constant is TRUE, in either position.
    [DataRow("select b from t where 1 = 1 or a * 2000000000 > 1", 3)]
    [DataRow("select b from t where a * 2000000000 > 1 or 1 = 1", 3)]
    // The collapse holds under NOT and inside a nested chain.
    [DataRow("select b from t where not (1 = 0 and a * 2000000000 > 1)", 3)]
    [DataRow("select b from t where (1 = 0 and a * 2000000000 > 1) or a = 2", 1)]
    public void AbsorbingConstant_CollapsesTheChain(string sql, int expectedRows) =>
        AreEqual(expectedRows, RowCount(Seeded(), sql));

    /// <summary>
    /// Context-free, unlike the filter-only rule: a CHECK constraint rejects
    /// the row on the folded FALSE instead of raising the dropped operand's
    /// Msg 8134 (probe-confirmed — real reports Msg 547).
    /// </summary>
    [TestMethod]
    public void AbsorbingConstantInCheckConstraint_RejectsRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table ck (x int, constraint c1 check (1 = 0 and x / 0 = 1))");
        _ = simulation.AssertSqlError("insert ck values (10)", 547);
    }

    [TestMethod]
    public void AbsorbingConstantInCaseWhen_TakesElseWithoutEvaluating() =>
        AreEqual(3, Seeded().ExecuteScalar<int>(
            "select count(*) from t where case when 1 = 0 and a * 2000000000 > 1 then 1 else 0 end = 0"));

    // === A filter keeps only TRUE, so a never-TRUE predicate settles it ===

    [TestMethod]
    [DataRow("select b from t where null > a and a * 2000000000 > 1")]
    [DataRow("select b from t where a * 2000000000 > 1 and null > 1")]
    [DataRow("select b from t where null > a * 2000000000 and 1 / 0 = 1")]
    [DataRow("select b from t where a * 2000000000 between null and 5")]
    [DataRow("select b from t where a * 2000000000 between 5 and null")]
    [DataRow("select b from t where b between null and a / 0")]
    public void NeverTrueFilter_DoesNotEvaluateTheRest(string sql) =>
        AreEqual(0, RowCount(Seeded(), sql));

    /// <summary>
    /// A NULL-constant <em>bound</em> keeps its exact three-valued meaning
    /// outside a filter: real answers 'T' for a row the surviving half puts out
    /// of range, and rejects x = 10 from <c>CHECK (x BETWEEN NULL AND 5)</c>.
    /// So the never-TRUE reading stays at the filter sites.
    /// </summary>
    [TestMethod]
    public void NullBetweenBound_KeepsThreeValuedMeaningOutsideAFilter()
    {
        var simulation = Seeded();
        AreEqual(2, simulation.ExecuteScalar<int>(
            "select count(*) from t where case when a not between null and 1 then 'T' else 'F' end = 'T'"));
        _ = simulation.ExecuteNonQuery("create table ck (x int, constraint c1 check (x between null and 5))");
        _ = simulation.AssertSqlError("insert ck values (10)", 547);
        AreEqual(1, simulation.ExecuteNonQuery("insert ck values (1)"));
    }

    // === The GROUP BY containment pass runs on the folded tree ===

    [TestMethod]
    [DataRow("select a from t group by a having null <> b")]
    [DataRow("select a from t group by a having null > b")]
    [DataRow("select a from t group by a having null <> b and a > 1")]
    [DataRow("select count(*) from t group by a having null > b")]
    [DataRow("select a from t group by a having 1 = 0 and b > 1")]
    [DataRow("select a from t group by a having b > 1 and 1 = 0")]
    [DataRow("select a from t group by a having not (1 = 0 and b > 1)")]
    [DataRow("select a from t where 1 = 1 group by a having null <> b")]
    public void FoldedHavingConjunct_LeavesItsColumnOutOfTheGroupByCheck(string sql) =>
        _ = Seeded().ExecuteScalar<int>($"select count(*) from ({sql}) q(c1)");

    /// <summary>
    /// The filter-only reading doesn't take the sibling out of the tree, so an
    /// ungrouped column beside a folded conjunct still reports Msg 8121 —
    /// which is what separates the never-TRUE rule from the absorbing one
    /// (probe-confirmed: real reports it for the AND, not for <c>1 = 0 AND</c>).
    /// </summary>
    [TestMethod]
    [DataRow("select a from t group by a having null <> b and b > 1")]
    [DataRow("select a from t group by a having null <> b or b > 1")]
    [DataRow("select a from t group by a having count(*) > null and b > 1")]
    [DataRow("select a from t group by a having b > 1 and null > 1")]
    // A WHERE that folds doesn't excuse the HAVING either.
    [DataRow("select a from t where null > a * 2000000000 group by a having b > 1")]
    public void UngroupedColumnBesideAFoldedConjunct_StillRaisesMsg8121(string sql)
    {
        var ex = Seeded().AssertSqlError(sql, 8121);
        AreEqual(
            "Column 't.b' is invalid in the HAVING clause because it is not contained in either an aggregate function or the GROUP BY clause.",
            ex.Message);
    }

    [TestMethod]
    public void FoldedHavingClause_LeavesTheSelectListCheckStanding() =>
        _ = Seeded().AssertSqlError("select b from t group by a having null <> b", 8120);

    // === BETWEEN evaluates its bounds the way real does ===

    [TestMethod]
    [DataRow("select b from t where b between 99999 and a / 0", 0)]
    [DataRow("select b from t where not b between 99999 and a / 0", 3)]
    public void BetweenStopsOnceTheLowerHalfIsFalse(string sql, int expectedRows) =>
        AreEqual(expectedRows, RowCount(Seeded(), sql));

    [TestMethod]
    // The lower half runs first, so its error is reported however the range
    // would have come out.
    [DataRow("select b from t where b between a / 0 and 100")]
    [DataRow("select b from t where not b between a / 0 and 100")]
    // A lower half that passes (or is UNKNOWN) still needs the upper one.
    [DataRow("select b from t where b between 0 and a / 0")]
    [DataRow("select b from t where not b between 0 and a / 0")]
    [DataRow("select b from t where a / 0 between 1 and 2")]
    public void BetweenStillRaisesTheBoundItHasToEvaluate(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8134);
}
