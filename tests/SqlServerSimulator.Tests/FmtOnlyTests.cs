using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public sealed class FmtOnlyTests
{
    /// <summary>
    /// <c>WAITFOR</c> doesn't wait under FMTONLY (probed 2026-10-17 against
    /// SQL Server 2025, a three-second delay returning at once), so a command
    /// timeout shorter than the delay never fires.
    /// </summary>
    [TestMethod]
    public void WaitFor_DoesNotWait()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("set fmtonly on; waitfor delay '00:01:00'; set fmtonly off; select 1");
        command.CommandTimeout = 1;
        AreEqual(1, command.ExecuteScalar());
    }

    [TestMethod]
    [DataRow("insert t values (2)")]
    [DataRow("update t set a = 3")]
    [DataRow("delete t")]
    [DataRow("merge t using (select 1 a) s on t.a = s.a when matched then update set a = 4;")]
    [DataRow("insert t select a from t")]
    [DataRow("select a into #y from t")]
    public void SuppressedWrite_ReportsZeroRowsAffected(string statement)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); insert t values (1)");
        using var connection = sim.CreateOpenConnection();
        AreEqual(0, connection.CreateCommand($"set fmtonly on; {statement}").ExecuteNonQuery());
        _ = connection.CreateCommand("set fmtonly off").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand("select count(*) from t where a = 1").ExecuteScalar());
    }

    [TestMethod]
    public void SuppressedWrite_CountsUnderNoCount()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int)");
        AreEqual(0, sim.ExecuteNonQuery("set nocount on; set fmtonly on; insert t values (1); set fmtonly off"));
    }

    [TestMethod]
    public void SuppressedWrite_OutputReturnsEmptyResultSet()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int)");
        using var reader = sim.ExecuteReader("set fmtonly on; insert t output inserted.a values (5); set fmtonly off");
        AreEqual(1, reader.FieldCount);
        AreEqual("a", reader.GetName(0));
        IsFalse(reader.Read());
    }
}
