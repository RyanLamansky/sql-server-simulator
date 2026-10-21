using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// DDL on permanent objects inside a transaction rolls back with it, as on
/// SQL Server 2025 (probed 2026-09-25): created objects vanish, dropped ones
/// return with their rows, and a table's column, constraint, index and
/// rename changes revert.
/// </summary>
[TestClass]
public sealed class TransactionalDdlTests
{
    [TestMethod]
    public void RolledBackCreates_LeaveNoObjects()
        => AreEqual("|||||", new Simulation().ExecuteScalar("""
            create table keep (a int primary key);
            begin tran;
            create table fresh (a int references keep (a));
            exec ('create view v as select a from keep');
            exec ('create procedure p as select 1');
            exec ('create function f() returns int as begin return 1 end');
            create sequence s;
            create synonym syn for keep;
            rollback;
            select concat(object_id('fresh'), '|', object_id('v'), '|', object_id('p'), '|', object_id('f'), '|', object_id('s'), '|', object_id('syn'))
            """));

    [TestMethod]
    public void RolledBackDrop_RestoresTheTableAndItsRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table keep (a int primary key); insert keep values (1), (2)",
            "create trigger tr on keep after insert as select 1");
        AreEqual(2, sim.ExecuteScalar("""
            begin tran; drop table keep; rollback;
            select count(*) from keep
            """));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.triggers where name = 'tr'"));
    }

    [TestMethod]
    public void RolledBackCreate_UnwiresItsForeignKeys()
    {
        // The rolled-back child's foreign key no longer protects the parent.
        var sim = new Simulation();
        sim.ExecuteBatches("create table keep (a int primary key)");
        _ = sim.ExecuteNonQuery("begin tran; create table child (a int references keep (a)); rollback");
        _ = sim.ExecuteNonQuery("drop table keep");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('keep')"));
    }

    [TestMethod]
    public void RolledBackAlterTable_RestoresColumnsConstraintsIndexesAndRows()
        => AreEqual("-|10|4|0|0|0|1x5,2y6", new Simulation().ExecuteScalar("""
            create table keep (a int primary key, n varchar(10), z int);
            insert keep values (1, 'x', 5), (2, 'y', 6);
            create table ref (a int);
            begin tran;
            alter table keep add b int null default 7;
            alter table keep add constraint ck check (a > 0);
            alter table ref add constraint fk foreign key (a) references keep (a);
            create index ix on keep (n);
            exec sp_rename 'keep.n', 'nn', 'COLUMN';
            alter table keep drop column z;
            update keep set a = a + 10;
            rollback;
            select concat(isnull(str(col_length('keep', 'b')), '-'), '|', col_length('keep', 'n'), '|', col_length('keep', 'z'), '|',
                (select count(*) from sys.check_constraints where name = 'ck'), '|',
                (select count(*) from sys.foreign_keys where name = 'fk'), '|',
                (select count(*) from sys.indexes where name = 'ix'), '|',
                (select string_agg(concat(a, n, z), ',') within group (order by a) from keep))
            """));

    [TestMethod]
    public void RolledBackRename_RestoresTheName()
        => AreEqual("1|", new Simulation().ExecuteScalar("""
            create table keep (a int);
            begin tran; exec sp_rename 'keep', 'kept'; rollback;
            select concat(case when object_id('keep') is null then 0 else 1 end, '|', object_id('kept'))
            """));

    [TestMethod]
    public void SavepointRollback_UndoesOnlyTheLaterDdl()
        => AreEqual("1|", new Simulation().ExecuteScalar("""
            begin tran;
            create table first (a int);
            save tran sp;
            create table second (a int);
            rollback tran sp;
            commit;
            select concat(case when object_id('first') is null then 0 else 1 end, '|', object_id('second'))
            """));

    [TestMethod]
    public void RolledBackSchemasTypesAndTransfers_Revert()
        => AreEqual("|||dbo|0", new Simulation().ExecuteScalar("""
            create table t (a int);
            begin tran;
            exec ('create schema s');
            create type ty from int;
            create type tt as table (a int);
            alter schema s transfer dbo.t;
            exec sp_addextendedproperty 'MS_Description', 'x', 'SCHEMA', 'dbo';
            rollback;
            select concat(schema_id('s'), '|', type_id('ty'), '|', type_id('tt'), '|', object_schema_name(object_id('dbo.t')), '|',
                (select count(*) from fn_listextendedproperty(null, 'SCHEMA', 'dbo', null, null, null, null)))
            """));

    [TestMethod]
    public void RolledBackSecurityStatements_Revert()
        => AreEqual("||0|", new Simulation().ExecuteScalar("""
            create table t (a int);
            begin tran;
            create role r;
            create user u without login;
            alter role r add member u;
            grant select on t to r;
            deny insert on t to u;
            create login l with password = 'Xx12345678!x';
            rollback;
            select concat(user_id('r'), '|', user_id('u'), '|',
                (select count(*) from sys.database_permissions where major_id = object_id('t')), '|', suser_id('l'))
            """));

    /// <summary>
    /// A column change that a rollback or a failure undoes leaves the table's
    /// rows readable and writable under the layout they were written in, off-row
    /// values included; so does one committed over rows the transaction had
    /// already updated, deleted or inserted.
    /// </summary>
    [TestMethod]
    [DataRow("begin tran; alter table t alter column a bigint; rollback")]
    [DataRow("begin tran; alter table t add b int; rollback")]
    [DataRow("begin tran; alter table t drop column a; rollback")]
    [DataRow("begin tran; alter table t add b int; insert t (id, a, v) values (9, 9, @big); rollback")]
    [DataRow("begin try alter table t add b int not null default 0 constraint ck check (b > 0) end try begin catch end catch")]
    [DataRow("begin try alter table t alter column a tinyint end try begin catch end catch")]
    [DataRow("begin tran; update t set v = @big + 'y' where id = 1; delete t where id = 2; alter table t alter column a bigint; commit")]
    [DataRow("begin tran; update t set v = @big + 'y' where id = 1; delete t where id = 2; alter table t add b int; rollback")]
    [DataRow("begin tran; delete t; alter table t add b int; insert t (id, a, v) values (1, 1, @big), (2, 2, @big), (3, 300, @big); commit")]
    public void UndoneOrCommittedColumnChange_KeepsTheRowsLayout(string change)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, a int, v varchar(max)); declare @big varchar(max) = replicate(cast('x' as varchar(max)), 9000); insert t values (1, 1, @big), (2, 2, @big), (3, 300, @big)");
        _ = sim.ExecuteNonQuery("declare @big varchar(max) = replicate(cast('x' as varchar(max)), 9000); " + change);
        AreEqual(2, sim.ExecuteScalar("""
            update t set v = 'short' where id = 1;
            update t set a = 4 where id = 3;
            delete t where id = 2;
            insert t (id, a, v) values (5, 5, replicate(cast('z' as varchar(max)), 9000));
            delete t where id = 5;
            select count(*) from t where len(v) > 0
            """));
    }

    /// <summary>
    /// A rolled-back rebuild or <c>ALTER INDEX … SET</c> leaves the storage
    /// options as they stood (probed 2026-10-21 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create table t (a int)", "alter table t rebuild with (data_compression = page)", "1|NONE")]
    [DataRow("create table t (a int primary key)", "alter index all on t set (allow_row_locks = off); alter table t rebuild with (data_compression = row)", "1|NONE")]
    [DataRow("create table t (a int, index ix clustered (a))", "alter index ix on t set (allow_page_locks = off); alter index ix on t rebuild with (data_compression = page)", "1|NONE")]
    public void RolledBackStorageOptionChange_LeavesTheOptions(string create, string change, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            {create};
            begin tran; {change}; rollback;
            select concat(min(cast(i.allow_row_locks & i.allow_page_locks as int)), '|', min(p.data_compression_desc))
            from sys.indexes i join sys.partitions p on p.object_id = i.object_id and p.index_id = i.index_id
            where i.object_id = object_id('t')
            """));

    /// <summary>
    /// A rolled-back <c>TRUNCATE</c> leaves a pending <c>DBCC CHECKIDENT</c>
    /// reseed in place (probed 2026-10-21 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("", 100)]
    [DataRow("insert t (v) values (1);", 101)]
    public void RolledBackTruncate_KeepsAPendingReseed(string before, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table t (id int identity, v int);
            {before}
            dbcc checkident('t', reseed, 100) with no_infomsgs;
            begin tran; truncate table t; rollback;
            insert t (v) values (2);
            select id from t where v = 2
            """));

    /// <summary>A rolled-back edge constraint change leaves the table's edge constraints as they stood.</summary>
    [TestMethod]
    [DataRow("alter table e add constraint ec2 connection (n to n)", "ec")]
    [DataRow("alter table e drop constraint ec", "ec")]
    public void RolledBackEdgeConstraintChange_LeavesTheConstraints(string change, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table n (id int) as node;
            create table e (constraint ec connection (n to n)) as edge;
            begin tran; {change}; rollback;
            select string_agg(name, ',') from sys.edge_constraints where parent_object_id = object_id('e')
            """));

    /// <summary>A rolled-back period change leaves the table's period, and the columns it names, as they stood.</summary>
    [TestMethod]
    [DataRow("create table t (id int primary key, vf datetime2 not null default '2000-01-01', vt datetime2 not null default '9999-12-31')",
        "alter table t add period for system_time (vf, vt)", "")]
    [DataRow("create table t (id int primary key, vf datetime2 generated always as row start not null, vt datetime2 generated always as row end not null, period for system_time (vf, vt))",
        "alter table t drop period for system_time", "vf-vt")]
    [DataRow("create table t (x int, id int primary key, vf datetime2 generated always as row start not null, vt datetime2 generated always as row end not null, period for system_time (vf, vt))",
        "alter table t drop column x", "vf-vt")]
    public void RolledBackPeriodChange_LeavesThePeriod(string create, string change, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(create);
        _ = sim.ExecuteNonQuery($"begin tran; {change}; rollback");
        AreEqual(expected, sim.ExecuteScalar("""
            select isnull((select concat(col_name(object_id, start_column_id), '-', col_name(object_id, end_column_id)) from sys.periods where object_id = object_id('t')), '')
            """));
        _ = sim.ExecuteNonQuery("insert t (id) values (1); update t set id = 2");
        AreEqual(expected == "" ? 0 : 1, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t') and generated_always_type = 1"));
    }

    /// <summary>
    /// A transaction that writes rows and then truncates their table commits
    /// with only what it wrote after the truncation, off-row values included.
    /// </summary>
    [TestMethod]
    public void WritesThenTruncate_CommitKeepsOnlyTheRowsAfter()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, v varchar(max)); insert t select value, replicate(cast('x' as varchar(max)), 9000) from generate_series(1, 3)");
        _ = sim.ExecuteNonQuery("""
            declare @big varchar(max) = replicate(cast('y' as varchar(max)), 9000);
            begin tran;
            update t set v = @big where id = 1;
            delete t where id = 2;
            truncate table t;
            insert t values (5, @big), (6, @big);
            commit
            """);
        AreEqual("5:9000,6:9000", sim.ExecuteScalar("select string_agg(concat(id, ':', len(v)), ',') within group (order by id) from t"));
        _ = sim.ExecuteNonQuery("delete t where id = 5; update t set v = 'z' where id = 6");
        AreEqual("6:1", sim.ExecuteScalar("select string_agg(concat(id, ':', len(v)), ',') from t"));
    }

    /// <summary>
    /// A rolled-back <c>TRUNCATE</c> whose transaction wrote a row again where
    /// a truncated one stood gives a snapshot reader back the row's history.
    /// </summary>
    [TestMethod]
    public void RolledBackTruncateAndInsert_SnapshotReadsTheRowsHistory()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database current set allow_snapshot_isolation on; create table t (id int primary key, v varchar(10))");
        _ = sim.ExecuteNonQuery("insert t values (1, 'old')");
        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran; select count(*) from t").ExecuteScalar();
        _ = writer.CreateCommand("update t set v = 'new' where id = 1").ExecuteNonQuery();
        _ = writer.CreateCommand("begin tran; truncate table t; insert t values (1, 'x'); rollback").ExecuteNonQuery();
        AreEqual("old", reader.CreateCommand("select v from t where id = 1").ExecuteScalar());
        AreEqual("new", writer.CreateCommand("select v from t where id = 1").ExecuteScalar());
    }
}
