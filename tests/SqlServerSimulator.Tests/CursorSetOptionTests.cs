using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A cursor against the session's SET options and its select list row by
/// row: the options it was declared under bind its OPEN and its live
/// fetches, <c>SET ROWCOUNT</c> caps what it populates, and a keyset or
/// dynamic cursor projects each row only as a fetch lands on it. Every
/// expectation probed 2026-10-06 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CursorSetOptionTests
{
    private const string Table = "create table t (a int primary key); insert t values (1), (2), (3), (4);";

    [TestMethod]
    [DataRow("static")]
    [DataRow("keyset")]
    [DataRow("dynamic")]
    [DataRow("fast_forward")]
    public void Open_UnderChangedOptions_IsMsg16958(string kind)
    {
        var error = new Simulation().AssertSqlError($"{Table} declare c cursor {kind} for select a from t; set ansi_nulls off; open c", 16958);
        AreEqual(3, error.Errors[0].State);
    }

    [TestMethod]
    [DataRow("set rowcount 2")]
    [DataRow("set ansi_padding off")]
    [DataRow("set ansi_warnings off")]
    [DataRow("set concat_null_yields_null off")]
    [DataRow("set numeric_roundabort on")]
    [DataRow("set dateformat dmy")]
    [DataRow("set datefirst 1")]
    [DataRow("set ansi_null_dflt_on off")]
    [DataRow("set forceplan on")]
    public void Open_UnderEachPlanOption_IsMsg16958(string change)
        => _ = new Simulation().AssertSqlError($"{Table} declare c cursor fast_forward for select a from t; {change}; open c", 16958);

    [TestMethod]
    [DataRow("set arithabort off")]
    [DataRow("set quoted_identifier off")]
    [DataRow("set nocount on")]
    [DataRow("set xact_abort on")]
    [DataRow("set lock_timeout 100")]
    [DataRow("set transaction isolation level serializable")]
    [DataRow("set ansi_nulls off; set ansi_nulls on")]
    public void Open_UnderOtherOptions_Runs(string change)
        => AreEqual(1, new Simulation().ExecuteScalar($"{Table} declare c cursor fast_forward for select a from t; declare @a int; {change}; open c; fetch next from c into @a; select @@fetch_status + 1"));

    [TestMethod]
    public void LiveFetch_UnderChangedOptions_IsMsg16958_StaticFetchesOn()
    {
        _ = new Simulation().AssertSqlError($"{Table} declare c cursor keyset for select a from t; open c; set ansi_nulls off; fetch next from c", 16958);
        AreEqual(0, new Simulation().ExecuteScalar($"{Table} declare c cursor static for select a from t; declare @a int; open c; set ansi_nulls off; fetch next from c into @a; select @@fetch_status"));
    }

    [TestMethod]
    [DataRow("static")]
    [DataRow("keyset")]
    public void RowCount_CapsThePopulation(string kind)
        => AreEqual("2|-1", new Simulation().ExecuteScalar($"""
            {Table}
            set rowcount 2;
            declare c cursor {kind} for select a from t order by a;
            open c;
            declare @rows int = @@cursor_rows, @a int;
            fetch next from c into @a; fetch next from c into @a; fetch next from c into @a;
            set rowcount 0;
            select concat(@rows, '|', @@fetch_status)
            """));

    [TestMethod]
    [DataRow("keyset")]
    [DataRow("dynamic")]
    [DataRow("fast_forward")]
    public void ProjectionError_SurfacesOnlyAtTheRowsFetch_AndLeavesTheCursorWhereItWas(string kind)
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand($"create table t (id int primary key); insert t values (1), (2), (3); declare c cursor global {kind} for select 1 / (id - 2) from t; open c").ExecuteNonQuery();
        AreEqual(-1, connection.CreateCommand("fetch next from c").ExecuteScalar());
        var first = Throws<SimulatedSqlException>(() => connection.CreateCommand("fetch next from c").ExecuteNonQuery());
        AreEqual(8134, first.Number);
        var again = Throws<SimulatedSqlException>(() => connection.CreateCommand("fetch next from c").ExecuteNonQuery());
        AreEqual(8134, again.Number);
        AreEqual(-1, connection.CreateCommand("select @@fetch_status").ExecuteScalar());
    }

    [TestMethod]
    public void ProjectionError_InAStaticCursor_StopsTheOpen()
        => _ = new Simulation().AssertSqlError("create table t (id int primary key); insert t values (1), (2), (3); declare c cursor static for select 1 / (id - 2) from t; open c", 8134);
}
