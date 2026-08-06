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

    /// <summary>
    /// Counts a statement's rows without wrapping it in a derived table, for
    /// the cases that carry an <c>ORDER BY</c> with no <c>TOP</c> / <c>OFFSET</c>
    /// — Msg 1033 refuses those inside a derived table, on real as here, so
    /// <see cref="RowCount"/>'s wrapper would measure the wrapper's own
    /// rejection instead of the statement.
    /// </summary>
    private static int UnwrappedRowCount(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var rows = 0;
        while (reader.Read())
            rows++;
        return rows;
    }

    /// <summary>
    /// <c>NOT</c> asks its operand whether it can ever be FALSE, and an
    /// <c>OR</c> answers from its own operands — so <c>NOT (x OR NULL = 1)</c>
    /// keeps nothing, since the OR is never FALSE and the NOT is therefore
    /// never TRUE. Stacked <c>NOT</c>s ask through each other. Probed against
    /// SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("select a from t where not (a = 2 or b = 30)", 1)]
    [DataRow("select a from t where not not (a = 2)", 1)]
    [DataRow("select a from t where not (a = 2 or null = 1)", 0)]
    [DataRow("select a from t where not (null = 1 or null = 2)", 0)]
    [DataRow("select a from t where not not (a = 2 or null = 1)", 1)]
    public void NotOverOr_AsksWhetherTheOperandCanBeFalse(string sql, int expected)
        => AreEqual(expected, RowCount(Seeded(), sql));

    /// <summary>
    /// A never-true filter over a derived table is still offered to the
    /// pushdown walk, which reads whether the predicate is a written constant
    /// before trying to rebind it against the inner query.
    /// </summary>
    [TestMethod]
    public void NeverTrueFilterOverADerivedTable_KeepsNothing()
        => AreEqual(0, Seeded().ExecuteScalar<int>(
            "select count(*) from (select a, b from t where b > 0) d where null > 1"));

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

    // === Two NULL bounds make the whole range UNKNOWN, in every context ===

    [TestMethod]
    [DataRow("select b from t where a / 0 between null and null")]
    [DataRow("select b from t where not a / 0 between null and null")]
    [DataRow("select b from t where a / 0 not between null and null")]
    [DataRow("select b from t where not a / 0 not between null and null")]
    [DataRow("select b from t where a * 2000000000 between (null) and cast(null as int)")]
    [DataRow("select b from t where a / 0 between -cast(null as int) and convert(int, null)")]
    public void BothBetweenBoundsNull_DoesNotEvaluateTheSubject(string sql) =>
        AreEqual(0, RowCount(Seeded(), sql));

    /// <summary>
    /// UNKNOWN whatever the negation, so the fold is context-free the way the
    /// absorbing collapse is: a value position reads not-TRUE for all four
    /// spellings and a CHECK constraint admits the row (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("5 between null and null", "x between null and null")]
    [DataRow("not 5 between null and null", "not x between null and null")]
    [DataRow("5 not between null and null", "x not between null and null")]
    [DataRow("not 5 not between null and null", "not x not between null and null")]
    public void BothBetweenBoundsNull_ReadsUnknownOutsideAFilter(string predicate, string checkPredicate)
    {
        var simulation = Seeded();
        AreEqual(3, simulation.ExecuteScalar<int>(
            $"select count(*) from t where case when {predicate} then 'T' else 'F' end = 'F'"));
        _ = simulation.ExecuteNonQuery($"create table ck (x int, constraint c1 check ({checkPredicate}))");
        AreEqual(1, simulation.ExecuteNonQuery("insert ck values (10)"));
    }

    [TestMethod]
    [DataRow("select a from t group by a having b between null and null")]
    [DataRow("select a from t group by a having not b between null and null")]
    [DataRow("select a from t group by a having b not between null and null")]
    [DataRow("select a from t group by a having not b not between null and null")]
    [DataRow("select a from t group by a having -b between null and (null)")]
    public void BothBetweenBoundsNullInHaving_LeavesItsColumnOutOfTheGroupByCheck(string sql) =>
        AreEqual(0, RowCount(Seeded(), sql));

    /// <summary>
    /// One NULL bound is not the same predicate: the surviving half still binds,
    /// so real reports the ungrouped column (probe-confirmed for both bound
    /// positions and every negation spelling).
    /// </summary>
    [TestMethod]
    [DataRow("select a from t group by a having b between null and 5")]
    [DataRow("select a from t group by a having not b between null and 5")]
    [DataRow("select a from t group by a having b not between null and 5")]
    [DataRow("select a from t group by a having not b not between null and 5")]
    [DataRow("select a from t group by a having b between 5 and null")]
    [DataRow("select a from t group by a having b between null and null and b > 1")]
    public void OneNullBetweenBound_StillRaisesMsg8121(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8121);

    // === NOT of a never-FALSE predicate is never TRUE ===

    [TestMethod]
    // A negated range with a NULL bound is TRUE or UNKNOWN, so the outer NOT
    // can only answer FALSE or UNKNOWN.
    [DataRow("select b from t where not b not between null and a / 0")]
    [DataRow("select b from t where not b not between a / 0 and null")]
    [DataRow("select b from t where not (b not between null and a * 2000000000)")]
    // The same through the AND / OR chains.
    [DataRow("select b from t where (b between null and 5) or (b between null and a / 0)")]
    [DataRow("select b from t where not ((b not between null and 5) and (b not between null and a / 0))")]
    // A folded UNKNOWN comparison under NOT settles its whole conjunction.
    [DataRow("select b from t where a / 0 = 1 and not (null) between b and 5")]
    [DataRow("select b from t where not null > b and a * 2000000000 > 1")]
    public void NeverFalseUnderNot_DoesNotEvaluateTheRest(string sql) =>
        AreEqual(0, RowCount(Seeded(), sql));

    [TestMethod]
    // One negation leaves a predicate that can be TRUE, so the bound runs.
    [DataRow("select b from t where b not between null and a / 0")]
    [DataRow("select b from t where not b between null and a / 0")]
    // And so does a second range beside a never-TRUE one under OR.
    [DataRow("select b from t where (b between null and 5) or a / 0 > 1")]
    public void OneNegationOfANullBoundedRange_StillRaisesTheBoundsError(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8134);

    // === A NULL constant among the IN elements ===

    [TestMethod]
    [DataRow("select b from t where b not in (a / 0, 1, null)")]
    [DataRow("select b from t where not b in (a / 0, 1, null)")]
    [DataRow("select b from t where b not in (a * 2000000000, cast(null as int))")]
    [DataRow("select b from t where not b in (null, a / 0)")]
    public void NotInWithANullElement_DoesNotEvaluateTheOtherElements(string sql) =>
        AreEqual(0, RowCount(Seeded(), sql));

    [TestMethod]
    // Un-negated, the list can still match, so every element runs.
    [DataRow("select b from t where b in (a / 0, 1, null)")]
    [DataRow("select b from t where not (b not in (a / 0, 1, null))")]
    public void InWithANullElement_StillRaisesTheElementsError(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8134);

    /// <summary>
    /// Never-TRUE, not constant-UNKNOWN: outside a filter <c>x NOT IN (1, NULL)</c>
    /// is FALSE for a matching row, so a CHECK constraint rejects it (Msg 547 on
    /// real) and the containment pass still sees the column.
    /// </summary>
    [TestMethod]
    public void NotInWithANullElement_KeepsItsMeaningOutsideAFilter()
    {
        var simulation = Seeded();
        _ = simulation.AssertSqlError("select a from t group by a having b not in (1, null)", 8121);
        _ = simulation.ExecuteNonQuery("create table ck (x int, constraint c1 check (x not in (1, null)))");
        _ = simulation.AssertSqlError("insert ck values (1)", 547);
        AreEqual(1, simulation.ExecuteNonQuery("insert ck values (10)"));
    }

    // === A range half that folds to FALSE settles the range ===

    [TestMethod]
    [DataRow("select b from t where 84 between a / 0 and 61", 0)]
    [DataRow("select b from t where 84 not between a / 0 and 61", 3)]
    [DataRow("select b from t where 84 between a * 2000000000 and 61", 0)]
    public void ConstantFalseRangeHalf_CollapsesTheRange(string sql, int expectedRows) =>
        AreEqual(expectedRows, RowCount(Seeded(), sql));

    /// <summary>
    /// Context-free like the absorbing collapse it extends: the range reads a
    /// definite FALSE in a value position and in a CHECK constraint, which
    /// rejects the row rather than raising the dropped half's Msg 8134
    /// (probe-confirmed — real reports Msg 547).
    /// </summary>
    [TestMethod]
    public void ConstantFalseRangeHalf_CollapsesOutsideAFilterToo()
    {
        var simulation = Seeded();
        AreEqual(3, simulation.ExecuteScalar<int>(
            "select count(*) from t where case when 84 between a / 0 and 61 then 'T' else 'F' end = 'F'"));
        _ = simulation.ExecuteNonQuery("create table ck (x int, constraint c1 check (84 between x / 0 and 61))");
        _ = simulation.AssertSqlError("insert ck values (10)", 547);
    }

    [TestMethod]
    // The collapse hides the surviving half from the containment pass, the way
    // an absorbing constant hides its siblings.
    [DataRow("select a from t group by a having 84 between b and 61")]
    [DataRow("select a from t group by a having 84 not between b and 61")]
    public void ConstantFalseRangeHalfInHaving_LeavesTheOtherBoundOutOfTheCheck(string sql) =>
        _ = Seeded().ExecuteScalar<int>($"select count(*) from ({sql}) q(c1)");

    [TestMethod]
    // No half folds FALSE, so nothing collapses: the range binds and evaluates
    // as written.
    [DataRow("select a from t group by a having 84 between 61 and b", 8121)]
    [DataRow("select a from t group by a having 84 between b and 99", 8121)]
    [DataRow("select b from t where 84 between a / 0 and 99", 8134)]
    [DataRow("select b from t where 84 between b and a / 0", 8134)]
    public void RangeWithNoConstantFalseHalf_StillEvaluates(string sql, int errorNumber) =>
        _ = Seeded().AssertSqlError(sql, errorNumber);

    // === A simple CASE no WHEN can match runs its ELSE alone ===

    [TestMethod]
    // A NULL constant on the input side.
    [DataRow("case cast(null as int) when a / 0 then 1 else 2 end")]
    [DataRow("case -cast(null as int) when a / 0 then 1 else 2 end")]
    [DataRow("case null when a / 0 then 1 else 2 end")]
    // Real folds the input first, so an arithmetic or NULLIF-produced NULL
    // reaches the same rule the written one does (probe-confirmed).
    [DataRow("case cast(null as int) / 17 when a / 0 then 1 else 2 end")]
    [DataRow("case nullif(1, 1) when a / 0 then 1 else 2 end")]
    // A NULL constant on every compare-value side settles it just as well, and
    // then the input itself never runs.
    [DataRow("case a / 0 when cast(null as int) then 1 else 2 end")]
    [DataRow("case a / 0 when null then 1 when cast(null as int) then 3 else 2 end")]
    public void SimpleCaseWithNoReachableArm_TakesElseWithoutEvaluating(string caseExpression) =>
        AreEqual(3, Seeded().ExecuteScalar<int>($"select count(*) from t where {caseExpression} = 2"));

    [TestMethod]
    public void SimpleCaseWithNoReachableArmAndNoElse_IsNull() =>
        AreEqual(3, Seeded().ExecuteScalar<int>(
            "select count(*) from t where case a / 0 when cast(null as int) then 1 end is null"));

    [TestMethod]
    // One live comparison leaves the whole set standing.
    [DataRow("select case a / 0 when cast(null as int) then 1 when 5 then 2 else 3 end from t")]
    [DataRow("select case a / 0 when 5 then 1 else 2 end from t")]
    [DataRow("select case 5 when cast(null as int) then 1 when a / 0 then 2 else 3 end from t")]
    // A NULL the row supplies isn't a compile-time one: real evaluates the
    // compare values and reports their error (probe-confirmed).
    [DataRow("select case nullif(b, b) when a / 0 then 1 else 2 end from t")]
    public void SimpleCaseWithALiveArm_StillRaisesTheOperandsError(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8134);

    // === NULLIF's first argument settles the same way ===

    [TestMethod]
    [DataRow("nullif(cast(null as int), a / 0)")]
    [DataRow("nullif(-cast(null as int), a / 0)")]
    [DataRow("nullif(cast(null as int) / 17, a / 0)")]
    [DataRow("nullif(nullif(1, 1), a / 0)")]
    public void NullifWithAConstantNullFirstArgument_DoesNotEvaluateTheSecond(string call) =>
        AreEqual(3, Seeded().ExecuteScalar<int>($"select count(*) from t where {call} is null"));

    [TestMethod]
    // A value on the left leaves the comparison live.
    [DataRow("select nullif(1, a / 0) from t")]
    [DataRow("select nullif(b, a / 0) from t")]
    // A NULL the row supplies isn't a compile-time one.
    [DataRow("select nullif(nullif(b, b), a / 0) from t")]
    // The bare NULL literal real refuses outright (Msg 4151) is left unfolded.
    [DataRow("select nullif(null, a / 0) from t")]
    public void NullifWithoutAConstantNullFirstArgument_StillEvaluatesTheSecond(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8134);

    // === A never-TRUE HAVING makes the whole statement's result empty ===

    [TestMethod]
    // The HAVING is a written constant that folds to FALSE or to UNKNOWN …
    [DataRow("select a from t where a / 0 > 1 group by a having 1 = 0")]
    [DataRow("select a from t where a / 0 > 1 group by a having 1 > 2")]
    [DataRow("select a from t where a / 0 > 1 group by a having not 1 = 1")]
    [DataRow("select a from t where a / 0 > 1 group by a having null is not null")]
    [DataRow("select a from t where a / 0 > 1 group by a having null = null")]
    [DataRow("select a from t where a / 0 > 1 group by a having not null = null")]
    [DataRow("select a from t where a / 0 > 1 group by a having 1 = 0 or null > 1")]
    // … or one of the shapes the never-TRUE rule already settles.
    [DataRow("select a from t where a / 0 > 1 group by a having max(a) between null and 5")]
    [DataRow("select a from t where a / 0 > 1 group by a having not null in (a)")]
    [DataRow("select a from t where a / 0 > 1 group by a having 1 = 0 and max(a) > 1")]
    // No GROUP BY: the HAVING still settles the implicit single group away.
    [DataRow("select max(a / 0) from t having 1 = 0")]
    public void NeverTrueHaving_LeavesTheRestOfTheStatementUnevaluated(string sql) =>
        AreEqual(0, RowCount(Seeded(), sql));

    /// <summary>
    /// The emptiness is settled after binding, not instead of it: real reports
    /// every one of these under a constant-FALSE HAVING (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("select a from t where zzz > 1 group by a having 1 = 0", 207)]
    [DataRow("select b from t group by a having 1 = 0", 8120)]
    [DataRow("select a from t where 1 = 0 group by a having b > 1", 8121)]
    public void NeverTrueHaving_StillReportsTheBindingError(string sql, int errorNumber) =>
        _ = Seeded().AssertSqlError(sql, errorNumber);

    [TestMethod]
    // A HAVING that can be TRUE settles nothing, so the WHERE runs as written.
    [DataRow("select a from t where a / 0 > 1 group by a having 1 = 1")]
    [DataRow("select a from t where a / 0 > 1 group by a having max(a) > 1")]
    // A fold that raises leaves the HAVING standing, the way it does everywhere.
    [DataRow("select a from t group by a having 1 / 0 = 1")]
    public void ReachableHaving_StillEvaluatesTheRestOfTheStatement(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8134);

    // === A HAVING settles a comparison against a folded-NULL constant ===

    [TestMethod]
    [DataRow("select a from t group by a having cast(null as int) / 17 = b")]
    [DataRow("select a from t group by a having null + 1 = b")]
    [DataRow("select a from t group by a having b = cast(null as int) / 17")]
    [DataRow("select a from t group by a having b not in (cast(null as int) / 44 + -73)")]
    [DataRow("select a from t group by a having b between cast(null as int) / 17 and nullif(1, 1)")]
    [DataRow("select a from t group by a having not (cast(null as int) / 17 = b)")]
    public void HavingComparisonAgainstAFoldedNull_HidesItsOperandAndAnswersNoRows(string sql) =>
        AreEqual(0, RowCount(Seeded(), sql));

    [TestMethod]
    public void HavingComparisonAgainstAFoldedNull_LeavesTheStatementEmptyWithoutRunningTheWhere() =>
        AreEqual(0, RowCount(Seeded(), "select a from t where a / 0 > 1 group by a having cast(null as int) / 17 = max(b)"));

    /// <summary>
    /// Fences. The rule is per comparison, so a surviving conjunct still reports
    /// its ungrouped column; and it is the HAVING's reading alone — the same
    /// comparison in a WHERE still raises the other side's error, which is what
    /// real does there (its own no-rows answer for that statement arrives only
    /// once a DISTINCT / GROUP BY / TOP / join changes the plan).
    /// </summary>
    [TestMethod]
    public void FoldedNullComparison_IsHavingOnlyAndPerConjunct()
    {
        var simulation = Seeded();
        _ = simulation.AssertSqlError("select a from t group by a having cast(null as int) / 17 = a and b > 1", 8121);
        _ = simulation.AssertSqlError("select b from t where cast(null as int) / 17 > a * 2000000000", 8115);
        _ = simulation.AssertSqlError("select b from t where a * 2000000000 > cast(null as int) / 17", 8115);
        _ = simulation.AssertSqlError("select b from t where nullif(1, 1) > a / 0", 8134);
    }

    // === An unreachable CASE / COALESCE arm drops its aggregates ===

    [TestMethod]
    // The simple form's comparison, settled while compiling.
    [DataRow("select case 23 when -38 then count(a / 0) else 2 end from t")]
    [DataRow("select case 23 when -38 then sum(a / 0) else 2 end from t")]
    [DataRow("select case cast(null as int) when 1 then sum(a / 0) else 2 end from t")]
    // The searched form's condition.
    [DataRow("select case when 1 = 0 then sum(a / 0) else 2 end from t")]
    [DataRow("select case when null is not null then sum(a / 0) else 2 end from t")]
    // An arm after the one real settled TRUE, and the ELSE with it.
    [DataRow("select case when 1 = 1 then 2 when 1 = 1 then sum(a / 0) else sum(a * 2000000000) end from t")]
    [DataRow("select case 23 when 23 then 2 when 9 then sum(a / 0) else 3 end from t")]
    // COALESCE settles the same way off its first non-NULL constant argument.
    [DataRow("select coalesce(2, sum(a / 0)) from t")]
    [DataRow("select coalesce(cast(null as int), 2, sum(a / 0)) from t")]
    public void UnreachableArmsAggregate_IsNotEvaluated(string sql) =>
        AreEqual(2, Seeded().ExecuteScalar<int>(sql));

    /// <summary>
    /// The aggregate leaves the <em>evaluation</em>, not the query: real keeps
    /// the statement a vector aggregate and keeps reporting Msg 8120 for an
    /// ungrouped column beside it (both probe-confirmed).
    /// </summary>
    [TestMethod]
    public void UnreachableArmsAggregate_StillShapesTheQuery()
    {
        var simulation = Seeded();
        AreEqual(1, RowCount(simulation, "select case when 1 = 1 then 2 else sum(a) end from t"));
        AreEqual(3, RowCount(simulation, "select case when 1 = 0 then sum(a) else 2 end from t group by a"));
        _ = simulation.AssertSqlError("select a, case when 1 = 0 then sum(b) else 2 end from t", 8120);
        _ = simulation.AssertSqlError("select a, coalesce(2, sum(b)) from t", 8120);
    }

    [TestMethod]
    // A reachable arm evaluates its aggregate …
    [DataRow("select case 23 when 23 then sum(a / 0) else 2 end from t")]
    [DataRow("select case when 1 = 1 then sum(a / 0) else 2 end from t")]
    // … and so does one real can only settle per row.
    [DataRow("select case when a = 1 then sum(a / 0) else 2 end from t group by a")]
    [DataRow("select case count(*) when 3 then sum(a / 0) else 2 end from t")]
    // COALESCE keeps its tail once the leading argument isn't a constant value.
    [DataRow("select coalesce(max(a), sum(a / 0)) from t")]
    [DataRow("select coalesce(cast(null as int), sum(a / 0), 2) from t")]
    public void ReachableArmsAggregate_StillEvaluates(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8134);

    // === A settled arm makes the whole CASE / COALESCE a constant ===

    /// <summary>
    /// Real's own constant classification, which the ORDER BY gates read: the
    /// arm it settled on decides, so a column or an aggregate in an arm it
    /// dropped doesn't keep the term sortable (probe-confirmed in both
    /// directions, on the statement path and inside <c>OVER (…)</c>).
    /// </summary>
    [TestMethod]
    [DataRow("select a from t order by case 1 when 1 then 5 else a end", 408)]
    [DataRow("select a from t order by case 1 when 2 then a else 5 end", 408)]
    [DataRow("select a from t group by a order by case 23 when -38 then count(*) end", 408)]
    [DataRow("select a from t order by coalesce(61, a)", 408)]
    [DataRow("select a from t group by a order by coalesce(61, max(b))", 408)]
    [DataRow("select row_number() over (order by case 1 when 2 then a else 5 end) from t", 5308)]
    [DataRow("select row_number() over (order by coalesce(61, a)) from t", 5308)]
    // A condition the predicate folds settles the arm too, so the CASE it
    // guards is constant even though the condition names a column.
    [DataRow("select a from t order by case when null > a then 1 else 2 end", 408)]
    [DataRow("select a from t order by case when 1 = 0 and a > 1 then 1 else 2 end", 408)]
    public void ArmSettledConstant_IsRejectedInAnOrderBy(string sql, int errorNumber) =>
        _ = Seeded().AssertSqlError(sql, errorNumber);

    [TestMethod]
    // The settled arm isn't a constant, so the term sorts.
    [DataRow("select a from t order by case 1 when 1 then a else 5 end")]
    [DataRow("select a from t order by coalesce(a, 61)")]
    [DataRow("select a from t order by case when a > 1 then 1 else 2 end")]
    public void ArmSettledOnALiveValue_StillSorts(string sql) =>
        AreEqual(3, UnwrappedRowCount(Seeded(), sql));

    [TestMethod]
    // A CASE real folds to a constant feeds the folds that read one: NULLIF's
    // first argument here, and a simple CASE's input in the second case.
    [DataRow("select nullif(case 23 when -38 then count(*) end, a / 0) from t", 1)]
    [DataRow("select case coalesce(cast(null as int), cast(null as int)) when a / 0 then 1 else 2 end from t", 3)]
    public void ArmSettledConstant_FeedsTheFoldsThatReadOne(string sql, int expectedRows) =>
        AreEqual(expectedRows, RowCount(Seeded(), sql));

    // === An IN list carrying its own left operand ===

    [TestMethod]
    [DataRow("select b from t where b not in (a / 0, b)")]
    [DataRow("select b from t where b not in (b, a / 0)")]
    [DataRow("select b from t where not b in (a / 0, b)")]
    [DataRow("select b from t where b not in (a / 0, (b))")]
    [DataRow("select b from t where (b) not in (a / 0, b)")]
    public void NotInCarryingItsOwnOperand_IsNeverTrue(string sql) =>
        AreEqual(0, RowCount(Seeded(), sql));

    [TestMethod]
    // Un-negated the list can still be TRUE, so the elements run — and real
    // raises for one written element order and answers for the other, so the
    // simulator keeps its own left-to-right evaluation.
    [DataRow("select b from t where b in (a / 0, b)")]
    // A different column isn't the same operand.
    [DataRow("select b from t where b not in (a / 0, a)")]
    public void InCarryingItsOwnOperand_StillEvaluatesTheElements(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8134);

    // === COUNT of an expression real types NOT NULL ===

    [TestMethod]
    [DataRow("select count(61 / 0) from t", 3)]
    [DataRow("select count(all -4 + 61 / 0) from t", 3)]
    [DataRow("select count(2000000000 * 3) from t", 3)]
    [DataRow("select count(-(61 / 0)) from t", 3)]
    [DataRow("select count((61 / 0)) from t", 3)]
    // A constant that is NULL still counts nothing.
    [DataRow("select count(cast(null as int)) from t", 0)]
    public void CountOfANonNullConstantComputation_DoesNotEvaluateIt(string sql, int expected) =>
        AreEqual(expected, Seeded().ExecuteScalar<int>(sql));

    [TestMethod]
    public void CountBigOfANonNullConstantComputation_DoesNotEvaluateIt() =>
        AreEqual(3L, Seeded().ExecuteScalar<long>("select count_big(61 / 0) from t"));

    [TestMethod]
    // Real evaluates the argument once a GROUP BY names a grouping expression …
    [DataRow("select count(61 / 0) from t group by a")]
    // … and for every shape whose result the count actually reads.
    [DataRow("select count(distinct 61 / 0) from t")]
    [DataRow("select sum(61 / 0) from t")]
    [DataRow("select max(61 / 0) from t")]
    // A column makes it no longer a constant computation.
    [DataRow("select count(a / 0) from t")]
    public void CountReduction_KeepsItsFences(string sql) =>
        _ = Seeded().AssertSqlError(sql, 8134);
}
