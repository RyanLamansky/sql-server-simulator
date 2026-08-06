using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET DATEFIRST n</c> and the date parts that read it, probed against SQL
/// Server 2025 (2026-08-06). See <c>docs/claude/scalars.md</c>.
/// </summary>
[TestClass]
public sealed class SetDateFirstTests
{
    private static object? Scalar(string commandText) => new Simulation().ExecuteScalar(commandText);

    /// <summary><c>@@DATEFIRST</c> defaults to 7 (Sunday) and tracks the option.</summary>
    [TestMethod]
    public void DateFirstReadsBackAsTinyint()
    {
        AreEqual((byte)7, Scalar("SELECT @@DATEFIRST"));
        AreEqual((byte)1, Scalar("SET DATEFIRST 1; SELECT @@DATEFIRST"));
        AreEqual((byte)3, Scalar("DECLARE @d int = 3; SET DATEFIRST @d; SELECT @@DATEFIRST"));
    }

    /// <summary>
    /// <c>DATEPART(weekday, …)</c> counts from the named first weekday.
    /// 2026-08-02 is a Sunday, 2026-08-03 a Monday, 2026-08-08 a Saturday.
    /// </summary>
    [TestMethod]
    [DataRow(7, 1, 2, 7)]
    [DataRow(1, 7, 1, 6)]
    [DataRow(3, 5, 6, 4)]
    [DataRow(5, 3, 4, 2)]
    public void WeekdayCountsFromTheNamedDay(int dateFirst, int sunday, int monday, int saturday)
    {
        AreEqual(sunday, Scalar($"SET DATEFIRST {dateFirst}; SELECT DATEPART(weekday, '2026-08-02')"));
        AreEqual(monday, Scalar($"SET DATEFIRST {dateFirst}; SELECT DATEPART(dw, '2026-08-03')"));
        AreEqual(saturday, Scalar($"SET DATEFIRST {dateFirst}; SELECT DATEPART(dw, '2026-08-08')"));
    }

    /// <summary>
    /// The weekday NAME is the calendar day's own, so <c>DATENAME</c> ignores
    /// the option where <c>DATEPART</c> follows it.
    /// </summary>
    [TestMethod]
    public void DateNameOfWeekdayIgnoresDateFirst()
    {
        AreEqual("Friday", Scalar("SET DATEFIRST 5; SELECT DATENAME(dw, '2026-08-07')"));
        AreEqual("Sunday", Scalar("SET DATEFIRST 5; SELECT DATENAME(weekday, '2026-08-02')"));
        AreEqual(1, Scalar("SET DATEFIRST 5; SELECT DATEPART(dw, '2026-08-07')"));
    }

    /// <summary>
    /// <c>DATEPART(week, …)</c> moves with the option — 2026-01-04 is a Sunday,
    /// so it opens week 2 under DATEFIRST 7 and stays in week 1 under
    /// DATEFIRST 1 — while <c>iso_week</c> stays Monday-anchored.
    /// </summary>
    [TestMethod]
    public void WeekMovesWithDateFirstButIsoWeekDoesNot()
    {
        AreEqual(2, Scalar("SET DATEFIRST 7; SELECT DATEPART(week, '2026-01-04')"));
        AreEqual(1, Scalar("SET DATEFIRST 1; SELECT DATEPART(week, '2026-01-04')"));
        AreEqual(2, Scalar("SET DATEFIRST 1; SELECT DATEPART(week, '2026-01-05')"));
        AreEqual(1, Scalar("SET DATEFIRST 4; SELECT DATEPART(week, '2026-01-01')"));
        AreEqual(2, Scalar("SET DATEFIRST 4; SELECT DATEPART(week, '2026-01-08')"));
        AreEqual(2, Scalar("SET DATEFIRST 1; SELECT DATEPART(iso_week, '2026-01-05')"));
        AreEqual(2, Scalar("SET DATEFIRST 7; SELECT DATEPART(iso_week, '2026-01-05')"));
    }

