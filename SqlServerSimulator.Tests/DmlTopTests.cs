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
}
