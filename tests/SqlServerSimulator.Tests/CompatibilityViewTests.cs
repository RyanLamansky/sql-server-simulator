using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The SQL Server 2000 compatibility views real defines over the modern
/// catalog: <c>sysforeignkeys</c>, <c>sysconstraints</c>, <c>sysreferences</c>,
/// <c>sysmembers</c>, <c>sysfiles</c>, <c>sysfilegroups</c>,
/// <c>sysfulltextcatalogs</c>, <c>sysprotects</c> and <c>syspermissions</c>,
/// each checked row for row against SQL Server 2025 over the same fixture
/// (probed 2026-10-15).
/// </summary>
[TestClass]
public sealed class CompatibilityViewTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (a int not null, b int not null, c int null unique, constraint pk_p primary key (a, b));
            create table q (id int identity primary key nonclustered, pa int, pb int, d int default 5 check (d > 0), e int constraint df_e default 1,
                constraint fk_q foreign key (pa, pb) references p (a, b), constraint ck_q check (pa < 100));
            create role r1;
            create user u1 without login;
            alter role r1 add member u1;
            grant select on p to u1;
            grant select (a) on p to r1;
            deny update on q to u1;
            grant execute to r1;
            grant create table to u1;
            create fulltext catalog ftc as default;
            alter table q nocheck constraint fk_q;
            create table r (x int, y int, constraint ck_r check not for replication (x > 0 and y > 0),
                constraint fk_r foreign key (x) references q (id) not for replication);
            alter table r nocheck constraint ck_r;
            deny select (b) on p to r1;
            grant references on p to r1;
            grant update (b) on p to r1;
            create table wide (c1 int, c2 int, c3 int, c4 int, c5 int, c6 int, c7 int, c8 int, c9 int, c10 int);
            grant select (c9, c10) on wide to u1
            """);
        return sim;
    }

    private static string Rows(string query) =>
        (string)Seeded().ExecuteScalar($"select string_agg(x, ' ') within group (order by x) from ({query}) t(x)")!;

    [TestMethod]
    public void Sysforeignkeys()
        => AreEqual(
            "fk_q|q|p|2|1|1 fk_q|q|p|3|2|2 fk_r|r|q|1|1|1",
            Rows("select concat_ws('|', object_name(constid), object_name(fkeyid), object_name(rkeyid), fkey, rkey, keyno) from sysforeignkeys"));

    [TestMethod]
    public void Sysconstraints_UserNamed()
        => AreEqual(
            "ck_q|q|2|2068 ck_r|r|0|2115620 df_e|q|5|2069 fk_q|q|0|18467 fk_r|r|1|2099219 pk_p|p|0|2593",
            Rows("select concat_ws('|', object_name(constid), object_name(id), colid, status) from sysconstraints where object_name(constid) not like '%[_][_]%'"));

    [TestMethod]
    public void Sysconstraints_SystemNamed()
        => AreEqual(
            "C|q|4|133140 D|q|4|133141 P|q|0|134177 U|p|0|134178",
            Rows("select concat_ws('|', left(object_name(constid), 1), object_name(id), colid, status) from sysconstraints where object_name(constid) like '%[_][_]%'"));

    [TestMethod]
    public void Sysreferences()
        => AreEqual(
            "fk_q|q|p|1|2|2|3|0|1|2|0 fk_r|r|q|2|1|1|0|0|1|0|0",
            Rows("select concat_ws('|', object_name(constid), object_name(fkeyid), object_name(rkeyid), rkeyindid, keycnt, fkey1, fkey2, fkey16, rkey1, rkey2, rkey16) from sysreferences"));

    [TestMethod]
    public void Sysmembers()
        => AreEqual("dbo|db_owner u1|r1", Rows("select concat_ws('|', user_name(memberuid), user_name(groupuid)) from sysmembers"));

    [TestMethod]
    public void Sysprotects()
        => AreEqual(
            "0|r1|224|205|N 0|u1|198|205|N p|r1|193|205|0x02 p|r1|193|206|0x04 p|r1|197|205|0x04 p|r1|26|205|0x01 p|u1|193|205|0x01 q|u1|197|206|0x01 wide|u1|193|205|0x0006",
            Rows("select concat_ws('|', case when id > 0 then object_name(id) else cast(id as varchar) end, user_name(uid), action, protecttype, isnull(convert(varchar, columns, 1), 'N')) from sysprotects where id >= 0"));

    [TestMethod]
    public void Syspermissions()
        => AreEqual(
            "0|dbo 0|public 0|r1 0|u1 p|r1 p|u1 q|u1 wide|u1",
            Rows("select concat_ws('|', case when id > 0 then object_name(id) else cast(id as varchar) end, user_name(grantee)) from syspermissions where id >= 0"));

    [TestMethod]
    public void FilesAndFilegroups()
        => AreEqual(
            "1|1|1024|-1|8192|2|simulated 2|0|1024|268435456|8192|66|simulated_log ftc|1 PRIMARY|1|0|16",
            Rows("""
                select concat_ws('|', fileid, groupid, size, maxsize, growth, status, name) from sysfiles
                union all select concat_ws('|', groupname, groupid, allocpolicy, status) from sysfilegroups
                union all select concat_ws('|', name, status) from sysfulltextcatalogs
                """));

    [TestMethod]
    public void ResolveBareUnderSysAndThroughDbo()
        => AreEqual(3, Seeded().ExecuteScalar("select (select count(*) from sysforeignkeys) + (select count(*) from sys.sysforeignkeys) + (select count(*) from dbo.sysforeignkeys) - 6"));

    [TestMethod]
    public void RestrictedPrincipal_SeesWhatTheSourceViewsShow()
        => AreEqual(
            "0|2|0|2|9|7",
            Seeded().ExecuteScalar("""
                execute as user = 'u1';
                select concat_ws('|', (select count(*) from sysforeignkeys), (select count(*) from sysconstraints), (select count(*) from sysreferences),
                    (select count(*) from sysmembers), (select count(*) from sysprotects where id >= 0), (select count(*) from syspermissions where id >= 0));
                revert
                """));
}
