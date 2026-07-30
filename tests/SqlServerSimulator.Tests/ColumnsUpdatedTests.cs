using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The trigger-body change-detection intrinsics <c>UPDATE(column)</c> and
/// <c>COLUMNS_UPDATED()</c>, plus the stable <c>column_id</c> the bitmask is
/// keyed on. All behaviors probe-confirmed against SQL Server 2025.
/// </summary>
/// <remarks>
/// The trigger body writes its reading into a log table rather than selecting
/// it: trigger-body result sets are drained and discarded at the call site
/// (see <c>docs/claude/triggers.md</c>), so a SELECT there is unobservable.
/// </remarks>
[TestClass]
public sealed class ColumnsUpdatedTests
{
    /// <summary>
    /// Four-column target whose trigger records, per fire, the four
    /// <c>UPDATE(col)</c> readings followed by the raw mask and its length.
    /// </summary>
    private static DbConnection Seeded()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table log_t (note varchar(100) null);
            create table main_t (id int identity(1,1) primary key, a int null, b int null, c varchar(20) null default 'dflt');
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_x on main_t after insert, update, delete as
            begin
                insert log_t (note) select
                    cast(case when update(id) then 1 else 0 end as varchar)
                  + cast(case when update(a)  then 1 else 0 end as varchar)
                  + cast(case when update(b)  then 1 else 0 end as varchar)
                  + cast(case when update(c)  then 1 else 0 end as varchar)
                  + ' ' + convert(varchar(20), columns_updated(), 1)
                  + ' len=' + cast(datalength(columns_updated()) as varchar);
            end
            """).ExecuteNonQuery();
        return connection;
    }

    private static string Fire(DbConnection connection, string dml)
    {
        _ = connection.CreateCommand("delete log_t").ExecuteNonQuery();
        _ = connection.CreateCommand(dml).ExecuteNonQuery();
        return (string)connection.CreateCommand("select top 1 note from log_t").ExecuteScalar()!;
    }

    /// <summary>An INSERT reports every column updated, whatever it named.</summary>
    [TestMethod]
    public void Insert_ReportsEveryColumn_RegardlessOfColumnList()
    {
        using var connection = Seeded();
        AreEqual("1111 0x0F len=1", Fire(connection, "insert main_t (a) values (1)"));
        AreEqual("1111 0x0F len=1", Fire(connection, "insert main_t (a, c) values (2, 'x')"));
    }

    [TestMethod]
    public void Update_ReportsOnlySetClauseColumns()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("insert main_t (a) values (1)").ExecuteNonQuery();
        AreEqual("0010 0x04 len=1", Fire(connection, "update main_t set b = 9 where a = 1"));
        AreEqual("0101 0x0A len=1", Fire(connection, "update main_t set a = 5, c = 'y' where a = 1"));
    }

    /// <summary>Membership in the SET clause, not whether the value moved.</summary>
    [TestMethod]
    public void Update_SelfAssignment_StillReportsTheColumn()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("insert main_t (a) values (1)").ExecuteNonQuery();
        AreEqual("0100 0x02 len=1", Fire(connection, "update main_t set a = a where a = 1"));
    }

    /// <summary>
    /// The reading is a property of the statement, not of the rows: an UPDATE
    /// matching nothing still fires the trigger and still reports its SET
    /// columns.
    /// </summary>
    [TestMethod]
    public void Update_MatchingNoRows_StillFiresAndReportsSetColumns()
    {
        using var connection = Seeded();
        AreEqual("0010 0x04 len=1", Fire(connection, "update main_t set b = 1 where 1 = 0"));
    }

    /// <summary>A DELETE's mask is zero-length, not a run of zero bytes.</summary>
    [TestMethod]
    public void Delete_ReportsNoColumns_AndAnEmptyMask()
    {
        using var connection = Seeded();
        _ = connection.CreateCommand("insert main_t (a) values (2)").ExecuteNonQuery();
        AreEqual("0000 0x len=0", Fire(connection, "delete main_t where a = 2"));
    }

    [TestMethod]
    public void Merge_ReportsPerBranch()
    {
        using var connection = Seeded();
        AreEqual("1111 0x0F len=1", Fire(connection, """
            merge main_t as t using (select 99 as a) as s on t.a = s.a
            when not matched then insert (a) values (s.a);
            """));
        AreEqual("0010 0x04 len=1", Fire(connection, """
            merge main_t as t using (select 99 as a) as s on t.a = s.a
            when matched then update set b = 7;
            """));
    }

    // === Bit layout ===

    private static DbConnection SeededWide()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table log_t (note varchar(100) null);
            create table wide_t (c1 int, c2 int, c3 int, c4 int, c5 int, c6 int, c7 int, c8 int, c9 int, c10 int);
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create trigger tr_y on wide_t after update as
            begin
                insert log_t (note) select convert(varchar(20), columns_updated(), 1)
                    + ' len=' + cast(datalength(columns_updated()) as varchar);
            end
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("insert wide_t values (1,2,3,4,5,6,7,8,9,10)").ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Column_id N lands at bit (N-1)%8 of byte (N-1)/8, least-significant
    /// bit first, over ceil(columns/8) bytes.
    /// </summary>
    [TestMethod]
    public void Mask_IsLeastSignificantBitFirst_AcrossByteBoundaries()
    {
        using var connection = SeededWide();
        AreEqual("0x0100 len=2", Fire(connection, "update wide_t set c1 = 1"));
        AreEqual("0x0200 len=2", Fire(connection, "update wide_t set c2 = 1"));
        AreEqual("0x8000 len=2", Fire(connection, "update wide_t set c8 = 1"));
        AreEqual("0x0001 len=2", Fire(connection, "update wide_t set c9 = 1"));
        AreEqual("0x0102 len=2", Fire(connection, "update wide_t set c1 = 1, c10 = 1"));
    }

    /// <summary>
    /// The mask is keyed on the stable column_id, so a dropped column keeps
    /// its bit position and the surviving columns keep theirs.
    /// </summary>
    [TestMethod]
    public void Mask_KeyedOnColumnId_SurvivesDropColumn()
    {
        using var connection = SeededWide();
        _ = connection.CreateCommand("alter table wide_t drop column c2").ExecuteNonQuery();
        AreEqual("0x0400 len=2", Fire(connection, "update wide_t set c3 = 1"));
        AreEqual("0x0100 len=2", Fire(connection, "update wide_t set c1 = 1"));
    }

    // === Error paths ===

    /// <summary>
    /// A predicate, not a scalar — resolution happens where the body parses,
    /// which for the simulator's deferred module-body validation is the first
    /// fire rather than CREATE TRIGGER.
    /// </summary>
    [TestMethod]
    public void UpdatePredicate_UnknownColumn_RaisesMsg207()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (c1 int, c2 int)",
            "create trigger tr on t after update as begin if update(no_such_col) select 1; end",
            "insert t values (1, 2)");
        var ex = sim.AssertSqlError("update t set c1 = 9", 207);
        AreEqual("Invalid column name 'no_such_col'.", ex.Message);
    }

    [TestMethod]
    public void UpdatePredicate_OutsideATrigger_RaisesMsg140()
        => new Simulation().AssertSqlError(
            "create table t (c1 int); if update(c1) select 1",
            140,
            "Can only use IF UPDATE within a CREATE TRIGGER statement.");

    /// <summary>
    /// Asymmetric with <c>UPDATE(col)</c> on purpose: the mask function is a
    /// value expression and is legal outside a trigger, where it is NULL.
    /// </summary>
    [TestMethod]
    public void ColumnsUpdated_OutsideATrigger_IsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select columns_updated()"));

    /// <summary>The predicate gates the body it guards.</summary>
    [TestMethod]
    public void UpdatePredicate_GatesTheBody()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (c1 int, c2 int); create table hit (n int)",
            "create trigger tr on t after update as begin if update(c1) insert hit values (1); end",
            "insert t values (1, 2)");
        _ = sim.ExecuteNonQuery("update t set c2 = 9");
        AreEqual(0, sim.ExecuteScalar("select count(*) from hit"));
        _ = sim.ExecuteNonQuery("update t set c1 = 9");
        AreEqual(1, sim.ExecuteScalar("select count(*) from hit"));
    }
}
