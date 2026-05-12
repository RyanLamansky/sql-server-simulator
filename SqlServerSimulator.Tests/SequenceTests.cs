using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for sequence objects: <c>CREATE SEQUENCE</c> /
/// <c>DROP SEQUENCE</c> / <c>ALTER SEQUENCE</c> / <c>NEXT VALUE FOR</c>.
/// All assertions probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SequenceTests
{
    [TestMethod]
    public void NextValueFor_FirstCall_ReturnsStartValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s1 as int start with 100");
        AreEqual(100, simulation.ExecuteScalar<int>("select next value for s1"));
    }

    [TestMethod]
    public void NextValueFor_AcrossStatements_Advances()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s2 as int start with 1 increment by 1");
        AreEqual(1, simulation.ExecuteScalar<int>("select next value for s2"));
        AreEqual(2, simulation.ExecuteScalar<int>("select next value for s2"));
        AreEqual(3, simulation.ExecuteScalar<int>("select next value for s2"));
    }

    /// <summary>
    /// Probe-confirmed: multiple NEXT VALUE FOR same-sequence in one row emit
    /// the same value (per-row dedup). For a single-row SELECT projection,
    /// the comma-separated NEXT VALUE FOR list collapses to one advance.
    /// </summary>
    [TestMethod]
    public void NextValueFor_MultipleInOneRow_SameValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s3 as int start with 50");
        using var reader = simulation.CreateCommand("select next value for s3 as a, next value for s3 as b").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(50, reader.GetInt32(0));
        AreEqual(50, reader.GetInt32(1));
    }

    [TestMethod]
    public void NextValueFor_AcrossSelectRows_Advances()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence s4 as int start with 10 increment by 5;
            create table src (x int);
            insert src values (1),(2),(3)
            """);
        using var reader = simulation.CreateCommand("select next value for s4 from src").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 10, 15, 20 }, values);
    }

    [TestMethod]
    public void NextValueFor_InsertValuesMultiRow_Advances()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence s5 as int start with 100;
            create table t (id int);
            insert t values (next value for s5), (next value for s5), (next value for s5)
            """);
        using var reader = simulation.CreateCommand("select id from t order by id").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 100, 101, 102 }, values);
    }

    /// <summary>
    /// Same-row dedup across the two columns of an INSERT VALUES tuple:
    /// <c>(next, next)</c> writes the same value into both columns.
    /// </summary>
    [TestMethod]
    public void NextValueFor_InsertValuesSameRow_DedupsAcrossColumns()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence s6 as int start with 1;
            create table t (a int, b int);
            insert t values (next value for s6, next value for s6), (next value for s6, next value for s6)
            """);
        using var reader = simulation.CreateCommand("select a, b from t order by a").ExecuteReader();
        var rows = new List<(int A, int B)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        HasCount(2, rows);
        AreEqual((1, 1), rows[0]);
        AreEqual((2, 2), rows[1]);
    }

    [TestMethod]
    public void DefaultClause_NextValueFor_FiresPerInsert()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence s7 as int start with 1000 increment by 1;
            create table t (id int default (next value for s7) primary key, name varchar(20));
            insert t (name) values ('a'), ('b'), ('c')
            """);
        using var reader = simulation.CreateCommand("select id from t order by id").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1000, 1001, 1002 }, values);
    }

    [TestMethod]
    public void NextValueFor_DescendingCycle_WrapsToMax()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence s8 as int start with 10 increment by -1 minvalue 8 maxvalue 10 cycle;
            create table t (v int);
            insert t values (next value for s8), (next value for s8), (next value for s8), (next value for s8), (next value for s8)
            """);
        using var reader = simulation.CreateCommand("select v from t").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 10, 9, 8, 10, 9 }, values);
    }

    [TestMethod]
    public void NextValueFor_NoCycleExhausted_RaisesMsg11728()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s9 as int start with 1 increment by 1 maxvalue 3 no cycle");
        AreEqual(1, simulation.ExecuteScalar<int>("select next value for s9"));
        AreEqual(2, simulation.ExecuteScalar<int>("select next value for s9"));
        AreEqual(3, simulation.ExecuteScalar<int>("select next value for s9"));
        var ex = Throws<DbException>(() => simulation.ExecuteScalar("select next value for s9"));
        AreEqual("11728", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void NextValueFor_InWhereClause_RaisesMsg11720()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sw as int start with 1");
        var ex = Throws<DbException>(() => simulation.ExecuteScalar("select 1 where (next value for sw) > 0"));
        AreEqual("11720", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Create_IncrementZero_RaisesMsg11700()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("create sequence sz as int increment by 0"));
        AreEqual("11700", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Create_FloatType_RaisesMsg11702()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("create sequence sf as float"));
        AreEqual("11702", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Create_DecimalWithScale_RaisesMsg11702()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("create sequence sd as decimal(10,2)"));
        AreEqual("11702", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Create_StartOutOfRange_RaisesMsg11703()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("create sequence sr as int start with 1 minvalue 10"));
        AreEqual("11703", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Create_Duplicate_RaisesMsg2714()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sdup as int");
        var ex = Throws<DbException>(() => simulation.ExecuteNonQuery("create sequence sdup as int"));
        AreEqual("2714", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Drop_NonexistentWithoutIfExists_RaisesMsg3701()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("drop sequence does_not_exist"));
        AreEqual("3701", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Drop_NonexistentWithIfExists_Succeeds()
        => _ = new Simulation().ExecuteNonQuery("drop sequence if exists does_not_exist");

    [TestMethod]
    public void NextValueFor_OnNonSequence_RaisesMsg11726()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table tref (id int)");
        var ex = Throws<DbException>(() => simulation.ExecuteScalar("select next value for tref"));
        AreEqual("11726", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void NextValueFor_BigInt_Default()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sbig");
        // Default type bigint, default start = minvalue = long.MinValue.
        AreEqual(long.MinValue, simulation.ExecuteScalar<long>("select next value for sbig"));
    }

    [TestMethod]
    public void NextValueFor_AsVariableInit_Works()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence svar as int start with 42");
        AreEqual(42, simulation.ExecuteScalar<int>("declare @v int = next value for svar; select @v"));
    }

    [TestMethod]
    public void NextValueFor_AsSetAssignment_Works()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sset as int start with 100");
        AreEqual(100, simulation.ExecuteScalar<int>("declare @v int; set @v = next value for sset; select @v"));
    }

    [TestMethod]
    public void NextValueFor_AcrossSetStatements_Advances()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence smulti as int start with 1");
        using var reader = simulation.CreateCommand("""
            declare @a int; declare @b int;
            set @a = next value for smulti;
            set @b = next value for smulti;
            select @a, @b
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(2, reader.GetInt32(1));
    }

    [TestMethod]
    public void Drop_Sequence_RemovesIt()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sdr as int");
        _ = simulation.ExecuteNonQuery("drop sequence sdr");
        var ex = Throws<DbException>(() => simulation.ExecuteScalar("select next value for sdr"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    /// <summary>
    /// A column reference named <c>next</c> isn't misidentified as the NEXT
    /// VALUE FOR shape — the lookahead helper only triggers when the full
    /// <c>NEXT VALUE FOR &lt;name&gt;</c> sequence is present.
    /// </summary>
    [TestMethod]
    public void ColumnNamedNext_DoesNotTriggerNextValueFor()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (next int); insert t values (42)");
        AreEqual(42, simulation.ExecuteScalar<int>("select next from t"));
    }

    [TestMethod]
    public void Alter_RestartWith_ResetsCurrentValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sa as int start with 1");
        AreEqual(1, simulation.ExecuteScalar<int>("select next value for sa"));
        AreEqual(2, simulation.ExecuteScalar<int>("select next value for sa"));
        _ = simulation.ExecuteNonQuery("alter sequence sa restart with 100");
        AreEqual(100, simulation.ExecuteScalar<int>("select next value for sa"));
        AreEqual(101, simulation.ExecuteScalar<int>("select next value for sa"));
    }

    [TestMethod]
    public void Alter_IncrementBy_ChangesAdvance()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sb as int start with 1 increment by 1");
        AreEqual(1, simulation.ExecuteScalar<int>("select next value for sb"));
        _ = simulation.ExecuteNonQuery("alter sequence sb increment by 10");
        AreEqual(2, simulation.ExecuteScalar<int>("select next value for sb"));
        AreEqual(12, simulation.ExecuteScalar<int>("select next value for sb"));
    }

    [TestMethod]
    public void Alter_RestartBare_UsesOriginalStart()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sc as int start with 50");
        AreEqual(50, simulation.ExecuteScalar<int>("select next value for sc"));
        AreEqual(51, simulation.ExecuteScalar<int>("select next value for sc"));
        _ = simulation.ExecuteNonQuery("alter sequence sc restart");
        AreEqual(50, simulation.ExecuteScalar<int>("select next value for sc"));
    }

    [TestMethod]
    public void Alter_AfterExhausted_RestartUnsticks()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sd as int start with 1 increment by 1 maxvalue 2 no cycle");
        AreEqual(1, simulation.ExecuteScalar<int>("select next value for sd"));
        AreEqual(2, simulation.ExecuteScalar<int>("select next value for sd"));
        var ex = Throws<DbException>(() => simulation.ExecuteScalar("select next value for sd"));
        AreEqual("11728", ex.Data["HelpLink.EvtID"]);
        _ = simulation.ExecuteNonQuery("alter sequence sd restart with 1");
        AreEqual(1, simulation.ExecuteScalar<int>("select next value for sd"));
    }

    [TestMethod]
    public void SysSequences_ListsRegisteredSequences()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sysview1 as int start with 100 increment by 5 cycle");
        using var reader = simulation.CreateCommand("""
            select name, start_value, increment, is_cycling, is_exhausted, current_value
            from sys.sequences where name = 'sysview1'
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual("sysview1", reader.GetString(0));
        AreEqual(100L, reader.GetInt64(1));
        AreEqual(5L, reader.GetInt64(2));
        IsTrue(reader.GetBoolean(3));
        IsFalse(reader.GetBoolean(4));
        AreEqual(100L, reader.GetInt64(5));
    }

    [TestMethod]
    public void SysSequences_AfterAdvance_CurrentValueUpdates()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sysview2 as int start with 1 increment by 1");
        _ = simulation.ExecuteScalar("select next value for sysview2");
        _ = simulation.ExecuteScalar("select next value for sysview2");
        // After two advances starting from 1 with increment 1, current_value
        // is the next value to emit: 3.
        AreEqual(3L, simulation.ExecuteScalar<long>("select current_value from sys.sequences where name = 'sysview2'"));
    }
}
