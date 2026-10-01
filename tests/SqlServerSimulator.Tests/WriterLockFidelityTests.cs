using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What a writer locks beside its row, what it waits in, and what it leaves
/// a snapshot: the index keys the lock DMVs report for a written row, the U a
/// writer reads its target under, the key-range locks an <c>IGNORE_DUP_KEY</c>
/// index's check takes, a foreign key's cascade versioned like any other
/// write, and Msg 3961 for a snapshot older than a table's definition — each
/// probed 2026-10-01 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class WriterLockFidelityTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Shapes = """
        create table h2 (k int constraint uq_h2 unique, v int);
        create table h3 (k int, v int); create index ix_h3 on h3 (k);
        create table c1 (k int primary key, v int);
        create table c2 (k int primary key, u int, v int); create unique index ux_c2 on c2 (u);
        create table c3 (k int primary key, n int, v int); create index ix_c3 on c3 (n);
        create table c4 (k int primary key, f int, v int); create unique index fx_c4 on c4 (f) where f > 100;
        create table c5 (k int primary key, u int, v int); create unique index ix_c5 on c5 (u) with (ignore_dup_key = on);
        create table c6 (k int primary key, n int, v int); create index ix_c6 on c6 (n) include (v);
        create table hu (k int, v int, constraint uq_hu unique (k) with (ignore_dup_key = on));
        create table d1 (k int primary key, w int); insert d1 values (1, 0), (5, 0);
        insert h2 values (1, 1), (5, 5); insert h3 values (1, 1), (5, 5); insert c1 values (1, 1), (5, 5);
        insert c2 values (1, 1, 1), (5, 5, 5); insert c3 values (1, 1, 1), (5, 5, 5); insert c4 values (1, 1, 1), (5, 200, 5);
        insert c5 values (1, 1, 1), (5, 5, 5); insert c6 values (1, 1, 1), (5, 5, 5); insert hu values (1, 1), (5, 5);
        """;

    // A session's row and key locks: type, mode, and a KEY's description.
    private static string RowAndKeyLocks(DbConnection connection, int? spid = null) =>
        (string)connection.CreateCommand($"""
            select isnull(string_agg(concat(resource_type, ' ', request_mode, iif(request_status = 'WAIT', ' WAIT', ''), iif(resource_type = 'KEY', ' ' + resource_description, '')), ', ')
                within group (order by resource_type, request_mode, resource_description), '')
            from sys.dm_tran_locks where request_session_id = {spid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "@@spid"} and resource_type in ('KEY', 'RID')
            """).ExecuteScalar()!;

    private static int Spid(DbConnection connection) => (short)connection.CreateCommand("select @@spid").ExecuteScalar()!;

    /// <summary>
    /// Each write's locks as real reports them, past the object's IX and
    /// real's page locks: the row, and the key in each index the write
    /// changed — for an insert or delete every index the row is in, for an
    /// update the old and new key of every index whose row changed, a
    /// filtered index only where its filter admits the image.
    /// </summary>
    [TestMethod]
    [DataRow("insert h2 values (3, 3)", "KEY X (3), RID X", DisplayName = "Heap UNIQUE insert")]
    [DataRow("insert h3 values (3, 3)", "KEY X (3), RID X", DisplayName = "Heap non-unique index insert")]
    [DataRow("insert c2 values (3, 7, 3)", "KEY X (3), KEY X (7)", DisplayName = "Unique index insert")]
    [DataRow("insert c3 values (3, 7, 3)", "KEY X (3), KEY X (7,3)", DisplayName = "Non-unique index insert")]
    [DataRow("insert c4 values (3, 3, 3)", "KEY X (3)", DisplayName = "Filtered index insert, filtered out")]
    [DataRow("insert c4 values (4, 150, 4)", "KEY X (150), KEY X (4)", DisplayName = "Filtered index insert, admitted")]
    [DataRow("update c3 set v = 9 where k = 1", "KEY X (1)", DisplayName = "Update of a column no index carries")]
    [DataRow("update c6 set v = 9 where k = 1", "KEY X (1), KEY X (1,1)", DisplayName = "Update of an INCLUDE column")]
    [DataRow("update c1 set k = 2 where k = 1", "KEY X (1), KEY X (2)", DisplayName = "Clustered key moved")]
    [DataRow("update c2 set u = 2 where k = 1", "KEY X (1), KEY X (1), KEY X (2)", DisplayName = "Unique index key moved")]
    [DataRow("update h2 set k = 2 where k = 1", "KEY X (1), KEY X (2), RID X", DisplayName = "Heap UNIQUE key moved")]
    [DataRow("update c4 set f = 300 where k = 5", "KEY X (200), KEY X (300), KEY X (5)", DisplayName = "Filtered key moved, admitted")]
    [DataRow("update c4 set f = 2 where k = 1", "KEY X (1)", DisplayName = "Filtered key moved, filtered out")]
    [DataRow("delete c2 where k = 1", "KEY X (1), KEY X (1)", DisplayName = "Delete, unique index")]
    [DataRow("delete h3 where k = 1", "KEY X (1), RID X", DisplayName = "Delete, heap index")]
    [DataRow("select * from c2 with (xlock) where k = 1", "KEY X (1)", DisplayName = "XLOCK read locks only its row")]
    [DataRow("insert c5 values (3, 3, 3)", "KEY RangeS-U (5), KEY RangeX-X (3), KEY X (3)", DisplayName = "IGNORE_DUP_KEY index insert")]
    [DataRow("insert c5 values (3, 9, 3)", "KEY RangeS-U (ffffffffffff), KEY RangeX-X (9), KEY X (3)", DisplayName = "IGNORE_DUP_KEY index insert past the last key")]
    [DataRow("insert c5 values (9, 1, 9)", "KEY U (1)", DisplayName = "IGNORE_DUP_KEY duplicate ignored")]
    [DataRow("insert hu values (3, 3)", "KEY RangeS-U (5), KEY RangeX-X (3), RID X", DisplayName = "IGNORE_DUP_KEY heap UNIQUE insert")]
    [DataRow("update c5 set u = 3 where k = 5", "KEY X (3), KEY X (5), KEY X (5)", DisplayName = "IGNORE_DUP_KEY index update takes no range")]
    public void WrittenRow_LocksItsIndexKeys(string write, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Shapes);
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("begin tran; " + write).ExecuteNonQuery();
        AreEqual(expected, RowAndKeyLocks(connection));
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual("", RowAndKeyLocks(connection));
    }

    // Starts sql on its own thread and returns once it shows waiting in the
    // lock DMVs.
    private Task StartBlocked(DbConnection connection, string sql, DbConnection observer)
    {
        var spid = Spid(connection);
        var task = Task.Run(() => connection.CreateCommand(sql).ExecuteNonQuery(), TestContext.CancellationToken);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while ((int)observer.CreateCommand($"select count(*) from sys.dm_tran_locks where request_session_id = {spid} and request_status = 'WAIT'").ExecuteScalar()! == 0)
        {
            if (task.IsCompleted || elapsed.Elapsed > TimeSpan.FromSeconds(30))
                Fail($"`{sql}` never waited");
            Thread.Sleep(5);
        }
        return task;
    }

    /// <summary>
    /// What the second writer waits in, and where: a uniqueness check on the
    /// key in its unique index, in X; a MERGE's or UPDATE's target read on the
    /// row, in U; an <c>IGNORE_DUP_KEY</c> check in U on the key or
    /// <c>RangeS-U</c> on the next key past a new one.
    /// </summary>
    [TestMethod]
    [DataRow("insert h2 values (3, 3)", "insert h2 values (3, 4)", "KEY X WAIT (3)", DisplayName = "Heap UNIQUE second insert")]
    [DataRow("insert c2 values (3, 7, 3)", "insert c2 values (4, 7, 4)", "KEY X WAIT (7)", DisplayName = "Unique index second insert")]
    [DataRow("insert c2 values (3, 7, 3)", "update c2 set u = 7 where k = 1", "KEY X WAIT (7)", DisplayName = "Unique index key-moving update")]
    [DataRow("delete c2 where k = 5", "insert c2 values (6, 5, 6)", "KEY X WAIT (5)", DisplayName = "Insert of a key an open delete took")]
    [DataRow("update c2 set v = 50 where k = 5", "merge c2 t using (values (5)) s (k) on t.k = s.k when matched then update set v = 9;", "KEY U WAIT (5)", DisplayName = "MERGE matching a written row")]
    [DataRow("update c2 set v = 50 where k = 5", "merge c2 t using (values (5)) s (k) on t.v = s.k when matched then update set v = 9;", "KEY U WAIT (5)", DisplayName = "MERGE scanning past a written row")]
    [DataRow("update h3 set v = 50 where k = 1", "update h3 set v = 9 where v = 5", "RID U WAIT", DisplayName = "UPDATE scanning past a written row")]
    [DataRow("update c1 set v = 50 where k = 5", "update c1 set v = 9 from c1 join d1 on c1.k = d1.k", "KEY U WAIT (5)", DisplayName = "Joined UPDATE")]
    [DataRow("update c1 set v = 50 where k = 5", "update c1 set v = 9 from d1 join c1 on c1.k = d1.k", "KEY U WAIT (5)", DisplayName = "Joined UPDATE, target on the right")]
    [DataRow("update h3 set v = 50 where k = 1", "update h3 set v = 9 from h3 join d1 on h3.k = d1.k", "RID U WAIT", DisplayName = "Joined UPDATE of a heap")]
    [DataRow("update c1 set v = 50 where k = 5", "delete c1 from c1 join d1 on c1.k = d1.k", "KEY U WAIT (5)", DisplayName = "Joined DELETE")]
    [DataRow("update d1 set w = 9 where k = 5", "update c1 set v = 9 from c1 join d1 on c1.k = d1.k", "KEY S WAIT (5)", DisplayName = "Joined UPDATE reading a written partner")]
    [DataRow("insert c5 values (3, 7, 3)", "insert c5 values (4, 7, 4)", "KEY U WAIT (7)", DisplayName = "IGNORE_DUP_KEY second insert of a key")]
    [DataRow("insert c5 values (3, 3, 3)", "insert c5 values (4, 4, 4)", "KEY RangeS-U WAIT (5)", DisplayName = "IGNORE_DUP_KEY insert into the same gap")]
    public async Task SecondWriter_WaitsWhereRealWaits(string first, string second, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Shapes);
        using var holder = simulation.CreateOpenConnection();
        using var waiter = simulation.CreateOpenConnection();
        using var observer = simulation.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran; " + first).ExecuteNonQuery();
        var blocked = StartBlocked(waiter, second, observer);
        var waits = RowAndKeyLocks(observer, Spid(waiter));
        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
        try
        {
            await blocked;
        }
        catch (SimulatedSqlException ex) when (ex.Number is 2601 or 2627)
        {
            // A key the rollback restored is a duplicate after all; only
            // where the second writer waited matters here.
        }
        Contains(expected, waits);
    }

    /// <summary>
    /// A writer judges its target rows as committed, waiting out another
    /// session's write rather than reading through it: each statement here
    /// judged the uncommitted 50 and so wrote 51, matched nothing or deleted
    /// nothing once that write rolled back.
    /// </summary>
    [TestMethod]
    [DataRow("update c1 set v = v + 1 where k = 5", "6", DisplayName = "UPDATE from the restored row")]
    [DataRow("merge c1 t using (values (5)) s (k) on t.k = s.k and t.v = 5 when matched then update set v = t.v + 1;", "6", DisplayName = "MERGE seeking")]
    [DataRow("merge c1 t using (values (5)) s (v) on t.v = s.v when matched then update set v = t.v + 1;", "6", DisplayName = "MERGE scanning")]
    [DataRow("delete c1 where v = 5", "", DisplayName = "DELETE")]
    [DataRow("update c1 set v = c1.v + 1 from c1 join d1 on c1.k = d1.k where c1.k = 5", "6", DisplayName = "Joined UPDATE")]
    [DataRow("update c1 set v = c1.v + 1 from c1 join d1 on c1.k = d1.k where c1.v = 5", "6", DisplayName = "Joined UPDATE, the write hiding the row from WHERE")]
    [DataRow("update c1 set v = c1.v + 1 from d1 join c1 on c1.k = d1.k and c1.v = 5", "6", DisplayName = "Joined UPDATE, the write hiding the row from ON")]
    [DataRow("delete c1 from c1 join d1 on c1.k = d1.k where c1.v = 5", "", DisplayName = "Joined DELETE")]
    public async Task TargetRead_WaitsOutAnUncommittedWrite(string write, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Shapes);
        using var holder = simulation.CreateOpenConnection();
        using var writer = simulation.CreateOpenConnection();
        using var observer = simulation.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran; update c1 set v = 50 where k = 5").ExecuteNonQuery();
        var blocked = StartBlocked(writer, write, observer);
        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
        await blocked;
        AreEqual(expected, simulation.ExecuteScalar("select isnull(string_agg(v, ','), '') from c1 where k = 5"));
    }

    /// <summary>
    /// A writer meeting a row another session deleted, or rewrote so the
    /// write's own predicate or seek no longer reaches it, waits for that
    /// session and, once it rolls back, writes the restored row.
    /// </summary>
    [TestMethod]
    [DataRow("delete c1 where k = 5", "update c1 set v = c1.v + 1 from c1 join d1 on c1.k = d1.k", "c1", "1:2 5:6", DisplayName = "Joined UPDATE over a delete")]
    [DataRow("delete c1 where k = 5", "update c1 set v = v + 1 where v = 5", "c1", "1:1 5:6", DisplayName = "UPDATE scanning over a delete")]
    [DataRow("delete c1 where k = 5", "update c1 set v = v + 1 where k = 5", "c1", "1:1 5:6", DisplayName = "UPDATE seeking a deleted key")]
    [DataRow("delete c1 where k = 5", "update c1 set v = v + 1", "c1", "1:2 5:6", DisplayName = "UPDATE of every row over a delete")]
    [DataRow("update c3 set n = 2 where k = 1", "update c3 set v = 9 where n = 1", "c3", "1:9 5:5", DisplayName = "UPDATE seeking an index key moved away")]
    [DataRow("update c3 set n = 2 where k = 1", "delete c3 where n = 1", "c3", "5:5", DisplayName = "DELETE seeking an index key moved away")]
    [DataRow("update h3 set k = 2 where k = 1", "update h3 set v = 9 where k = 1", "h3", "1:9 5:5", DisplayName = "Heap UPDATE seeking an index key moved away")]
    [DataRow("delete c1 where k = 5", "merge c1 t using (values (5)) s (k) on t.k = s.k when matched then update set v = t.v + 1;", "c1", "1:1 5:6", DisplayName = "MERGE matching a deleted key")]
    [DataRow("update c3 set n = 2 where k = 1", "merge c3 t using (values (1)) s (n) on t.n = s.n when matched then update set v = 9;", "c3", "1:9 5:5", DisplayName = "MERGE matching an index key moved away")]
    public async Task TargetRead_WaitsOutAWriteThatHidTheRow(string hidingWrite, string write, string table, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Shapes);
        using var holder = simulation.CreateOpenConnection();
        using var writer = simulation.CreateOpenConnection();
        using var observer = simulation.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran; " + hidingWrite).ExecuteNonQuery();
        var blocked = StartBlocked(writer, write, observer);
        Contains(" U WAIT", RowAndKeyLocks(observer, Spid(writer)));
        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
        await blocked;
        AreEqual(expected, simulation.ExecuteScalar($"select string_agg(concat(k, ':', v), ' ') within group (order by k) from {table}"));
    }

    /// <summary>
    /// Sessions incrementing rows from their own value never lose an increment
    /// — each waits out another's write of a row and judges it afresh — and,
    /// where a statement writes two rows, never deadlock, as on SQL Server
    /// 2025: each takes its rows' X in the order its walk met them.
    /// </summary>
    [TestMethod]
    [DataRow("update c set v = v + 1 where k = 1", DisplayName = "UPDATE, clustered")]
    [DataRow("update h set v = v + 1 where k = 1", DisplayName = "UPDATE, heap scan")]
    [DataRow("update c set v = v + 1", DisplayName = "UPDATE of every row, clustered")]
    [DataRow("update h set v = v + 1", DisplayName = "UPDATE of every row, heap")]
    [DataRow("merge c t using (values (1)) s (k) on t.k = s.k when matched then update set v = t.v + 1;", DisplayName = "MERGE")]
    [DataRow("merge c t using d s on t.k = s.k when matched then update set v = t.v + 1;", DisplayName = "MERGE of two rows")]
    [DataRow("update c set v = v + 1 where exists (select 1 from d where d.k = c.k)", DisplayName = "UPDATE, correlated subquery")]
    [DataRow("update c set v = c.v + 1 from c join d on c.k = d.k", DisplayName = "Joined UPDATE")]
    [DataRow("update c set v = c.v + 1 from d join c on c.k = d.k", DisplayName = "Joined UPDATE, target on the right")]
    [DataRow("update c set v = c.v + 1 from c join (select 1 k) s on c.k = s.k", DisplayName = "Joined UPDATE, derived table")]
    [DataRow("update a set v = a.v + 1 from c a join d on a.k = d.k", DisplayName = "Joined UPDATE, aliased target")]
    [DataRow("update h set v = h.v + 1 from h join d on h.k = d.k", DisplayName = "Joined UPDATE, heap")]
    [DataRow("update c set v = c.v + 1 from c where k = 1", DisplayName = "UPDATE … FROM the target alone")]
    [DataRow("update cv set v = v + 1", DisplayName = "UPDATE through a join view")]
    [DataRow("with x as (select k, v from c where exists (select 1 from d where d.k = c.k)) update x set v = v + 1", DisplayName = "UPDATE of a CTE")]
    [DataRow("update c set v = c.v + 1 from c cross apply (select d.k from d where d.k = c.k) s", DisplayName = "UPDATE with CROSS APPLY")]
    public void ConcurrentIncrements_AllLand(string increment)
    {
        const int Workers = 8, Rounds = 40;
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table c (k int primary key, v int); insert c values (1, 0), (2, 0);
            create table h (k int, v int); insert h values (1, 0), (2, 0);
            create table d (k int primary key); insert d values (1), (2);
            """);
        _ = simulation.ExecuteNonQuery("create view cv as select c.k, c.v from c join d on c.k = d.k");
        var table = increment.Contains(" h ", StringComparison.Ordinal) ? "h" : "c";
        _ = Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = TestContext.CancellationToken }, _ =>
        {
            using var connection = simulation.CreateOpenConnection();
            for (var round = 0; round < Rounds; round++)
                _ = connection.CreateCommand(increment).ExecuteNonQuery();
        });
        AreEqual(Workers * Rounds, simulation.ExecuteScalar($"select v from {table} where k = 1"));
        AreEqual(0, simulation.ExecuteScalar($"select count(*) from {table} where v not in (0, {Workers * Rounds})"));
    }

    /// <summary>
    /// Sessions draining one queue never both delete a row: each judges a row
    /// another is deleting as that delete leaves it, so the rows deleted add
    /// up to the rows there were.
    /// </summary>
    [TestMethod]
    [DataRow("delete top (3) q where exists (select 1 from d where d.k = q.k and d.k = @p)", DisplayName = "DELETE, correlated subquery")]
    [DataRow("delete top (3) q from q join d on q.k = d.k where d.k = @p", DisplayName = "Joined DELETE")]
    [DataRow("delete top (3) a from d join q a on a.k = d.k where d.k = @p", DisplayName = "Joined DELETE, aliased target on the right")]
    public void ConcurrentDeletes_DeleteEachRowOnce(string dequeue)
    {
        const int Workers = 8, Rows = 200;
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table q (id int primary key, k int);
            insert q select value, value % 2 from generate_series(1, {Rows});
            create table d (k int primary key); insert d values (0), (1);
            """);
        var deleted = 0;
        _ = Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = TestContext.CancellationToken }, worker =>
        {
            using var connection = simulation.CreateOpenConnection();
            // Half the sessions drain each parity, so each keeps meeting rows
            // another is deleting.
            var command = connection.CreateCommand(dequeue.Replace("@p", (worker % 2).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            while ((int)connection.CreateCommand("select count(*) from q").ExecuteScalar()! > 0)
                _ = Interlocked.Add(ref deleted, command.ExecuteNonQuery());
        });
        AreEqual(Rows, deleted);
    }

    /// <summary>
    /// A statement redefining a table waits in Sch-M for a transaction still
    /// writing it, where it swapped the rows away and the writer's rollback
    /// then failed on pages that were gone.
    /// </summary>
    [TestMethod]
    [DataRow("truncate table t", 0)]
    [DataRow("alter table t alter column v bigint", 1)]
    [DataRow("alter table t switch to t2", 0)]
    public async Task RedefiningStatement_WaitsForAnOpenWriter(string ddl, int rowsAfter)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (k int primary key, v int); insert t values (1, 1); create table t2 (k int primary key, v int)");
        using var holder = simulation.CreateOpenConnection();
        using var redefiner = simulation.CreateOpenConnection();
        using var observer = simulation.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran; insert t values (2, 2)").ExecuteNonQuery();
        var blocked = StartBlocked(redefiner, ddl, observer);
        AreEqual("OBJECT Sch-M LCK_M_SCH_M", observer.CreateCommand($"""
            select concat(l.resource_type, ' ', l.request_mode, ' ', w.wait_type) from sys.dm_tran_locks l join sys.dm_os_waiting_tasks w on w.session_id = l.request_session_id
            where l.request_session_id = {Spid(redefiner)} and l.request_status = 'WAIT'
            """).ExecuteScalar());
        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
        await blocked;
        AreEqual(rowsAfter, simulation.ExecuteScalar("select count(*) from t"));
    }

    private const string Cascades = """
        alter database current set allow_snapshot_isolation on;
        create table p (k int primary key, v int);
        create table ch (id int primary key, pk int references p (k) on delete cascade on update cascade, v int);
        create table cn (id int primary key, pk int references p (k) on delete set null, v int);
        insert p values (1, 1), (2, 2), (3, 3); insert ch values (10, 1, 0), (11, 2, 0), (12, 3, 0); insert cn values (20, 1, 0), (21, 2, 0);
        """;

    /// <summary>
    /// A foreign key's referential action writes its child rows as any write
    /// does: a SNAPSHOT transaction keeps reading them as they were, through
    /// the cascade's commit, where it read the cascade's effect in flight.
    /// </summary>
    [TestMethod]
    [DataRow("delete p where k = 1", DisplayName = "ON DELETE CASCADE / SET NULL")]
    [DataRow("update p set k = 33 where k = 3", DisplayName = "ON UPDATE CASCADE")]
    public void CascadedChildWrites_AreVersioned(string parentWrite)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Cascades);
        using var reader = simulation.CreateOpenConnection();
        using var writer = simulation.CreateOpenConnection();
        const string Children = "select concat((select string_agg(concat(id, ':', pk), ',') within group (order by id) from ch), ' ', (select string_agg(concat(id, ':', pk), ',') within group (order by id) from cn))";
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran").ExecuteNonQuery();
        var before = reader.CreateCommand(Children).ExecuteScalar();
        AreEqual("10:1,11:2,12:3 20:1,21:2", before);
        _ = writer.CreateCommand("begin tran; " + parentWrite).ExecuteNonQuery();
        AreEqual(before, reader.CreateCommand(Children).ExecuteScalar());
        _ = writer.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(before, reader.CreateCommand(Children).ExecuteScalar());
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
        AreNotEqual(before, reader.CreateCommand(Children).ExecuteScalar());
    }

    [TestMethod]
    public void CascadeMeetingAChangedChild_IsAnUpdateConflict()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Cascades);
        using var reader = simulation.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from p").ExecuteScalar();
        _ = simulation.ExecuteNonQuery("update ch set v = 5 where id = 11");
        var error = ThrowsExactly<SimulatedSqlException>(() => reader.CreateCommand("delete p where k = 2").ExecuteNonQuery());
        AreEqual(3960, error.Number);
        Contains("'dbo.ch'", error.Message);
        AreEqual(0, reader.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void CascadeIntoSystemVersionedChild_KeepsItsHistory()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table p (k int primary key);
            create table ch (id int primary key, pk int null references p (k) on delete cascade on update cascade,
                s datetime2 generated always as row start, e datetime2 generated always as row end, period for system_time (s, e))
                with (system_versioning = on (history_table = dbo.ch_h));
            create table cn (id int primary key, pk int null references p (k) on delete set null,
                s datetime2 generated always as row start, e datetime2 generated always as row end, period for system_time (s, e))
                with (system_versioning = on (history_table = dbo.cn_h));
            insert p values (1), (2); insert ch (id, pk) values (10, 1), (11, 2); insert cn (id, pk) values (20, 1);
            """);
        _ = simulation.ExecuteNonQuery("delete p where k = 1; update p set k = 22 where k = 2");
        AreEqual("10:1 11:2 | 20:1", simulation.ExecuteScalar("""
            select concat((select string_agg(concat(id, ':', pk), ' ') within group (order by id) from ch_h), ' | ',
                (select string_agg(concat(id, ':', pk), ' ') within group (order by id) from cn_h))
            """));
        AreEqual("11:22 | 20:", simulation.ExecuteScalar("""
            select concat((select string_agg(concat(id, ':', pk), ' ') from ch), ' | ', (select string_agg(concat(id, ':', pk), ' ') from cn))
            """));
    }

    [TestMethod]
    public void EdgeConstraintCascade_IsVersioned()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table n (id int) as node;
            create table e (constraint ec connection (n to n) on delete cascade) as edge;
            insert n values (1), (2);
            insert e values ((select $node_id from n where id = 1), (select $node_id from n where id = 2));
            """);
        using var reader = simulation.CreateOpenConnection();
        using var writer = simulation.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran").ExecuteNonQuery();
        AreEqual(1, reader.CreateCommand("select count(*) from e").ExecuteScalar());
        _ = writer.CreateCommand("begin tran; delete n where id = 1").ExecuteNonQuery();
        AreEqual(1, reader.CreateCommand("select count(*) from e").ExecuteScalar());
        _ = writer.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(1, reader.CreateCommand("select count(*) from e").ExecuteScalar());
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(0, reader.CreateCommand("select count(*) from e").ExecuteScalar());
    }

    private const string DefinitionChanged = "Snapshot isolation transaction failed in database 'simulated' because the object accessed by the statement has been modified by a DDL statement in another concurrent transaction since the start of this transaction.  It is disallowed because the metadata is not versioned. A concurrent update to metadata can lead to inconsistency if mixed with snapshot isolation.";

    /// <summary>
    /// Metadata isn't versioned: a SNAPSHOT transaction reaching a table
    /// another transaction created or redefined after its snapshot raises
    /// Msg 3961 and rolls back, reading or writing alike; a change that rolled
    /// back, or doesn't redefine the table, leaves it readable.
    /// </summary>
    [TestMethod]
    [DataRow("truncate table t", "select count(*) from t", true)]
    [DataRow("begin tran; truncate table t; commit", "select count(*) from t", true)]
    [DataRow("truncate table t", "insert t values (9, 9)", true)]
    [DataRow("alter table t add w int null", "select count(*) from t", true)]
    [DataRow("alter table t rebuild", "select count(*) from t", true)]
    [DataRow("create index ix on t (v)", "select count(*) from t", true)]
    [DataRow("exec sp_rename 't.v', 'vv', 'COLUMN'", "select count(*) from t", true)]
    [DataRow("exec ('create trigger tr on t after insert as set nocount on')", "select count(*) from t", true)]
    [DataRow("alter table t switch to t2", "select count(*) from t2", true)]
    [DataRow("create table n (k int)", "select count(*) from n", true)]
    [DataRow("truncate table t", "select count(*) from vt", true)]
    [DataRow("begin tran; truncate table t; rollback", "select count(*) from t", false)]
    [DataRow("update statistics t", "select count(*) from t", false)]
    [DataRow("create statistics st on t (v)", "select count(*) from t", false)]
    [DataRow("grant select on t to public", "select count(*) from t", false)]
    [DataRow("truncate table t", "select count(*) from t with (nolock)", false)]
    public void SnapshotOlderThanTheTableDefinition_RaisesMsg3961(string ddl, string access, bool raises)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table o (k int); insert o values (1);
            create table t (k int primary key, v int); insert t values (1, 1);
            create table t2 (k int primary key, v int);
            """);
        _ = simulation.ExecuteNonQuery("create view vt as select k from t");
        using var reader = simulation.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from o").ExecuteScalar();
        _ = simulation.ExecuteNonQuery(ddl);
        if (!raises)
        {
            _ = reader.CreateCommand(access).ExecuteNonQuery();
            AreEqual(1, reader.CreateCommand("select @@trancount").ExecuteScalar());
            return;
        }
        var error = ThrowsExactly<SimulatedSqlException>(() => reader.CreateCommand(access).ExecuteNonQuery());
        AreEqual(3961, error.Number);
        AreEqual(DefinitionChanged, error.Message);
        AreEqual(0, reader.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void Msg3961_CaughtDoomsTheTransaction()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table o (k int); create table t (k int);
            """);
        using var reader = simulation.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from o").ExecuteScalar();
        _ = simulation.ExecuteNonQuery("truncate table t");
        AreEqual("3961 1 -1", reader.CreateCommand("""
            declare @r varchar(20);
            begin try select count(*) from t; end try
            begin catch set @r = concat(error_number(), ' ', @@trancount, ' ', xact_state()); end catch;
            select @r; rollback
            """).ExecuteScalar());
    }

    [TestMethod]
    public void SnapshotTakenAfterTheDefinitionChange_ReadsTheTable()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            alter database current set allow_snapshot_isolation on;
            create table t (k int); insert t values (1);
            """);
        using var reader = simulation.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran").ExecuteNonQuery();
        _ = simulation.ExecuteNonQuery("truncate table t");
        AreEqual(0, reader.CreateCommand("select count(*) from t").ExecuteScalar());
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
    }
}
