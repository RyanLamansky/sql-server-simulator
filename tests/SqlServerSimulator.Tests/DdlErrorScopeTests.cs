using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// How far a failing table- or index-structure statement reaches: its
/// severity-16 run-time errors end the batch and roll the transaction back as
/// under <c>SET XACT_ABORT ON</c>, the two it raises while compiling (Msg 4902,
/// Msg 2705) end the batch alone, and a severity-11 miss carries on — save
/// ALTER INDEX's Msg 2727. Every expectation probed 2026-09-26 against SQL
/// Server 2025.
/// </summary>
[TestClass]
public sealed class DdlErrorScopeTests
{
    private const string Setup = """
        create table t (a int, b int, c as a + b); insert t values (1, 1), (1, 2);
        create table v (id int primary key); create table w (vid int references v(id));
        """;

    private static int Run(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [TestMethod]
    [DataRow("alter table t alter column b bigint", 5074)]
    [DataRow("alter table t add constraint ck check (a > 5)", 547)]
    [DataRow("alter table t drop column nope", 4924)]
    [DataRow("alter table t drop constraint nope", 3728)]
    [DataRow("create index ix on t (nope)", 1911)]
    [DataRow("truncate table nope", 4701)]
    [DataRow("drop table v", 3726)]
    [DataRow("alter index nope on t rebuild", 2727)]
    [DataRow("create unique clustered index ux on v (id)", 1902)]
    public void AStructuralFailure_EndsTheBatchAndRollsBack(string statement, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        using var connection = sim.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => Run(connection, $"begin tran; insert v values (7); {statement}; insert v values (8)"));
        AreEqual(number, error.Number);
        AreEqual("0:0", Scalar(connection, "select concat(@@trancount, ':', (select count(*) from v))"));
    }

    [TestMethod]
    [DataRow("alter table nope add x int", 4902)]
    [DataRow("alter table t add a int", 2705)]
    public void ACompileTimeFailure_EndsTheBatchButKeepsTheTransaction(string statement, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        using var connection = sim.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => Run(connection, $"begin tran; insert v values (7); {statement}; insert v values (8)"));
        AreEqual(number, error.Number);
        AreEqual("1:1", Scalar(connection, "select concat(@@trancount, ':', (select count(*) from v))"));
    }

    [TestMethod]
    public void AMissingIndexToDrop_CarriesOn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        using var connection = sim.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => Run(connection, "begin tran; insert v values (7); drop index nope on t; insert v values (8)"));
        AreEqual("1:2", Scalar(connection, "select concat(@@trancount, ':', (select count(*) from v))"));
    }

    /// <summary>Msg 3621 after a unique index's duplicate or a column rewrite's bad value reports line 1.</summary>
    [TestMethod]
    [DataRow("create table u (a int); insert u values (1), (1);", "\ncreate unique index ux on u (a)", 1505)]
    [DataRow("create table u (a int); insert u values (300);", "\nalter table u alter column a tinyint", 220)]
    public void TheTerminationNotice_ReportsLineOne(string setup, string statement, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(setup);
        var errors = sim.AssertSqlError(statement, number).Errors;
        AreEqual(2, errors[0].LineNumber);
        AreEqual(3621, errors[1].Number);
        AreEqual(1, errors[1].LineNumber);
    }

    /// <summary>A foreign key's existing-data refusal names the parent as schema.table.</summary>
    [TestMethod]
    public void ForeignKeyValidation_NamesTheParentWithoutItsDatabase()
        => Contains("table \"dbo.v\", column 'id'", new Simulation().AssertSqlError(Setup + " alter table t add constraint fk foreign key (a) references v(id)", 547).Errors[0].Message);
}
