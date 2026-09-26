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
}
