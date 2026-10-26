using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Composable DML: an <c>INSERT … SELECT</c> reading the rows a nested
/// <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or <c>MERGE</c> sends through
/// its <c>OUTPUT</c>. Every expectation probed 2026-10-26 against SQL Server
/// 2025.
/// </summary>
[TestClass]
public sealed class ComposableDmlTests
{
    private const string Tables = """
        create table t (a int primary key, b int);
        create table h (a int, act nvarchar(10));
        insert t values (1, 10), (2, 20), (3, 30), (5, 50);
        """;

    [TestMethod]
    public void Merge_SendsItsActions_AndRowCountIsTheOuterInserts()
        => AreEqual("2|1UPDATE,4INSERT", new Simulation().ExecuteScalar(Tables + """
            insert h (a, act) select x.a, x.act from (merge t using (values (4, 40), (1, 11)) s (a, b) on t.a = s.a
                when matched then update set b = s.b when not matched then insert values (s.a, s.b) output inserted.a, $action) x (a, act);
            select concat(@@rowcount, '|', (select string_agg(concat(a, act), ',') within group (order by a) from h))
            """));

    [TestMethod]
    public void Merge_OverTablesItsBatchCreates()
        => AreEqual(2, new Simulation().ExecuteScalar(Tables + """
            create table m (a int primary key);
            insert h (a, act) select x.a, x.act from (merge m using (values (1), (2)) s (a) on m.a = s.a when not matched then insert values (s.a) output inserted.a, $action) x (a, act);
            select count(*) from h where act = 'INSERT'
            """));

    [TestMethod]
    public void Delete_WritesEveryRow_WhileTheOuterWhereFiltersWhatItInserts()
        => AreEqual("1|0|5", new Simulation().ExecuteScalar(Tables + """
            insert h select d.a, 'del' from (delete t output deleted.a where a >= 3) d where d.a > 3;
            select concat(@@rowcount, '|', (select count(*) from t where a >= 3), '|', (select a from h))
            """));

    [TestMethod]
    public void Update_FeedsExpressionsAndAColumnAliasList()
        => AreEqual("100upd|11", new Simulation().ExecuteScalar(Tables + """
            insert h (a, act) select u.a * 100, u.note from (update t set b = b + 1 output inserted.a, 'upd' as note where a = 1) u;
            select concat((select concat(a, act) from h), '|', (select b from t where a = 1))
            """));

    [TestMethod]
    public void IdentityFunctions_ReportTheNestedWrite_NotTheOuterInsert()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int primary key); create table n (id int identity(7, 1), a int); create table audit (id int identity(100, 1), a int)");
        AreEqual("|1", sim.ExecuteScalar("insert audit (a) select i.a from (insert t output inserted.a values (1)) i; select concat(scope_identity(), '|', @@rowcount)"));
        AreEqual("7|7", sim.ExecuteScalar("insert audit (a) select i.id from (insert n (a) output inserted.id values (2)) i; select concat(scope_identity(), '|', @@identity)"));
    }

    [TestMethod]
    public void TheOuterInsertsFailure_RollsTheNestedWriteBack()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Tables + "create table pk (a int primary key); insert pk values (1)");
        _ = sim.AssertSqlError("insert pk select u.a from (update t set b = 999 output inserted.a where a = 1) u", 2627);
        AreEqual(10, sim.ExecuteScalar("select b from t where a = 1"));
    }

    [TestMethod]
    public void EachExecution_RunsTheNestedWriteAgain()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Tables);
        using var connection = sim.CreateOpenConnection();
        for (var i = 0; i < 3; i++)
            _ = connection.CreateCommand("insert h (a) select d.a from (delete top (1) t output deleted.a) d").ExecuteNonQuery();
        AreEqual("3|1", sim.ExecuteScalar("select concat((select count(*) from h), '|', (select count(*) from t))"));
    }

    [TestMethod]
    public void Variables_AWithPrefix_AndATableVariableTarget()
        => AreEqual("1|2", new Simulation().ExecuteScalar(Tables + """
            declare @v int = 1;
            declare @t table (a int);
            with c as (select 1 x) insert h (a, act) select d.a, 'var' from (delete t output deleted.a where a = @v) d where d.a = @v;
            insert @t select d.a from (update t set b = b output deleted.a where a in (2, 3)) d;
            select concat((select count(*) from h where act = 'var'), '|', (select count(*) from @t))
            """));

    [TestMethod]
    [DataRow("insert h select d.a, 'x' from (delete t output deleted.a) d join t on 1 = 1", 10727)]
    [DataRow("insert h select d.a, 'x' from (delete t output deleted.a) d cross apply (select 1 y) z", 10727)]
    [DataRow("insert h select d.a, 'x' from (delete t output deleted.a) d, t", 10727)]
    [DataRow("insert h select distinct d.a, 'x' from (delete t output deleted.a) d", 10722)]
    [DataRow("insert h select top 1 d.a, 'x' from (delete t output deleted.a) d", 10717)]
    [DataRow("insert h select d.a, 'x' from (delete t output deleted.a) d order by d.a", 10717)]
    [DataRow("insert h select d.a, 'x' from (delete t output deleted.a) d group by d.a", 10717)]
    [DataRow("insert h select d.a, 'x' from (delete t output deleted.a) d where d.a > 0 having 1 = 1", 10717)]
    [DataRow("insert h select max(d.a), 'x' from (delete t output deleted.a) d", 5322)]
    [DataRow("insert h select row_number() over (order by d.a), 'x' from (delete t output deleted.a) d", 5329)]
    [DataRow("insert h select d.a, (select 'x') from (delete t output deleted.a) d", 5323)]
    [DataRow("insert h select d.a, 'x' from (delete t output deleted.a) d where d.a in (select a from t)", 5323)]
    [DataRow("insert h select d.a, 'x' from (delete t output deleted.a) d union all select 1, 'z'", 10732)]
    [DataRow("insert h select d.a, 'x' from (delete t where a = 2) d", 10716)]
    [DataRow("insert h select d.a, 'x' from (delete t output deleted.a into h (a) where a = 2) d", 10716)]
    [DataRow("insert h select x.a, 'x' from (insert t (a, b) output inserted.a select y.a + 10, 0 from (delete t output deleted.a) y) x", 10731)]
    [DataRow("select * from (delete t output deleted.a) d", 10729)]
    public void TheOuterQuery_IsRefusedWhatRealRefuses(string statement, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Tables);
        _ = sim.AssertSqlError(statement, number);
        AreEqual(4, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    [DataRow("create table g (a int); exec('create trigger tr on g after insert as begin declare @x int = 1 end')", "insert g select d.a from (delete t output deleted.a) d", 355)]
    [DataRow("create table p (a int primary key); create table g (a int references p(a))", "insert g select d.a from (delete t output deleted.a) d", 356)]
    [DataRow("create table g (a int primary key); create table c (a int references g(a))", "insert g select d.a from (delete t output deleted.a) d", 356)]
    public void ATargetWithATriggerOrAForeignKey_IsRefused(string setup, string statement, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Tables + setup);
        _ = sim.AssertSqlError(statement, number);
        AreEqual(4, sim.ExecuteScalar("select count(*) from t"));
    }
}
