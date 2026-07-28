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
        // start_value / increment / current_value are sql_variant carrying the
        // sequence's declared type (int here); SqlClient surfaces the inner int
        // via GetValue.
        AreEqual("sql_variant", reader.GetDataTypeName(1));
        AreEqual(100, reader.GetValue(1));
        AreEqual(5, reader.GetValue(2));
        IsTrue(reader.GetBoolean(3));
        IsFalse(reader.GetBoolean(4));
        AreEqual(100, reader.GetValue(5));
    }

    [TestMethod]
    public void SysSequences_AfterAdvance_CurrentValueUpdates()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sysview2 as int start with 1 increment by 1");
        _ = simulation.ExecuteScalar("select next value for sysview2");
        _ = simulation.ExecuteScalar("select next value for sysview2");
        // After two advances starting from 1 with increment 1, current_value
        // is the next value to emit: 3 (int inner base type for an int sequence).
        AreEqual(3, simulation.ExecuteScalar<int>("select current_value from sys.sequences where name = 'sysview2'"));
    }

    // A decimal sequence's inner type reports BaseType 'numeric' — the
    // simulator's single decimal family surfaces as numeric (documented quirk),
    // diverging from real's 'decimal' for a decimal-declared sequence.
    [TestMethod]
    [DataRow("bigint", "bigint")]
    [DataRow("int", "int")]
    [DataRow("smallint", "smallint")]
    [DataRow("tinyint", "tinyint")]
    [DataRow("decimal(18,0)", "numeric")]
    public void SysSequences_StartValue_InnerBaseTypeMatchesDeclaredType(string declared, string expectedBaseType)
        => AreEqual(expectedBaseType, new Simulation().ExecuteScalar($"""
            create sequence sv_bt as {declared} start with 5;
            select sql_variant_property(start_value, 'BaseType') from sys.sequences where name = 'sv_bt'
            """));

    [TestMethod]
    public void SysSequences_BigIntSequence_StartValueUnwrapsToLong()
        => AreEqual(5000000000L, new Simulation().ExecuteScalar("""
            create sequence sv_big as bigint start with 5000000000;
            select start_value from sys.sequences where name = 'sv_big'
            """));

    /// <summary>
    /// last_used_value is NULL until the first NEXT VALUE FOR (probe-confirmed:
    /// a freshly created sequence reports NULL here even though current_value is
    /// the start value), then reports the last emitted value as a sql_variant,
    /// and resets to NULL after ALTER SEQUENCE … RESTART. DacFx's sequence
    /// reverse-engineering query projects [s].[last_used_value].
    /// </summary>
    [TestMethod]
    public void SysSequences_LastUsedValue_NullUntilFirstUse()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence lastused as int start with 5 increment by 1");
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select last_used_value from sys.sequences where name = 'lastused'"));
        _ = simulation.ExecuteScalar("select next value for lastused");
        AreEqual(5, simulation.ExecuteScalar("select last_used_value from sys.sequences where name = 'lastused'"));
        _ = simulation.ExecuteScalar("select next value for lastused");
        AreEqual(6, simulation.ExecuteScalar("select last_used_value from sys.sequences where name = 'lastused'"));
        _ = simulation.ExecuteNonQuery("alter sequence lastused restart");
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select last_used_value from sys.sequences where name = 'lastused'"));
    }

    /// <summary>
    /// precision / scale mirror the sequence's declared numeric type
    /// (int → 10/0, bigint → 19/0). SMO's Sequence property-bag query projects
    /// them as [NumericPrecision] / [NumericScale]; a missing column would fail
    /// the whole bag query Msg 207.
    /// </summary>
    [TestMethod]
    public void SysSequences_PrecisionScale_MirrorDeclaredType()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence sp_int as int start with 1;
            create sequence sp_big as bigint start with 1
            """);
        using var reader = simulation.CreateCommand("""
            select cast(precision as int), cast(scale as int)
            from sys.sequences where name = 'sp_int'
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(10, reader.GetInt32(0));
        AreEqual(0, reader.GetInt32(1));
        reader.Close();

        using var big = simulation.CreateCommand("""
            select cast(precision as int), cast(scale as int)
            from sys.sequences where name = 'sp_big'
            """).ExecuteReader();
        IsTrue(big.Read());
        AreEqual(19, big.GetInt32(0));
        AreEqual(0, big.GetInt32(1));
    }

    /// <summary>
    /// Every modeled <c>sys.sequences</c> column resolves in a single projection
    /// — the SMO Sequence property-bag reads the whole set, so one missing
    /// column fails the bag query and every Sequence property errors.
    /// </summary>
    [TestMethod]
    public void SysSequences_FullModeledColumnSet_Resolves()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sq_full as int start with 1");
        AreEqual(1, simulation.ExecuteScalar<int>("""
            select count(*) from sys.sequences where name = 'sq_full' and (
                name is not null and object_id is not null and schema_id is not null
                and start_value is not null and increment is not null
                and minimum_value is not null and maximum_value is not null
                and is_cycling is not null and is_cached is not null
                and current_value is not null and system_type_id is not null
                and user_type_id is not null and is_exhausted is not null
                and precision is not null and create_date is not null
                and modify_date is not null and (cache_size is null or cache_size = 0)
                and (principal_id is null or principal_id = 0) and (scale = 0 or scale is not null))
            """));
    }

    /// <summary>
    /// All references to one sequence within a single inserted row return the
    /// same value — including a DEFAULT-clause reference on a column the
    /// INSERT didn't list. Probe-confirmed against SQL Server 2025: the row
    /// lands as <c>(1, 1)</c> having consumed exactly one sequence value, not
    /// two. Previously the DEFAULT was evaluated under a freshly bumped row
    /// stamp and drew a second value, silently storing <c>(2, 1)</c>.
    /// </summary>
    [TestMethod]
    public void SingleRowValues_AndDefault_ShareOneSequenceValue()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create sequence s as int start with 1 increment by 1;
            create table d (id int default (next value for s), v int);
            insert into d (v) values (next value for s)
            """);

        using var reader = sim.ExecuteReader("select id, v from d");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(1, reader.GetInt32(1));
        IsFalse(reader.Read());

        // Exactly one value consumed — asserted through last_used_value, which
        // is unambiguously "the most recent value emitted". (sys.sequences'
        // current_value has its own off-by-one divergence, tracked separately.)
        AreEqual(1L, Convert.ToInt64(sim.ExecuteScalar("select last_used_value from sys.sequences where name = 's'")));
    }

    /// <summary>
    /// A <em>multi-row</em> constructor referencing a sequence that an unlisted
    /// target column also defaults from is rejected with <b>Msg 11731</b> at
    /// bind time — real declines to define which value the row would share
    /// (probe-confirmed; the single-row form above is accepted).
    /// </summary>
    [TestMethod]
    [DataRow("insert into d (v) values (next value for s), (next value for s)")]
    [DataRow("insert into d (v) values (next value for s), (999)")]
    public void MultiRowValues_SequenceDefaultColumnUnlisted_Raises11731(string insert)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create sequence s as int start with 1 increment by 1;
            create table d (id int default (next value for s), v int)
            """);

        var ex = sim.AssertSqlError(insert, 11731);
        AreEqual(
            "A column that uses a sequence object in the default constraint must be present in the target columns list, if the same sequence object appears in a row constructor.",
            ex.Message);
    }

    /// <summary>
    /// The shapes Msg 11731 must <em>not</em> catch: the defaulted column
    /// listed explicitly, a different sequence in the constructor, and a
    /// multi-row insert whose tuples reference no sequence at all. Each
    /// advances once per row, matching real.
    /// </summary>
    [TestMethod]
    [DataRow("insert into d (id, v) values (next value for s, next value for s), (next value for s, next value for s)", "1/1 2/2")]
    [DataRow("insert into d (v) values (next value for s2), (next value for s2)", "1/100 2/101")]
    [DataRow("insert into d (v) values (10), (20)", "1/10 2/20")]
    public void MultiRowValues_NonConflictingShapes_Succeed(string insert, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create sequence s as int start with 1 increment by 1;
            create sequence s2 as int start with 100 increment by 1;
            create table d (id int default (next value for s), v int)
            """);
        _ = sim.ExecuteNonQuery(insert);

        var rows = new List<string>();
        using var reader = sim.ExecuteReader("select id, v from d order by id");
        while (reader.Read())
            rows.Add($"{reader.GetInt32(0)}/{reader.GetInt32(1)}");

        AreEqual(expected, string.Join(" ", rows));
    }
}
