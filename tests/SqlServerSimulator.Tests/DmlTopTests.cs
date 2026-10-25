using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>TOP (expr) [PERCENT]</c> on UPDATE / DELETE /
/// INSERT — the row-count cap SSMS's "Edit Top 200 Rows" emits. Facts
/// probed against SQL Server 2025: parens are mandatory (legacy no-paren
/// form → Msg 102); non-PERCENT values must be non-negative integers
/// (Msg 1060 non-integer / NULL, Msg 127 negative); PERCENT values must be
/// numeric in [0, 100] (Msg 1031, Msg 1014 for NULL) and the cap is
/// <c>ceil(count * pct / 100)</c>. Only the affected COUNT is asserted —
/// which rows the cap keeps is arbitrary scan order.
/// </summary>
[TestClass]
public sealed class DmlTopTests
{
    private static Simulation Seed(int rows = 10)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity primary key, v int)");
        for (var i = 1; i <= rows; i++)
            _ = simulation.ExecuteNonQuery($"insert t (v) values ({i})");
        return simulation;
    }

    private static int Count(Simulation simulation, string where) =>
        simulation.ExecuteScalar<int>($"select count(*) from t where {where}");

    // ---- UPDATE ----

    [TestMethod]
    public void UpdateTop_IntegerLiteral_CapsRows()
    {
        var simulation = Seed();
        AreEqual(2, simulation.ExecuteNonQuery("update top (2) t set v = v + 100"));
        AreEqual(2, Count(simulation, "v > 100"));
    }

    [TestMethod]
    public void UpdateTop_ArithmeticExpression_CapsRows()
    {
        var simulation = Seed();
        AreEqual(3, simulation.ExecuteNonQuery("update top (1 + 2) t set v = v + 100"));
    }

    [TestMethod]
    public void UpdateTop_Variable_CapsRows()
    {
        var simulation = Seed();
        AreEqual(4, simulation.ExecuteNonQuery("declare @n int = 4; update top (@n) t set v = v + 100"));
    }

    [TestMethod]
    public void UpdateTop_Zero_AffectsNoRows()
    {
        var simulation = Seed();
        AreEqual(0, simulation.ExecuteNonQuery("update top (0) t set v = v + 100"));
        AreEqual(0, Count(simulation, "v > 100"));
    }

    [TestMethod]
    public void UpdateTop_LargerThanRowCount_CapsAtRowCount()
    {
        var simulation = Seed();
        AreEqual(10, simulation.ExecuteNonQuery("update top (999) t set v = v + 100"));
    }

    [TestMethod]
    public void UpdateTop_BigintValue_CapsAtRowCount()
    {
        var simulation = Seed();
        AreEqual(10, simulation.ExecuteNonQuery("update top (9999999999) t set v = v + 100"));
    }

    [TestMethod]
    public void UpdateTop_WithWhere_CapsWithinFilter()
    {
        var simulation = Seed();
        AreEqual(2, simulation.ExecuteNonQuery("update top (2) t set v = v + 1000 where v > 5"));
        AreEqual(2, Count(simulation, "v > 1000"));
    }

    [TestMethod]
    public void UpdateTop_RowCountReflectsCap()
    {
        var simulation = Seed();
        AreEqual(2, simulation.ExecuteScalar<int>("update top (2) t set v = v + 1; select @@rowcount"));
    }

    [TestMethod]
    public void UpdateTop_OutputReturnsCappedSet()
    {
        var simulation = Seed();
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("update top (3) t set v = v + 1 output inserted.id");
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
            count++;
        AreEqual(3, count);
    }

    // ---- UPDATE PERCENT ----

    [TestMethod]
    public void UpdateTopPercent_Fifty_RoundsUp()
    {
        var simulation = Seed(10);
        AreEqual(5, simulation.ExecuteNonQuery("update top (50) percent t set v = v + 1000"));
    }

    [TestMethod]
    public void UpdateTopPercent_FiftyOfThree_CeilingTwo()
    {
        var simulation = Seed(3);
        AreEqual(2, simulation.ExecuteNonQuery("update top (50) percent t set v = v + 1000"));
    }

    [TestMethod]
    public void UpdateTopPercent_FractionalValue_CeilingOfProduct()
    {
        var simulation = Seed(10);
        // 10 * 33.3 / 100 = 3.33 -> ceil = 4
        AreEqual(4, simulation.ExecuteNonQuery("update top (33.3) percent t set v = v + 1000"));
    }

    [TestMethod]
    public void UpdateTopPercent_TinyNonZero_RoundsUpToOne()
    {
        var simulation = Seed(10);
        // 10 * 2 / 100 = 0.2 -> ceil = 1
        AreEqual(1, simulation.ExecuteNonQuery("update top (2) percent t set v = v + 1000"));
    }

    [TestMethod]
    public void UpdateTopPercent_Zero_AffectsNoRows()
    {
        var simulation = Seed(10);
        AreEqual(0, simulation.ExecuteNonQuery("update top (0) percent t set v = v + 1000"));
    }

    [TestMethod]
    public void UpdateTopPercent_Hundred_AffectsAll()
    {
        var simulation = Seed(10);
        AreEqual(10, simulation.ExecuteNonQuery("update top (100) percent t set v = v + 1000"));
    }

    // ---- Error paths ----

    [TestMethod]
    public void UpdateTop_NoParens_RaisesMsg102()
        => new Simulation().AssertSqlError("update top 2 t set v = 1", 102);

    [TestMethod]
    public void UpdateTop_Negative_RaisesMsg127()
        => Seed().AssertSqlError("update top (-1) t set v = 1", 127, "A TOP N or FETCH rowcount value may not be negative.");

    [TestMethod]
    public void UpdateTop_Null_RaisesMsg1060()
        => Seed().AssertSqlError("update top (null) t set v = 1", 1060, "The number of rows provided for a TOP or FETCH clauses row count parameter must be an integer.");

    [TestMethod]
    public void UpdateTop_NonInteger_RaisesMsg1060()
        => Seed().AssertSqlError("update top (2.9) t set v = 1", 1060);

    [TestMethod]
    public void UpdateTopPercent_OverHundred_RaisesMsg1031()
        => Seed().AssertSqlError("update top (150) percent t set v = 1", 1031, "Percent values must be between 0 and 100.");

    [TestMethod]
    public void UpdateTopPercent_Negative_RaisesMsg1031()
        => Seed().AssertSqlError("update top (-5) percent t set v = 1", 1031);

    [TestMethod]
    public void UpdateTopPercent_Null_RaisesMsg1014()
        => Seed().AssertSqlError("update top (null) percent t set v = 1", 1014, "A TOP or FETCH clause contains an invalid value.");

    // ---- DELETE ----

    [TestMethod]
    public void DeleteTop_WithFrom_CapsRows()
    {
        var simulation = Seed();
        AreEqual(3, simulation.ExecuteNonQuery("delete top (3) from t"));
        AreEqual(7, simulation.ExecuteScalar<int>("select count(*) from t"));
    }

    [TestMethod]
    public void DeleteTop_WithoutFrom_CapsRows()
    {
        var simulation = Seed();
        AreEqual(2, simulation.ExecuteNonQuery("delete top (2) t"));
        AreEqual(8, simulation.ExecuteScalar<int>("select count(*) from t"));
    }

    [TestMethod]
    public void DeleteTopPercent_RoundsUp()
    {
        var simulation = Seed(10);
        AreEqual(5, simulation.ExecuteNonQuery("delete top (50) percent from t"));
    }

    [TestMethod]
    public void DeleteTop_NoParens_RaisesMsg102()
        => new Simulation().AssertSqlError("delete top 2 from t", 102);

    [TestMethod]
    public void DeleteTop_Negative_RaisesMsg127()
        => Seed().AssertSqlError("delete top (-1) from t", 127);

    // ---- INSERT ----

    [TestMethod]
    public void InsertTop_MultiValues_CapsInsertedRows()
    {
        var simulation = Seed(0);
        AreEqual(2, simulation.ExecuteNonQuery("insert top (2) t (v) values (91), (92), (93), (94)"));
        AreEqual(2, simulation.ExecuteScalar<int>("select count(*) from t"));
    }

    [TestMethod]
    public void InsertTop_Into_Select_CapsInsertedRows()
    {
        var simulation = Seed(10);
        AreEqual(2, simulation.ExecuteNonQuery("insert top (2) into t (v) select v from t"));
        AreEqual(12, simulation.ExecuteScalar<int>("select count(*) from t"));
    }

    [TestMethod]
    public void InsertTopPercent_Select_RoundsUp()
    {
        var simulation = Seed(10);
        AreEqual(5, simulation.ExecuteNonQuery("insert top (50) percent into t (v) select v from t"));
    }

    // ---- Harvested SSMS "Edit Top 200 Rows" shape ----

    [TestMethod]
    public void UpdateTop200_ParameterizedMultiPredicate_UpdatesOneRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table Invoices (
                InvoiceID int identity primary key,
                CustomerID int,
                InvoiceDate datetime,
                Total money);
            insert Invoices (CustomerID, InvoiceDate, Total)
            values (1, '2020-01-01', 100), (2, '2020-02-01', 200), (3, '2020-03-01', 300)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(
            "update top (200) Invoices set InvoiceDate = @p " +
            "where InvoiceID = @k1 and CustomerID = @k2 and Total = @k3",
            ("@p", new DateTime(2021, 6, 15)),
            ("@k1", 2),
            ("@k2", 2),
            ("@k3", 200m));
        AreEqual(1, command.ExecuteNonQuery());

        using var rowcount = connection.CreateCommand("select @@rowcount");
        AreEqual(1, rowcount.ExecuteScalar());
        AreEqual(new DateTime(2021, 6, 15), simulation.ExecuteScalar<DateTime>("select InvoiceDate from Invoices where InvoiceID = 2"));
    }

    /// <summary>
    /// <c>INSERT TOP (n) … SELECT</c> and <c>SET ROWCOUNT</c> stop reading the
    /// source once they have their rows, so a later row's error never raises
    /// and draws no identity value — an <c>ORDER BY</c>'d source too (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("insert top (2) t (v) select 10 / (k - 3) from src")]
    [DataRow("insert top (2) t (v) select 10 / (k - 3) from src order by k")]
    [DataRow("insert top (2) into t (v) (select 10 / (k - 3) from src)")]
    [DataRow("set rowcount 2; insert t (v) select 10 / (k - 3) from src; set rowcount 0")]
    public void InsertTop_StopsReadingTheSource(string insert)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table src (k int); insert src values (1), (2), (3), (4), (5)",
            "create table t (id int identity, v int)",
            insert,
            "insert t (v) values (0)");
        AreEqual("1:-5,2:-10,3:0", sim.ExecuteScalar("select string_agg(concat(id, ':', v), ',') within group (order by id) from t"));
    }

    /// <summary>
    /// <c>DELETE</c> and <c>UPDATE</c> stop walking their target once a
    /// row-count <c>TOP</c> — or with none, <c>SET ROWCOUNT</c> — has its rows,
    /// so a WHERE or SET only a later row would raise on never runs (probed
    /// 2026-10-25 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("delete top (1) from d where 1 / x = 1", "2:0,3:2")]
    [DataRow("set rowcount 1; delete from d where 1 / x >= 0; set rowcount 0", "2:0,3:2")]
    [DataRow("set rowcount 2; delete top (1) from d where 1 / x >= 0; set rowcount 0", "2:0,3:2")]
    [DataRow("set rowcount 1; update d set x = 10 / x; set rowcount 0", "1:10,2:0,3:2")]
    [DataRow("set rowcount 1; update d set x = x + 100 where 1 / x >= 0; set rowcount 0", "1:101,2:0,3:2")]
    public void DeleteAndUpdate_StopWalkingTheTarget(string statement, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table d (id int, x int); insert d values (1, 1), (2, 0), (3, 2)", statement);
        AreEqual(expected, sim.ExecuteScalar("select string_agg(concat(id, ':', x), ',') within group (order by id) from d"));
    }

    /// <summary>A cap the walk meets only past the failing row raises its error, as real does.</summary>
    [TestMethod]
    [DataRow("delete top (2) from d where 1 / x >= 0")]
    [DataRow("set rowcount 1; delete top (2) from d where 1 / x >= 0")]
    [DataRow("delete top (50) percent from d where 1 / x >= 0")]
    public void DeleteTop_NeedingALaterRow_RaisesItsError(string statement) =>
        _ = new Simulation().AssertSqlError($"create table d (id int, x int); insert d values (1, 1), (2, 0), (3, 2); {statement}", 8134);

    private const string PercentSource = "create table src (k int primary key, v varchar(10)); insert src values (1, '1'), (2, '2'), (3, 'x'), (4, '4');";

    /// <summary>
    /// An unordered <c>TOP (n) PERCENT</c> over a lone unfiltered table counts
    /// its rows before computing any select list, so a row past the cap never
    /// raises; a filter or join, folded-true <c>WHERE 1 = 1</c> aside, has
    /// real compute every row's first (probed 2026-10-11 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select top 50 percent cast(v as int) n from src", null)]
    [DataRow("select top 50 percent cast(v as int) n from src where 1 = 1", null)]
    [DataRow("select top 50 percent cast(v as int) n from src where k > 0", 245)]
    [DataRow("select top 50 percent cast(a.v as int) n from src a join src b on a.k = b.k", 245)]
    [DataRow("select distinct top 50 percent cast(v as int) n from src", 245)]
    public void SelectTopPercent_ComputesTheSelectListPastTheCount(string query, int? error)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(PercentSource);
        if (error is { } number)
            _ = simulation.AssertSqlError(query, number);
        else
            AreEqual("1,2", simulation.ExecuteScalar($"select string_agg(n, ',') from ({query}) q"));
    }

    /// <summary>
    /// A percentage read as the statement runs is judged past the count: a
    /// row's error comes first, and an empty input still refuses it (probed
    /// 2026-10-11 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select top (@p) percent v from src where 10 / (k - 3) > -50", 8134)]
    [DataRow("select top (@p) percent v from src where k > 100", 1031)]
    [DataRow("insert top (@p) percent src (k, v) select k + 10, v from src where 10 / (k - 3) > -50", 8134)]
    public void TopPercentVariable_IsJudgedPastTheCount(string statement, int error)
        => new Simulation().AssertSqlError($"{PercentSource} declare @p float = 150; {statement}", error);

    /// <summary>
    /// An <c>INSERT TOP (n) PERCENT</c>'s source computes its select list past
    /// the count whatever it filters or joins, the rows it writes drawing
    /// identity values up to the one that fails; a source that fails while it
    /// is counted draws none (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("insert top (50) percent t (v) select cast(v as int) from src", "1:1,2:2,3:0")]
    [DataRow("insert top (50) percent t (v) select cast(a.v as int) from src a join src b on a.k = b.k where b.k > 0", "1:1,2:2,3:0")]
    [DataRow("insert top (75) percent t (v) select cast(v as int) from src", "4:0")]
    [DataRow("insert top (50) percent t (v) select top 3 cast(v as int) from src order by k", "1:0")]
    [DataRow("insert top (50) percent t (v) select cast(v as int) from src union all select 5", "1:0")]
    public void InsertTopPercent_ComputesTheSourcePastTheCount(string insert, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            PercentSource,
            "create table t (id int identity, v int)",
            $"begin try {insert} end try begin catch end catch",
            "insert t (v) values (0)");
        AreEqual(expected, sim.ExecuteScalar("select string_agg(concat(id, ':', v), ',') within group (order by id) from t"));
    }

    /// <summary>
    /// A DML statement's own <c>TOP</c> replaces <c>SET ROWCOUNT</c> rather
    /// than composing with it, which caps only a statement without one
    /// (probed 2026-10-11 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("update top (2) t set v = 0", 2)]
    [DataRow("delete top (4) t", 4)]
    [DataRow("delete top (50) percent t", 5)]
    [DataRow("insert top (100) percent t (v) select v from t", 10)]
    [DataRow("insert top (3) t (v) select v from t", 3)]
    [DataRow("update t set v = 0", 1)]
    [DataRow("insert t (v) select v from t", 1)]
    public void DmlTop_ReplacesSetRowCount(string statement, int expected)
    {
        var simulation = Seed(10);
        AreEqual(expected, simulation.ExecuteScalar<int>($"set rowcount 1; {statement}; select @@rowcount"));
    }
}
