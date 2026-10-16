namespace SqlServerSimulator;

/// <summary>
/// <c>FORCESEEK</c> across joins, derived tables, CTEs, set operations and
/// inline functions: which shapes real's optimizer plans and which it refuses
/// with Msg 8622. Every shape was probed 2026-10-16 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ForceSeekSpreadTests
{
    private static readonly string[] Setup =
    [
        """
        create table t1 (id int primary key, v int)
        create table t2 (id int primary key, v int)
        create table t3 (id int primary key, v int)
        create table h (id int, v int)
        create index ix_v on t1(v)
        """,
        """
        create function zf() returns table as return select id, v from t1
        """,
        """
        create function zfh() returns table as return select id, v from h
        """,
        """
        create function zf2(@p int) returns table as return select id, v from t1 where id = @p
        """,
        """
        create function zm() returns @r table (id int, v int) as begin insert @r select id, v from t1; return end
        """,
        """
        create view vt as select * from zf()
        """,
        """
        create view vth as select * from zfh()
        """,
        """
        create view vt2 as select * from zf2(1)
        """,
        """
        create view vm as select * from zm()
        """,
        """
        create view vi as select id from t1 intersect select id from t2
        """,
        """
        create view ve as select id from t1 except select id from t2
        """,
        """
        create view vih as select id from t1 intersect select id from h
        """,
        """
        create view vu as select id from t1 union select id from t2
        """,
        """
        create view vua as select id from t1 union all select id from t2
        """,
        """
        create view vij as select t1.id, t2.v from t1 join t2 on t1.id = t2.id
        """,
    ];

    private static Simulation Prepared()
    {
        var simulation = new Simulation();
        foreach (var batch in Setup)
            _ = simulation.ExecuteNonQuery(batch);
        return simulation;
    }

    [TestMethod]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id where t2.v = 5")]
    [DataRow("select * from t1 with (forceseek), t2 with (forceseek) where t1.id = t2.id")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id or t1.id = 1")]
    [DataRow("select * from t1 with (forceseek) cross apply (select * from t2 with (forceseek) where t2.id = t1.id) x")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id + 1 = t2.id where t2.id = 1")]
    [DataRow("select * from vi with (forceseek)")]
    [DataRow("select * from vih with (forceseek) where id = 1")]
    [DataRow("select * from vua with (forceseek)")]
    [DataRow(";with c as (select id from t1 except select id from h) select * from c with (forceseek) where id = 1")]
    [DataRow("select * from ve with (forceseek)")]
    [DataRow(";with c as (select id from t1 except select id from t2) select * from c with (forceseek)")]
    [DataRow("select * from vt with (forceseek)")]
    [DataRow("select * from vth with (forceseek) where id = 1")]
    [DataRow(";with c as (select * from zf()) select * from c with (forceseek)")]
    [DataRow(";with c as (select * from zfh()) select * from c with (forceseek) where id = 1")]
    [DataRow("select * from vij with (forceseek) where v = 1")]
    [DataRow("update t1 set v = 1 from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id")]
    [DataRow("select * from t1 with (forceseek) where exists (select 1 from t2 with (forceseek) where t2.id = t1.id)")]
    [DataRow("select * from t1 with (forceseek) where id in (select id from t2 with (forceseek) where v = 1)")]
    [DataRow("select * from t1 with (forceseek) where id = (select max(id) from t2 with (forceseek))")]
    [DataRow("select * from vt a with (forceseek) join t2 b with (forceseek) on a.id = b.id")]
    [DataRow("select * from t1 with (forceseek(ix_v(v))) join t2 with (forceseek) on t1.v = t2.id")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id option (table hint(t1, forceseek))")]
    [DataRow("select * from t1 join t2 on t1.id = t2.id option (table hint(t1, forceseek), table hint(t2, forceseek))")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id where t1.id = 1 or t2.id = 2")]
    [DataRow("select * from t1 with (forceseek) join (select * from t2 with (forceseek)) b on t1.id = b.id")]
    [DataRow("select * from vt with (forceseek) join t3 with (forceseek) on vt.id = t3.id")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id and t2.v = 1")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id = @@spid")]
    [DataRow("select * from (select * from t2 with (forceseek)) b where b.v = 3")]
    [DataRow("select * from (select top 5 * from t2 with (forceseek)) b where b.id = 3")]
    [DataRow("select * from t1 with (forceseek) where id in (select id from t2 with (forceseek))")]
    [DataRow("select * from t1 with (forceseek) where v in (select id from t2 with (forceseek) where v = 1)")]
    [DataRow("declare @v int = 1; select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id = @v")]
    public void AnUnplannableSeek_IsMsg8622(string query) =>
        _ = Prepared().AssertSqlError(query, 8622);

    [TestMethod]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from t1 with (forceseek) join t2 on t1.id = t2.id")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id join t3 with (forceseek) on t2.id = t3.id where t3.id = 1")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id and t2.id = 1")]
    [DataRow("select * from t1 with (forceseek) left join t2 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from t1 with (forceseek) left join t2 with (forceseek) on t1.id = t2.id where t2.id = 1")]
    [DataRow("select * from t1 with (forceseek), t2 with (forceseek) where t1.id = t2.id and t2.id = 3")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.v = t2.id where t1.v = 4")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id where t1.v = 4")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id where t1.id > 0")]
    [DataRow("select * from t1 with (forceseek) join t2 on t1.id = t2.id join t3 with (forceseek) on t2.id = t3.id")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id join t3 on t3.id = t2.id")]
    [DataRow("select * from t2 with (forceseek) join t1 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from t1 with (forceseek) full join t2 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from t1 with (forceseek) cross apply (select * from t2 with (forceseek) where t2.id = t1.id) x where t1.id = 1")]
    [DataRow("select * from t1 a with (forceseek) join t1 b with (forceseek) on a.id = b.id where b.id = 2")]
    [DataRow("select * from t1 a with (forceseek) join t1 b with (forceseek) on a.id = b.v where b.id = 2")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id + 1 where t2.id = 1")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id + 1 = t2.id where t1.id = 1")]
    [DataRow("select * from vi with (forceseek) where id = 1")]
    [DataRow("select * from ve with (forceseek) where id = 1")]
    [DataRow("select * from vu with (forceseek) where id = 1")]
    [DataRow(";with c as (select id from t1 intersect select id from t2) select * from c with (forceseek) where id = 1")]
    [DataRow("select * from vt with (forceseek) where id = 1")]
    [DataRow(";with c as (select * from zf()) select * from c with (forceseek) where id = 1")]
    [DataRow("select * from vt2 with (forceseek)")]
    [DataRow("select * from vm with (forceseek)")]
    [DataRow("select * from vt with (forceseek) where v = 3")]
    [DataRow("select * from vij with (forceseek) where id = 1")]
    [DataRow("update t1 set v = 1 from t1 join t2 with (forceseek) on t1.id = t2.id")]
    [DataRow("delete t1 from t1 join t2 with (forceseek) on t1.id = t2.id join t3 with (forceseek) on t2.id = t3.id")]
    [DataRow("select * from t1 where exists (select 1 from t2 with (forceseek) where t2.id = t1.id)")]
    [DataRow("select * from t1 with (forceseek) where id = (select max(id) from t2)")]
    [DataRow("select * from vt a with (forceseek) join t2 b with (forceseek) on a.id = b.id where b.id = 1")]
    [DataRow("select * from t2 b cross apply zf2(b.id) x")]
    [DataRow("select * from vt2 with (forceseek) where v = 1")]
    [DataRow("select * from vm with (forceseek) where id = 1")]
    [DataRow("select * from t1 with (forceseek) join vt with (forceseek) on t1.id = vt.id where t1.id = 1")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id join t3 with (forceseek) on t3.v = t1.v where t3.id = 1")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id join t3 with (forceseek) on t3.id = t1.v where t3.id = 1")]
    [DataRow("select * from t1 with (forceseek, index(ix_v)) join t2 with (forceseek) on t1.v = t2.id where t2.id = 1")]
    [DataRow("select * from t1 join t2 on t1.id = t2.id where t1.id = 1 option (table hint(t1, forceseek), table hint(t2, forceseek))")]
    [DataRow("select * from t1 with (forceseek) inner loop join t2 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from t1 with (forceseek) inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from t1 with (forceseek) inner merge join t2 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id where t1.id in (1, 2)")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id where t1.id = @@spid")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id between t2.id and t2.id + 5 where t2.id = 1")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id where t2.id is null")]
    [DataRow("select * from (select * from t1 with (forceseek) where id = 1) a join t2 with (forceseek) on a.id = t2.id")]
    [DataRow("select * from t1 with (forceseek) join (select * from t2 where id = 1) b on t1.id = b.id")]
    [DataRow("select * from t1 with (forceseek) join (select * from t2 with (forceseek)) b on t1.id = b.id where b.id = 3")]
    [DataRow("merge t1 using t2 with (forceseek) on t1.id = t2.id when matched then update set v = 1;")]
    [DataRow("merge t1 with (forceseek) using t2 on t1.id = t2.id when matched then update set v = 1;")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id join t3 on t3.id = t2.id where t3.id = 1")]
    [DataRow("select * from vi with (forceseek) join t3 on vi.id = t3.id")]
    [DataRow("select * from vt with (forceseek) join t3 on vt.id = t3.id")]
    [DataRow("select * from vt with (forceseek) join t3 with (forceseek) on vt.id = t3.id where t3.id = 5")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t2.id = 1")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.v = t2.id where t1.v = 1")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id > 1")]
    [DataRow("select * from t1 inner merge join t2 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id in (1, 2)")]
    [DataRow("select * from t1 with (forceseek) join t2 with (forceseek) on t1.id = t2.id where t1.id = 1 option (hash join)")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id join t3 on t3.id = t1.id where t3.id = 1")]
    [DataRow("select * from t1 left hash join t2 with (forceseek) on t1.id = t2.id where t1.id = 1")]
    [DataRow("select * from (select * from t2 with (forceseek)) b where b.id = 3")]
    [DataRow("select * from t1 join (select * from t2 with (forceseek)) b on t1.id = b.id")]
    [DataRow(";with c as (select * from t2 with (forceseek)) select * from c where id = 3")]
    [DataRow("select * from (select id from t2 with (forceseek) group by id) b where b.id = 3")]
    [DataRow("select * from vij where id = 1 and exists (select 1 from (select * from t2 with (forceseek)) z where z.id = vij.id)")]
    [DataRow("select * from t1 where id in (select id from t2 with (forceseek) where v = 1)")]
    [DataRow("select * from t1 with (forceseek) where id in (select id from t2 where v = 1)")]
    [DataRow("select * from t1 with (forceseek) where id in (select v from t2 with (forceseek) where id = 1)")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id = 1 + 1")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id = abs(-1)")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id = cast('1' as int)")]
    [DataRow("select * from t1 inner hash join t2 with (forceseek) on t1.id = t2.id where t1.id is null")]
    public void APlannableSeek_Runs(string query) =>
        _ = Prepared().ExecuteNonQuery(query);
}
