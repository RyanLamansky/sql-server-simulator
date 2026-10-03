using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Writes through a partitioned view — a <c>UNION ALL</c> view over member
/// tables whose CHECK constraints keep a partitioning column's values apart —
/// routed to the members, and real's refusals of a view that doesn't qualify.
/// Probed against SQL Server 2025 on 2026-10-01.
/// </summary>
[TestClass]
public sealed class PartitionedViewTests
{
    private static Simulation Setup()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table m1 (k int not null check (k between 1 and 99), v int, primary key (k));
            create table m2 (k int not null check (k between 100 and 199), v int, primary key (k));
            create table u (id int, x int);
            insert m1 values (1, 10), (2, 20);
            insert m2 values (150, 1500);
            insert u values (1, 5), (150, 6);
            """,
            "create view pv as select k, v from m1 union all select k, v from m2");
        return simulation;
    }

    private const string Members = "select concat((select string_agg(concat(k, ':', v), ' ') within group (order by k) from m1), ' | ', (select string_agg(concat(k, ':', v), ' ') within group (order by k) from m2))";

    [TestMethod]
    [DataRow("insert pv values (5, 50), (120, 1200)", 2, "1:10 2:20 5:50 | 120:1200 150:1500", DisplayName = "INSERT routes each row")]
    [DataRow("insert pv (v, k) values (8, 160)", 1, "1:10 2:20 | 150:1500 160:8", DisplayName = "INSERT with a column list")]
    [DataRow("insert pv select id + 10, x from u where id = 1", 1, "1:10 2:20 11:5 | 150:1500", DisplayName = "INSERT … SELECT")]
    [DataRow("insert pv exec ('select 3, 30')", 1, "1:10 2:20 3:30 | 150:1500", DisplayName = "INSERT … EXEC")]
    [DataRow("update pv set v = v + 1 where k in (2, 150)", 2, "1:10 2:21 | 150:1501", DisplayName = "UPDATE in place")]
    [DataRow("update pv set k = 120 where k = 2", 1, "1:10 | 120:20 150:1500", DisplayName = "UPDATE moving a row to another member")]
    [DataRow("update pv set k = case k when 1 then 150 when 150 then 1 end where k in (1, 150)", 2, "1:1500 2:20 | 150:10", DisplayName = "UPDATE trading rows between members")]
    [DataRow("update pv set v = u.x from pv join u on u.id = pv.k", 2, "1:5 2:20 | 150:6", DisplayName = "Joined UPDATE")]
    [DataRow("update a set k = 101 from pv a join u on u.id = a.k where u.x = 5", 1, "2:20 | 101:10 150:1500", DisplayName = "Joined UPDATE moving a row")]
    [DataRow("delete pv where k < 100", 2, " | 150:1500", DisplayName = "DELETE")]
    [DataRow("delete a from pv a join u on u.id = a.k", 2, "2:20 | ", DisplayName = "Joined DELETE")]
    public void Write_RoutesToTheMembers(string statement, int rowCount, string expected)
    {
        var simulation = Setup();
        AreEqual(rowCount, simulation.ExecuteNonQuery(statement));
        AreEqual(expected, simulation.ExecuteScalar(Members));
    }

    /// <summary>
    /// A view, CTE or derived table over the partitioned view writes through
    /// it, its filter choosing the rows.
    /// </summary>
    [TestMethod]
    [DataRow("update pvw set vv = 0 where kk = 150", 1, "1:10 2:20 | 150:0", DisplayName = "View over it, renamed")]
    [DataRow("update pvw set vv = 0", 2, "1:10 2:0 | 150:0", DisplayName = "View over it, filtered")]
    [DataRow("insert pvw values (3, 30)", 1, "1:10 2:20 3:30 | 150:1500", DisplayName = "INSERT through a view over it")]
    [DataRow("with c as (select * from pv) delete c where k = 2", 1, "1:10 | 150:1500", DisplayName = "CTE")]
    [DataRow("update d set k = 199 from (select k, v from pv) d where k = 1", 1, "2:20 | 150:1500 199:10", DisplayName = "Derived table")]
    public void WriteThroughALevelOverIt_RoutesToTheMembers(string statement, int rowCount, string expected)
    {
        var simulation = Setup();
        simulation.ExecuteBatches("create view pvw as select k as kk, v as vv from pv where v > 15");
        AreEqual(rowCount, simulation.ExecuteNonQuery(statement));
        AreEqual(expected, simulation.ExecuteScalar(Members));
    }

    /// <summary>
    /// A row no member's constraints admit is Msg 4457, which ends the batch
    /// with an open transaction left standing, after the members' own
    /// errors; and <c>WITH CHECK OPTION</c> over the view isn't enforced.
    /// </summary>
    [TestMethod]
    public void ValueFittingNoMember_EndsTheBatch()
    {
        var simulation = Setup();
        using var connection = simulation.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; insert pv values (500, 1); select 1").ExecuteScalar());
        AreEqual(4457, error.Number);
        AreEqual(1, connection.CreateCommand("select @@trancount").ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        simulation.AssertSqlError("update pv set k = 999 where k = 150", 4457, "The attempted insert or update of the partitioned view failed because the value of the partitioning column does not belong to any of the partitions.");
        _ = simulation.AssertSqlError("insert pv values (null, 1)", 4457);
        _ = simulation.AssertSqlError("insert pv values (1, 5), (500, 6)", 2627);
        _ = simulation.AssertSqlError("insert pv values (500, 6), (1, 5)", 2627);
        AreEqual("4457 0", simulation.ExecuteScalar("begin try insert pv values (500, 1) end try begin catch select concat(error_number(), ' ', @@trancount) end catch"));
        simulation.ExecuteBatches("create view pvc as select k, v from pv where v < 100 with check option");
        AreEqual(1, simulation.ExecuteNonQuery("insert pvc values (3, 300)"));
    }

    /// <summary>
    /// The members' triggers fire last member first, each for its own rows
    /// only.
    /// </summary>
    [TestMethod]
    public void MemberTriggers_FireLastMemberFirst()
    {
        var simulation = Setup();
        simulation.ExecuteBatches(
            "create table log1 (n int identity, msg varchar(20))",
            "create trigger t1 on m1 after insert, update as insert log1 (msg) select concat('m1:', count(*)) from inserted",
            "create trigger t2 on m2 after insert, update as insert log1 (msg) select concat('m2:', count(*)) from inserted");
        _ = simulation.ExecuteNonQuery("update pv set v = 0");
        _ = simulation.ExecuteNonQuery("insert pv values (3, 3)");
        AreEqual("m2:1 m1:2 m1:1", simulation.ExecuteScalar("select string_agg(msg, ' ') within group (order by n) from log1"));
        simulation.AssertSqlError("update pv set k = 3 where k = 1", 4453, "Cannot UPDATE partitioning column 'k' of view 'simulated.dbo.pv' because the table '[simulated].[dbo].[m1]' has a INSERT, UPDATE or DELETE trigger.");
    }

    /// <summary>
    /// What makes a <c>UNION ALL</c> view a partitioned view is settled at each
    /// write: the CHECK shapes that keep the members apart, negations and
    /// fractional bounds included, and the refusals of members that don't.
    /// </summary>
    [TestMethod]
    [DataRow("k in (1, 2, 3)", "k in (4, 5)", 0, DisplayName = "IN lists")]
    [DataRow("k = 1 or k = 3", "k = 2 or k > 10", 1, DisplayName = "OR")]
    [DataRow("k not between 2 and 5", "k between 2 and 5", 1, DisplayName = "NOT BETWEEN")]
    [DataRow("k <> 2", "k = 2", 1, DisplayName = "Inequality")]
    [DataRow("not (k >= 2)", "k >= 2", 1, DisplayName = "NOT")]
    [DataRow("k < 2.5", "k > 2.5", 0, DisplayName = "Fractional bound")]
    [DataRow("10 > k", "10 <= k", 0, DisplayName = "Constant on the left")]
    public void CheckShapes_KeepingTheMembersApart(string first, string second, int inSecond)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            $"create table a1 (k int not null check ({first}), v int, primary key (k)); create table a2 (k int not null check ({second}), v int, primary key (k))",
            "create view av as select k, v from a1 union all select k, v from a2");
        AreEqual(2, simulation.ExecuteNonQuery("insert av values (1, 1), (2, 2)"));
        AreEqual(inSecond, simulation.ExecuteScalar("select count(*) from a2"));
    }

    [TestMethod]
    [DataRow("k int not null, v int, primary key (k)", "k int not null, v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "update av set v = 1", 4436, "UNION ALL view 'simulated.dbo.av' is not updatable because a partitioning column was not found.", DisplayName = "No CHECK")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 5), v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "update av set v = 1", 4436, "UNION ALL view 'simulated.dbo.av' is not updatable because a partitioning column was not found.", DisplayName = "Overlapping ranges")]
    [DataRow("id int primary key, k int not null check (k < 10), v int", "id int primary key, k int not null check (k >= 10), v int", "select id, k, v from a1 union all select id, k, v from a2", "delete av", 4436, "UNION ALL view 'simulated.dbo.av' is not updatable because a partitioning column was not found.", DisplayName = "Partitioning column outside the key")]
    [DataRow("k int not null check (k < 10), v int", "k int not null check (k >= 10), v int", "select k, v from a1 union all select k, v from a2", "delete av", 4440, "UNION ALL view 'simulated.dbo.av' is not updatable because a primary key was not found on table '[simulated].[dbo].[a1]'.", DisplayName = "No primary key")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 union all select k, v from a1", "delete av", 4442, "UNION ALL view 'simulated.dbo.av' is not updatable because base table '[simulated].[dbo].[a1]' is used multiple times.", DisplayName = "Table used twice")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v, v v2 from a1 union all select k, v, k from a2", "delete av", 4443, "UNION ALL view 'simulated.dbo.av' is not updatable because column 'v' of base table '[simulated].[dbo].[a2]' is used multiple times.", DisplayName = "Column used twice")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k bigint not null check (k >= 10), v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "delete av", 4444, "UNION ALL view 'simulated.dbo.av' is not updatable because the primary key of table '[simulated].[dbo].[a1]' is not included in the union result.", DisplayName = "Key converted by the union")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 union all select v, k from a2", "delete av", 4445, "UNION ALL view 'simulated.dbo.av' is not updatable because the primary key of table '[simulated].[dbo].[a2]' is not unioned with primary keys of preceding tables.", DisplayName = "Keys in other columns")]
    [DataRow("k int not null check (k < 10), v int, w int, primary key (k)", "k int not null check (k >= 10), v int, w int, primary key (k)", "select k, v from a1 union all select k, v from a2", "update av set k = 11 where k = 1", 4438, "Partitioned view 'simulated.dbo.av' is not updatable because it does not deliver all columns from its member tables.", DisplayName = "Column not delivered")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v bigint, primary key (k)", "select k, v from a1 union all select k, v from a2", "update av set k = 11 where k = 1", 271, "The column \"v\" cannot be modified because it is either a computed column or is the result of a UNION operator.", DisplayName = "Column converted by the union, row moving")]
    [DataRow("k int not null check (k < 10), v varchar(10), primary key (k)", "k int not null check (k >= 10), v varchar(20), primary key (k)", "select k, v from a1 union all select k, v from a2", "update av set k = 11 where k = 1", 4456, "The partitioned view \"simulated.dbo.av\" is not updatable because one or more of the non-partitioning columns of its member tables have mismatched types.", DisplayName = "Lengths differ, row moving")]
    [DataRow("k varchar(10) not null check (k < 'm'), v int, primary key (k)", "k varchar(20) not null check (k >= 'm'), v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "insert av values ('a', 1)", 4454, "Cannot update the partitioned view \"simulated.dbo.av\" because the partitioning columns of its member tables have mismatched types.", DisplayName = "Partitioning lengths differ")]
    [DataRow("k int not null check (k < 10), v int, i int identity, primary key (k)", "k int not null check (k >= 10), v int, i int, primary key (k)", "select k, v, i from a1 union all select k, v, i from a2", "insert av values (1, 1, 1)", 4433, "Cannot INSERT into partitioned view 'simulated.dbo.av' because table '[simulated].[dbo].[a1]' has an IDENTITY constraint.", DisplayName = "Identity, INSERT")]
    [DataRow("k int not null check (k < 10), v int, i int identity, primary key (k)", "k int not null check (k >= 10), v int, i int, primary key (k)", "select k, v, i from a1 union all select k, v, i from a2", "update av set k = 11 where k = 1", 4450, "Cannot update partitioned view 'simulated.dbo.av' because the definition of the view column 'i' in table '[simulated].[dbo].[a1]' has an IDENTITY constraint.", DisplayName = "Identity, row moving")]
    [DataRow("k int not null check (k < 10), v int, ts rowversion, primary key (k)", "k int not null check (k >= 10), v int, ts rowversion, primary key (k)", "select k, v, ts from a1 union all select k, v, ts from a2", "update av set v = 1", 4431, "Partitioned view 'simulated.dbo.av' is not updatable because table '[simulated].[dbo].[a1]' has a timestamp column.", DisplayName = "Rowversion")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "insert av (k) values (1)", 4448, "Cannot INSERT into partitioned view 'simulated.dbo.av' because values were not supplied for all columns.", DisplayName = "Column left out")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "insert av values (1, default)", 4449, "Using defaults is not allowed in views that contain a set operator.", DisplayName = "DEFAULT")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "update av set v = 1 output inserted.k", 489, "The OUTPUT clause cannot be specified because the target view \"av\" is a partitioned view.", DisplayName = "OUTPUT")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "delete top (1) av", 417, "TOP is not allowed in an UPDATE or DELETE statement against a partitioned view.", DisplayName = "TOP")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "merge av t using (values (1)) s (k) on t.k = s.k when matched then delete;", 5317, "The target of a MERGE statement cannot be a partitioned view.", DisplayName = "MERGE")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 union all select k, v from a2", "insert av select k, v from a1", 4439, "Partitioned view 'simulated.dbo.av' is not updatable because the source query contains references to partition table '[simulated].[dbo].[a1]'.", DisplayName = "Source reading a member")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 where v > 0 union all select k, v from a2", "delete av", 4426, "View 'av' is not updatable because the definition contains a UNION operator.", DisplayName = "Member with a filter")]
    [DataRow("k int not null check (k < 10), v int, primary key (k)", "k int not null check (k >= 10), v int, primary key (k)", "select k, v from a1 where v > 0 union all select k, v from a2", "update av set v = 1", 4406, "Update or insert of view or function 'av' failed because it contains a derived or constant field.", DisplayName = "Member with a filter, UPDATE")]
    public void NotQualifying_RefusedAsRealRefusesIt(string first, string second, string body, string statement, int number, string message)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches($"create table a1 ({first}); create table a2 ({second})", $"create view av as {body}");
        simulation.AssertSqlError(statement, number, message);
    }

    /// <summary>
    /// A member's <c>INSTEAD OF</c> trigger refuses every write, a cascading
    /// key referencing a member and a member's DML trigger refuse a write that
    /// may move a row, and a statement reading a member is Msg 4439 wherever
    /// it does.
    /// </summary>
    [TestMethod]
    public void MemberObjects_RefuseTheWrite()
    {
        var simulation = Setup();
        _ = simulation.AssertSqlError("update pv set v = 0 where k in (select k from m1)", 4439);
        _ = simulation.AssertSqlError("update pv set v = (select max(k) from m2) where k = 1", 4439);
        _ = simulation.AssertSqlError("update pv set v = 1 from pv join m1 on m1.k = pv.k", 4439);
        simulation.ExecuteBatches("create table c (k int references m1 (k) on delete cascade)");
        simulation.AssertSqlError("update pv set k = 3 where k = 1", 4452, "Cannot UPDATE partitioning column 'k' of view 'simulated.dbo.pv' because the table '[simulated].[dbo].[m1]' has a CASCADE DELETE or CASCADE UPDATE constraint.");
        AreEqual(1, simulation.ExecuteNonQuery("update pv set v = 3 where k = 1"));
        simulation.ExecuteBatches("create trigger tm2 on m2 instead of delete as select 1");
        simulation.AssertSqlError("delete pv", 4434, "Partitioned view 'simulated.dbo.pv' is not updatable because table '[simulated].[dbo].[m2]' has an INSTEAD OF trigger.");
    }

    /// <summary>
    /// A row a write moves to another member is inserted there, its
    /// constraints refusing it as the <c>UPDATE</c>.
    /// </summary>
    [TestMethod]
    public void MovedRow_CheckedAsTheUpdate()
    {
        var simulation = Setup();
        simulation.ExecuteBatches("alter table m2 with nocheck add constraint ck_v check (v < 100)");
        var error = simulation.AssertSqlError("update pv set k = 120, v = 500 where k = 1", 547);
        Contains("The UPDATE statement conflicted with the CHECK constraint \"ck_v\"", error.Errors[0].Message);
        AreEqual("1:10 2:20 | 150:1500", simulation.ExecuteScalar(Members));
    }

    /// <summary>
    /// A body whose branches are written in parentheses is the same
    /// partitioned view.
    /// </summary>
    [TestMethod]
    public void ParenthesizedBranches_RouteToTheMembers()
    {
        var simulation = Setup();
        simulation.ExecuteBatches("create view ppv as (select k, v from m1) union all (select k, v from m2)");
        AreEqual(2, simulation.ExecuteNonQuery("insert ppv values (3, 30), (160, 1600)"));
        AreEqual(1, simulation.ExecuteNonQuery("update ppv set v = 0 where k = 160"));
        AreEqual(1, simulation.ExecuteNonQuery("delete ppv where k = 1"));
        AreEqual("2:20 3:30 | 150:1500 160:0", simulation.ExecuteScalar(Members));
    }

    /// <summary>
    /// A level over the partitioned view that limits its rows or projects a
    /// window is evaluated per member when a write reads it: a <c>TOP</c>
    /// picks each member's own rows, and a window numbers and frames each
    /// member's rows apart in the order the member reads them, its
    /// <c>ORDER BY</c> ignored.
    /// </summary>
    [TestMethod]
    [DataRow("update top1 set v = v + 1", 2, "1:10 2:21 | 150:1501", DisplayName = "TOP picks per member")]
    [DataRow("delete top1", 2, "1:10 | ", DisplayName = "TOP, DELETE")]
    [DataRow("update numbered set v = rn * 100 + c", 3, "1:102 2:202 | 150:101", DisplayName = "Window per member, order ignored")]
    [DataRow("delete numbered where rn = 1", 2, "2:20 | ", DisplayName = "Window per member, DELETE")]
    [DataRow("with q as (select top 1 * from pv order by v) delete q", 2, "2:20 | ", DisplayName = "CTE with TOP")]
    [DataRow("update d set v = d.r from (select k, v, rank() over (order by v) r from pv) d", 3, "1:1 2:1 | 150:1", DisplayName = "Derived table, every row a peer")]
    public void RowLimitedOrWindowedLevel_EvaluatedPerMember(string statement, int rowCount, string expected)
    {
        var simulation = Setup();
        simulation.ExecuteBatches(
            "create view top1 as select top 1 k, v from pv order by v desc",
            "create view numbered as select k, v, row_number() over (order by v desc) rn, count(*) over () c from pv");
        AreEqual(rowCount, simulation.ExecuteNonQuery(statement));
        AreEqual(expected, simulation.ExecuteScalar(Members));
    }

    /// <summary>A statement reading a column a level over the partitioned view derives reads it as the level computes it.</summary>
    [TestMethod]
    public void DerivedColumnOfALevel_ReadAsTheLevelComputesIt()
    {
        var simulation = Setup();
        simulation.ExecuteBatches("create view doubled as select k, v, v * 2 as d from pv");
        AreEqual(2, simulation.ExecuteNonQuery("update doubled set v = d + 1 where d > 30"));
        AreEqual(1, simulation.ExecuteNonQuery("delete doubled where d = 20"));
        AreEqual(1, simulation.ExecuteNonQuery("update x set v = x.d from doubled x join u on u.id = x.k where u.x = 6"));
        AreEqual("2:41 | 150:6002", simulation.ExecuteScalar(Members));
    }

    /// <summary>
    /// A cursor reading a partitioned view is a read-only snapshot: a
    /// positioned write naming a level over it is Msg 16929 and one naming
    /// another table Msg 16933, each followed by Msg 3621 — but one naming
    /// the partitioned view itself while the cursor reads it ends the session.
    /// </summary>
    [TestMethod]
    public void PositionedWrite_RefusedOrEndsTheSession()
    {
        var simulation = Setup();
        simulation.ExecuteBatches("create view pvw as select k, v from pv where v > 0");
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("declare c cursor for select k from pv; open c; fetch c").ExecuteNonQuery();
        AreEqual("TSQL | Snapshot | Read Only | Global (0)", connection.CreateCommand("select properties from sys.dm_exec_cursors(@@spid)").ExecuteScalar());
        var readOnly = Throws<SimulatedSqlException>(() => connection.CreateCommand("delete pvw where current of c").ExecuteNonQuery());
        AreEqual(16929, readOnly.Number);
        AreEqual(3621, readOnly.Errors[1].Number);
        _ = connection.CreateCommand("close c; deallocate c; declare c2 cursor for select k from m1; open c2; fetch c2").ExecuteNonQuery();
        AreEqual(16933, Throws<SimulatedSqlException>(() => connection.CreateCommand("update pv set v = 0 where current of c2").ExecuteNonQuery()).Number);
        _ = connection.CreateCommand("close c2; deallocate c2").ExecuteNonQuery();

        using var command = connection.CreateCommand(
            "declare c3 cursor for select pv.k from pv join u on u.id = pv.k; begin tran; insert m1 values (7, 70); update pv set v = 0 where current of c3; insert m1 values (8, 80)");
        var ended = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
        AreEqual(
            "596/21/1|0/20/0",
            string.Join("|", ended.Errors.Cast<SimulatedError>().Select(error => $"{error.Number}/{error.Class}/{error.State}")));
        AreEqual(System.Data.ConnectionState.Closed, connection.State);
        AreEqual("1:10 2:20 | 150:1500", simulation.ExecuteScalar(Members));
    }

    /// <summary>
    /// <c>BULK INSERT</c> into a partitioned view, or a view over one, is Msg
    /// 4437 naming the partitioned view once its members qualify — settled as
    /// the batch compiles when the file can be read — while one into a union
    /// that is no partitioned view takes the refusal of an <c>INSERT</c>
    /// naming every column.
    /// </summary>
    [TestMethod]
    public void BulkInsert_RefusedWithMsg4437()
    {
        var simulation = BulkInsertTests.WithFiles(("/rows.csv", "1,1\n"));
        simulation.ExecuteBatches(
            "create table m1 (k int not null check (k < 10), v int, primary key (k)); create table m2 (k int not null check (k >= 10), v int, primary key (k)); create table h (k int, v int)",
            "create view pv as select k, v from m1 union all select k, v from m2",
            "create view over_pv as select k, v from pv",
            "create view plain as select k, v from h union all select k, v from m1",
            "create view joined as select h.k, h.v from h join m1 on h.k = m1.k union all select k, v from m2");
        simulation.AssertSqlError("bulk insert pv from '/rows.csv' with (fieldterminator = ',')", 4437, "Partitioned view 'simulated.dbo.pv' is not updatable as the target of a bulk operation.");
        simulation.AssertSqlError("bulk insert over_pv from '/rows.csv' with (fieldterminator = ',')", 4437, "Partitioned view 'simulated.dbo.pv' is not updatable as the target of a bulk operation.");
        simulation.AssertSqlError("select 1; if 1 = 0 bulk insert pv from '/rows.csv' with (fieldterminator = ',')", 4437, "Partitioned view 'simulated.dbo.pv' is not updatable as the target of a bulk operation.");
        _ = simulation.AssertSqlError("bulk insert pv from '/missing.csv'", 4860);
        simulation.AssertSqlError("bulk insert plain from '/rows.csv' with (fieldterminator = ',')", 4440, "UNION ALL view 'simulated.dbo.plain' is not updatable because a primary key was not found on table '[simulated].[dbo].[h]'.");
        simulation.AssertSqlError("bulk insert joined from '/rows.csv' with (fieldterminator = ',')", 4406, "Update or insert of view or function 'joined' failed because it contains a derived or constant field.");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from pv"));
    }

    /// <summary>
    /// Each member's CHECK constraints over the partitioning column count one
    /// at a time: a member qualifies when one of them keeps it apart from the
    /// others, never their intersection, and a row is routed by the first
    /// created, meeting the rest in that member (Msg 547).
    /// </summary>
    [TestMethod]
    public void TwoChecksOnAMember_RoutedByTheFirst()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table r1 (k int primary key, v int, constraint r1b check (k < 10), constraint r1a check (k >= 0)); create table r2 (k int primary key check (k between 10 and 19), v int)",
            "create view pr as select k, v from r1 union all select k, v from r2",
            "create table s1 (k int primary key, v int, constraint s1a check (k >= 0), constraint s1b check (k < 10)); create table s2 (k int primary key check (k between 10 and 19), v int)",
            "create view ps as select k, v from s1 union all select k, v from s2",
            "create table w1 (k int primary key, v int, constraint w1a check (k < 12), constraint w1b check (k not between 10 and 11)); create table w2 (k int primary key check (k between 10 and 19), v int)",
            "create view pw as select k, v from w1 union all select k, v from w2");
        AreEqual(1, simulation.ExecuteNonQuery("insert pr values (12, 1)"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from r2"));
        Contains("\"r1a\"", simulation.AssertSqlError("insert pr values (-5, 1)", 547).Errors[0].Message);
        Contains("\"s1b\"", simulation.AssertSqlError("insert ps values (12, 1)", 547).Errors[0].Message);
        _ = simulation.AssertSqlError("insert ps values (-5, 1)", 4457);
        _ = simulation.AssertSqlError("insert pw values (5, 1)", 4436);
    }

    /// <summary>
    /// A member column converted to the type it already has is the column
    /// itself to a write, though a key column so converted still fails the
    /// key check (Msg 4444).
    /// </summary>
    [TestMethod]
    public void NoOpConversion_IsTheColumnItself()
    {
        var simulation = Setup();
        simulation.ExecuteBatches(
            "create view pc as select k, cast(v as int) v from m1 union all select k, convert(int, v) v from m2",
            "create view pck as select cast(k as int) k, v from m1 union all select k, v from m2",
            "create view pcb as select k, cast(v as bigint) v from m1 union all select k, v from m2");
        AreEqual(2, simulation.ExecuteNonQuery("insert pc values (3, 30), (160, 1600)"));
        AreEqual(5, simulation.ExecuteNonQuery("update pc set v = 0"));
        AreEqual("1:0 2:0 3:0 | 150:0 160:0", simulation.ExecuteScalar(Members));
        _ = simulation.AssertSqlError("update pck set v = 1", 4444);
        _ = simulation.AssertSqlError("insert pcb values (4, 4)", 271);
    }

    /// <summary>
    /// A write through a partitioned view lets go of the locks binding the
    /// view took on its members once the statement ends, where they once
    /// stayed with the session and blocked every later schema change.
    /// </summary>
    [TestMethod]
    [DataRow("update pv set v = v + 1 where k in (2, 150)")]
    [DataRow("delete pv where k = 1")]
    [DataRow("insert pv values (5, 50)")]
    public void WriteThroughTheView_LeavesNoLockBehind(string write)
    {
        var sim = Setup();
        using var writer = sim.CreateOpenConnection();
        var spid = writer.CreateCommand("select @@spid").ExecuteScalar();
        _ = writer.CreateCommand(write).ExecuteNonQuery();

        AreEqual(0, sim.ExecuteScalar($"select count(*) from sys.dm_tran_locks where request_session_id = {spid} and resource_type <> 'DATABASE'"));
        _ = sim.ExecuteNonQuery("alter table m1 add c int null");
    }
}
