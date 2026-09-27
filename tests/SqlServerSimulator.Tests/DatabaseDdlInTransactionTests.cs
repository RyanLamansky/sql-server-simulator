using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Database-level DDL inside a user transaction: refused ahead of running, the
/// statement alone ends, and the transaction stays open and committable.
/// Probed 2026-09-27 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DatabaseDdlInTransactionTests
{
    [TestMethod]
    [DataRow("create database zz", 226, 5, "CREATE DATABASE statement not allowed within multi-statement transaction.")]
    [DataRow("alter database current set ansi_nulls on", 226, 6, "ALTER DATABASE statement not allowed within multi-statement transaction.")]
    [DataRow("drop database if exists nope", 574, 0, "DROP DATABASE statement cannot be used inside a user transaction.")]
    public void TheStatementIsRefused_AndTheBatchCarriesOn(string statement, int number, int state, string message)
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"begin tran; {statement}; select 'after'";
        var error = Throws<SimulatedSqlException>(() =>
        {
            using var reader = command.ExecuteReader();
            while (reader.NextResult())
            {
            }
        }).Errors[0];
        AreEqual((number, state, message), (error.Number, error.State, error.Message));
        command.CommandText = "select concat(@@trancount, ':', xact_state(), ':', (select count(*) from sys.databases where name = 'zz'))";
        AreEqual("1:1:0", command.ExecuteScalar());
    }

    [TestMethod]
    public void ATryBlock_CatchesIt()
        => AreEqual(226, new Simulation().ExecuteScalar("begin tran; begin try alter database current set ansi_nulls on end try begin catch select error_number() end catch"));
}
