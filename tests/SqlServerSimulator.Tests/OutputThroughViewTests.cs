using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>OUTPUT</c> on an INSERT / UPDATE / DELETE whose target is a view:
/// <c>INSERTED</c> / <c>DELETED</c> are the view's own columns, each read as
/// the view projects it (probed 2026-09-27 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class OutputThroughViewTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int, s varchar(10)); insert t values (1, 10, 'x'), (2, 20, 'y'), (3, 30, 'z')",
            "create view v as select id, a as x, s, a * 2 as dbl from t where a >= 20");
        return sim;
    }

    [TestMethod]
    public void Update_OutputsTheViewsColumnsDerivedOnesIncluded()
    {
        using var reader = Seeded().ExecuteReader("update v set x = x + 1 output deleted.*, inserted.* where id = 2");
        AreEqual(8, reader.FieldCount);
        AreEqual("x", reader.GetName(1));
        AreEqual("dbl", reader.GetName(3));
        IsTrue(reader.Read());
        AreEqual(40, reader.GetInt32(3));
        AreEqual(21, reader.GetInt32(5));
        AreEqual(42, reader.GetInt32(7));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Insert_OutputsTheViewsColumns()
    {
        using var reader = Seeded().ExecuteReader("insert v (id, x) output inserted.* values (4, 5)");
        IsTrue(reader.Read());
        AreEqual(5, reader.GetInt32(1));
        IsTrue(reader.IsDBNull(2));
        AreEqual(10, reader.GetInt32(3));
    }

    [TestMethod]
    public void Delete_OutputIntoATableVariable()
        => AreEqual(60, Seeded().ExecuteScalar("""
            declare @d table (id int, x int, s varchar(10), dbl int);
            delete v output deleted.* into @d where id = 3;
            select dbl from @d
            """));

    [TestMethod]
    public void BaseColumnNameTheViewRenamed_Msg207()
        => _ = Seeded().AssertSqlError("update v set x = 1 output inserted.a", 207);

    [TestMethod]
    public void ViewQualifier_Msg4104()
        => _ = Seeded().AssertSqlError("update v set x = 1 output v.x", 4104);

    [TestMethod]
    public void DerivedColumnIsReadOnlyWhenNamed()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int); insert t values (1, 0)",
            "create view v as select id, a, 10 / a as q from t");
        AreEqual(1, sim.ExecuteScalar("update v set a = 0 output inserted.id"));
        _ = sim.AssertSqlError("update v set a = 0 output inserted.q", 8134);
    }

    [TestMethod]
    public void ThroughAChainOfViews()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int); insert t values (1, 10)",
            "create view v1 as select id, a as x from t",
            "create view v2 as select id as k, x as y, x + 100 as z from v1");
        AreEqual(112, sim.ExecuteScalar("update v2 set y = 12 output inserted.z"));
        AreEqual(103, sim.ExecuteScalar("insert v2 (k, y) output inserted.z values (2, 3)"));
    }

    [TestMethod]
    public void MaskedColumnsMaskAsTheViewReadsThem()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table t (id int primary key, s varchar(20) masked with (function = 'default()'));
            insert t values (1, 'secret');
            create user u without login;
            """,
            "create view v as select id, s from t",
            "grant select, update on v to u");
        AreEqual("xxxx", sim.ExecuteScalar("execute as user = 'u'; update v set s = 'new' output inserted.s; revert;"));
    }

    [TestMethod]
    public void BaseTableAfterTrigger_Msg334NamesTheBaseTable()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int)",
            "create trigger ta on t after insert as select 1",
            "create view v as select id, a from t");
        sim.AssertSqlError("insert v output inserted.a values (1, 2)", 334,
            "The target table 't' of the DML statement cannot have any enabled triggers if the statement contains an OUTPUT clause without INTO clause.");
    }

    [TestMethod]
    public void InsteadOfInsert_OutputIntoLandsBeforeTheBodyRuns()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int); create table log (id int, a int)",
            "create view v as select id, a from t",
            "create trigger tr on v instead of insert as select count(*) from log");
        AreEqual(1, sim.ExecuteScalar("insert v output inserted.* into log values (1, 2)"));
    }

    [TestMethod]
    public void InsteadOfUpdate_InsertedIsMsg404AndDeletedReads()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int); insert t values (1, 1); create table log (id int, a int)",
            "create view v as select id, a from t",
            "create trigger tr on v instead of update as update t set a = i.a * 10 from t join inserted i on t.id = i.id");
        var ex = sim.AssertSqlError("update v set a = 5 output inserted.id, inserted.a into log", 404);
        AreEqual(2, ex.Errors.Count);
        _ = sim.ExecuteNonQuery("update v set a = 5 output deleted.id, deleted.a into log");
        AreEqual(1, sim.ExecuteScalar("select a from log"));
        AreEqual(50, sim.ExecuteScalar("select a from t"));
    }

    [TestMethod]
    public void TableInsteadOfUpdate_OutputIntoWrites()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int); insert t values (1, 1); create table log (id int, a int, olda int)",
            "create trigger tr on t instead of update as select 1");
        _ = sim.ExecuteNonQuery("update t set a = 5 output inserted.id, inserted.a, deleted.a into log");
        AreEqual(5, sim.ExecuteScalar("select a from log"));
        AreEqual(1, sim.ExecuteScalar("select olda from log"));
    }

    [TestMethod]
    public void TableInsteadOfDelete_OutputIntoWrites()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int primary key, a int); insert t values (1, 7); create table log (id int, a int)",
            "create trigger tr on t instead of delete as select 1");
        _ = sim.ExecuteNonQuery("delete t output deleted.* into log");
        AreEqual(7, sim.ExecuteScalar("select a from log"));
    }
}
