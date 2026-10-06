using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A DDL trigger's <c>ROLLBACK</c> or unhandled error undoes the statement it
/// fired for, outside a transaction as within one (probed 2026-10-06 against
/// SQL Server 2025).
/// </summary>
[TestClass]
public sealed class DdlTriggerVetoTests
{
    [TestMethod]
    public void Rollback_UndoesTheCreateAndEndsTheBatch()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create trigger dt on database for create_table as rollback");
        _ = simulation.AssertSqlError("create table t (a int); select 'after'", 3609);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('t')"));
        AreEqual(0, simulation.ExecuteScalar("select @@trancount"));
    }

    [TestMethod]
    public void BodyError_UndoesTheCreate()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create trigger dt on database for create_table as select 1/0");
        _ = simulation.AssertSqlError("create table t (a int)", 8134);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('t')"));
    }

    [TestMethod]
    public void Rollback_RestoresADroppedTableWithItsRows()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int); insert t values (1), (2)", "create trigger dt on database for drop_table as rollback");
        _ = simulation.AssertSqlError("drop table t", 3609);
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Rollback_UndoesAnAddedColumn()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int)", "create trigger dt on database for alter_table as rollback");
        _ = simulation.AssertSqlError("alter table t add b int", 3609);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t') and name = 'b'"));
    }

    [TestMethod]
    public void BodyThatPasses_LeavesTheDdl()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create trigger dt on database for create_table as print 'ok'", "create table t (a int)");
        IsNotNull(simulation.ExecuteScalar("select object_id('t')"));
    }

    [TestMethod]
    public void ServerTriggerRollback_UndoesTheLogin()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create trigger st on all server for create_login as rollback");
        _ = simulation.AssertSqlError("create login l1 with password = 'Xy!12345abcdEF'", 3609);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.server_principals where name = 'l1'"));
    }
}
