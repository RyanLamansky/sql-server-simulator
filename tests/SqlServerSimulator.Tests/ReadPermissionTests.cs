using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The SELECT a statement takes on what it reads outside a SELECT statement —
/// a cursor's query, an OUTPUT clause's <c>INSERTED</c> / <c>DELETED</c>, a
/// MERGE's target and <c>USING</c> source — and the INSERT an
/// <c>OUTPUT … INTO</c> target takes (probed 2026-10-25 against SQL Server
/// 2025). Each refusal here was a read real refuses and the simulator let
/// through.
/// </summary>
[TestClass]
public sealed class ReadPermissionTests
{
    /// <summary>
    /// <c>u</c> reads <c>t</c>'s and <c>src</c>'s <c>id</c> and <c>pub</c> but
    /// not their <c>secret</c>, may write <c>t</c> — its <c>pub</c> alone by
    /// UPDATE — and reads nothing of <c>noread</c>; <c>n</c> holds nothing.
    /// </summary>
    private static Simulation Seeded(string? grants = null)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            """
            create table t (id int primary key, secret varchar(20), pub varchar(20));
            insert t values (1, 's1', 'p1'), (2, 's2', 'p2');
            create table src (id int primary key, secret varchar(20), pub varchar(20));
            insert src values (1, 'S1', 'P1'), (2, 'S2', 'P2');
            create table noread (id int primary key, secret varchar(20));
            insert noread values (1, 'top');
            create table outt (pub varchar(20));
            create user u without login;
            create user n without login;
            grant insert, delete, update (pub), select (id, pub) on t to u;
            grant select (id, pub) on src to u
            """,
            "create view v_noread as select id, secret from noread",
            "create synonym syn for noread");
        if (grants is not null)
            _ = sim.ExecuteNonQuery(grants);
        return sim;
    }

    private static string Numbers(SimulatedSqlException ex) => string.Join(",", ex.Errors.Select(static e => $"{e.Number}"));

    // ---- A cursor's query ----

    [TestMethod]
    [DataRow("declare c cursor for select secret from noread", 229)]
    [DataRow("declare c cursor static for select secret from t", 230)]
    [DataRow("declare c cursor for select id from t where secret = 's1'", 230)]
    [DataRow("declare @c cursor; set @c = cursor for select secret from noread", 229)]
    public void Cursor_ReadingWhatThePrincipalCantRead_IsRefusedAtDeclare(string declare, int number)
        => _ = Seeded().AssertSqlError($"execute as user = 'u'; {declare}", number);

    [TestMethod]
    public void Cursor_RefusedInTry_ReportsTheLineItsQueryStartsOn()
        => AreEqual(4, Seeded().ExecuteScalar("""
            execute as user = 'u';
            begin try
              declare c cursor for
                select secret
                from noread
            end try
            begin catch
              select error_line()
            end catch
            """));

    [TestMethod]
    public void Cursor_OverGrantedColumns_Fetches()
        => AreEqual(1, Seeded().ExecuteScalar("execute as user = 'u'; declare c cursor for select id from t order by id; open c; fetch next from c"));

    [TestMethod]
    public void Cursor_AProcedureDeclares_OpensOutsideItThroughTheOwnershipChain()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create procedure p as declare gc cursor global for select secret from noread",
            "grant execute on p to u");
        AreEqual("top", sim.ExecuteScalar("execute as user = 'u'; exec p; open gc; fetch next from gc"));
    }

    // ---- OUTPUT's INSERTED / DELETED ----

    [TestMethod]
    [DataRow("insert t output inserted.secret values (10, 'n', 'n')")]
    [DataRow("insert t (id, pub) output inserted.secret values (10, 'n')")]
    [DataRow("declare @tv table (s varchar(20)); insert t output inserted.secret into @tv values (10, 'n', 'n')")]
    [DataRow("update t set pub = 'q' output deleted.secret where id = 1")]
    [DataRow("update t set pub = 'q' output len(inserted.secret) where id = 1")]
    [DataRow("delete t output deleted.secret where id = 1")]
    [DataRow("delete t output deleted.*")]
    [DataRow("merge t using src s on t.id = s.id when matched then update set pub = s.pub output deleted.secret;")]
    [DataRow("merge t using src s on t.id = s.id when matched then update set pub = s.pub output inserted.*;")]
    public void Output_ReadingAColumnThePrincipalCantRead_IsRefusedAndWritesNothing(string statement)
    {
        var sim = Seeded();
        sim.AssertSqlError($"execute as user = 'u'; {statement}", 230, "The SELECT permission was denied on the column 'secret' of the object 't', database 'simulated', schema 'dbo'.");
        AreEqual("1:s1:p1,2:s2:p2", sim.ExecuteScalar("select string_agg(concat(id, ':', secret, ':', pub), ',') within group (order by id) from t"));
    }

    [TestMethod]
    [DataRow("insert t output inserted.pub values (10, 'n', 'n')", "n")]
    [DataRow("update t set pub = 'q' output inserted.pub where id = 1", "q")]
    [DataRow("delete t output deleted.pub where id = 1", "p1")]
    [DataRow("merge t using src s on t.id = s.id when matched then update set pub = s.pub output $action, inserted.id;", "UPDATE")]
    public void Output_ReadingGrantedColumns_Returns(string statement, string first)
        => AreEqual(first, Seeded().ExecuteScalar($"execute as user = 'u'; {statement}"));

    [TestMethod]
    public void Output_ThroughAView_ChecksTheViewsColumn()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create view vt as select id, secret, pub from t",
            "grant delete, select (id) on vt to u");
        sim.AssertSqlError("execute as user = 'u'; delete vt output deleted.secret where id = 1", 230, "The SELECT permission was denied on the column 'secret' of the object 'vt', database 'simulated', schema 'dbo'.");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; delete vt output deleted.id where id = 1"));
    }

    [TestMethod]
    [DataRow("delete t output deleted.secret", "SELECT,DELETE")]
    [DataRow("update t set pub = 'x' output deleted.secret", "SELECT,UPDATE")]
    public void Output_WithoutEitherPermission_ReportsTheReadThenTheWrite(string statement, string permissions)
    {
        var ex = Seeded().AssertSqlError($"execute as user = 'n'; {statement}", 229);
        AreEqual("229,229", Numbers(ex));
        AreEqual(permissions, string.Join(",", ex.Errors.Select(static e => e.Message.Split(' ')[1])));
    }

    // ---- OUTPUT … INTO ----

    [TestMethod]
    public void OutputInto_ATableThePrincipalCantInsertInto_IsRefused()
    {
        var sim = Seeded();
        sim.AssertSqlError("execute as user = 'u'; delete t output deleted.pub into outt where id = 1", 229, "The INSERT permission was denied on the object 'outt', database 'simulated', schema 'dbo'.");
        _ = sim.ExecuteNonQuery("grant insert on outt to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; delete t output deleted.pub into outt where id = 1");
        AreEqual("p1", sim.ExecuteScalar("select pub from outt"));
    }

    [TestMethod]
    [DataRow("delete t output deleted.pub into outt", "SELECT,DELETE")]
    [DataRow("update t set pub = 'q' output inserted.pub into outt", "SELECT,UPDATE")]
    [DataRow("merge t using src s on t.id = s.id when matched then update set pub = s.pub output inserted.pub into outt;", "SELECT,UPDATE")]
    public void OutputInto_IsCheckedAfterTheTarget(string statement, string permissions)
    {
        var ex = Seeded().AssertSqlError($"execute as user = 'n'; {statement}", 229);
        AreEqual(permissions, string.Join(",", ex.Errors.Select(static e => e.Message.Split(' ')[1])));
    }

    [TestMethod]
    public void OutputInto_AfterTheMergeSource()
        => Seeded("grant select, update on t to n").AssertSqlError(
            "execute as user = 'n'; merge t using src s on t.id = s.id when matched then update set pub = s.pub output inserted.pub into outt;",
            229,
            "The SELECT permission was denied on the object 'src', database 'simulated', schema 'dbo'.");

    [TestMethod]
    public void OutputInto_ATableVariableOrTemporaryTable_NeedsNoGrant()
        => AreEqual(2, Seeded().ExecuteScalar("""
            execute as user = 'u';
            declare @o table (pub varchar(20));
            create table #o (pub varchar(20));
            delete t output deleted.pub into @o where id = 1;
            delete t output deleted.pub into #o where id = 2;
            select (select count(*) from @o) + (select count(*) from #o)
            """));

    // ---- MERGE's target ----

    [TestMethod]
    [DataRow("when matched then update set pub = s.pub", null)]
    [DataRow("when matched then update set secret = s.pub", "The UPDATE permission was denied on the column 'secret' of the object 't', database 'simulated', schema 'dbo'.")]
    [DataRow("when matched and t.secret = 's1' then delete", "The SELECT permission was denied on the column 'secret' of the object 't', database 'simulated', schema 'dbo'.")]
    [DataRow("when matched then update set pub = t.secret", "The SELECT permission was denied on the column 'secret' of the object 't', database 'simulated', schema 'dbo'.")]
    public void Merge_ChecksTheTargetsColumnsItReadsAndSets(string clause, string? message)
    {
        var statement = $"execute as user = 'u'; merge t using src s on t.id = s.id {clause};";
        if (message is null)
            _ = Seeded().ExecuteNonQuery(statement);
        else
            Seeded().AssertSqlError(statement, 230, message);
    }

    [TestMethod]
    public void Merge_ReadingNoTargetColumn_TakesNoSelectOnIt()
    {
        var sim = Seeded("grant insert on t to n; grant select on src to n");
        _ = sim.ExecuteNonQuery("execute as user = 'n'; merge t using src s on 1 = 0 when not matched then insert (id, secret, pub) values (s.id + 300, 'x', 'y');");
        AreEqual(2, sim.ExecuteScalar("select count(*) from t where id > 300"));
        sim.AssertSqlError(
            "execute as user = 'n'; merge t using src s on t.id = s.id + 400 when not matched then insert (id, secret, pub) values (s.id + 400, 'x', 'y');",
            229,
            "The SELECT permission was denied on the object 't', database 'simulated', schema 'dbo'.");
    }

    [TestMethod]
    [DataRow("when not matched by source then delete when not matched then insert values (s.id, s.secret, s.pub) when matched then update set pub = s.pub", "SELECT,INSERT,DELETE,UPDATE")]
    [DataRow("when matched and s.id = 1 then delete when matched then update set pub = s.pub", "SELECT,DELETE,UPDATE")]
    public void Merge_ReportsTheTargetsPermissionsInRealsOrder(string clauses, string permissions)
    {
        var ex = Seeded().AssertSqlError($"execute as user = 'n'; merge t using src s on t.id = s.id {clauses};", 229);
        AreEqual(permissions, string.Join(",", ex.Errors.Select(static e => e.Message.Split(' ')[1])));
    }

    // ---- MERGE's USING source ----

    [TestMethod]
    [DataRow("noread s on t.id = s.id", "noread")]
    [DataRow("v_noread s on t.id = s.id", "v_noread")]
    [DataRow("syn s on t.id = s.id", "syn")]
    [DataRow("(select id, secret from noread) s on t.id = s.id", "noread")]
    [DataRow("src s2 join noread s on s2.id = s.id on t.id = s.id", "noread")]
    public void Merge_ASourceThePrincipalCantRead_IsRefused(string source, string refused)
        => Seeded("grant select, update on t to n").AssertSqlError(
            $"execute as user = 'n'; merge t using {source} when matched then update set pub = s.secret;",
            229,
            $"The SELECT permission was denied on the object '{refused}', database 'simulated', schema 'dbo'.");

    [TestMethod]
    public void Merge_ACteSourceThePrincipalCantRead_IsRefused()
        => Seeded("grant select, update on t to n").AssertSqlError(
            "execute as user = 'n'; with c as (select id, secret from noread) merge t using c on t.id = c.id when matched then update set pub = c.secret;",
            229,
            "The SELECT permission was denied on the object 'noread', database 'simulated', schema 'dbo'.");

    [TestMethod]
    [DataRow("on t.id = s.id when matched then update set pub = s.secret")]
    [DataRow("on t.id = s.id when matched and s.secret = 'S1' then delete")]
    [DataRow("on t.id = s.id when matched then update set pub = s.pub output s.secret")]
    [DataRow("on 1 = 0 when not matched then insert (id, secret, pub) values (500, 'x', 'y')")]
    public void Merge_ChecksTheSourcesColumnsItReads_EveryColumnWhenItReadsNone(string rest)
        => Seeded().AssertSqlError(
            $"execute as user = 'u'; merge t using src s {rest};",
            230,
            "The SELECT permission was denied on the column 'secret' of the object 'src', database 'simulated', schema 'dbo'.");

    [TestMethod]
    public void Merge_ChecksTheSourceAfterTheTarget()
        => Seeded("grant select on t to n").AssertSqlError(
            "execute as user = 'n'; merge t using src s on t.id = s.id when matched then update set pub = s.pub;",
            229,
            "The UPDATE permission was denied on the object 't', database 'simulated', schema 'dbo'.");

    [TestMethod]
    public void Merge_ATableVariableOrTemporarySource_NeedsNoGrant()
        => AreEqual("x|y", Seeded().ExecuteScalar("""
            execute as user = 'u';
            declare @tv table (id int, pub varchar(20));
            insert @tv values (1, 'x');
            create table #s (id int, pub varchar(20));
            insert #s values (2, 'y');
            merge t using @tv s on t.id = s.id when matched then update set pub = s.pub;
            merge t using #s s on t.id = s.id when matched then update set pub = s.pub;
            select string_agg(pub, '|') within group (order by id) from t
            """));

    [TestMethod]
    public void Merge_AViewSourceThePrincipalMayRead_ReadsThroughTheOwnershipChain()
    {
        var sim = Seeded("grant select on v_noread to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; merge t using v_noread s on t.id = s.id when matched then update set pub = s.secret;");
        AreEqual("top", sim.ExecuteScalar("select pub from t where id = 1"));
    }
}
