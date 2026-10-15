using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The SQL Server 2000 compatibility views real defines over the modern
/// catalog — the database-scope ones checked row for row against SQL Server
/// 2025 over the same fixture, the server-scope ones over the same option and
/// login sequences (probed 2026-10-15).
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

    [TestMethod]
    [DataRow("", 65536, 1627389952)]
    [DataRow("auto_close on", 65537, 1627389952)]
    [DataRow("auto_shrink on", 4259840, 1627389952)]
    [DataRow("recovery simple", 65544, 1627389952)]
    [DataRow("recovery bulk_logged", 65540, 1627389952)]
    [DataRow("page_verify torn_page_detection", 16, 1627389952)]
    [DataRow("page_verify none", 0, 1627389952)]
    [DataRow("restricted_user", 67584, 1627389952)]
    [DataRow("single_user", 69632, 1627389952)]
    [DataRow("read_only", 1073808384, 1627389952)]
    [DataRow("ansi_null_default on", 65536, 1627406336)]
    [DataRow("quoted_identifier on", 65536, 1635778560)]
    [DataRow("cursor_default local", 65536, 1628438528)]
    [DataRow("auto_create_statistics off", 65536, 1610612736)]
    [DataRow("auto_update_statistics off", 65536, 553648128)]
    [DataRow("db_chaining on", 65536, 1627390976)]
    public void Sysdatabases_PacksOptionsAsReal(string option, int status, int status2)
        => AreEqual($"{status}|{status2}|170|0|0|998|0x01", new Simulation().ExecuteScalar($"""
            create database d;
            {(option.Length == 0 ? "" : $"alter database d set {option};")}
            select concat_ws('|', status, status2, cmptlevel, mode, category, version, convert(varchar, sid, 1)) from sysdatabases where name = 'd'
            """));

    [TestMethod]
    public void Sysdatabases_SystemDatabases()
        => AreEqual(
            "master|1|65544|1090520064 tempdb|2|65544|1090520064 model|3|65536|1090519040 msdb|4|65544|1627390976",
            new Simulation().ExecuteScalar("select string_agg(concat_ws('|', name, dbid, status, status2), ' ') within group (order by dbid) from master..sysdatabases where dbid <= 4"));

    [TestMethod]
    public void Syslogins_StatusAccessAndRoles()
        => AreEqual(
            "sa|9|master|0|1|1|0|0 zza|9|master|0|1|0|1|0 zzb|10|tempdb|1|0|0|0|1 zzc|10|master|0|0|0|0|0",
            new Simulation().ExecuteScalar("""
                use master;
                create login zza with password = 'Zz!12345678', check_policy = off;
                create login zzb with password = 'Zz!12345678', check_policy = off, default_database = tempdb;
                deny connect sql to zzb;
                alter server role dbcreator add member zza;
                alter server role ##MS_DefinitionReader## add member zzb;
                create login zzc with password = 'Zz!12345678', check_policy = off;
                revoke connect sql from zzc;
                select string_agg(concat_ws('|', name, status, dbname, denylogin, hasaccess, sysadmin, dbcreator, [##MS_DefinitionReader##]), ' ') within group (order by name)
                from syslogins where name like 'zz%' or name = 'sa'
                """));

    [TestMethod]
    public void Sysaltfiles_ListsEveryDatabasesFilesForControlServer()
    {
        var sim = new Simulation();
        AreEqual(
            "1|1|1024|-1|8192|2|0|d|/var/opt/mssql/data/d.mdf 2|0|1024|268435456|8192|66|0|d_log|/var/opt/mssql/data/d_log.ldf",
            sim.ExecuteScalar("create database d; select string_agg(concat_ws('|', fileid, groupid, size, maxsize, growth, status, perf, name, filename), ' ') within group (order by fileid) from sysaltfiles where dbid = db_id('d')"));
        sim.AssertSqlError(
            "use master; create login zza with password = 'Zz!12345678', check_policy = off; execute as login = 'zza'; select count(*) from sysaltfiles",
            229, "The SELECT permission was denied on the object 'sysaltfiles', database 'mssqlsystemresource', schema 'sys'.");
    }

    [TestMethod]
    public void Syscurconfigs_StatusPacksDynamicAndAdvanced()
        => AreEqual(
            "101|3 102|1 103|2",
            new Simulation().ExecuteScalar("select string_agg(concat_ws('|', config, status), ' ') within group (order by config) from syscurconfigs where config in (101, 102, 103)"));

    [TestMethod]
    public void Syscharsets_AndTheEmptyDeviceViews()
        => AreEqual(
            "117|iso_1|nocase_iso|0|0",
            new Simulation().ExecuteScalar("""
                select concat_ws('|', count(*), max(case when type = 1001 and id = 1 then name end), max(case when id = 52 then name end),
                    (select count(*) from sysdevices), (select count(*) from sysopentapes))
                from syscharsets
                """));

    [TestMethod]
    public void Sysoledbusers_ListsLinkedLogins()
    {
        var sim = new Simulation();
        sim.AddRemoteSimulation("zzls", new Simulation());
        AreEqual(
            "N|N|N|1 rmt|N|0x01|0",
            sim.ExecuteScalar("""
                use master;
                exec sp_addlinkedserver @server = 'zzls', @srvproduct = '', @provider = 'SQLNCLI', @datasrc = 'localhost';
                exec sp_addlinkedsrvlogin 'zzls', 'false', 'sa', 'rmt', 'pw';
                select string_agg(concat_ws('|', isnull(rmtloginame, 'N'), isnull(rmtpassword, 'N'), isnull(convert(varchar, loginsid, 1), 'N'), status), ' ') within group (order by rmtloginame)
                from sysoledbusers where rmtsrvid = (select server_id from sys.servers where name = 'zzls')
                """));
    }

    [TestMethod]
    public void Systypes_ListsTheDatabasesOwnTypes()
        => AreEqual(
            "t1|167|1|257|10|0|0|872468488|257|1|0|39|10|N tt|243|1|261|-1|0|0|N|261|0|0|0|0|N t2|56|0|258|4|10|0|N|258|0|1|56|10|0 t3|106|0|259|5|9|2|N|259|0|1|55|9|2 t4|231|0|260|-1|0|0|872468488|260|1|1|39|-1|N",
            new Simulation().ExecuteScalar("""
                create type t1 from varchar(10) not null;
                create type t2 from int;
                create type t3 from decimal(9,2);
                create type t4 from nvarchar(max);
                create type tt as table (a int);
                select string_agg(concat_ws('|', name, xtype, status, xusertype, length, xprec, xscale, isnull(cast(collationid as varchar), 'N'), usertype,
                    cast(variable as int), cast(allownulls as int), type, isnull(cast(prec as varchar), 'N'), isnull(cast(scale as varchar), 'N')), ' ')
                    within group (order by status desc, xusertype)
                from systypes where xusertype > 256
                """));

    [TestMethod]
    public void Systypes_ResolvesAsRealSpellsIt()
        => AreEqual(
            "36|36|36|36|36",
            new Simulation().ExecuteScalar("""
                select concat_ws('|', (select count(*) from systypes), (select count(*) from sys.systypes), (select count(*) from dbo.systypes),
                    (select count(*) from master..systypes), (select count(*) from master.sys.systypes))
                """));

    /// <summary>
    /// The usage views list every column and parameter a user-defined type or
    /// a typed <c>xml</c> declares — a table's, a table type's, a function's
    /// result and a function's return value as parameter 0 (probed 2026-10-15
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void UsageViews()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create type a1 from varchar(10)",
            "create type tt as table (x a1, y int)",
            """create xml schema collection xsc as N'<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="e" type="xs:string"/></xs:schema>'""",
            "create table t (id int, c a1, d xml(xsc), e xml, f a1 not null)",
            "create procedure p @p1 a1, @p2 tt readonly, @p3 xml(document xsc) = null, @p4 int as select 1",
            "create function f (@x a1) returns a1 as begin return @x end",
            "create function tvf (@x a1) returns @r table (r a1, s xml(xsc)) as begin return end");
        AreEqual(
            "c|t|2|a1 c|t|5|a1 c|tt|1|a1 c|tvf|1|a1 p|f|0|a1 p|f|1|a1 p|p|1|a1 p|p|2|tt p|tvf|1|a1 xc|t|3|xsc xc|tvf|2|xsc xp|p|3|xsc",
            simulation.ExecuteScalar("""
                select string_agg(x, ' ') within group (order by x) from (
                    select concat_ws('|', 'c', case when object_name(object_id) like 'TT[_]tt[_]%' then 'tt' else object_name(object_id) end, column_id, type_name(user_type_id)) from sys.column_type_usages
                    union all select concat_ws('|', 'p', object_name(object_id), parameter_id, type_name(user_type_id)) from sys.parameter_type_usages
                    union all select concat_ws('|', 'xc', object_name(object_id), column_id, 'xsc') from sys.column_xml_schema_collection_usages
                    union all select concat_ws('|', 'xp', object_name(object_id), parameter_id, 'xsc') from sys.parameter_xml_schema_collection_usages) u(x)
                """));
    }

    [TestMethod]
    public void SecurableClasses()
        => AreEqual("29|DATABASE|0|EXTERNAL MODEL|110", new Simulation().ExecuteScalar("""
            select concat_ws('|', count(*), min(case when class = 0 then class_desc end), min(class), max(case when class = 110 then class_desc end), max(class))
            from sys.securable_classes
            """));
}
