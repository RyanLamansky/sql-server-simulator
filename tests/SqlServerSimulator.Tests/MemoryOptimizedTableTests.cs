using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// In-Memory OLTP: the <c>MEMORY_OPTIMIZED_DATA</c> filegroup and its
/// containers, memory-optimized tables and table types with their hash and
/// range indexes, the DDL and hints they refuse, the isolation rules their
/// access follows and the write conflicts that replace waiting. Every
/// expectation was probed 2026-10-02 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class MemoryOptimizedTableTests
{
    private const string Container = """
        alter database current add filegroup fx contains memory_optimized_data;
        alter database current add file (name = 'c1', filename = '/data/c1') to filegroup fx;
        """;

    private static Simulation WithContainer()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Container);
        return sim;
    }

    [TestMethod]
    public void CreateTable_WithoutFilegroup_IsMsg41337State100()
        => AreEqual(100, new Simulation().AssertSqlError(
            "create table t (id int not null primary key nonclustered) with (memory_optimized = on)", 41337).State);

    [TestMethod]
    public void CreateTable_FilegroupWithoutContainer_IsMsg41337State1()
        => AreEqual(1, new Simulation().AssertSqlError("""
            alter database current add filegroup fx contains memory_optimized_data;
            create table t (id int not null primary key nonclustered) with (memory_optimized = on)
            """, 41337).State);

    [TestMethod]
    public void Filegroup_ReportsMemoryOptimizedType()
    {
        using var reader = WithContainer().ExecuteReader("select type, type_desc, is_default from sys.filegroups where name = 'fx'");
        IsTrue(reader.Read());
        AreEqual("FX", reader.GetString(0));
        AreEqual("MEMORY_OPTIMIZED_DATA_FILEGROUP", reader.GetString(1));
        IsTrue(reader.GetBoolean(2));
    }

    [TestMethod]
    public void Container_IsFilestreamFileFrom65537()
    {
        using var reader = WithContainer().ExecuteReader("select file_id, type, type_desc, size, max_size, growth from sys.database_files where name = 'c1'");
        IsTrue(reader.Read());
        AreEqual(65537, reader.GetInt32(0));
        AreEqual((byte)2, reader.GetByte(1));
        AreEqual("FILESTREAM", reader.GetString(2));
        AreEqual(0, reader.GetInt32(3));
        AreEqual(-1, reader.GetInt32(4));
        AreEqual(0, reader.GetInt32(5));
    }

    [TestMethod]
    public void ContainerId_IsPastFileId()
        => AreEqual(65537, WithContainer().ExecuteScalar("select isnull(file_id('c1'), 0) + file_idex('c1')"));

    [TestMethod]
    public void SecondMemoryOptimizedFilegroup_IsMsg10797()
        => _ = WithContainer().AssertSqlError("alter database current add filegroup fx2 contains memory_optimized_data", 10797);

    [TestMethod]
    public void ContainerSize_IsMsg5509()
        => _ = new Simulation().AssertSqlError("""
            alter database current add filegroup fx contains memory_optimized_data;
            alter database current add file (name = 'c1', filename = '/data/c1', size = 10MB) to filegroup fx
            """, 5509);

    [TestMethod]
    public void ContainerMaxSize_IsMsg41873()
        => _ = new Simulation().AssertSqlError("""
            alter database current add filegroup fx contains memory_optimized_data;
            alter database current add file (name = 'c1', filename = '/data/c1', maxsize = 100MB) to filegroup fx
            """, 41873);

    [TestMethod]
    public void FilegroupReadOnly_IsMsg41361()
        => _ = WithContainer().AssertSqlError("alter database current modify filegroup fx read_only", 41361);

    [TestMethod]
    public void RemoveFilegroup_IsMsg5042()
        => _ = WithContainer().AssertSqlError("alter database current remove filegroup fx", 5042);

    [TestMethod]
    public void CreateDatabase_MemoryOptimizedGroup_RegistersContainers()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create database m on primary (name = m, filename = '/data/m.mdf'),
                filegroup mo contains memory_optimized_data (name = m_c1, filename = '/data/m_c1'), (name = m_c2, filename = '/data/m_c2');
            select count(*) from m.sys.database_files where type = 2 and file_id > 65536
            """));

    [TestMethod]
    public void ElevateToSnapshot_IsReported()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            alter database current set memory_optimized_elevate_to_snapshot on;
            select cast(is_memory_optimized_elevate_to_snapshot_on as int) * cast(databasepropertyex(db_name(), 'IsMemoryOptimizedElevateToSnapshotEnabled') as int)
            from sys.databases where name = db_name()
            """));

    [TestMethod]
    public void CreateTable_ReportsMemoryOptimizedAndDurability()
    {
        using var reader = WithContainer().ExecuteReader("""
            create table t (id int not null primary key nonclustered, v int not null index ix nonclustered) with (memory_optimized = on, durability = schema_only);
            select is_memory_optimized, durability, durability_desc, objectproperty(object_id, 'TableIsMemoryOptimized') from sys.tables where name = 't'
            """);
        IsTrue(reader.Read());
        IsTrue(reader.GetBoolean(0));
        AreEqual((byte)1, reader.GetByte(1));
        AreEqual("SCHEMA_ONLY", reader.GetString(2));
        AreEqual(1, reader.GetInt32(3));
    }

    [TestMethod]
    public void DurableTableWithoutPrimaryKey_IsMsg41321()
        => _ = WithContainer().AssertSqlError("create table t (id int not null) with (memory_optimized = on)", 41321);

    [TestMethod]
    public void SchemaOnlyTableWithoutIndex_IsMsg41327()
        => _ = WithContainer().AssertSqlError("create table t (id int not null) with (memory_optimized = on, durability = schema_only)", 41327);

    [TestMethod]
    public void DefaultClusteredPrimaryKey_IsMsg12317()
        => _ = WithContainer().AssertSqlError("create table t (id int not null primary key) with (memory_optimized = on)", 12317);

    [TestMethod]
    public void DurabilityWithoutMemoryOptimized_IsMsg10779()
        => new Simulation().AssertSqlError(
            "create table t (id int not null primary key nonclustered) with (durability = schema_only)",
            10779,
            "The durability option 'schema_only' is supported only with memory optimized tables.");

    [TestMethod]
    public void HashWithoutBucketCount_IsMsg10789()
        => WithContainer().AssertSqlError(
            "create table t (id int not null primary key nonclustered hash) with (memory_optimized = on)",
            10789,
            "The option 'bucket_count' must be specified for index '' on table 't'. It is required for hash indexes.");

    [TestMethod]
    public void BucketCountOutOfRange_IsMsg41303()
        => _ = WithContainer().AssertSqlError("create table t (id int not null primary key nonclustered hash with (bucket_count = 0)) with (memory_optimized = on)", 41303);

    [TestMethod]
    public void HashIndexOnDiskTable_IsMsg10791()
        => _ = new Simulation().AssertSqlError("create table t (id int not null primary key nonclustered hash (id) with (bucket_count = 16))", 10791);

    [TestMethod]
    public void HashIndexes_RoundBucketCountsAndNumberInReverse()
    {
        using var reader = WithContainer().ExecuteReader("""
            create table t (id int not null, v int not null,
                constraint pk primary key nonclustered hash (id) with (bucket_count = 100),
                index ixh hash (v) with (bucket_count = 64),
                index ixr nonclustered (v desc)) with (memory_optimized = on);
            select i.name, i.index_id, i.type_desc, i.data_space_id, h.bucket_count
            from sys.indexes i left join sys.hash_indexes h on h.object_id = i.object_id and h.index_id = i.index_id
            where i.object_id = object_id('t') order by i.index_id
            """);
        string Row() => $"{(reader.IsDBNull(0) ? "-" : reader.GetString(0))}|{reader.GetInt32(1)}|{reader.GetString(2)}|{reader.GetInt32(3)}|{(reader.IsDBNull(4) ? "-" : reader.GetInt32(4))}";
        IsTrue(reader.Read());
        AreEqual("-|0|HEAP|0|-", Row());
        IsTrue(reader.Read());
        AreEqual("ixr|2|NONCLUSTERED|0|-", Row());
        IsTrue(reader.Read());
        AreEqual("ixh|3|NONCLUSTERED HASH|0|64", Row());
        IsTrue(reader.Read());
        AreEqual("pk|4|NONCLUSTERED HASH|0|128", Row());
    }

    [TestMethod]
    public void RefusedColumnType_IsMsg10794()
        => WithContainer().AssertSqlError(
            "create table t (id int not null primary key nonclustered, d datetimeoffset) with (memory_optimized = on)",
            10794,
            "The type 'datetimeoffset(7)' is not supported with memory optimized tables.");

    [TestMethod]
    public void MaxTypes_AreTaken()
        => AreEqual(1, WithContainer().ExecuteScalar("""
            create table t (id int not null primary key nonclustered, a nvarchar(max), b varbinary(max)) with (memory_optimized = on);
            insert t values (1, replicate(cast(N'x' as nvarchar(max)), 5000), 0x01);
            select count(*) from t where len(a) = 5000
            """));

    [TestMethod]
    public void IdentitySeed_IsMsg12339()
        => _ = WithContainer().AssertSqlError("create table t (id int identity(5, 2) not null primary key nonclustered) with (memory_optimized = on)", 12339);

    [TestMethod]
    public void TemporaryTable_IsMsg12322()
        => _ = WithContainer().AssertSqlError("create table #t (id int not null primary key nonclustered) with (memory_optimized = on)", 12322);

    [TestMethod]
    public void ForeignKeyToDiskTable_IsMsg10778()
        => _ = WithContainer().AssertSqlError("""
            create table d (id int not null primary key);
            create table t (id int not null primary key nonclustered, p int references d (id)) with (memory_optimized = on)
            """, 10778);

    [TestMethod]
    public void ForeignKeyCascade_IsMsg10794()
        => WithContainer().AssertSqlError("""
            create table p (id int not null primary key nonclustered) with (memory_optimized = on);
            create table t (id int not null primary key nonclustered, p int references p (id) on delete cascade) with (memory_optimized = on)
            """, 10794, "The option 'CASCADE' is not supported with memory optimized tables.");

    [TestMethod]
    public void ForeignKeyToUniqueKey_IsMsg10780()
        => _ = WithContainer().AssertSqlError("""
            create table p (id int not null primary key nonclustered, v int not null unique nonclustered) with (memory_optimized = on);
            create table t (id int not null primary key nonclustered, p int references p (v)) with (memory_optimized = on)
            """, 10780);

    [TestMethod]
    public void ForeignKeyBetweenMemoryOptimizedTables_IsEnforced()
        => _ = WithContainer().AssertSqlError("""
            create table p (id int not null primary key nonclustered) with (memory_optimized = on);
            create table t (id int not null primary key nonclustered, p int not null references p (id)) with (memory_optimized = on);
            insert t values (1, 9)
            """, 547);

    [TestMethod]
    public void CreateIndex_IsMsg10794()
        => WithContainer().AssertSqlError("""
            create table t (id int not null primary key nonclustered, v int) with (memory_optimized = on);
            create index ix on t (v)
            """, 10794, "The operation 'CREATE INDEX' is not supported with memory optimized tables.");

    [TestMethod]
    public void Truncate_IsMsg10794()
        => WithContainer().AssertSqlError("""
            create table t (id int not null primary key nonclustered) with (memory_optimized = on);
            truncate table t
            """, 10794, "The statement 'TRUNCATE TABLE' is not supported with memory optimized tables.");

    [TestMethod]
    public void AlterTable_AddAlterAndDropIndex()
        => AreEqual("ix|1024", WithContainer().ExecuteScalar("""
            create table t (id int not null primary key nonclustered, v int not null) with (memory_optimized = on);
            alter table t add index ix hash (v) with (bucket_count = 8);
            alter table t alter index ix rebuild with (bucket_count = 1000);
            alter table t add index gone nonclustered (v);
            alter table t drop index gone;
            select string_agg(name + '|' + cast(bucket_count as varchar), ',') from sys.hash_indexes
            """));

    [TestMethod]
    public void AlterTableAddIndex_OnDiskTable_IsMsg10785()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, v int);
            alter table t add index ix nonclustered (v)
            """, 10785);

    [TestMethod]
    public void AlterColumn_UnderAnIndex_IsRefusedEvenGrowing()
        => _ = WithContainer().AssertSqlError("""
            create table t (id int not null primary key nonclustered, n nvarchar(20) index ix nonclustered) with (memory_optimized = on);
            alter table t alter column n nvarchar(40) null
            """, 5074);

    [TestMethod]
    public void MergeTarget_IsMsg10794()
        => WithContainer().AssertSqlError("""
            create table t (id int not null primary key nonclustered) with (memory_optimized = on);
            merge t using (values (1)) s (id) on t.id = s.id when not matched then insert values (s.id);
            """, 10794, "The operation 'MERGE' is not supported with memory optimized tables.");

    [TestMethod]
    public void UpdatingPrimaryKey_IsMsg12302()
        => _ = WithContainer().AssertSqlError("""
            create table t (id int not null primary key nonclustered, v int) with (memory_optimized = on);
            update t set id = id where 1 = 0
            """, 12302);

    [TestMethod]
    public void InterpretedTrigger_IsMsg10777()
    {
        var sim = WithContainer();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key nonclustered) with (memory_optimized = on)");
        _ = sim.AssertSqlError("create trigger tr on t after insert as select 1", 10777);
    }

    [TestMethod]
    public void RefusedHint_IsMsg10794()
        => WithContainer().AssertSqlError("""
            create table t (id int not null primary key nonclustered) with (memory_optimized = on);
            select * from t with (updlock)
            """, 10794, "The table option 'updlock' is not supported with memory optimized tables.");

    [TestMethod]
    public void SnapshotHintOnDiskTable_IsMsg367()
        => _ = new Simulation().AssertSqlError("""
            create table d (id int);
            select * from d with (snapshot)
            """, 367);

    [TestMethod]
    public void ReadCommittedReadInTransaction_IsMsg41368AndRollsBack()
    {
        var sim = WithContainer();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key nonclustered) with (memory_optimized = on);
            create table d (id int)
            """);
        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; insert d values (1); select * from t; select 1").ExecuteNonQuery());
        AreEqual(41368, ex.Number);
        AreEqual(0, connection.CreateCommand("select @@trancount + (select count(*) from d)").ExecuteScalar());
    }

    [TestMethod]
    public void InsertInReadCommittedTransaction_ReadsNothingAndPasses()
        => AreEqual(1, WithContainer().ExecuteScalar("""
            create table t (id int not null primary key nonclustered) with (memory_optimized = on);
            begin tran; insert t values (1); commit;
            select count(*) from t
            """));

    [TestMethod]
    public void SnapshotHintOrElevation_LiftsMsg41368()
        => AreEqual(2, WithContainer().ExecuteScalar("""
            create table t (id int not null primary key nonclustered) with (memory_optimized = on);
            insert t values (1);
            begin tran; insert t select id + 1 from t with (snapshot); commit;
            alter database current set memory_optimized_elevate_to_snapshot on;
            begin tran; select count(*) from t; commit
            """));

    [TestMethod]
    public void SnapshotSession_IsMsg41332()
        => _ = WithContainer().AssertSqlError("""
            create table t (id int not null primary key nonclustered) with (memory_optimized = on);
            set transaction isolation level snapshot;
            select * from t
            """, 41332);

    [TestMethod]
    public void RepeatableReadSession_NeedsSnapshotHint()
        => _ = WithContainer().AssertSqlError("""
            create table t (id int not null primary key nonclustered) with (memory_optimized = on);
            set transaction isolation level repeatable read;
            select count(*) from t with (snapshot);
            select * from t
            """, 41333);

    [TestMethod]
    public void ReadUncommittedSession_IsMsg10794()
        => _ = WithContainer().AssertSqlError("""
            create table t (id int not null primary key nonclustered) with (memory_optimized = on);
            set transaction isolation level read uncommitted;
            select * from t
            """, 10794);

    [TestMethod]
    public void Reader_DoesNotWaitForUncommittedWriter()
    {
        var sim = WithContainer();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key nonclustered, v int not null) with (memory_optimized = on);
            insert t values (1, 1)
            """);
        using var writer = sim.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; update t with (snapshot) set v = 10 where id = 1").ExecuteNonQuery();
        using var reader = sim.CreateOpenConnection();
        AreEqual(1, reader.CreateCommand("set lock_timeout 0; select v from t where id = 1").ExecuteScalar());
        _ = writer.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(10, reader.CreateCommand("select v from t where id = 1").ExecuteScalar());
    }

    [TestMethod]
    public void ConflictingWriter_IsMsg41302AtOnce()
    {
        var sim = WithContainer();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key nonclustered, v int not null) with (memory_optimized = on);
            insert t values (1, 1), (2, 2)
            """);
        using var first = sim.CreateOpenConnection();
        _ = first.CreateCommand("begin tran; update t with (snapshot) set v = 10 where id = 1").ExecuteNonQuery();
        using var second = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => second.CreateCommand("update t set v = 20 where id = 1").ExecuteNonQuery());
        AreEqual(41302, ex.Number);
        AreEqual(110, ex.State);
        AreEqual(111, Throws<SimulatedSqlException>(() => second.CreateCommand("delete t where id = 1").ExecuteNonQuery()).State);
        AreEqual(1, second.CreateCommand("update t set v = 20 where id = 2").ExecuteNonQuery());
        _ = first.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void ConflictInTransaction_DoomsItAndRollsBackAtBatchEnd()
    {
        var sim = WithContainer();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key nonclustered, v int not null) with (memory_optimized = on);
            insert t values (1, 1)
            """);
        using var first = sim.CreateOpenConnection();
        _ = first.CreateCommand("begin tran; update t with (snapshot) set v = 10 where id = 1").ExecuteNonQuery();
        using var second = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => second.CreateCommand("begin tran; update t with (snapshot) set v = 20 where id = 1; select 1").ExecuteNonQuery());
        AreEqual(41302, ex.Number);
        AreEqual(3998, ex.Errors[^1].Number);
        AreEqual(0, second.CreateCommand("select @@trancount").ExecuteScalar());
        _ = first.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void ConflictCaught_DoomsTheTransaction()
    {
        var sim = WithContainer();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key nonclustered, v int not null) with (memory_optimized = on);
            insert t values (1, 1)
            """);
        using var first = sim.CreateOpenConnection();
        _ = first.CreateCommand("begin tran; update t with (snapshot) set v = 10 where id = 1").ExecuteNonQuery();
        using var second = sim.CreateOpenConnection();
        AreEqual("41302|-1", second.CreateCommand("""
            begin tran;
            begin try update t with (snapshot) set v = 20 where id = 1 end try
            begin catch select cast(error_number() as varchar) + '|' + cast(xact_state() as varchar) end catch;
            rollback
            """).ExecuteScalar());
        _ = first.CreateCommand("commit").ExecuteNonQuery();
    }

    [TestMethod]
    public void TableType_IsMemoryOptimizedAndItsVariableIgnoresRollback()
        => AreEqual(2, new Simulation().ExecuteBatchesScalar(
            "create type dbo.tt as table (id int not null, index ix nonclustered (id)) with (memory_optimized = on)",
            """
            declare @t dbo.tt;
            insert @t values (1);
            begin tran; insert @t values (2); rollback;
            select count(*) + (select count(*) from sys.table_types where is_memory_optimized = 1) - 1 from @t
            """));

    [TestMethod]
    public void TableTypeWithoutIndex_IsMsg41327()
        => _ = new Simulation().AssertSqlError("create type dbo.tt as table (id int not null) with (memory_optimized = on)", 41327);

    [TestMethod]
    public void TableTypeDurability_IsMsg10788()
        => _ = new Simulation().AssertSqlError("create type dbo.tt as table (id int not null primary key nonclustered) with (memory_optimized = on, durability = schema_only)", 10788);

    [TestMethod]
    public void DuplicateKey_NamesTheTableAlone()
        => WithContainer().AssertSqlError("""
            create table t (id int not null constraint pk primary key nonclustered) with (memory_optimized = on);
            insert t values (1), (1)
            """, 2627, "Violation of PRIMARY KEY constraint 'pk'. Cannot insert duplicate key in object 't'. The duplicate key value is (1).");

    [TestMethod]
    public void HelpIndex_LocatesIndexesInMemory()
    {
        var sim = WithContainer();
        _ = sim.ExecuteNonQuery("create table t (id int not null constraint pk primary key nonclustered hash with (bucket_count = 8), v int not null index ix nonclustered) with (memory_optimized = on)");
        using var reader = sim.ExecuteReader("exec sp_helpindex 't'");
        IsTrue(reader.Read());
        AreEqual("nonclustered located in MEMORY ", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual("nonclustered hash, unique, primary key located in MEMORY ", reader.GetString(1));
    }

    private const string NativeProcedure = """
        create procedure dbo.p @id int with native_compilation, schemabinding as
        begin atomic with (transaction isolation level = snapshot, language = N'us_english')
            insert dbo.t values (@id);
            insert dbo.t values (@id);
        end
        """;

    [TestMethod]
    public void NativeProcedure_IsReportedNativelyCompiledAndSchemaBound()
    {
        var sim = WithContainer();
        sim.ExecuteBatches("create table t (id int not null primary key nonclustered) with (memory_optimized = on)", NativeProcedure);
        AreEqual(2, sim.ExecuteScalar("select cast(uses_native_compilation as int) + cast(is_schema_bound as int) from sys.sql_modules where object_id = object_id('dbo.p')"));
    }

    [TestMethod]
    public void NativeProcedure_ErrorRollsBackItsBlockAndEndsTheBatch()
    {
        var sim = WithContainer();
        sim.ExecuteBatches("create table t (id int not null primary key nonclustered) with (memory_optimized = on); create table d (id int)", NativeProcedure);
        using var connection = sim.CreateOpenConnection();
        AreEqual(2627, Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; insert d values (1); exec dbo.p 1; insert d values (2)").ExecuteNonQuery()).Number);
        AreEqual("1|1|1|0", connection.CreateCommand("select concat(@@trancount, '|', xact_state(), '|', (select count(*) from d), '|', (select count(*) from t with (snapshot)))").ExecuteScalar());
        _ = connection.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(2627, Throws<SimulatedSqlException>(() => connection.CreateCommand("exec dbo.p 2").ExecuteNonQuery()).Number);
        AreEqual("0|0", connection.CreateCommand("select concat(@@trancount, '|', (select count(*) from t))").ExecuteScalar());
    }

    [TestMethod]
    public void NativeProcedure_ReachesMemoryOptimizedTablesOnly()
    {
        var sim = WithContainer();
        _ = sim.ExecuteNonQuery("create table d (id int)");
        _ = sim.AssertSqlError("""
            create procedure dbo.p with native_compilation, schemabinding as
            begin atomic with (transaction isolation level = snapshot, language = N'us_english')
                select id from dbo.d;
            end
            """, 10775);
    }

    [TestMethod]
    public void NativeProcedure_BodyMustBeAnAtomicBlock()
        => _ = new Simulation().AssertSqlError("create procedure dbo.p with native_compilation, schemabinding as begin select 1 end", 10783);

    [TestMethod]
    public void AtomicBlock_NeedsLanguage()
        => new Simulation().AssertSqlError(
            "create procedure dbo.p with native_compilation, schemabinding as begin atomic with (transaction isolation level = snapshot) select 1 end",
            10784,
            "The WITH clause of BEGIN ATOMIC statement must specify a value for the option 'language'.");

    [TestMethod]
    public void AtomicBlock_RefusesReadCommitted()
        => _ = new Simulation().AssertSqlError(
            "create procedure dbo.p with native_compilation, schemabinding as begin atomic with (transaction isolation level = read committed, language = N'us_english') select 1 end",
            10794);

    [TestMethod]
    public void NativeScalarFunction_Runs()
        => AreEqual(42, new Simulation().ExecuteBatchesScalar(
            """
            create function dbo.f (@x int) returns int with native_compilation, schemabinding as
            begin atomic with (transaction isolation level = snapshot, language = N'us_english')
                return @x * 2;
            end
            """,
            "select dbo.f(21)"));
}
