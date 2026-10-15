using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The argument-less server and client environment scalars —
/// <c>CURRENT_TIMEZONE()</c>, <c>CURRENT_TIMEZONE_ID()</c>, <c>HOST_ID()</c>,
/// <c>PUBLISHINGSERVERNAME()</c> — plus <c>FILEPROPERTYEX</c>'s NULL, the
/// argument-less <c>GROUPING_ID()</c> and <c>IIF()</c>'s missing condition,
/// each probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ServerEnvironmentFunctionTests
{
    [TestMethod]
    public void CurrentTimeZone_IsUtc()
        => AreEqual("(UTC) Coordinated Universal Time|UTC", new Simulation().ExecuteScalar("select current_timezone() + '|' + current_timezone_id()"));

    [TestMethod]
    public void CurrentTimeZone_ResultTypes()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("select current_timezone() a, current_timezone_id() b, host_id() c, publishingservername() d, filepropertyex('x', 'y') e into t");
        AreEqual(
            "a nvarchar 512 1|b nvarchar 512 1|c char 10 0|d nvarchar 512 1|e sql_variant 8016 1",
            simulation.ExecuteScalar("select string_agg(concat(c.name, ' ', t.name, ' ', c.max_length, ' ', c.is_nullable), '|') within group (order by c.column_id) from sys.columns c join sys.types t on t.user_type_id = c.user_type_id where c.object_id = object_id('t')"));
    }

    [TestMethod]
    public void CurrentTimeZone_WithArgument_RaisesMsg174()
        => new Simulation().AssertSqlError("select current_timezone(1)", 174, "The current_timezone function requires 0 argument(s).");

    [TestMethod]
    public void HostId_IsThePaddedProcessId()
        => AreEqual(
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture).PadRight(10) + "|1",
            new Simulation().ExecuteScalar("select host_id() + '|' + cast(count(*) as varchar) from sys.dm_exec_sessions where session_id = @@spid and host_process_id = cast(host_id() as int)"));

    [TestMethod]
    public void HostId_WithArgument_RaisesMsg174()
        => new Simulation().AssertSqlError("select host_id(1)", 174, "The host_id function requires 0 argument(s).");

    [TestMethod]
    public void PublishingServerName_IsTheServerName()
        => AreEqual(1, new Simulation().ExecuteScalar("select case when publishingservername() = @@servername then 1 else 0 end"));

    [TestMethod]
    public void FilePropertyEx_IsNullForAKnownFile()
        => AreEqual(1, new Simulation().ExecuteScalar("select case when filepropertyex((select top (1) name from sys.database_files), 'AccountType') is null then 1 else 0 end"));

    [TestMethod]
    public void FilePropertyEx_OneArgument_RaisesMsg174()
        => new Simulation().AssertSqlError("select filepropertyex('a')", 174, "The filepropertyex function requires 2 argument(s).");

    [TestMethod]
    public void FilePropertyEx_EvaluatesItsArguments()
        => new Simulation().AssertSqlError("select filepropertyex(1 / 0, 'a')", 8134, "Divide by zero error encountered.");

    [TestMethod]
    public void GroupingId_WithoutArguments_IsZeroAnywhere()
        => AreEqual(2, new Simulation().ExecuteScalar("select count(grouping_id()) from (values (1), (2)) v(a) where grouping_id() = 0"));

    [TestMethod]
    public void GroupingId_WithoutArguments_OrdersByPositionZero()
        => new Simulation().AssertSqlError(
            "select a, count(*) from (values (1)) v(a) group by a order by grouping_id()", 108,
            "The ORDER BY position number 0 is out of range of the number of items in the select list.");

    [TestMethod]
    public void Iif_WithoutArguments_RaisesMsg4145()
        => new Simulation().AssertSqlError(
            "select iif()", 4145,
            "An expression of non-boolean type specified in a context where a condition is expected, near '('.");
}
