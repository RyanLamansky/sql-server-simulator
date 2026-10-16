using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary><c>sp_server_diagnostics</c>, probed 2026-10-16 against SQL Server 2025.</summary>
[TestClass]
public sealed class ServerDiagnosticsTests
{
    [TestMethod]
    public void Report_SendsTheFiveComponents_AndReturnsOne()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("""
            declare @r int;
            exec @r = sp_server_diagnostics;
            select @r, @@rowcount;
            """).ExecuteReader();
        CollectionAssert.AreEqual(new[] { "create_time", "component_type", "component_name", "state", "state_desc", "data" }, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray());
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add($"{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetInt32(3).ToInvariantString()}|{reader.GetString(4)}");
            var root = System.Xml.Linq.XElement.Parse(reader.GetString(5)).Name.LocalName;
            AreEqual(reader.GetString(2) switch { "query_processing" => "queryProcessing", "io_subsystem" => "ioSubsystem", var name => name }, root);
        }
        CollectionAssert.AreEqual(new[]
        {
            "instance|system|1|clean",
            "instance|resource|1|clean",
            "instance|query_processing|1|clean",
            "instance|io_subsystem|1|clean",
            "instance|events|0|unknown",
        }, rows);
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual((1, 5), (reader.GetInt32(0), reader.GetInt32(1)));
    }

    [TestMethod]
    [DataRow("exec sp_server_diagnostics -1", 3217)]
    [DataRow("exec sp_server_diagnostics 4", 3217)]
    [DataRow("exec sp_server_diagnostics @x = 1", 3217)]
    [DataRow("exec sp_server_diagnostics 1, 2", 8144)]
    [DataRow("exec sp_server_diagnostics 'x'", 214)]
    [DataRow("exec sp_server_diagnostics null", 214)]
    public void Arguments_AreRefusedAsRealRefusesThem(string sql, int number)
    {
        var error = new Simulation().AssertSqlError(sql, number).Errors[0];
        AreEqual(("sp_server_diagnostics", 1), (error.Procedure, error.LineNumber));
    }

    [TestMethod]
    public void AnIntervalRefusal_EndsTheBatch_WhereATypeRefusalReturnsOne()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ended = Throws<SimulatedSqlException>(() => connection.CreateCommand("exec sp_server_diagnostics -1; select 1 / 0").ExecuteNonQuery());
        DoesNotContain(8134, ended.Errors.Cast<SimulatedError>().Select(error => error.Number));
        var typeRefusal = Throws<SimulatedSqlException>(() => connection.CreateCommand("declare @r int; exec @r = sp_server_diagnostics 'x'; select @r").ExecuteScalar());
        AreEqual(214, typeRefusal.Number);
        using var reader = connection.CreateCommand("declare @r int; begin try exec @r = sp_server_diagnostics 'x'; end try begin catch end catch select @r").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
    }
}