    /// <summary>
    /// <c>DATEDIFF(week, …)</c> is DATEFIRST-independent: the boundary it counts
    /// stays Saturday-to-Sunday whatever the option says (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    public void DateDiffWeekIgnoresDateFirst(int dateFirst)
    {
        AreEqual(0, Scalar($"SET DATEFIRST {dateFirst}; SELECT DATEDIFF(week, '2026-08-02', '2026-08-03')"));
        AreEqual(1, Scalar($"SET DATEFIRST {dateFirst}; SELECT DATEDIFF(week, '2026-08-01', '2026-08-02')"));
    }

    /// <summary>
    /// <c>DATETRUNC(week, …)</c> anchors on the named first weekday; the ISO
    /// variant does not. 2026-08-05 is a Wednesday.
    /// </summary>
    [TestMethod]
    public void DateTruncWeekAnchorsOnDateFirst()
    {
        AreEqual(new DateTime(2026, 8, 2), Scalar("SET DATEFIRST 7; SELECT DATETRUNC(week, CAST('2026-08-05' AS datetime2(0)))"));
        AreEqual(new DateTime(2026, 8, 3), Scalar("SET DATEFIRST 1; SELECT DATETRUNC(week, CAST('2026-08-05' AS datetime2(0)))"));
        AreEqual(new DateTime(2026, 8, 5), Scalar("SET DATEFIRST 3; SELECT DATETRUNC(week, CAST('2026-08-05' AS datetime2(0)))"));
        AreEqual(new DateTime(2026, 8, 3), Scalar("SET DATEFIRST 7; SELECT DATETRUNC(iso_week, CAST('2026-08-05' AS datetime2(0)))"));
    }

    /// <summary>
    /// The body's own <c>SET DATEFIRST</c> binds while it runs and reverts on
    /// return, for a procedure and for dynamic SQL alike.
    /// </summary>
    [TestMethod]
    public void ModuleScopedSetReverts()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using (var create = connection.CreateCommand("CREATE PROC p AS BEGIN SET DATEFIRST 3; SELECT @@DATEFIRST; END"))
        {
            _ = create.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand("SET DATEFIRST 7; EXEC p;"))
        {
            AreEqual((byte)3, command.ExecuteScalar());
        }

        using (var command = connection.CreateCommand("SELECT @@DATEFIRST"))
        {
            AreEqual((byte)7, command.ExecuteScalar());
        }

        using (var command = connection.CreateCommand("EXEC ('SET DATEFIRST 2; SELECT @@DATEFIRST;');"))
        {
            AreEqual((byte)2, command.ExecuteScalar());
        }

        using (var command = connection.CreateCommand("SELECT @@DATEFIRST"))
        {
            AreEqual((byte)7, command.ExecuteScalar());
        }
    }

    /// <summary>
    /// Argument errors, as probed: outside 1..7 is Msg 2742 echoing the value
    /// (a NULL variable rendering as 0), and a parameter that isn't an
    /// <c>int</c> is Msg 2743 state 3.
    /// </summary>
    [TestMethod]
    public void ArgumentErrors()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError("SET DATEFIRST 8", 2742, "SET DATEFIRST 8 is out of range.");
        simulation.AssertSqlError("SET DATEFIRST 0", 2742, "SET DATEFIRST 0 is out of range.");
        simulation.AssertSqlError("SET DATEFIRST -1", 2742, "SET DATEFIRST -1 is out of range.");
        simulation.AssertSqlError("DECLARE @d int = 9; SET DATEFIRST @d;", 2742, "SET DATEFIRST 9 is out of range.");
        simulation.AssertSqlError("DECLARE @d int = NULL; SET DATEFIRST @d;", 2742, "SET DATEFIRST 0 is out of range.");
        simulation.AssertSqlError("SET DATEFIRST 3000000000", 2743, "SET DATEFIRST option requires integer parameter.");
        var wideVariable = simulation.AssertSqlError("DECLARE @b bigint = 9; SET DATEFIRST @b;", 2743);
        AreEqual((byte)3, wideVariable.State);
    }
}
