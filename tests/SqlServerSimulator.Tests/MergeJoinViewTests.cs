using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>MERGE</c> into a view whose body joins several tables: every action must
/// land in one base table, a lone <c>DELETE</c> removes from the first table
/// the view reads, and each action is carried back to the base row the view
/// row shows. Probed 2026-09-28 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class MergeJoinViewTests
{
    private static Simulation Seeded(params ReadOnlySpan<string> extra)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table a (id int primary key, x int, b_id int);
            create table b (id int primary key, y int);
            insert a values (1, 10, 1), (2, 20, 2), (3, 30, null), (4, 40, 2);
            insert b values (1, 100), (2, 200), (3, 300);
            """,
            "create view v as select a.id, a.x, a.b_id, b.id as bid, b.y from a join b on a.b_id = b.id");
        simulation.ExecuteBatches(extra);
        return simulation;
    }

    private static string Tables(Simulation simulation) =>
        (string)simulation.ExecuteScalar("""
            select concat(
                (select string_agg(concat(id, ':', x, ':', b_id), ',') within group (order by id) from a), ' / ',
                (select string_agg(concat(id, ':', y), ',') within group (order by id) from b))
            """)!;

    private const string Untouched = "1:10:1,2:20:2,3:30:,4:40:2 / 1:100,2:200,3:300";

    [TestMethod]
    public void UpdateOneTablesColumns_WritesThatTable()
    {
        var simulation = Seeded();
        AreEqual(1, simulation.ExecuteScalar("merge v using (values (1, 11)) s(id, x) on v.id = s.id when matched then update set x = s.x; select @@rowcount"));
        _ = simulation.ExecuteNonQuery("merge v using (values (1, 111)) s(id, y) on v.id = s.id when matched then update set y = s.y;");
        AreEqual("1:11:1,2:20:2,3:30:,4:40:2 / 1:111,2:200,3:300", Tables(simulation));
    }

    [TestMethod]
    public void TargetsSpanningTwoTables_Msg4405()
    {
        var simulation = Seeded();
        simulation.AssertSqlError("merge v using (values (1, 11, 111)) s(id, x, y) on v.id = s.id when matched then update set x = s.x, y = s.y;", 4405, "View or function 'v' is not updatable because the modification affects multiple base tables.");
        _ = simulation.AssertSqlError("merge v using (values (7, 700)) s(id, y) on v.bid = s.id when not matched then insert (id, y) values (s.id, s.y);", 4405);
        _ = simulation.AssertSqlError("merge v using (values (1, 11, 1)) s(id, x, b) on v.id = s.id when matched then update set y = s.x when not matched then insert (id, x, b_id) values (s.id, s.x, s.b);", 4405);
        _ = simulation.AssertSqlError("merge v using (values (1)) s(id) on 1 = 0 when not matched then insert default values;", 4405);
        _ = simulation.AssertSqlError("merge v using (values (9, 90, 1, 1, 1)) s(a, b, c, d, e) on 1 = 0 when not matched then insert values (s.a, s.b, s.c, s.d, s.e);", 4405);
        AreEqual(Untouched, Tables(simulation));
    }

    [TestMethod]
    public void DerivedTarget_Msg4406_NamingTheViewAsWritten()
        => Seeded("create view vd as select a.id, a.x + 1 as x1, b.y from a join b on a.b_id = b.id")
            .AssertSqlError("merge vd using (values (1, 11)) s(id, x) on vd.id = s.id when matched then update set x1 = s.x;", 4406, "Update or insert of view or function 'vd' failed because it contains a derived or constant field.");

    [TestMethod]
    public void Insert_LandsInTheTableItsColumnsName()
    {
        var simulation = Seeded();
        AreEqual(2, simulation.ExecuteScalar("merge v using (values (1, 11, 1), (6, 60, 2)) s(id, x, b) on v.id = s.id when matched then update set x = s.x when not matched then insert (id, x, b_id) values (s.id, s.x, s.b); select @@rowcount"));
        _ = simulation.ExecuteNonQuery("merge v using (values (7, 700)) s(id, y) on v.bid = s.id when not matched then insert (bid, y) values (s.id, s.y);");
        AreEqual("1:11:1,2:20:2,3:30:,4:40:2,6:60:2 / 1:100,2:200,3:300,7:700", Tables(simulation));
    }

    [TestMethod]
    public void DeleteAlone_RemovesFromTheFirstTableTheViewReads()
    {
        var simulation = Seeded("create view v2 as select a.id, a.x, b.id as bid, b.y from b join a on a.b_id = b.id");
        _ = simulation.ExecuteNonQuery("merge v using (values (1)) s(id) on v.id = s.id when matched then delete;");
        _ = simulation.ExecuteNonQuery("merge v2 using (values (4)) s(id) on v2.id = s.id when matched then delete;");
        AreEqual("2:20:2,3:30:,4:40:2 / 1:100,3:300", Tables(simulation));
    }

    [TestMethod]
    public void DeleteBesideAnUpdate_RemovesFromTheUpdatedTable()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("merge v using (values (1, 0), (2, 1)) s(id, d) on v.id = s.id when matched and s.d = 1 then delete when matched then update set y = 99;");
        AreEqual("1:10:1,2:20:2,3:30:,4:40:2 / 1:99,3:300", Tables(simulation));
    }

    [TestMethod]
    public void NotMatchedBySource_ActsOnTheViewsOtherRows()
    {
        var simulation = Seeded();
        AreEqual(2, simulation.ExecuteScalar("merge v using (values (1)) s(id) on v.id = s.id when not matched by source then update set x = 0; select @@rowcount"));
        AreEqual("1:10:1,2:0:2,3:30:,4:0:2 / 1:100,2:200,3:300", Tables(simulation));
    }

    [TestMethod]
    public void ABaseRowTwoViewRowsShow_TakesOneDelete_ButNotTwoUpdates()
    {
        var simulation = Seeded("create view v2 as select b.id, b.y, a.id as aid from b join a on a.b_id = b.id");
        AreEqual(1, simulation.ExecuteScalar("merge v2 using (values (2)) s(id) on v2.id = s.id when matched then delete; select @@rowcount"));
        var retry = Seeded();
        _ = retry.AssertSqlError("merge v using (values (2), (4)) s(id) on v.id = s.id when matched then update set y = 5;", 8672);
        AreEqual(Untouched, Tables(retry));
    }

    [TestMethod]
    public void Msg8672_EndsTheBatch_AndRollsTheTransactionBack()
    {
        var simulation = Seeded();
        using var connection = simulation.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            begin tran;
            insert a values (9, 9, 1);
            merge v using (values (2), (4)) s(id) on v.id = s.id when matched then update set y = 5;
            select 'not reached';
            """).ExecuteScalar());
        AreEqual("0|4", connection.CreateCommand("select concat(@@trancount, '|', (select count(*) from a))").ExecuteScalar());
    }

    [TestMethod]
    public void Output_ReadsTheViewsRows_InsertedLimitedToTheWrittenTable()
    {
        var simulation = Seeded();
        using var reader = simulation.CreateCommand("""
            merge v using (values (1, 11, 1), (6, 60, 2)) s(id, x, b) on v.id = s.id
            when matched then update set x = s.x
            when not matched then insert (id, x, b_id) values (s.id, s.x, s.b)
            output $action, inserted.id, inserted.x, deleted.id, deleted.x, deleted.y;
            """).ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "null" : reader.GetValue(i).ToString())));
        AreEqual("UPDATE,1,11,1,10,100|INSERT,6,60,null,null,null", string.Join("|", rows));
    }

    [TestMethod]
    public void OutputInsertedOfTheOtherTable_Msg404PerColumn()
    {
        var ex = Seeded().AssertSqlError("merge v using (values (1, 11)) s(id, x) on v.id = s.id when matched then update set x = s.x output inserted.*;", 404);
        AreEqual(2, ex.Errors.Count);
        AreEqual("The column reference \"inserted.bid\" is not allowed because it refers to a base table that is not being modified in this statement.", ex.Errors[0].Message);
        AreEqual("The column reference \"inserted.y\" is not allowed because it refers to a base table that is not being modified in this statement.", ex.Errors[1].Message);
    }

    [TestMethod]
    public void OutputInserted_ReadsTheIdentityAndComputedValuesWritten()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table ai (id int identity(10, 5) primary key, x int, b_id int, c as x * 2); create table b (id int primary key, y int); insert b values (1, 100);",
            "create view vi as select ai.id, ai.x, ai.c, ai.b_id, b.y from ai join b on ai.b_id = b.id");
        AreEqual("10|10|10", simulation.ExecuteScalar("""
            declare @t table (id int, c int);
            merge vi using (values (5, 1)) s(x, b) on vi.x = s.x when not matched then insert (x, b_id) values (s.x, s.b) output inserted.id, inserted.c into @t;
            select concat((select id from @t), '|', (select c from @t), '|', scope_identity())
            """));
    }

    [TestMethod]
    public void OutputToTheClient_RefusedByTheWrittenTablesTrigger_Msg334()
        => Seeded("create trigger a_tr on a after update as print 'tr'")
            .AssertSqlError("merge v using (values (1, 11)) s(id, x) on v.id = s.id when matched then update set x = s.x output inserted.x;", 334, "The target table 'a' of the DML statement cannot have any enabled triggers if the statement contains an OUTPUT clause without INTO clause.");

    [TestMethod]
    public void TheWrittenTablesAfterTrigger_Fires()
    {
        var simulation = Seeded("create table seen (n int)", "create trigger a_tr on a after update as insert seen select count(*) from inserted");
        _ = simulation.ExecuteNonQuery("merge v using (values (1, 11)) s(id, x) on v.id = s.id when matched then update set x = s.x;");
        AreEqual(1, simulation.ExecuteScalar("select n from seen"));
    }

    [TestMethod]
    public void WithCheckOption_JudgesTheWrittenRowThroughTheJoin()
    {
        var simulation = Seeded("create view vc as select a.id, a.x, a.b_id, b.id as bid, b.y from a join b on a.b_id = b.id where a.x < 100 with check option");
        _ = simulation.AssertSqlError("merge vc using (values (1, 500)) s(id, x) on vc.id = s.id when matched then update set x = s.x;", 550);
        _ = simulation.AssertSqlError("merge vc using (values (8, 50, 9)) s(id, x, b) on vc.id = s.id when not matched then insert (id, x, b_id) values (s.id, s.x, s.b);", 550);
        _ = simulation.ExecuteNonQuery("merge vc using (values (1, 50)) s(id, x) on vc.id = s.id when matched then update set x = s.x;");
        AreEqual("1:50:1,2:20:2,3:30:,4:40:2 / 1:100,2:200,3:300", Tables(simulation));
    }

    [TestMethod]
    public void UpdateOfANullExtendedRow_Msg8705_WritingNothing()
    {
        var simulation = Seeded("create view vo as select a.id, a.x, a.b_id, b.id as bid, b.y from a left join b on a.b_id = b.id");
        var ex = simulation.AssertSqlError("merge vo using (values (3, 9), (1, 8)) s(id, y) on vo.id = s.id when matched then update set y = s.y;", 8705);
        StartsWith("A DML statement encountered a missing entry in index ID 1 of table ID ", ex.Errors[0].Message);
        AreEqual(Untouched, Tables(simulation));
        _ = simulation.ExecuteNonQuery("merge vo using (values (1, 8)) s(id, y) on vo.id = s.id when matched then update set y = s.y;");
        AreEqual("1:10:1,2:20:2,3:30:,4:40:2 / 1:8,2:200,3:300", Tables(simulation));
    }

    [TestMethod]
    public void ViewsOverJoinViews_WriteThrough()
    {
        var simulation = Seeded(
            "create table c (id int primary key, z int); insert c values (1, 1000), (2, 2000);",
            "create view vv as select id, x, y from v where y > 150",
            "create view vn as select v.id, v.x, v.y, c.z from v join c on c.id = v.bid");
        AreEqual(1, simulation.ExecuteScalar("merge vv using (values (2, 21), (1, 11)) s(id, x) on vv.id = s.id when matched then update set x = s.x; select @@rowcount"));
        _ = simulation.ExecuteNonQuery("merge vn using (values (1, 11)) s(id, x) on vn.id = s.id when matched then update set x = s.x;");
        AreEqual("1:11:1,2:21:2,3:30:,4:40:2 / 1:100,2:200,3:300", Tables(simulation));
    }

    [TestMethod]
    public void ThreeTableView_DeleteAlone_RemovesFromTheFirst()
    {
        var simulation = Seeded(
            "create table c (id int primary key, z int); insert c values (1, 1000), (2, 2000);",
            "create view v3 as select a.id, a.x, b.y, c.z from a join b on a.b_id = b.id join c on c.id = b.id");
        _ = simulation.ExecuteNonQuery("merge v3 using (values (1)) s(id) on v3.id = s.id when matched then delete;");
        AreEqual("3|3|2", simulation.ExecuteScalar("select concat((select count(*) from a), '|', (select count(*) from b), '|', (select count(*) from c))"));
    }

    [TestMethod]
    public void BaseConstraints_StillBind()
    {
        var simulation = Seeded();
        _ = simulation.AssertSqlError("merge v using (values (1, 9)) s(id, x) on 1 = 0 when not matched then insert (id, x, b_id) values (s.id, s.x, 1);", 2627);
        _ = simulation.AssertSqlError("merge v using (values (9)) s(x) on 1 = 0 when not matched then insert (x) values (s.x);", 515);
    }

    [TestMethod]
    public void WhenCondition_ReadsTheOtherTablesColumns()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("merge v as t using (values (1), (2)) s(id) on t.id = s.id when matched and t.y > 150 then update set t.x = 0;");
        AreEqual("1:10:1,2:0:2,3:30:,4:40:2 / 1:100,2:200,3:300", Tables(simulation));
    }

    [TestMethod]
    public void InsteadOfTriggersCoveringEveryAction_StillTakeTheWrite()
    {
        var simulation = Seeded("create table seen (n int)", "create trigger v_io on v instead of update as insert seen select count(*) from inserted");
        _ = simulation.ExecuteNonQuery("merge v using (values (1, 11)) s(id, x) on v.id = s.id when matched then update set y = s.x, x = s.x;");
        AreEqual(1, simulation.ExecuteScalar("select n from seen"));
        AreEqual(Untouched, Tables(simulation));
    }
}
