using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the parens-less <c>CURRENT_DATE</c> identifier (SQL Server
/// 2022+). Returns the current system date as a <c>date</c> value;
/// reads the per-statement frozen instant via the same plumbing as
/// <c>SYSDATETIME</c> / <c>GETUTCDATE</c>.
/// </summary>
[TestClass]
public sealed class CurrentDateTests
{
    [TestMethod]
    public void CurrentDate_ReturnsTodayMidnight()
    {
        var today = DateTime.UtcNow.Date;
        var result = new Simulation().ExecuteScalar("select current_date");
        AreEqual(today, result);
    }

    [TestMethod]
    public void CurrentDate_Type_IsDate()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select current_date";
        using var reader = cmd.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("date", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void CurrentDate_Frozen_PerStatement()
        => AreEqual(1, new Simulation().ExecuteScalar("select iif(current_date = current_date, 1, 0)"));

    [TestMethod]
    public void CurrentDate_EquivalentToSysDateTimeDateOnly()
        => AreEqual(1, new Simulation().ExecuteScalar("select iif(current_date = cast(sysdatetime() as date), 1, 0)"));

    [TestMethod]
    public void CurrentDate_WithParens_RaisesSyntaxError()
        => new Simulation().AssertSqlError("select current_date()", 102);
}
