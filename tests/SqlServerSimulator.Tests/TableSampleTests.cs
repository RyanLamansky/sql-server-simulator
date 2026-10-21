using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The checks <c>TABLESAMPLE</c> and <c>REPEATABLE</c> make of their values
/// as the statement compiles to run (probed 2026-10-21 against SQL Server
/// 2025); the sample itself returns every row.
/// </summary>
[TestClass]
public sealed class TableSampleTests
{
    private const string Fixture = "create table zt (a int); insert zt values (1), (2);";

    [TestMethod]
    [DataRow("(10)")]
    [DataRow("(5 + 5 percent)")]
    [DataRow("('10' percent)")]
    [DataRow("((10) percent)")]
    [DataRow("(1e1 percent)")]
    [DataRow("($5 percent)")]
    [DataRow("(cast(5 as float) percent)")]
    [DataRow("(100 percent)")]
    [DataRow("(2147483648 rows)")]
    [DataRow("(9223372036854775807 rows)")]
    [DataRow("(cast(5 as tinyint) rows)")]
    [DataRow("(10 percent) repeatable (abs(-2))")]
    [DataRow("system (2 rows) repeatable (1 + 1)")]
    public void AValueRealAccepts_SamplesEveryRow(string clause)
        => AreEqual(2, new Simulation().ExecuteScalar($"{Fixture} select count(*) from zt tablesample {clause}"));

    [TestMethod]
    [DataRow("(101 percent)", "101.000000")]
    [DataRow("(-1 percent)", "-1.000000")]
    [DataRow("(100.0000001 percent)", "100.000000")]
    [DataRow("(-1e-300 percent)", "-0.000000")]
    public void APercentageOutside0To100_RaisesMsg476(string clause, string shown)
        => new Simulation().AssertSqlError($"{Fixture} select count(*) from zt tablesample {clause}", 476,
            $"Invalid PERCENT tablesample size \"{shown}\" for table \"zt\". The PERCENT tablesample size must be between 0 and 100.");

    [TestMethod]
    [DataRow("(10.5 rows)", 0)]
    [DataRow("(N'10' rows)", 0)]
    [DataRow("(cast(5 as decimal(5, 0)) rows)", 0)]
    [DataRow("(cast(1 as bit) rows)", 0)]
    [DataRow("(cast(null as int) rows)", 0)]
    [DataRow("(10 percent) repeatable ('5')", 0)]
    [DataRow("(99999999999999999999 rows)", 1)]
    [DataRow("(1 / 0 rows)", 1)]
    [DataRow("(10 percent) repeatable (@@spid)", 1)]
    public void ARowCountOrSeedNotAnInteger_RaisesMsg477(string clause, int state)
    {
        var ex = new Simulation().AssertSqlError($"{Fixture} select count(*) from zt tablesample {clause}", 477);
        AreEqual(state, ex.State);
        AreEqual("Invalid ROWS value or REPEATABLE seed in the TABLESAMPLE clause for table \"zt\". The value or seed must be an integer.", ex.Errors[0].Message);
    }

    [TestMethod]
    [DataRow("zt tablesample (0 rows)", "0", "zt")]
    [DataRow("dbo.zt tablesample (-1 rows)", "-1", "dbo.zt")]
    [DataRow("zt tablesample (10 percent) repeatable (-3)", "-3", "zt")]
    public void ARowCountOrSeedBelow1_RaisesMsg479(string source, string shown, string table)
        => new Simulation().AssertSqlError($"{Fixture} select count(*) from {source}", 479,
            $"Invalid ROWS value or REPEATABLE seed \"{shown}\" in the TABLESAMPLE clause for table \"{table}\". The value or seed must be greater than 0.");

    [TestMethod]
    [DataRow("((select 5) percent)")]
    [DataRow("('abc' percent)")]
    [DataRow("(null percent)")]
    [DataRow("(1 / 0 percent)")]
    [DataRow("(rand() percent)")]
    public void APercentageThatDoesntFold_RaisesMsg482(string clause)
        => new Simulation().AssertSqlError($"{Fixture} select count(*) from zt tablesample {clause}", 482,
            "Non-constant or invalid expression is in the TABLESAMPLE or the REPEATABLE clause.");

    [TestMethod]
    [DataRow("(0x05 percent)", 206)]
    [DataRow("(newid() percent)", 206)]
    [DataRow("(getdate() percent)", 257)]
    [DataRow("(a percent)", 207)]
    [DataRow("(zt.a percent)", 4104)]
    [DataRow("(@n percent)", 497)]
    [DataRow("(10 percent) repeatable (@n)", 497)]
    public void ANameOrAnOperandFloatRefuses_RaisesItsError(string clause, int number)
        => _ = new Simulation().AssertSqlError($"{Fixture} declare @n int = 10; select count(*) from zt tablesample {clause}", number);

    [TestMethod]
    [DataRow("(percent)", 156)]
    [DataRow("()", 102)]
    [DataRow("(10 bogus)", 102)]
    [DataRow("(10 percent percent)", 156)]
    [DataRow("(10, 5)", 102)]
    [DataRow("(10 percent) repeatable ()", 102)]
    [DataRow("(10 percent) repeatable (5, 6)", 102)]
    public void AMalformedClause_IsASyntaxError(string clause, int number)
        => _ = new Simulation().AssertSqlError($"{Fixture} select count(*) from zt tablesample {clause}", number);

    [TestMethod]
    public void TheCheck_RunsOnlyWhereTheStatementRuns()
        => AreEqual("ran", new Simulation().ExecuteScalar($"{Fixture} if 1 = 0 select count(*) from zt tablesample (101 percent); select 'ran'"));

    [TestMethod]
    public void ARefusal_ReportsAheadOfTheWhereClausesNames()
    {
        var ex = new Simulation().AssertSqlError($"{Fixture} select count(*) from zt tablesample (-5 percent) where zzbad = 1 and zzbad2 = 1", 476);
        AreEqual("476,207,207", string.Join(",", ex.Errors.Select(static error => error.Number)));
    }
}
