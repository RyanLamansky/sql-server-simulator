using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET DATEFORMAT</c> and the numeric date strings it reorders — a sample
/// of a matrix of twenty-three strings under all six orders and five types,
/// probed 2026-09-24 against SQL Server 2025, where every cell matched. Each
/// expected value is real's <c>CONVERT(varchar(10), TRY_CAST(s AS type), 23)</c>,
/// and <c>#</c> is a string it refuses.
/// </summary>
[TestClass]
public sealed class SetDateFormatTests
{
    private static string Read(string order, string text, string type) =>
        (string)new Simulation().ExecuteScalar(
            $"set dateformat {order}; select isnull(convert(varchar(10), try_cast('{text}' as {type}), 23), '#')")!;

    [TestMethod]
    [DataRow("dmy", "12/11/2024", "date", "2024-11-12")]
    [DataRow("dmy", "12/31/2024", "date", "#")]
    [DataRow("dmy", "24-12-11", "date", "2011-12-24")]
    [DataRow("dmy", "1/2/3", "datetime2", "2003-02-01")]
    // A year-first date: year-month-day to the newer types whatever the order,
    // the order's month / day sequence to the legacy pair.
    [DataRow("dmy", "2024-12-11", "date", "2024-12-11")]
    [DataRow("dmy", "2024-12-11", "datetime", "2024-11-12")]
    [DataRow("dmy", "2024-11-12 10:00", "smalldatetime", "2024-12-11")]
    [DataRow("dmy", "2024-11-12T10:00:00", "datetime", "2024-11-12")]
    [DataRow("dmy", "2024-31-12", "datetime", "2024-12-31")]
    [DataRow("dmy", "2024-31-12", "date", "#")]
    // Month names and unseparated digits don't move.
    [DataRow("dmy", "Dec 11 2024", "date", "2024-12-11")]
    [DataRow("dmy", "20241211", "datetime", "2024-12-11")]
    [DataRow("ymd", "12/11/2024", "date", "2024-12-11")]
    [DataRow("ymd", "12.11.24", "date", "2012-11-24")]
    [DataRow("ymd", "1/2/3", "date", "2001-02-03")]
    // ydm refuses every form but a four-digit-year-first one to the newer types.
    [DataRow("ydm", "24-12-11", "datetime", "2024-11-12")]
    [DataRow("ydm", "24-12-11", "date", "#")]
    [DataRow("ydm", "12/11/2024", "date", "#")]
    [DataRow("ydm", "12/11/2024", "datetime", "2024-11-12")]
    // A year in the middle: the legacy pair always, the newer types under myd and dym.
    [DataRow("myd", "12-2024-11", "date", "2024-12-11")]
    [DataRow("dym", "12-2024-11", "datetime2", "2024-11-12")]
    [DataRow("mdy", "12-2024-11", "date", "#")]
    [DataRow("dmy", "12-2024-11", "datetime", "2024-11-12")]
    [DataRow("dym", "24-12-11", "date", "2012-11-24")]
    [DataRow("myd", "11-12-24", "date", "2012-11-24")]
    public void NumericDate_ReadsInTheSessionsOrder(string order, string text, string type, string expected)
        => AreEqual(expected, Read(order, text, type));

    [TestMethod]
    public void InvalidOrder_RaisesMsg2741()
        => new Simulation().AssertSqlError("set dateformat xyz", 2741, "SET DATEFORMAT date order 'xyz' is invalid.");

    [TestMethod]
    [DataRow("set dateformat DMY")]
    [DataRow("set dateformat 'dmy'")]
    [DataRow("declare @f varchar(3) = 'dmy'; set dateformat @f")]
    public void Order_TakesANameALiteralOrAVariable(string set)
        => AreEqual("dmy", new Simulation().ExecuteScalar($"{set}; select date_format from sys.dm_exec_sessions where session_id = @@spid"));

    [TestMethod]
    public void Order_RevertsWhenAProcedureOrDynamicSqlReturns()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p as set dateformat dmy");
        AreEqual("mdy", sim.ExecuteScalar("exec p; exec ('set dateformat ymd'); select date_format from sys.dm_exec_sessions where session_id = @@spid"));
    }

    [TestMethod]
    public void Language_SetsTheOrder_UnlessThisBatchSetItFirst()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual("dmy", connection.CreateCommand("set language british; select date_format from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar());
        AreEqual("ymd", connection.CreateCommand("set dateformat ymd; set language british; select date_format from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar());
        AreEqual("mdy", connection.CreateCommand("set language us_english; select date_format from sys.dm_exec_sessions where session_id = @@spid").ExecuteScalar());
    }

    [TestMethod]
    public void SameText_UnderTwoOrders_ReadsEachWay()
    {
        // A cached plan belongs to the order it was parsed under.
        using var connection = new Simulation().CreateOpenConnection();
        const string Query = "select convert(varchar(10), cast('01/02/2024' as datetime), 23)";
        AreEqual("2024-01-02", connection.CreateCommand(Query).ExecuteScalar());
        AreEqual("2024-01-02", connection.CreateCommand(Query).ExecuteScalar());
        _ = connection.CreateCommand("set dateformat dmy").ExecuteNonQuery();
        AreEqual("2024-02-01", connection.CreateCommand(Query).ExecuteScalar());
    }

    [TestMethod]
    public void StoredConversions_ReadInTheSessionsOrder()
        => AreEqual("2024-02-01", new Simulation().ExecuteScalar("""
            create table t (d date);
            set dateformat dmy;
            insert t values ('01/02/2024');
            select convert(varchar(10), d, 23) from t
            """));
}
