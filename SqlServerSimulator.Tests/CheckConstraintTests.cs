using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for CHECK constraints: inline + table-level grammar,
/// named + auto-named, three-valued-logic NULL semantics (UNKNOWN passes),
/// and Msg 547 wording. The column-suffix variation (inline vs table-level)
/// is sourced from probes against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CheckConstraintTests
{
    [TestMethod]
    public void Check_Inline_AcceptsConformingRow()
    {
        Assert.AreEqual(5, new Simulation().ExecuteScalar("""
            create table t (qty int constraint ck_qty check (qty > 0));
            insert t values (5);
            select qty from t
            """));
    }

    [TestMethod]
    public void Check_Inline_RejectsViolatingRow_RaisesMsg547()
    {
        var ex = Assert.Throws<DbException>(() => new Simulation().ExecuteNonQuery("""
            create table t (qty int constraint ck_qty check (qty > 0));
            insert t values (-1)
            """));
        Assert.AreEqual("The INSERT statement conflicted with the CHECK constraint \"ck_qty\". The conflict occurred in database \"simulated\", table \"dbo.t\", column 'qty'.", ex.Message);
        Assert.AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Check_TableLevel_OmitsColumnSuffix()
    {
        // Table-level CHECK references multiple columns and produces a
        // simpler Msg 547 (no "column 'X'" suffix), matching SQL Server's
        // wording.
        var ex = Assert.Throws<DbException>(() => new Simulation().ExecuteNonQuery("""
            create table t (a int, b int, constraint ck_ab check (a < b));
            insert t values (5, 2)
            """));
        Assert.AreEqual("The INSERT statement conflicted with the CHECK constraint \"ck_ab\". The conflict occurred in database \"simulated\", table \"dbo.t\".", ex.Message);
    }

    [TestMethod]
    public void Check_NullOperand_PassesViaTriState()
    {
        // SQL Server's CHECK semantics: a predicate that evaluates to UNKNOWN
        // (NULL operand in a comparison) lets the row through — opposite of
        // WHERE where UNKNOWN excludes. Probed against SQL Server 2025.
        Assert.AreEqual(DBNull.Value, new Simulation().ExecuteScalar("""
            create table t (qty int constraint ck_qty check (qty > 0));
            insert t values (null);
            select qty from t
            """));
    }

    [TestMethod]
    public void Check_TableLevel_AndPredicate_BothMustHold()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, constraint ck_and check (a > 0 and b > 0));
            insert t values (1, 1)
            """);

        var failLeft = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (-1, 5)"));
        Assert.AreEqual("547", failLeft.Data["HelpLink.EvtID"]);
        var failRight = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (5, -1)"));
        Assert.AreEqual("547", failRight.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Check_TableLevel_OrPredicate_EitherSuffices()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, constraint ck_or check (a > 0 or b > 0));
            insert t values (5, -1), (-1, 5)
            """);

        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (-1, -1)"));
        Assert.AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Check_TableLevel_NotPredicate()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, constraint ck_nz check (not (a = 0)));
            insert t values (1), (-1)
            """);

        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (0)"));
        Assert.AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Check_Unnamed_AutoNameMatchesShape()
    {
        // Auto-name shape for inline CHECK: CK__<table8>__<col8>__<8 hex>.
        // Test asserts on prefix, leaving the 8-hex suffix unspecified
        // (deterministic FNV hash; cosmetic).
        var ex = Assert.Throws<DbException>(() => new Simulation().ExecuteNonQuery("""
            create table t (qty int check (qty > 0));
            insert t values (-1)
            """));
        Assert.StartsWith("The INSERT statement conflicted with the CHECK constraint \"CK__t__qty__", ex.Message);
    }

    [TestMethod]
    public void Check_Unnamed_TableLevel_AutoNameOmitsColumn()
    {
        var ex = Assert.Throws<DbException>(() => new Simulation().ExecuteNonQuery("""
            create table t (a int, b int, check (a < b));
            insert t values (5, 2)
            """));
        Assert.StartsWith("The INSERT statement conflicted with the CHECK constraint \"CK__t__", ex.Message);
        // Inline auto-name has __qty__ between table and hex; table-level skips it.
        Assert.DoesNotContain("__a__", ex.Message);
    }

    [TestMethod]
    public void Check_MultipleConstraints_AllEnforced()
    {
        // Two separate CHECK constraints on the same table; either rejects.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int constraint ck_a check (a > 0), b int constraint ck_b check (b < 100));
            insert t values (5, 50)
            """);

        var failA = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (-1, 50)"));
        Assert.Contains("ck_a", failA.Message);
        var failB = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (5, 200)"));
        Assert.Contains("ck_b", failB.Message);
    }

    [TestMethod]
    public void Check_ParenthesizedPredicate()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, constraint ck check ((a > 0 or b > 0) and a + b < 100));
            insert t values (5, -1)
            """);
        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (50, 60)"));
        Assert.AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Check_OnMergeInsert_EnforcedViaSamePath()
    {
        // EF Core's batched MERGE-INSERT shape; CHECK enforcement must fire.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (qty int constraint ck_qty check (qty > 0))");
        var ex = Assert.Throws<DbException>(() =>
            simulation.ExecuteNonQuery("merge into t using (values (-1)) as src (qty) on 1=0 when not matched then insert (qty) values (src.qty);"));
        Assert.AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Check_ColumnEqualityWithLiterals()
    {
        // Common shape: status code IN equivalent via OR chain (since IN
        // isn't yet modeled). `status = 'A' OR status = 'B'`.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (status nvarchar(1) constraint ck_status check (status = 'A' or status = 'B'));
            insert t values ('A'), ('B')
            """);

        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values ('C')"));
        Assert.AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    // Msg 8141: an inline column-level CHECK constraint may only reference
    // its owning column. Table-level CHECK has no such restriction. Probed
    // against SQL Server 2025 (2026-05-11).

    [TestMethod]
    public void InlineCheck_ReferencesPeerColumn_Msg8141()
        => new Simulation().AssertSqlError(
            "create table t (a int check (b > 0), b int)",
            8141,
            "Column CHECK constraint for column 'a' references another column, table 't'.");

    [TestMethod]
    public void InlineCheck_OwningAndPeer_StillMsg8141()
        => new Simulation().AssertSqlError(
            "create table t (a int check (a > 0 and b > 0), b int)",
            8141);

    [TestMethod]
    public void InlineCheck_PeerOnSecondColumn_Msg8141()
        => new Simulation().AssertSqlError(
            "create table t (a int, b int check (a > 0))",
            8141,
            "Column CHECK constraint for column 'b' references another column, table 't'.");

    [TestMethod]
    public void InlineCheck_PeerInsideFunctionCall_Msg8141()
        => new Simulation().AssertSqlError(
            "create table t (a int, b nvarchar(10) check (len(a) > 0))",
            8141);

    [TestMethod]
    public void InlineCheck_PeerInsideInList_Msg8141()
        => new Simulation().AssertSqlError(
            "create table t (a int check (a in (b, 1, 2)), b int)",
            8141);

    [TestMethod]
    public void InlineCheck_NamedConstraint_PeerColumn_Msg8141()
        => new Simulation().AssertSqlError(
            "create table t (a int constraint ck_peer check (b > 0), b int)",
            8141);

    [TestMethod]
    public void InlineCheck_OnlyOwningColumn_Works()
        => _ = new Simulation().ExecuteNonQuery("""
            create table t (a int check (a > 0), b int);
            insert t values (1, 100)
            """);

    /// <summary>
    /// Predicate has no column references at all — should not trip Msg 8141.
    /// </summary>
    [TestMethod]
    public void InlineCheck_NoColumnRef_Works()
        => _ = new Simulation().ExecuteNonQuery("""
            create table t (a int check (1 = 1), b int);
            insert t values (1, 2)
            """);

    [TestMethod]
    public void TableLevelCheck_ReferencesMultipleColumns_Works()
    {
        // Table-level CHECK (no owning column) is allowed to reference any
        // columns — the Msg 8141 rule is inline-only.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, check (a < b));
            insert t values (1, 2)
            """);

        var ex = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (5, 3)"));
        Assert.AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    /// <summary>
    /// Paren-wrapped value LHS in CHECK: DACFx emits the
    /// <c>((value_expr) = (literal))</c> shape (WWI's
    /// <c>CK_Sales_SpecialDeals_Exactly_One_NOT_NULL_Pricing_Option_Is_Required</c>
    /// is the canonical instance). The boolean parser disambiguates the
    /// leading <c>(</c> via a token-lookahead at the matching <c>)</c>.
    /// </summary>
    [TestMethod]
    public void TableLevelCheck_ParenWrappedValueLhs_AcceptsConforming()
        => Assert.AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (a int, b int, c int,
                check (((case when a is null then 0 else 1 end
                        + case when b is null then 0 else 1 end)
                       + case when c is null then 0 else 1 end) = (1)));
            insert t values (1, null, null);
            select count(*) from t
            """));

    [TestMethod]
    public void TableLevelCheck_ParenWrappedValueLhs_RejectsTwoSet()
    {
        var ex = Assert.Throws<DbException>(() => new Simulation().ExecuteNonQuery("""
            create table t (a int, b int, c int,
                constraint ck_one_only check (((case when a is null then 0 else 1 end
                        + case when b is null then 0 else 1 end)
                       + case when c is null then 0 else 1 end) = (1)));
            insert t values (1, 2, null)
            """));
        Assert.AreEqual("547", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("ck_one_only", ex.Message);
    }

    [TestMethod]
    public void TableLevelCheck_ParenWrappedValueLhs_RejectsAllNull()
    {
        var ex = Assert.Throws<DbException>(() => new Simulation().ExecuteNonQuery("""
            create table t (a int, b int, c int,
                check (((case when a is null then 0 else 1 end
                        + case when b is null then 0 else 1 end)
                       + case when c is null then 0 else 1 end) = (1)));
            insert t values (null, null, null)
            """));
        Assert.AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    // sys.check_constraints.definition holds the predicate's original syntax,
    // wrapped in one paren pair (the simulator doesn't re-normalize to SQL
    // Server's canonical [col]=(1)-style form). Covers inline column-level,
    // table-level, and the broad grammar (OR / IN / functions) that the
    // filtered-index renderer deliberately doesn't touch.
    [TestMethod]
    [DataRow("a int check (a > 0)", "(a > 0)")]
    [DataRow("a int, b int, check (a > 0 and b < 10)", "(a > 0 and b < 10)")]
    [DataRow("s varchar(2) check (s in ('A', 'B'))", "(s in ('A', 'B'))")]
    [DataRow("n varchar(9) check (len(n) > 0 or n is null)", "(len(n) > 0 or n is null)")]
    public void CheckConstraint_Definition_HoldsOriginalSyntax(string columns, string expected)
        => Assert.AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table t (id int not null primary key, {columns});
            select definition from sys.check_constraints
            """));

    [TestMethod]
    public void CheckConstraint_AlterAddCheck_Definition_HoldsOriginalSyntax()
        => Assert.AreEqual("(b >= a)", new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int, b int);
            alter table t add constraint ck check (b >= a);
            select definition from sys.check_constraints where name = 'ck'
            """));
}
