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
        var ex = Throws<SimulatedSqlException>(() => simulation.ExecuteScalar("select next value for s9"));
        AreEqual(11728, ex.Number);
    }

    [TestMethod]
    public void NextValueFor_InWhereClause_RaisesMsg11720()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sw as int start with 1");
        var ex = Throws<SimulatedSqlException>(() => simulation.ExecuteScalar("select 1 where (next value for sw) > 0"));
        AreEqual(11720, ex.Number);
    }

    [TestMethod]
    public void Create_IncrementZero_RaisesMsg11700()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteNonQuery("create sequence sz as int increment by 0"));
        AreEqual(11700, ex.Number);
    }

    [TestMethod]
    public void Create_FloatType_RaisesMsg11702()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteNonQuery("create sequence sf as float"));
        AreEqual(11702, ex.Number);
    }

    [TestMethod]
    public void Create_DecimalWithScale_RaisesMsg11702()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteNonQuery("create sequence sd as decimal(10,2)"));
        AreEqual(11702, ex.Number);
    }

    [TestMethod]
    public void Create_StartOutOfRange_RaisesMsg11703()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteNonQuery("create sequence sr as int start with 1 minvalue 10"));
        AreEqual(11703, ex.Number);
    }

    [TestMethod]
    public void Create_Duplicate_RaisesMsg2714()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence sdup as int");
        var ex = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery("create sequence sdup as int"));
        AreEqual(2714, ex.Number);
    }

    [TestMethod]
    public void Drop_NonexistentWithoutIfExists_RaisesMsg3701()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteNonQuery("drop sequence does_not_exist"));
        AreEqual(3701, ex.Number);
    }

    [TestMethod]
    public void Drop_NonexistentWithIfExists_Succeeds()
        => _ = new Simulation().ExecuteNonQuery("drop sequence if exists does_not_exist");

    [TestMethod]
    public void NextValueFor_OnNonSequence_RaisesMsg11726()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table tref (id int)");
        var ex = Throws<SimulatedSqlException>(() => simulation.ExecuteScalar("select next value for tref"));
        AreEqual(11726, ex.Number);
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
        var ex = Throws<SimulatedSqlException>(() => simulation.ExecuteScalar("select next value for sdr"));
        AreEqual(208, ex.Number);
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
        // The next draw follows the last by the new increment (probed
        // 2026-10-04 against SQL Server 2025).
        _ = simulation.ExecuteNonQuery("alter sequence sb increment by 10");
        AreEqual(11, simulation.ExecuteScalar<int>("select next value for sb"));
        AreEqual(21, simulation.ExecuteScalar<int>("select next value for sb"));
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
        var ex = Throws<SimulatedSqlException>(() => simulation.ExecuteScalar("select next value for sd"));
        AreEqual(11728, ex.Number);
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
        // After two advances from 1 with increment 1, current_value is the
        // value most recently *emitted* — 2, not the 3 that comes next
        // (probe-confirmed against SQL Server 2025; it agrees with
        // last_used_value once anything has been issued).
        AreEqual(2, simulation.ExecuteScalar<int>("select current_value from sys.sequences where name = 'sysview2'"));
    }

    // A decimal sequence's values report the spelling it was declared with
    // (probed 2026-10-04 against SQL Server 2025).
    [TestMethod]
    [DataRow("bigint", "bigint")]
    [DataRow("int", "int")]
    [DataRow("smallint", "smallint")]
    [DataRow("tinyint", "tinyint")]
    [DataRow("decimal(18,0)", "decimal")]
    [DataRow("numeric(18,0)", "numeric")]
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
    /// <c>is_cached</c> is 0 only under <c>NO CACHE</c>; <c>cache_size</c> is an
    /// explicit <c>CACHE n</c>'s size and otherwise NULL, through ALTER as
    /// through CREATE. Probed 2026-09-26 against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void SysSequences_ReportsTheDeclaredCache()
        => AreEqual("a:1:20|b:0:-|c:1:7|d:1:-|e:0:-", new Simulation().ExecuteScalar("""
            create sequence a cache 20; create sequence b no cache; create sequence c; create sequence d cache; create sequence e cache 20;
            alter sequence e no cache; alter sequence c cache 7;
            select string_agg(concat(name, ':', cast(is_cached as int), ':', isnull(cast(cache_size as varchar(10)), '-')), '|') within group (order by name) from sys.sequences
            """));

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

    /// <summary>
    /// <c>sys.sequences.current_value</c> is the most recent value emitted, or
    /// — before anything has been — the position the next <c>NEXT VALUE FOR</c>
    /// will return. Every stage below is verbatim from SQL Server 2025 for a
    /// <c>START WITH 10 INCREMENT BY 5</c> sequence; the simulator previously
    /// projected its internal next-to-issue counter, reporting one increment
    /// ahead after any use and disagreeing with <c>last_used_value</c> where
    /// real has the two equal.
    /// </summary>
    [TestMethod]
    public void SysSequences_CurrentValue_TracksLastEmittedNotNext()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create sequence s as int start with 10 increment by 5");

        AssertSequenceState(sim, "fresh", currentValue: 10, lastUsed: null);

        _ = sim.ExecuteScalar("select next value for s");
        AssertSequenceState(sim, "after issuing 10", currentValue: 10, lastUsed: 10);

        _ = sim.ExecuteNonQuery("alter sequence s restart with 100");
        AssertSequenceState(sim, "after RESTART WITH 100", currentValue: 100, lastUsed: null);

        _ = sim.ExecuteScalar("select next value for s");
        _ = sim.ExecuteScalar("select next value for s");
        AssertSequenceState(sim, "after issuing 100 and 105", currentValue: 105, lastUsed: 105);
    }

    /// <summary>
    /// <c>RESTART WITH n</c> moves the sequence's <em>start</em> as well as its
    /// position: <c>sys.sequences.start_value</c> reports n afterwards, and a
    /// later bare <c>RESTART</c> returns to n rather than to the value the
    /// sequence was declared with (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void AlterSequenceRestartWith_MovesStartValue()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create sequence s as int start with 10 increment by 5");
        AreEqual(10, sim.ExecuteScalar<int>("select start_value from sys.sequences where name = 's'"));

        _ = sim.ExecuteNonQuery("alter sequence s restart with 100");
        AreEqual(100, sim.ExecuteScalar<int>("select start_value from sys.sequences where name = 's'"));

        _ = sim.ExecuteScalar("select next value for s");
        _ = sim.ExecuteNonQuery("alter sequence s restart");

        // Back to 100 — the restarted origin — not the declared 10.
        AssertSequenceState(sim, "after bare RESTART", currentValue: 100, lastUsed: null);
        AreEqual(100, sim.ExecuteScalar<int>("select next value for s"));
    }

    private static void AssertSequenceState(Simulation sim, string stage, int currentValue, int? lastUsed)
    {
        AreEqual(currentValue, sim.ExecuteScalar<int>("select current_value from sys.sequences where name = 's'"), stage);
        var actualLastUsed = sim.ExecuteScalar("select last_used_value from sys.sequences where name = 's'");
        if (lastUsed is { } expected)
            AreEqual(expected, Convert.ToInt32(actualLastUsed), stage);
        else
            AreEqual(DBNull.Value, actualLastUsed, stage);
    }

    /// <summary>
    /// <c>NEXT VALUE FOR</c> is rejected in every clause real names in Msg
    /// 11720 — probe-confirmed that all eight raise, and that the rejection is
    /// parse-time (uncatchable by TRY/CATCH in the same batch on real).
    /// </summary>
    [TestMethod]
    [DataRow("select t.a from t join u on u.b = next value for s")]
    [DataRow("insert dst output next value for s values (1)")]
    [DataRow("select top (next value for s) a from t")]
    [DataRow("select sum(a) over (order by next value for s) from t")]
    [DataRow("select a from t where a = next value for s")]
    [DataRow("select a from t order by next value for s")]
    [DataRow("select a from t group by next value for s")]
    public void NextValueFor_InDisallowedClause_Raises11720(string sql)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create sequence s as int start with 1 increment by 1;
            create table t (a int); create table u (b int); create table dst (a int)
            """);

        var ex = sim.AssertSqlError(sql, 11720);
        AreEqual(
            "NEXT VALUE FOR function is not allowed in the TOP, OVER, OUTPUT, ON, WHERE, GROUP BY, HAVING, or ORDER BY clauses.",
            ex.Message);
    }

    /// <summary>The clauses that legitimately accept it keep working.</summary>
    [TestMethod]
    [DataRow("select next value for s")]
    [DataRow("insert dst (a) values (next value for s)")]
    [DataRow("select next value for s from t")]
    public void NextValueFor_InAllowedPosition_Accepted(string sql)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create sequence s as int start with 1 increment by 1;
            create table t (a int); create table dst (a int);
            insert t values (1)
            """);
        _ = sim.ExecuteNonQuery(sql);
    }

    [TestMethod]
    public void NextValueFor_InADefault_RefusesADatabaseQualifiedName()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create sequence dbo.sq");
        sim.AssertSqlError("create table t (c int default next value for simulated.dbo.sq)", 11730, "Database name cannot be specified for the sequence object in default constraints.");
    }

    [TestMethod]
    public void NextValueFor_IsDescribedNotNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create sequence dbo.s1; create sequence dbo.s2 as smallint");
        CollectionAssert.AreEqual(new[] { false, false }, sim.ColumnNullability("select next value for dbo.s1, next value for dbo.s2"));
    }

    // Differential sweep against SQL Server 2025, probed 2026-10-04.

    [TestMethod]
    [DataRow("create sequence s as int minvalue 10 maxvalue 5", 11705)]
    [DataRow("create sequence s as int minvalue 5 maxvalue 5", 11705)]
    [DataRow("create sequence s as int start with 1 start with 2", 11712)]
    [DataRow("create sequence s as int cycle no cycle", 11712)]
    [DataRow("create sequence s as int minvalue 1 no minvalue", 11712)]
    [DataRow("create sequence s as int cache 0", 11706)]
    [DataRow("create sequence s as int start with 1.0", 11708)]
    [DataRow("create sequence s as int increment by 3.0", 11708)]
    [DataRow("create sequence #s", 11714)]
    [DataRow("create sequence s as int cache -1", 102)]
    [DataRow("create sequence s as int start with 1e2", 102)]
    [DataRow("create sequence s as int, start with 1", 102)]
    public void Create_RefusesWhatRealRefuses(string sql, int error) =>
        new Simulation().AssertSqlError(sql, error);

    [TestMethod]
    [DataRow("alter sequence s start with 5", 11710)]
    [DataRow("alter sequence s as bigint", 11711)]
    [DataRow("alter sequence s", 11715)]
    [DataRow("alter sequence s restart restart", 11712)]
    [DataRow("alter sequence s minvalue 50", 11704)]
    [DataRow("alter sequence s maxvalue 5", 11704)]
    [DataRow("alter sequence s maxvalue 0", 11705)]
    [DataRow("alter sequence s restart with -5", 11703)]
    [DataRow("alter sequence s restart with 1.5", 11708)]
    [DataRow("alter sequence s cache 0", 11706)]
    [DataRow("alter sequence nosuch restart", 15151)]
    [DataRow("alter sequence t restart", 15151)]
    public void Alter_RefusesWhatRealRefuses(string sql, int error)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int start with 10 increment by 5 minvalue 0 maxvalue 1000 no cache; create table t (a int)");
        _ = simulation.ExecuteScalar("select next value for s");
        _ = simulation.ExecuteScalar("select next value for s");
        _ = simulation.AssertSqlError(sql, error);
        // A refused ALTER leaves the sequence as it was.
        AreEqual(20, simulation.ExecuteScalar<int>("select next value for s"));
    }

    [TestMethod]
    public void Alter_NoMinOrMaxValue_RestoresTheTypeBounds()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int start with 10 minvalue 5 maxvalue 100; alter sequence s no minvalue no maxvalue");
        AreEqual("-2147483648|2147483647", simulation.ExecuteScalar("select concat(cast(minimum_value as int), '|', cast(maximum_value as int)) from sys.sequences"));
    }

    [TestMethod]
    public void Alter_OfAnExhaustedSequence_ResumesFromTheLastValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int start with 1 maxvalue 2 no cache");
        _ = simulation.ExecuteScalar("select next value for s");
        _ = simulation.ExecuteScalar("select next value for s");
        _ = simulation.ExecuteNonQuery("alter sequence s increment by -1");
        IsFalse((bool)simulation.ExecuteScalar("select is_exhausted from sys.sequences")!);
        AreEqual(1, simulation.ExecuteScalar<int>("select next value for s"));
    }

    [TestMethod]
    public void Alter_RollsBackWithItsTransaction()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int start with 1 no cache");
        _ = simulation.ExecuteNonQuery("begin tran; alter sequence s restart with 100; rollback");
        AreEqual(1, simulation.ExecuteScalar<int>("select next value for s"));
    }

    [TestMethod]
    public void Exhaustion_EndsTheBatch()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int start with 1 maxvalue 1 no cache; create table log (a int)");
        _ = simulation.ExecuteScalar("select next value for s");
        _ = simulation.AssertSqlError("select next value for s; insert log values (1)", 11728);
        AreEqual(0, simulation.ExecuteScalar<int>("select count(*) from log"));
        // A TRY catches it.
        AreEqual(11728, simulation.ExecuteScalar<int>("declare @e int; begin try declare @x int = next value for s; end try begin catch set @e = error_number(); end catch; select @e"));
    }

    [TestMethod]
    public void NumericSpelling_IsKeptThroughTheCatalogAndTheValues()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as numeric(5, 0)");
        AreEqual("108|numeric", simulation.ExecuteScalar("select concat(system_type_id, '|', type_name(system_type_id)) from sys.sequences"));
        AreEqual("numeric", simulation.ExecuteScalar("select sql_variant_property(next value for s, 'BaseType')"));
    }

    [TestMethod]
    public void Over_DrawsInItsOwnOrder()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int start with 1 no cache");
        using var reader = simulation.ExecuteReader("select x, next value for s over (order by x desc) n from (values (1), (2), (3)) v(x) order by x");
        var pairs = new List<string>();
        while (reader.Read())
            pairs.Add($"{reader.GetInt32(0)}:{reader.GetInt32(1)}");
        CollectionAssert.AreEqual(new[] { "1:3", "2:2", "3:1" }, pairs);
    }

    [TestMethod]
    public void Over_OrdersAnInsertSelectsDraws()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence s as int start with 1 no cache;
            create table src (k int, x varchar(5)); insert src values (3, 'c'), (1, 'a'), (2, 'b');
            create table t (n int, x varchar(5));
            insert t select next value for s over (order by k desc), x from src
            """);
        AreEqual("1c 2b 3a", simulation.ExecuteScalar("select string_agg(concat(n, x), ' ') within group (order by n) from t"));
    }

    [TestMethod]
    [DataRow("select next value for s over (partition by x order by x) from (values (1)) v(x)", 11716)]
    [DataRow("select next value for s over () from (values (1)) v(x)", 11718)]
    [DataRow("update t set n = next value for s over (order by k)", 11717)]
    public void Over_RefusesWhatRealRefuses(string sql, int error)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int no cache; create table t (k int, n int)");
        _ = simulation.AssertSqlError(sql, error);
    }

    [TestMethod]
    public void DefaultKeyword_SharesItsRowsDraw()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence s as int start with 1 no cache;
            create table t (a int default next value for s, b int);
            insert t (a, b) values (default, next value for s), (default, next value for s)
            """);
        AreEqual("1=1 2=2", simulation.ExecuteScalar("select string_agg(concat(a, '=', b), ' ') within group (order by a) from t"));
    }

    [TestMethod]
    public void AddedDefaultColumn_DrawsPerExistingRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence s as int start with 1 no cache;
            create table t (b int); insert t values (1), (2);
            alter table t add a int not null default next value for s
            """);
        AreEqual(2, simulation.ExecuteScalar<int>("select count(distinct a) from t"));
    }

    [TestMethod]
    public void AssigningSelect_RefusesTwoDrawsOfOneSequence()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int no cache");
        _ = simulation.AssertSqlError("declare @a int, @b int; select @a = next value for s, @b = next value for s", 11736);
    }

    [TestMethod]
    [DataRow("create procedure p as return next value for s")]
    [DataRow("select * from (values (next value for s)) v(n)")]
    [DataRow("create type tt as table (a int default next value for s)")]
    public void NestedPositions_Refuse11719(string sql)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int no cache");
        _ = simulation.AssertSqlError(sql, 11719);
    }

    [TestMethod]
    public void TopCount_IsRefusedWithoutDrawing()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int no cache");
        _ = simulation.AssertSqlError("select top (next value for s) x from (values (1)) v(x)", 11720);
        AreEqual(-2147483648, simulation.ExecuteScalar<int>("select next value for s"));
    }

    [TestMethod]
    public void NextValueFor_NamesAViewOrAVariable()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create sequence s", "create view v as select 1 x");
        _ = simulation.AssertSqlError("select next value for v", 11726);
        simulation.ValidateSyntaxError("declare @n sysname = 's'; select next value for @n", "@n");
    }

    [TestMethod]
    public void Drop_RefusesASequenceADefaultDrawsFrom()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s; create table t (a int constraint df default next value for s)");
        simulation.AssertSqlError("drop sequence s", 3729, "Cannot DROP SEQUENCE 's' because it is being referenced by object 'df'.");
        AreEqual("df", simulation.ExecuteScalar("select object_name(referencing_id) from sys.sql_expression_dependencies where referenced_id = object_id('s')"));
    }

    [TestMethod]
    public void GetRange_ReservesARange()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int start with 1 increment by 1 no cache");
        AreEqual("1|5|0|6", simulation.ExecuteScalar("""
            declare @f sql_variant, @l sql_variant, @c int;
            exec sp_sequence_get_range @sequence_name = N's', @range_size = 5, @range_first_value = @f output, @range_last_value = @l output, @range_cycle_count = @c output;
            select concat(cast(@f as int), '|', cast(@l as int), '|', @c, '|', next value for s)
            """));
    }

    [TestMethod]
    public void GetRange_WrapsACyclingSequence()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as tinyint start with 250 cycle no cache");
        AreEqual("250|13|1", simulation.ExecuteScalar("""
            declare @f sql_variant, @l sql_variant, @c int;
            exec sp_sequence_get_range 's', 20, @f output, @l output, @c output;
            select concat(cast(@f as int), '|', cast(@l as int), '|', @c)
            """));
    }

    [TestMethod]
    [DataRow("declare @f sql_variant; exec sp_sequence_get_range 's', 6, @f output", 11732)]
    [DataRow("declare @f sql_variant; exec sp_sequence_get_range 's', 0, @f output", 11733)]
    [DataRow("declare @f sql_variant; exec sp_sequence_get_range 'nosuch', 1, @f output", 208)]
    [DataRow("exec sp_sequence_get_range 's', 3", 201)]
    [DataRow("declare @f int; exec sp_sequence_get_range 's', 3, @f output", 257)]
    public void GetRange_RefusesWhatRealRefuses(string sql, int error)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int start with 1 minvalue 1 maxvalue 5 no cache");
        _ = simulation.AssertSqlError(sql, error);
        // Nothing was reserved.
        AreEqual(1, simulation.ExecuteScalar<int>("select next value for s"));
    }

    [TestMethod]
    public void InsertSelect_DefaultSharesTheSelectListDraw()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create sequence s start with 1;
            create sequence s2 start with 100;
            create table t (a int, b int default next value for s, c int default next value for s2);
            insert t (a) select next value for s2 from (values (1), (2)) v(x);
            insert t (a, c) select next value for s, next value for s from (values (1), (2)) v(x)
            """);
        AreEqual("100:1:100,101:2:101,3:3:3,4:4:4", simulation.ExecuteScalar("select string_agg(concat(a, ':', b, ':', c), ',') from t"));
    }

    [TestMethod]
    public void TempTableDefault_ResolvesTheSequenceInTempdb()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s start with 1");
        simulation.AssertSqlError("create table #t (a int default next value for s)", 208, "Invalid object name 's'.");
        simulation.AssertSqlError("create table #t (a int default next value for dbo.s)", 208, "Invalid object name 'dbo.s'.");
        AreEqual(211, simulation.AssertSqlError("create table #t (a int default next value for s)", 208).State);
        _ = simulation.ExecuteNonQuery("use tempdb; create sequence dbo.s start with 50; use simulated");
        AreEqual(50, simulation.ExecuteScalar("create table #t (a int default next value for s); insert #t default values; select a from #t"));
    }

    [TestMethod]
    public void ExecArgument_NextValueFor_Msg102AtNext()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s; exec ('create proc p @a int as select @a')");
        simulation.ValidateSyntaxError("exec p next value for s", "next");
    }

    [TestMethod]
    public void NamedWindow_OrdersTheDraws()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s start with 1; create table t (a int); insert t values (3), (1), (2)");
        AreEqual("1:1,2:2,3:3", simulation.ExecuteScalar("""
            select a, next value for s over w n into #r from t window w as (order by a);
            select string_agg(concat(a, ':', n), ',') within group (order by a) from #r
            """));
    }
}
