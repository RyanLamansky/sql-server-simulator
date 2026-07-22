using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A statement that is a bare object name (optionally followed by an argument
/// list) is an implicit <c>EXECUTE</c> — but only as the literal first
/// statement of a batch (probe-confirmed against SQL Server 2025, 2026-07-22:
/// <c>sp_datatype_info_100 0, 3</c> works, but the same after a prior statement
/// or a leading <c>;</c> raises Msg 102). This is the form mssql-jdbc's
/// <c>getTypeInfo</c> sends. The bare form routes through the same EXEC path as
/// the explicit keyword, so the results are identical.
/// </summary>
[TestClass]
public sealed class ImplicitExecTests
{
    private static int RowCount(string sql)
    {
        using var reader = new Simulation().ExecuteReader(sql);
        var count = 0;
        while (reader.Read())
            count++;
        return count;
    }

    [TestMethod]
    public void BareProcCall_FirstStatement_WithArgs_MatchesExecForm()
    {
        var bare = RowCount("sp_datatype_info_100 0, 3");
        AreEqual(RowCount("exec sp_datatype_info_100 0, 3"), bare);
        AreEqual(37, bare);
    }

    [TestMethod]
    public void BareProcCall_FirstStatement_NamedArgs_Works()
        => AreEqual(RowCount("exec sp_datatype_info_100 @data_type=93, @ODBCVer=3"),
                    RowCount("sp_datatype_info_100 @data_type=93, @ODBCVer=3"));

    [TestMethod]
    public void BareProcCall_FirstStatement_NoArgs_Works()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure myp as select 42 as v");
        AreEqual(42, sim.ExecuteScalar("myp"));
    }

    [TestMethod]
    public void BareProcCall_NotFirstStatement_RaisesMsg102()
        => new Simulation().AssertSqlError("select 1; sp_datatype_info_100 0, 3", 102);

    [TestMethod]
    public void BareProcCall_AfterLeadingSemicolon_RaisesMsg102()
        => new Simulation().AssertSqlError("; sp_datatype_info_100 0, 3", 102);
}
