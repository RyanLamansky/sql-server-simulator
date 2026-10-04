using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Change tracking: the database and table switches, the per-commit version,
/// <c>CHANGETABLE(CHANGES …)</c>'s net-change folding and column masks,
/// <c>CHANGETABLE(VERSION …)</c>, <c>WITH CHANGE_TRACKING_CONTEXT</c>, the three
/// scalars and the catalog. Every expectation was probed against SQL Server
/// 2025 on 2026-09-27.
/// </summary>
[TestClass]
public sealed class ChangeTrackingTests
{
    private const string Tracked = """
        alter database current set change_tracking = on;
        create table t (id int primary key, a int, b int, c int);
        alter table t enable change_tracking with (track_columns_updated = on);
        """;

    /// <summary>Each row as <c>id op version/creation</c>, in key order.</summary>
    private static string Changes(string lastSync) => $"""
        select string_agg(concat(id, ct.SYS_CHANGE_OPERATION, ct.SYS_CHANGE_VERSION, '/', ct.SYS_CHANGE_CREATION_VERSION), ' ') within group (order by id)
        from changetable(changes t, {lastSync}) ct
        """;

    [TestMethod]
    [DataRow("", "0", "1I1/1 2I1/1 3I1/1")]
    [DataRow("update t set a = 9 where id = 1;", "0", "1I2/1 2I1/1 3I1/1")]
    [DataRow("update t set a = 9 where id = 1;", "1", "1U2/")]
    [DataRow("delete t where id = 2;", "0", "1I1/1 2D2/1 3I1/1")]
    [DataRow("delete t where id = 2;", "1", "2D2/")]
    [DataRow("insert t values (4, 0, 0, 0); delete t where id = 4;", "1", "4D3/2")]
    [DataRow("delete t where id = 1; insert t values (1, 0, 0, 0);", "1", "1U3/3")]
    [DataRow("delete t where id = 1; insert t values (1, 0, 0, 0); delete t where id = 1; insert t values (1, 0, 0, 0);", "3", "1U5/5")]
    [DataRow("update t set id = id + 10 where id < 3;", "1", "1D2/ 2D2/ 11I2/2 12I2/2")]
    [DataRow("update t set id = id + 1;", "1", "1D2/ 2U2/2 3U2/2 4I2/2")]
    [DataRow("update t set a = 1 where 1 = 0;", "1", "")]
    [DataRow("", "null", "1I1/1 2I1/1 3I1/1")]
    [DataRow("", "-1", "")]
    [DataRow("update t set id = id where id = 1;", "1", "1U2/2")]
    [DataRow("update t set a = 1;", "99", "")]
    public void Changes_NetsEachRow(string writes, string lastSync, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            {Tracked}
            insert t values (1, 1, 1, 1), (2, 2, 2, 2), (3, 3, 3, 3);
            {writes}
            {Changes(lastSync)}
            """) is string s ? s : "");

    [TestMethod]
    public void Changes_OneTransactionIsOneVersionAndMergesItsWrites()
        => AreEqual("1D2/2 2I2/2 9I2/2 | 1D2/2 2I2/2 9U2/2 | 2", new Simulation().ExecuteScalar($"""
            {Tracked}
            insert t values (9, 9, 9, 9);
            begin tran;
            insert t values (1, 1, 1, 1); delete t where id = 1;
            insert t values (2, 2, 2, 2); delete t where id = 2; insert t values (2, 3, 3, 3);
            delete t where id = 9; insert t values (9, 10, 10, 10); update t set a = 4 where id = 9;
            commit;
            select concat(({Changes("0")}), ' | ', ({Changes("1")}), ' | ', change_tracking_current_version())
            """));

    [TestMethod]
    public void Transactions_RollbackAndSavepointsDropTheirChanges()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand($"""
            {Tracked}
            begin tran;
            insert t values (1, 1, 1, 1);
            select change_tracking_current_version();
            """).ExecuteNonQuery();
        // Inside the transaction the version hasn't moved and CHANGETABLE sees
        // only committed changes.
        AreEqual(0L, connection.CreateCommand("select change_tracking_current_version()").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand(Changes("0")).ExecuteScalar());
        _ = connection.CreateCommand("""
            rollback;
            begin tran;
            insert t values (4, 4, 4, 4);
            save tran s;
            insert t values (5, 5, 5, 5);
            rollback tran s;
            commit;
            """).ExecuteNonQuery();
        AreEqual("4I1/1", connection.CreateCommand(Changes("0")).ExecuteScalar());
    }

    [TestMethod]
    [DataRow("create table u (id int primary key); insert u values (1);", 0L)]
    [DataRow("update t set a = 1;", 0L)]
    [DataRow("insert t values (1, 1, 1, 1); update t set a = a;", 2L)]
    [DataRow("insert t values (1, 11, 1, 1); update t set a = a + 1;", 1L)]
    [DataRow("insert t values (1, 1, 1, 1); truncate table t;", 1L)]
    public void CurrentVersion_AdvancesOncePerCommittedTrackedWrite(string writes, long expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            {Tracked}
            alter table t add constraint ck check (a < 12);
            begin try {writes} end try begin catch end catch
            select change_tracking_current_version()
            """));

    [TestMethod]
    [DataRow("update t set b = 5 where id = 1;", "1:0x0000000003000000")]
    [DataRow("update t set c = 5, a = 3 where id = 1;", "1:0x000000000400000002000000")]
    [DataRow("update t set b = 5 where id = 1; update t set c = 5, a = 3, b = 1 where id = 1;", "1:")]
    [DataRow("update t set b = 5 where id = 1; update t set c = 5, a = 3 where id = 1;", "1:0x00000000030000000400000002000000")]
    [DataRow("update t set a = 1, b = 1, c = 1 where id = 1;", "1:")]
    [DataRow("update t set id = id where id = 1;", "1:")]
    [DataRow("update t set a = 1 where id = 1; update t set b = 1 where id = 1; update t set c = 1 where id = 1;", "1:0x00000000020000000300000004000000")]
    [DataRow("insert t values (2, 2, 2, 2); update t set a = 5 where id = 2;", "2:")]
    public void ChangeColumns_ListTheColumnsEveryUpdateSet(string writes, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            {Tracked}
            insert t values (1, 1, 1, 1);
            {writes}
            select string_agg(concat(id, ':', convert(varchar(100), sys_change_columns, 1)), ' ') from changetable(changes t, 1) ct
            """));

    [TestMethod]
    public void ChangeColumns_StampRowVersionAndSkipWithoutTracking()
        => AreEqual("0x000000000200000004000000 | ", new Simulation().ExecuteScalar("""
            alter database current set change_tracking = on;
            create table r (id int primary key, a int, b int, rv rowversion, cc as a + 1);
            create table n (id int primary key, a int, b int);
            alter table r enable change_tracking with (track_columns_updated = on);
            alter table n enable change_tracking;
            insert r (id, a, b) values (1, 1, 1); insert n values (1, 1, 1);
            update r set a = 5; update n set a = 5;
            select concat((select convert(varchar(100), sys_change_columns, 1) from changetable(changes r, 1) x), ' | ',
                (select convert(varchar(100), sys_change_columns, 1) from changetable(changes n, 1) y))
            """));

    [TestMethod]
    [DataRow("2, null", 1)]
    [DataRow("1, 0x0000000002000000", 0)]
    [DataRow("2, 0x0000000002000000", 1)]
    [DataRow("-1, 0x00000000ffffffff", 1)]
    [DataRow("1, 0x0000000001000000ff", 1)]
    [DataRow("1, 0x01000000ffffffff", 0)]
    [DataRow("1, cast(0x0000000001000000 as binary(12))", 1)]
    public void IsColumnInMask_ReadsTheIdList(string arguments, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select change_tracking_is_column_in_mask({arguments})"));

    [TestMethod]
    [DataRow("select change_tracking_is_column_in_mask(1, 0x)", 22101)]
    [DataRow("select change_tracking_is_column_in_mask(1, 0x00000000)", 22101)]
    [DataRow("select change_tracking_is_column_in_mask(1, 1)", 22101)]
    [DataRow("select change_tracking_is_column_in_mask(null, 0x01)", 8116)]
    [DataRow("select change_tracking_is_column_in_mask(cast(1 as bigint), 0x0000000001000000)", 8116)]
    [DataRow("select change_tracking_is_column_in_mask('a', 0x01)", 8116)]
    [DataRow("select change_tracking_is_column_in_mask(1, 'abc')", 257)]
    [DataRow("select change_tracking_is_column_in_mask(1)", 174)]
    [DataRow("select change_tracking_min_valid_version(cast(1 as tinyint))", 8116)]
    [DataRow("select change_tracking_min_valid_version(null)", 8116)]
    [DataRow("select change_tracking_current_version(1)", 174)]
    public void Scalars_RefuseWhatRealRefuses(string sql, int error)
        => _ = new Simulation().AssertSqlError(sql, error);

    [TestMethod]
    public void Context_TagsTheLastStatementToTouchEachRow()
        => AreEqual("1:0x08 2: 3:0x0A 4:0x", new Simulation().ExecuteScalar($"""
            {Tracked}
            insert t values (1, 1, 1, 1), (2, 2, 2, 2);
            declare @empty varbinary(128) = 0x;
            with change_tracking_context (@empty) insert t values (4, 4, 4, 4);
            begin tran;
            with change_tracking_context (0x07) update t set a = 2 where id < 3;
            with change_tracking_context (0x08) update t set a = 3 where id = 1;
            update t set a = 3 where id = 2;
            commit;
            declare @c binary(1) = 0x0a;
            with change_tracking_context (@c), s as (select 3 id) merge t using s on t.id = s.id when not matched then insert values (s.id, 0, 0, 0);
            select string_agg(concat(id, ':', convert(varchar(10), sys_change_context, 1)), ' ') within group (order by id) from changetable(changes t, 0) ct
            """));

    [TestMethod]
    [DataRow("declare @c varbinary(200) = 0x01; with change_tracking_context (@c) insert t values (1, 1, 1, 1)", 22109)]
    [DataRow("declare @c varbinary(max) = 0x01; with change_tracking_context (@c) insert t values (1, 1, 1, 1)", 22109)]
    [DataRow("declare @c varchar(10) = 'a'; with change_tracking_context (@c) insert t values (1, 1, 1, 1)", 22109)]
    [DataRow("with change_tracking_context ('abc') insert t values (1, 1, 1, 1)", 102)]
    [DataRow("with change_tracking_context (null) insert t values (1, 1, 1, 1)", 156)]
    [DataRow("with change_tracking_context (cast(0x01 as varbinary(1))) insert t values (1, 1, 1, 1)", 102)]
    public void Context_RefusesAnythingButAShortBinary(string sql, int error)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Tracked);
        _ = sim.AssertSqlError(sql, error);
    }

    [TestMethod]
    public void Version_LooksRowsUpAndReportsTheirLatestChange()
        => AreEqual("1:x: 2:y:2", new Simulation().ExecuteScalar("""
            alter database current set change_tracking = on;
            create table k (id int, s varchar(5), a int, primary key (s, id));
            insert k values (1, 'x', 1);
            alter table k enable change_tracking;
            insert k values (2, 'y', 2), (3, 'z', 3);
            update k set a = 5 where id = 2;
            delete k where id = 3;
            select string_agg(concat(v.id, ':', v.s, ':', v.SYS_CHANGE_VERSION), ' ') within group (order by v.id)
            from k cross apply changetable(version k, (id, s), (k.id, k.s)) v
            """));

    [TestMethod]
    public void Version_ReportsNullForTheSessionsOwnUncommittedChange()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand($"""
            {Tracked}
            insert t values (1, 1, 1, 1);
            begin tran;
            update t set a = 5;
            """).ExecuteNonQuery();
        AreEqual(DBNull.Value, connection.CreateCommand("select v.SYS_CHANGE_VERSION from changetable(version t, (id), (1)) v").ExecuteScalar());
        _ = connection.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(2L, connection.CreateCommand("select v.SYS_CHANGE_VERSION from changetable(version t, (id), (1)) v").ExecuteScalar());
    }

    [TestMethod]
    [DataRow("select * from changetable(version t, (id, a), (1, 1)) v", 22110)]
    [DataRow("select * from changetable(version t, (a), (1)) v", 22111)]
    [DataRow("select * from changetable(version k, (id), (1)) v", 22110)]
    [DataRow("select * from changetable(version k, (id, s, id), (1, 'x', 1)) v", 22110)]
    [DataRow("select * from changetable(version k, (id, s), (1)) v", 22103)]
    [DataRow("select * from changetable(version k, (id, s), ('2', 5)) v", 245)]
    [DataRow("select * from changetable(changes t, 0)", 22104)]
    [DataRow("select * from changetable(changes u, 0) c", 22105)]
    [DataRow("select * from changetable(changes v, 0) c", 22107)]
    [DataRow("select * from changetable(changes sys.objects, 0) c", 22107)]
    [DataRow("select * from changetable(changes nosuch, 0) c", 208)]
    [DataRow("select * from changetable(changes nosuch) c", 102)]
    [DataRow("select * from changetable(changes t, 1.5) c", 102)]
    [DataRow("declare @x int = 0; select * from changetable(changes t, @x + 1) c", 102)]
    [DataRow("declare @s varchar(5) = 'x'; select * from changetable(changes t, @s) c", 8114)]
    [DataRow("declare @d datetime = getdate(); select * from changetable(changes t, @d) c", 257)]
    [DataRow("select * from changetable(foo t, 0) c", 102)]
    public void ChangeTable_RefusesWhatRealRefuses(string sql, int error)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            $"""
            {Tracked}
            create table u (id int primary key);
            create table k (id int, s varchar(5), primary key (id, s));
            alter table k enable change_tracking;
            insert k values (2, 'y');
            """,
            "create view v as select id from t");
        _ = sim.AssertSqlError(sql, error);
    }

    [TestMethod]
    public void ChangeTable_UntrackedTableRefusesTheWholeBatch()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set change_tracking = on;
            create table t (id int primary key);
            create table u (id int primary key);
            """);
        _ = sim.AssertSqlError("insert u values (1); if 1 = 0 select * from changetable(changes t, 0) c", 22105);
        _ = sim.AssertSqlError("create procedure p as select * from changetable(changes t, 0) c", 22105);
        AreEqual(0, sim.ExecuteScalar("select count(*) from u"));
    }

    [TestMethod]
    public void ChangeTable_DescribesItsColumns()
        => AreEqual(
            "SYS_CHANGE_VERSION:bigint00 SYS_CHANGE_CREATION_VERSION:bigint00 SYS_CHANGE_OPERATION:nchar(1)01 SYS_CHANGE_COLUMNS:varbinary(4100)00 SYS_CHANGE_CONTEXT:varbinary(128)00 k:varchar(5)10 id:int10",
            new Simulation().ExecuteScalar("""
                alter database current set change_tracking = on;
                create table t (id int, k varchar(5), a int, primary key (k, id));
                alter table t enable change_tracking;
                select string_agg(concat(name, ':', system_type_name, cast(is_updateable as int), cast(is_case_sensitive as int)), ' ') within group (order by column_ordinal)
                from sys.dm_exec_describe_first_result_set(N'select * from changetable(changes t, 0) ct', null, 0)
                """));

    [TestMethod]
    public void ChangeTable_ColumnAliasListRenames()
        => AreEqual(1L, new Simulation().ExecuteScalar($"""
            {Tracked}
            insert t values (1, 1, 1, 1);
            select c.ver from changetable(changes t, 0) as c(ver, cre, op, cols, ctx, key_id)
            """));

    [TestMethod]
    [DataRow("alter table t enable change_tracking", 1718)]
    [DataRow("alter database current set change_tracking = off", 5089)]
    [DataRow("alter database current set change_tracking (auto_cleanup = on)", 5089)]
    [DataRow("alter database master set change_tracking = on", 5090)]
    [DataRow("alter database msdb set change_tracking = on", 5090)]
    [DataRow("alter database current set change_tracking = on (auto_cleanup = on, auto_cleanup = off)", 5091)]
    [DataRow("alter database current set change_tracking = on (change_retention = 0 days)", 5092)]
    [DataRow("alter database current set change_tracking = on (change_retention = 2000000 days)", 5092)]
    [DataRow("alter database current set change_tracking = on (change_retention = 1 weeks)", 155)]
    [DataRow("alter database current set change_tracking = on (change_retention = 1)", 102)]
    [DataRow("alter database current set change_tracking = on ()", 102)]
    [DataRow("alter database current set change_tracking on", 156)]
    [DataRow("alter database current set change_tracking = on, recursive_triggers on", 22114)]
    public void DatabaseSwitch_RefusesWhatRealRefuses(string sql, int error)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key)");
        _ = sim.AssertSqlError(sql, error);
    }

    [TestMethod]
    [DataRow("alter database current set change_tracking = on", 5088)]
    [DataRow("alter database current set change_tracking = off", 22115)]
    [DataRow("alter table t enable change_tracking", 4996)]
    [DataRow("alter table h enable change_tracking", 4997)]
    [DataRow("alter table u disable change_tracking", 4998)]
    [DataRow("alter table nosuch enable change_tracking", 4902)]
    [DataRow("create table #tt (id int primary key); alter table #tt enable change_tracking", 1718)]
    [DataRow("alter table t enable change_tracking with (track_columns_updated = bogus)", 102)]
    [DataRow("alter table u enable change_tracking (track_columns_updated = on)", 102)]
    [DataRow("alter table t drop constraint pk", 3735)]
    public void TableSwitch_RefusesWhatRealRefuses(string sql, int error)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database current set change_tracking = on;
            create table t (id int constraint pk primary key);
            create table u (id int primary key);
            create table h (a int);
            alter table t enable change_tracking;
            """);
        _ = sim.AssertSqlError(sql, error);
    }

    [TestMethod]
    public void Catalog_ReportsTheSettings()
        => AreEqual("0:5:2:HOURS 1:1:1:0:0 1 NULL", new Simulation().ExecuteScalar("""
            alter database current set change_tracking = on (change_retention = 5 hours, auto_cleanup = off);
            create table t (id int primary key, a int);
            alter table t enable change_tracking with (track_columns_updated = on);
            select concat(
                (select concat(is_auto_cleanup_on, ':', retention_period, ':', retention_period_units, ':', retention_period_units_desc) from sys.change_tracking_databases where database_id = db_id()), ' ',
                (select concat(count(*), ':', max(cast(is_track_columns_updated_on as int)), ':', max(object_id - object_id('t') + 1), ':', max(min_valid_version), ':', max(begin_version)) from sys.change_tracking_tables), ' ',
                cast(databasepropertyex(db_name(), 'IsChangeTrackingEnabled') as int), ' ',
                isnull(cast(change_tracking_min_valid_version(object_id('sys.objects')) as varchar), 'NULL'))
            """));

    [TestMethod]
    public void Settings_ChangeInPlaceAndTheVersionOutlivesAnOffOnCycle()
        => AreEqual("1:30:MINUTES 2:2", new Simulation().ExecuteScalar("""
            alter database current set change_tracking = on (change_retention = 5 hours, auto_cleanup = off);
            alter database current set change_tracking (change_retention = 30 minutes);
            alter database current set change_tracking (auto_cleanup = on);
            create table t (id int primary key);
            alter table t enable change_tracking;
            insert t values (1); insert t values (2);
            declare @settings varchar(50) = (select concat(is_auto_cleanup_on, ':', retention_period, ':', retention_period_units_desc) from sys.change_tracking_databases where database_id = db_id());
            alter table t disable change_tracking;
            alter database current set change_tracking = off;
            alter database current set change_tracking = on;
            alter table t enable change_tracking;
            select concat(@settings, ' ', change_tracking_current_version(), ':', change_tracking_min_valid_version(object_id('t')))
            """));

    [TestMethod]
    public void Truncate_RestartsTheHistoryAtTheCurrentVersion()
        => AreEqual("1:1 3I2/2", new Simulation().ExecuteScalar($"""
            {Tracked}
            insert t values (1, 1, 1, 1), (2, 2, 2, 2);
            begin tran; truncate table t; rollback;
            truncate table t;
            declare @state varchar(10) = concat(change_tracking_min_valid_version(object_id('t')), ':', change_tracking_current_version());
            insert t values (3, 3, 3, 3);
            select concat(@state, ' ', ({Changes("0")}))
            """));

    [TestMethod]
    public void EnableAndDisable_RollBackWithTheirTransaction()
        => AreEqual("0 1I1/1", new Simulation().ExecuteScalar($"""
            alter database current set change_tracking = on;
            create table t (id int primary key, a int);
            create table u (id int primary key);
            begin tran; alter table u enable change_tracking; rollback;
            alter table t enable change_tracking;
            insert t values (1, 1);
            begin tran; alter table t disable change_tracking; rollback;
            select concat((select count(*) from sys.change_tracking_tables where object_id = object_id('u')), ' ', ({Changes("0")}))
            """));

    [TestMethod]
    public void TriggersCascadesAndMerge_ShareTheFiringStatementsVersion()
        => AreEqual("10D2/ 20U3/ | 1I4/4 2I5/5 3I5/5 | 5", new Simulation().ExecuteScalar("""
            alter database current set change_tracking = on;
            create table p (id int primary key);
            create table c (id int primary key, pid int references p on delete cascade on update cascade);
            create table w (id int primary key, a int);
            alter table c enable change_tracking with (track_columns_updated = on);
            alter table w enable change_tracking;
            insert p values (1), (2); insert c values (10, 1), (20, 2);
            delete p where id = 1;
            update p set id = 3 where id = 2;
            insert w values (1, 1);
            merge w using (values (2, 2), (3, 3)) s(id, a) on w.id = s.id when not matched then insert values (s.id, s.a);
            select concat(
                (select string_agg(concat(id, SYS_CHANGE_OPERATION, SYS_CHANGE_VERSION, '/', SYS_CHANGE_CREATION_VERSION, SYS_CHANGE_COLUMNS), ' ') within group (order by id) from changetable(changes c, 1) x), ' | ',
                (select string_agg(concat(id, SYS_CHANGE_OPERATION, SYS_CHANGE_VERSION, '/', SYS_CHANGE_CREATION_VERSION), ' ') within group (order by id) from changetable(changes w, 0) y), ' | ',
                change_tracking_current_version())
            """));

    [TestMethod]
    public void UntrackedDatabase_ReportsNoVersionAndRefusesChangeTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key)");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select change_tracking_current_version()"));
        AreEqual(0, sim.ExecuteNonQuery("with change_tracking_context (0x01) delete t"));
        _ = sim.AssertSqlError("select * from changetable(changes t, 0) c", 22105);
    }

    // Differential sweep against SQL Server 2025, probed 2026-10-04.

    [TestMethod]
    [DataRow("alter database current set change_tracking = on (change_retention = 3 days, change_retention = 3 days)", 5091)]
    [DataRow("alter database current set change_tracking = on (change_retention = 1.5 days)", 102)]
    public void DatabaseOptions_RefuseWhatRealRefuses(string sql, int error) =>
        new Simulation().AssertSqlError(sql, error);

    [TestMethod]
    public void DatabaseOptions_RepeatedInTheOnForm_IsState2()
    {
        var error = new Simulation().AssertSqlError("alter database current set change_tracking = on (auto_cleanup = on, auto_cleanup = off)", 5091);
        AreEqual(2, error.State);
    }

    [TestMethod]
    public void Enable_RefusesAHistoryTable()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            alter database current set change_tracking = on;
            create table v (id int primary key, s datetime2 generated always as row start not null, e datetime2 generated always as row end not null, period for system_time (s, e))
                with (system_versioning = on (history_table = dbo.v_h))
            """);
        simulation.AssertSqlError("alter table v_h enable change_tracking", 13563, "Enabling Change Tracking for a temporal history table 'simulated.dbo.v_h' is not allowed.");
    }

    [TestMethod]
    public void UntrackedTable_InABranchNeverTaken_OfTheBatchCreatingIt_IsNotRefused()
    {
        var simulation = new Simulation();
        AreEqual("after", simulation.ExecuteScalar("""
            alter database current set change_tracking = on;
            create table u (id int primary key);
            if 1 = 0 select * from changetable(changes u, 0) c;
            select 'after'
            """));
    }

    [TestMethod]
    public void UntrackedTable_IsNamedAsWritten()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("alter database current set change_tracking = on; create table u (id int primary key)");
        simulation.AssertSqlError("select * from changetable(changes dbo.u, 0) c", 22105, "Change tracking is not enabled on table 'dbo.u'.");
    }

    [TestMethod]
    public void Context_FollowedByExec_IsASyntaxError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("alter database current set change_tracking = on");
        _ = simulation.AssertSqlError("with change_tracking_context (0x01) exec sp_who", 156);
    }

    [TestMethod]
    public void ChangeTable_TakesViewChangeTracking()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            {Tracked}
            create user u without login;
            grant select on t to u
            """);
        simulation.AssertSqlError(
            "execute as user = 'u'; select * from changetable(changes t, 0) c",
            229,
            "The VIEW CHANGE TRACKING permission was denied on the object 't', database 'simulated', schema 'dbo'.");
        _ = simulation.ExecuteNonQuery("grant view change tracking on t to u");
        AreEqual(0, simulation.ExecuteScalar<int>("execute as user = 'u'; select count(*) from changetable(changes t, 0) c"));
    }

    [TestMethod]
    public void KeyRewrittenInAnotherSpelling_KeepsTheFirstSpelling()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            alter database current set change_tracking = on;
            create table k (k varchar(10) collate Latin1_General_CI_AS primary key, v int);
            alter table k enable change_tracking;
            insert k values ('b', 1);
            update k set k = 'B' where k = 'b'
            """);
        AreEqual("b", simulation.ExecuteScalar("select k from changetable(changes k, 0) c"));
    }
}
