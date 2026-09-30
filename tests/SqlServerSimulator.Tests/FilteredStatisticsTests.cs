using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>CREATE STATISTICS … WHERE</c>, and the refusals and dependencies it
/// shares with a filtered index (probed 2026-09-30 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class FilteredStatisticsTests
{
    private const string Table = "create table t (a int, b int, c varchar(10))";

    [TestMethod]
    [DataRow("b > 1", "([b]>(1))")]
    [DataRow("b > 1 and c = 'x'", "([b]>(1) AND [c]='x')")]
    [DataRow("(b is not null)", "([b] IS NOT NULL)")]
    [DataRow("b in (1,2)", "([b] IN ((1), (2)))")]
    [DataRow("b in (1)", "([b]=(1))")]
    [DataRow("b = 1 and a <> 2 and c is null", "([b]=(1) AND [a]<>(2) AND [c] IS NULL)")]
    [DataRow("b > -1", "([b]>(-1))")]
    public void Catalog_CarriesTheFilter(string filter, string definition)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"{Table}; create statistics s on t (a) where {filter}");
        AreEqual(definition, simulation.ExecuteScalar("select filter_definition from sys.stats where name = 's'"));
        IsTrue((bool)simulation.ExecuteScalar("select has_filter from sys.stats where name = 's'")!);
        IsTrue((bool)simulation.ExecuteScalar("select user_created from sys.stats where name = 's'")!);
    }

    [TestMethod]
    public void OptionsFollowTheFilter_AndBeforeItIsASyntaxError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"{Table}; create statistics s on t (a) where b = 1 with fullscan, norecompute");
        IsTrue((bool)simulation.ExecuteScalar("select no_recompute from sys.stats where name = 's'")!);
        _ = simulation.AssertSqlError("create statistics s2 on t (a) with fullscan where b = 1", 156);
    }

    [TestMethod]
    [DataRow("b = 1 or b = 2", 156)]
    [DataRow("c like 'x%'", 156)]
    [DataRow("b between 1 and 2", 156)]
    [DataRow("b not in (1)", 102)]
    [DataRow("abs(b) = 1", 10735)]
    [DataRow("a = b", 10735)]
    [DataRow("1 = 1", 10735)]
    [DataRow("null = b", 10735)]
    [DataRow("b = @@spid", 10735)]
    [DataRow("b = null", 10620)]
    [DataRow("b <> null", 10620)]
    [DataRow("b in (1, null)", 10620)]
    [DataRow("b = (select 1)", 1046)]
    [DataRow("zz = 1", 207)]
    public void Filter_Refusals(string filter, int number)
        => _ = new Simulation().AssertSqlError($"{Table}; create statistics s on t (a) where {filter}", number);

    [TestMethod]
    public void AVariable_IsMsg112State4()
    {
        var error = new Simulation().AssertSqlError($"{Table}; declare @x int = 1; create statistics s on t (a) where b = @x", 112);
        AreEqual(4, error.State);
        AreEqual("Variables are not allowed in the CREATE INDEX statement.", error.Errors[0].Message);
    }

    [TestMethod]
    public void TheFilterBindsFirst_ItsColumnsBeforeTheKeys_AndItsTableBeforeTheStatementsOwn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Table);
        AreEqual(207, simulation.AssertSqlError("create statistics s on t (zz) where yy = 1", 207).Number);
        AreEqual(208, simulation.AssertSqlError("create statistics s on nosuch (a) where b = 1", 208).Number);
        AreEqual(1088, simulation.AssertSqlError("create statistics s on nosuch (a)", 1088).Number);
        AreEqual(1911, simulation.AssertSqlError("create statistics s on t (zz)", 1911).Number);
    }

    [TestMethod]
    public void AComputedColumnInTheFilter_Is10609_NamingTheTableAsWritten()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b as a + 1)");
        simulation.AssertSqlError("create statistics s on t (a) where b = 2", 10609,
            "Filtered statistics 's' cannot be created on table 't' because the column 'b' in the filter expression is a computed column. Rewrite the filter expression so that it does not include this column.");
        simulation.AssertSqlError("create statistics s on dbo.t (a) where b = 2", 10609,
            "Filtered statistics 's' cannot be created on table 'dbo.t' because the column 'b' in the filter expression is a computed column. Rewrite the filter expression so that it does not include this column.");
    }

    [TestMethod]
    public void Dependencies_BlockDropAndAlterOfTheirColumns()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"{Table}; create statistics s on t (a) where b = 1");
        var drop = simulation.AssertSqlError("alter table t drop column b", 5074);
        CollectionAssert.AreEqual(new[] { 5074, 4922 }, drop.Errors.Select(static e => e.Number).ToArray());
        AreEqual("The statistics 's' is dependent on column 'b'.", drop.Errors[0].Message);
        _ = simulation.AssertSqlError("alter table t drop column a", 5074);
        _ = simulation.AssertSqlError("alter table t alter column b bigint", 5074);
        _ = simulation.AssertSqlError("alter table t alter column b int not null", 5074);
        _ = simulation.AssertSqlError("alter table t alter column a bigint", 5074);
        // Widening a key column isn't a change the statistic can see; its filter's is.
        _ = simulation.ExecuteNonQuery("create statistics s2 on t (c)");
        _ = simulation.ExecuteNonQuery("alter table t alter column c varchar(20)");
        _ = simulation.ExecuteNonQuery("drop statistics t.s; drop statistics t.s2");
        _ = simulation.ExecuteNonQuery("alter table t drop column b");
    }

    [TestMethod]
    public void DroppingAnotherColumn_RenumbersTheStatistic()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int, c int); create statistics s on t (c) where b = 1; alter table t drop column a");
        AreEqual("c", simulation.ExecuteScalar("select col_name(object_id, column_id) from sys.stats_columns where object_id = object_id('t')"));
        AreEqual("([b]=(1))", simulation.ExecuteScalar("select filter_definition from sys.stats where name = 's'"));
    }

    [TestMethod]
    public void RenamingAFilterColumn_IsRefusedFromSpRename_LineAndAll()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"{Table}; create statistics s on t (a) where b = 1");
        var refusal = simulation.AssertSqlError("exec sp_rename 't.b', 'bb', 'COLUMN'", 5074);
        AreEqual("sp_rename", refusal.Errors[0].Procedure);
        AreEqual(905, refusal.Errors[0].LineNumber);
        AreEqual("RENAME COLUMN b failed because one or more objects access this column.", refusal.Errors[1].Message);
        // A key column is free to move.
        _ = simulation.ExecuteNonQuery("exec sp_rename 't.a', 'aa', 'COLUMN'");
    }

    [TestMethod]
    public void AFilteredIndexHoldsItsFilterColumns_Too()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"{Table}; create index ix on t (a) where b = 1");
        _ = simulation.AssertSqlError("alter table t drop column b", 5074);
        _ = simulation.AssertSqlError("alter table t alter column b bigint", 5074);
        var rename = simulation.AssertSqlError("exec sp_rename 't.b', 'bb', 'COLUMN'", 5074);
        AreEqual("The index 'ix' is dependent on column 'b'.", rename.Errors[0].Message);
    }

    [TestMethod]
    public void IndexFilters_ShareTheNewRefusals()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Table);
        _ = simulation.AssertSqlError("create index ix on t (a) where b = null", 10620);
        _ = simulation.AssertSqlError("declare @x int = 1; create index ix on t (a) where b = @x", 112);
        _ = simulation.AssertSqlError("create index ix on t (a) where b = (select 1)", 1046);
        _ = simulation.AssertSqlError("create index ix on nosuch (a) where b = 1", 208);
        _ = simulation.AssertSqlError("create index ix on nosuch (a)", 1088);
        _ = simulation.AssertSqlError("create index ix on t (zz) where yy = 1", 207);
        _ = simulation.ExecuteNonQuery("create index ix on t (a) where b in (1)");
        AreEqual("([b]=(1))", simulation.ExecuteScalar("select filter_definition from sys.indexes where name = 'ix'"));
    }

    [TestMethod]
    public void HistogramAndHelpStats_ReadTheFilteredRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, b int); insert t values (1,1),(2,2),(3,2); create statistics s on t (a) where b = 2 with fullscan");
        AreEqual("2,3", string.Join(",", simulation.ExecuteReader("dbcc show_statistics('t','s') with histogram, no_infomsgs").EnumerateRecords().Select(r => r.GetInt32(0))));
        AreEqual("s|a", simulation.ExecuteBatchesReader("exec sp_helpstats 't'").EnumerateRecords().Select(r => $"{r.GetString(0)}|{r.GetString(1)}").Single());
        _ = simulation.ExecuteNonQuery("create statistics e on t (a) where b = 9");
        AreEqual(0, simulation.ExecuteReader("dbcc show_statistics('t','e') with histogram, no_infomsgs").EnumerateRecords().Count());
    }
}
