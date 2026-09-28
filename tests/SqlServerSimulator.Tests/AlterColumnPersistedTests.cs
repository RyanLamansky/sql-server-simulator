using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER TABLE … ALTER COLUMN c { ADD | DROP } PERSISTED</c> (probed
/// 2026-09-28 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class AlterColumnPersistedTests
{
    [TestMethod]
    public void AddThenDrop_FlipsTheCatalog_AndKeepsTheValues()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, c as a * 2); insert t (a) values (1), (2), (null)");
        _ = sim.ExecuteNonQuery("alter table t alter column c add persisted");
        IsTrue((bool)sim.ExecuteScalar("select is_persisted from sys.computed_columns where object_id = object_id('t')")!);
        _ = sim.ExecuteNonQuery("alter table t alter column c add persisted; insert t (a) values (5); update t set a = 10 where a = 1");
        AreEqual("2:4,5:10,10:20", sim.ExecuteScalar("select string_agg(concat(a, ':', c), ',') within group (order by a) from t where a is not null"));
        _ = sim.ExecuteNonQuery("alter table t alter column c drop persisted; alter table t alter column c drop persisted");
        IsFalse((bool)sim.ExecuteScalar("select is_persisted from sys.computed_columns where object_id = object_id('t')")!);
        AreEqual("2:4,5:10,10:20", sim.ExecuteScalar("select string_agg(concat(a, ':', c), ',') within group (order by a) from t where a is not null"));
    }

    [TestMethod]
    public void SlotsAfterTheColumn_KeepTheirIndexes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int, c as a * 2, b int, d varchar(10));
            create unique index ux on t (b) include (d);
            insert t (a, b, d) values (1, 10, 'x'), (2, 20, 'y');
            alter table t alter column c add persisted
            """);
        AreEqual("2|20|4|y", sim.ExecuteScalar("select concat_ws('|', a, b, c, d) from t with (index(ux)) where b = 20"));
        _ = sim.AssertSqlError("insert t (a, b, d) values (3, 20, 'z')", 2601);
        _ = sim.ExecuteNonQuery("alter table t alter column c drop persisted");
        AreEqual("1|10|2|x", sim.ExecuteScalar("select concat_ws('|', a, b, c, d) from t with (index(ux)) where b = 10"));
    }

    [TestMethod]
    public void RolledBack_RestoresTheLayout()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int, c as a * 2, b int unique); insert t (a, b) values (1, 10)").ExecuteNonQuery();
        _ = connection.CreateCommand("begin tran; alter table t alter column c add persisted; rollback").ExecuteNonQuery();
        AreEqual("0|1|10|2", connection.CreateCommand(
            "select concat_ws('|', (select is_persisted from sys.computed_columns), a, b, c) from t where b = 10").ExecuteScalar());
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t (a, b) values (2, 10)").ExecuteNonQuery());
    }

    [TestMethod]
    public void AnIndexOverTheNonPersistedColumn_BlocksTheDropOnceAdded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, c as a * 2); create index ix on t (c); insert t (a) values (3); alter table t alter column c add persisted");
        AreEqual(6, sim.ExecuteScalar("select c from t with (index(ix)) where c = 6"));
        var ex = sim.AssertSqlError("alter table t alter column c drop persisted", 5074);
        AreEqual("The index 'ix' is dependent on column 'c'.", ex.Errors[0].Message);
        AreEqual((4922, (byte)9), (ex.Errors[1].Number, ex.Errors[1].State));
    }

    [TestMethod]
    public void Drop_ListsEveryBlocker_ButNotAnIncludedColumn()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (a int, c as a * 2 persisted, constraint ck check (c > 0));
            create index ix on t (c);
            create index ix2 on t (a) include (c);
            create statistics st on t (c);
            alter table t alter column c drop persisted
            """, 5074);
        AreEqual(
            "The object 'ck' is dependent on column 'c'.|The index 'ix' is dependent on column 'c'.|The statistics 'st' is dependent on column 'c'.|ALTER TABLE ALTER COLUMN c failed because one or more objects access this column.",
            string.Join('|', Enumerable.Range(0, ex.Errors.Count).Select(i => ex.Errors[i].Message)));
    }

    [TestMethod]
    [DataRow("alter table t alter column a add persisted", 4919, (byte)0)]
    [DataRow("alter table t alter column a drop persisted", 4919, (byte)0)]
    [DataRow("alter table t alter column d add persisted", 4936, (byte)1)]
    [DataRow("alter table t alter column zz add persisted", 4924, (byte)2)]
    [DataRow("alter table t alter column zz drop persisted", 4924, (byte)2)]
    [DataRow("alter table t alter column c add persisted not null", 156, (byte)1)]
    public void Refusals(string statement, int number, byte state)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, c as a * 2, d as newid())");
        AreEqual(state, sim.AssertSqlError(statement, number).State);
    }

    [TestMethod]
    public void Add_FailingOnARow_EndsTheStatement()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, c as 10 / a); insert t (a) values (1), (0)");
        var ex = sim.AssertSqlError("alter table t alter column c add persisted", 8134);
        Contains("The statement has been terminated.", ex.Message);
        IsFalse((bool)sim.ExecuteScalar("select is_persisted from sys.computed_columns")!);
    }

    [TestMethod]
    public void AddColumn_Persisted_StoresTheExistingRowsValues()
        => AreEqual("1:2,2:3", new Simulation().ExecuteScalar("""
            create table t (a int);
            insert t values (1), (2);
            alter table t add c as a + 1 persisted;
            select string_agg(concat(a, ':', c), ',') within group (order by a) from t
            """));
}
