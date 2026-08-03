using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The value-count rules an INSERT is measured against, and which diagnostic
/// each mismatch reports. Every expectation here is probe-confirmed against
/// SQL Server 2025 (17.0.4065.4).
/// </summary>
[TestClass]
public class InsertArityTests
{
    private static Simulation TwoColumnTable()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int)");
        return simulation;
    }

    // ---- no column list: Msg 213 against the table definition ----

    [TestMethod]
    public void NoColumnList_TooManyValues_Raises213()
    {
        var ex = TwoColumnTable().AssertSqlError("insert into t values (1, 2, 3)", 213);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
        AreEqual("Column name or number of supplied values does not match table definition.", ex.Message);
    }

    [TestMethod]
    public void NoColumnList_TooFewValues_Raises213()
    {
        // Previously an unguarded IndexOutOfRangeException escaped to the caller.
        var ex = TwoColumnTable().AssertSqlError("insert into t values (7)", 213);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
    }

    [TestMethod]
    public void NoColumnList_MatchingWidth_Inserts()
    {
        var simulation = TwoColumnTable();
        AreEqual(1, simulation.ExecuteNonQuery("insert into t values (1, 2)"));
        AreEqual(2, simulation.ExecuteScalar("select b from t"));
    }

    // ---- explicit column list: Msg 110 (too many) / Msg 109 (too few) ----

    [TestMethod]
    public void ColumnList_TooManyValues_Raises110()
    {
        var ex = TwoColumnTable().AssertSqlError("insert into t (a) values (10, 20)", 110);
        AreEqual(15, ex.Class);
        AreEqual(1, ex.State);
        AreEqual(
            "There are fewer columns in the INSERT statement than values specified in the VALUES clause. The number of values in the VALUES clause must match the number of columns specified in the INSERT statement.",
            ex.Message);
    }

    [TestMethod]
    public void ColumnList_TooFewValues_Raises109()
    {
        var ex = TwoColumnTable().AssertSqlError("insert into t (a, b) values (7)", 109);
        AreEqual(15, ex.Class);
        AreEqual(1, ex.State);
        AreEqual(
            "There are more columns in the INSERT statement than values specified in the VALUES clause. The number of values in the VALUES clause must match the number of columns specified in the INSERT statement.",
            ex.Message);
    }

    [TestMethod]
    public void ColumnList_TooManyValues_WithOutputClause_StillRaises110()
        => _ = TwoColumnTable().AssertSqlError("insert into t (a) output inserted.a values (1, 2)", 110);

    // ---- multi-row VALUES ----

    [TestMethod]
    [DataRow("insert into t values (1, 2), (3)")]
    [DataRow("insert into t values (1, 2), (3, 4, 5)")]
    [DataRow("insert into t values (1, 2, 3), (4, 5)")]
    [DataRow("insert into t (a) values (1), (2, 3)")]
    [DataRow("insert into t (a, b) values (1), (2, 3)")]
    public void MultiRow_TuplesDisagree_Raises10709(string commandText)
    {
        // Ragged tuples have no single width to measure, so Msg 10709 wins
        // over the arity diagnostics — including against a column list.
        var ex = TwoColumnTable().AssertSqlError(commandText, 10709);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
        AreEqual("The number of columns for each row in a table value constructor must be the same.", ex.Message);
    }

    [TestMethod]
    public void MultiRow_ConsistentButWrongAgainstTable_Raises213()
        => _ = TwoColumnTable().AssertSqlError("insert into t values (1, 2, 3), (4, 5, 6)", 213);

    [TestMethod]
    public void MultiRow_ConsistentButWrongAgainstColumnList_Raises110()
        => _ = TwoColumnTable().AssertSqlError("insert into t (a) values (1, 2), (3, 4)", 110);

    [TestMethod]
    public void MultiRow_MatchingWidth_Inserts()
        => AreEqual(2, TwoColumnTable().ExecuteNonQuery("insert into t values (1, 2), (3, 4)"));

    // ---- INSERT … SELECT ----

    [TestMethod]
    [DataRow("insert into t select 1, 2, 3")]
    [DataRow("insert into t select 1")]
    public void SelectSource_NoColumnList_Raises213(string commandText)
        => _ = TwoColumnTable().AssertSqlError(commandText, 213);

    [TestMethod]
    public void SelectSource_ColumnList_TooMany_Raises121()
        => _ = TwoColumnTable().AssertSqlError("insert into t (a) select 1, 2", 121);

    [TestMethod]
    public void SelectSource_ColumnList_TooFew_Raises120()
        => _ = TwoColumnTable().AssertSqlError("insert into t (a, b) select 1", 120);

    // ---- DEFAULT VALUES stays exempt ----

    [TestMethod]
    public void DefaultValues_IsNotArityChecked()
    {
        var simulation = TwoColumnTable();
        AreEqual(1, simulation.ExecuteNonQuery("insert into t default values"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t"));
    }

    // ---- IDENTITY changes the implicit width, and which error a surplus reports ----

    private static Simulation IdentityTable()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table ident (id int identity(1,1), a int, b int)");
        return simulation;
    }

    [TestMethod]
    public void Identity_NoColumnList_ExcludedFromWidth()
    {
        var simulation = IdentityTable();
        AreEqual(1, simulation.ExecuteNonQuery("insert into ident values (1, 2)"));
        AreEqual(1, simulation.ExecuteScalar("select id from ident"));
    }

    [TestMethod]
    public void Identity_NoColumnList_SurplusValue_Raises8101()
    {
        // The extra value would have to land in the identity column, so real
        // reports the identity diagnostic rather than Msg 213 — for any
        // surplus, including one wider than the table itself.
        var ex = IdentityTable().AssertSqlError("insert into ident values (1, 2, 3)", 8101);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
        AreEqual(
            "An explicit value for the identity column in table 'ident' can only be specified when a column list is used and IDENTITY_INSERT is ON.",
            ex.Message);
    }

    [TestMethod]
    public void Identity_NoColumnList_SurplusBeyondTableWidth_Raises8101()
        => _ = IdentityTable().AssertSqlError("insert into ident values (1, 2, 3, 4)", 8101);

    [TestMethod]
    public void Identity_NoColumnList_TooFewValues_Raises213()
        => _ = IdentityTable().AssertSqlError("insert into ident values (1)", 213);

    [TestMethod]
    public void Identity_SelectSource_NoColumnList_SurplusValue_Raises8101()
        => _ = IdentityTable().AssertSqlError("insert into ident select 1, 2, 3", 8101);

    [TestMethod]
    public void Identity_ColumnListArity_PrecedesTheIdentityGate()
    {
        // Naming the identity column with IDENTITY_INSERT OFF is Msg 544, but
        // a miscounted list is reported first (probe-confirmed both ways).
        var simulation = IdentityTable();
        _ = simulation.AssertSqlError("insert into ident (id, a) values (1)", 109);
        _ = simulation.AssertSqlError("insert into ident (id, a) values (1, 2, 3)", 110);
        _ = simulation.AssertSqlError("insert into ident (id, a) values (1, 2)", 544);
    }

    [TestMethod]
    public void Identity_SelectSourceArity_PrecedesTheIdentityGate()
    {
        var simulation = IdentityTable();
        _ = simulation.AssertSqlError("insert into ident (id, a) select 1", 120);
        _ = simulation.AssertSqlError("insert into ident (id, a) select 1, 2", 544);
    }

    [TestMethod]
    [DataRow("insert into ident values (1, 2)", 545)]
    [DataRow("insert into ident values (1, 2, 3)", 8101)]
    [DataRow("insert into ident values (1)", 213)]
    public void IdentityInsertOn_NoColumnList_AlwaysRefuses(string commandText, int errorNumber)
    {
        // With IDENTITY_INSERT ON the identity still drops out of the implicit
        // width, so the column-list-less form cannot succeed: a matching count
        // leaves the identity unsupplied (Msg 545) and a surplus reports
        // Msg 8101. Real refuses all three shapes.
        // IDENTITY_INSERT is session state, so this runs on one connection.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table ident (id int identity(1,1), a int, b int)").ExecuteNonQuery();
        _ = connection.CreateCommand("set identity_insert ident on").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand(commandText).ExecuteNonQuery());
        AreEqual(errorNumber, ex.Number);
    }

    // ---- computed columns drop out of the implicit width ----

    [TestMethod]
    public void ComputedColumn_ExcludedFromImplicitWidth()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table comp (a int, b as a + 1, c int)");
        AreEqual(1, simulation.ExecuteNonQuery("insert into comp values (1, 2)"));
        AreEqual(2, simulation.ExecuteScalar("select b from comp"));
        // No identity on the table, so the surplus is a plain Msg 213.
        _ = simulation.AssertSqlError("insert into comp values (1, 2, 3)", 213);
    }

    // ---- a DEFAULT clause does NOT drop a column from the width ----

    [TestMethod]
    public void DefaultedColumn_StillOccupiesAPosition()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table defs (a int, b int default 5, c int)");
        _ = simulation.AssertSqlError("insert into defs values (1, 2)", 213);
        AreEqual(1, simulation.ExecuteNonQuery("insert into defs values (1, default, 3)"));
        AreEqual(5, simulation.ExecuteScalar("select b from defs"));
    }

    // ---- rowversion keeps its position and takes DEFAULT ----

    private static Simulation RowVersionTable()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table rv (a int, ts rowversion, b int)");
        return simulation;
    }

    [TestMethod]
    public void RowVersion_OccupiesAPositionInTheImplicitWidth()
    {
        // Real counts the rowversion column, so the "obvious" two-value insert
        // is Msg 213 rather than a two-column write.
        var simulation = RowVersionTable();
        _ = simulation.AssertSqlError("insert into rv values (1, 2)", 213);
        AreEqual(1, simulation.ExecuteNonQuery("insert into rv values (1, default, 2)"));
        AreEqual(2, simulation.ExecuteScalar("select b from rv"));
    }

    [TestMethod]
    public void RowVersion_ExplicitValue_Raises273()
    {
        var ex = RowVersionTable().AssertSqlError("insert into rv values (1, 2, 3)", 273);
        AreEqual(16, ex.Class);
        AreEqual(1, ex.State);
    }

    [TestMethod]
    public void RowVersion_NamedInColumnListWithDefault_IsAccepted()
    {
        // Msg 273's own text advertises this escape hatch; the simulator used
        // to reject the column name outright.
        var simulation = RowVersionTable();
        AreEqual(1, simulation.ExecuteNonQuery("insert into rv (a, ts, b) values (1, default, 3)"));
        AreEqual(2, simulation.ExecuteNonQuery("insert into rv (a, ts, b) values (4, default, 5), (6, default, 7)"));
        _ = simulation.AssertSqlError("insert into rv (a, ts, b) values (1, 2, 3)", 273);
    }

    [TestMethod]
    public void RowVersion_OneRowSuppliesAValue_Raises273()
        => _ = RowVersionTable().AssertSqlError("insert into rv values (1, default, 2), (3, 4, 5)", 273);

    [TestMethod]
    public void RowVersion_ExcludedByAnExplicitColumnList()
        => AreEqual(1, RowVersionTable().ExecuteNonQuery("insert into rv (a, b) values (1, 2)"));

    [TestMethod]
    public void RowVersion_ArityPrecedesThe273Gate()
        => _ = RowVersionTable().AssertSqlError("insert into rv select 1, 2", 213);

    [TestMethod]
    public void RowVersion_SelectSourceReachingTheColumn_Raises273()
        => _ = RowVersionTable().AssertSqlError("insert into rv select 1, 2, 3", 273);

    // ---- INSERT through a view is measured against the view's projection ----

    private static Simulation ViewSimulation()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t1 (x int, y int)");
        _ = simulation.ExecuteNonQuery("create view view1 as select x from t1 where x > 0");
        return simulation;
    }

    [TestMethod]
    public void View_NoColumnList_TooManyValues_Raises213()
    {
        // The shape the differential sweep surfaced: the extra value used to
        // be dropped and (2, NULL) written to the base table.
        var simulation = ViewSimulation();
        _ = simulation.AssertSqlError("insert into view1 values (2, 'unknown')", 213);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t1"));
    }

    [TestMethod]
    public void View_ColumnList_TooManyValues_Raises110()
        => _ = ViewSimulation().AssertSqlError("insert into view1 (x) values (2, 99)", 110);

    [TestMethod]
    public void View_MatchingWidth_WritesThrough()
    {
        var simulation = ViewSimulation();
        AreEqual(1, simulation.ExecuteNonQuery("insert into view1 values (2)"));
        AreEqual(2, simulation.ExecuteScalar("select x from t1"));
    }

    [TestMethod]
    public void View_ProjectedRowVersion_KeepsItsPosition()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table tr (a int, ts rowversion, b int)");
        _ = simulation.ExecuteNonQuery("create view vr as select a, ts, b from tr");
        _ = simulation.AssertSqlError("insert into vr values (1, 2)", 213);
        AreEqual(1, simulation.ExecuteNonQuery("insert into vr values (1, default, 2)"));
    }

    [TestMethod]
    public void View_DerivedColumn_NoColumnList_Raises4406()
    {
        // A derived projection has no position to fill, so real refuses the
        // column-list-less form whatever the value count.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t1 (x int, y int)");
        _ = simulation.ExecuteNonQuery("create view vd as select x, x + 1 as d, y from t1");
        _ = simulation.AssertSqlError("insert into vd values (1, 2, 3)", 4406);
        _ = simulation.AssertSqlError("insert into vd values (1, 2)", 4406);
        AreEqual(1, simulation.ExecuteNonQuery("insert into vd (x, y) values (1, 2)"));
    }

    [TestMethod]
    public void JoinView_ColumnList_TooManyValues_Raises110()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table j1 (k int primary key, x int)");
        _ = simulation.ExecuteNonQuery("create table j2 (k int primary key, y int)");
        _ = simulation.ExecuteNonQuery("create view vj as select j1.k as k1, j1.x, j2.y from j1 join j2 on j1.k = j2.k");
        _ = simulation.AssertSqlError("insert into vj (k1, x) values (1, 2, 3)", 110);
    }

    // ---- the check is a compile-time one ----

    [TestMethod]
    public void ArityIsCheckedAtCompileTime_UntakenIfBranch()
    {
        var simulation = TwoColumnTable();
        _ = simulation.AssertSqlError("if 1 = 0 insert into t values (1, 2, 3)", 213);
        _ = simulation.AssertSqlError("if 1 = 0 insert into t (a) values (1, 2)", 110);
    }

    [TestMethod]
    public void FailedArity_WritesNothing()
    {
        // Real settles arity while compiling the batch, so a preceding
        // statement never runs either; the simulator dispatches statement by
        // statement, so only the offending statement is guaranteed inert.
        var simulation = TwoColumnTable();
        _ = simulation.AssertSqlError("insert into t values (1, 2, 3)", 213);
        _ = simulation.AssertSqlError("insert into t (a) values (1, 2)", 110);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    [DataRow("insert into t values (1, 2, 3)", 213)]
    [DataRow("insert into t (a) values (1, 2)", 110)]
    [DataRow("insert into t values (1, 2), (3)", 10709)]
    public void ArityIsCheckedWhenAModuleBodyBinds(string body, int errorNumber)
    {
        // Real aborts CREATE PROCEDURE on a bad-arity body; the procedure must
        // not exist afterwards.
        var simulation = TwoColumnTable();
        _ = simulation.AssertSqlError($"create procedure p as {body}", errorNumber);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.procedures where name = 'p'"));
    }

    // ---- table variables and temp tables take the same rules ----

    [TestMethod]
    public void TableVariable_TakesTheSameArityRules()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("declare @tv table (a int, b int); insert into @tv values (1, 2, 3)", 213);
        _ = simulation.AssertSqlError("declare @tv table (a int, b int); insert into @tv (a) values (1, 2)", 110);
    }

    [TestMethod]
    public void TempTable_TakesTheSameArityRules()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table #tmp (a int, b int)").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert into #tmp values (1, 2, 3)").ExecuteNonQuery());
        AreEqual(213, ex.Number);
    }
}
