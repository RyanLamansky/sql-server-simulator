using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The two knobs deciding whether a trigger fires while other triggers are
/// running: the per-database <c>RECURSIVE_TRIGGERS</c> option (a trigger
/// re-firing itself) and the <c>nested triggers</c> server option (an AFTER
/// trigger firing underneath another AFTER trigger). All behaviors
/// probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class TriggerNestingOptionTests
{
    /// <summary>
    /// Three tables the triggers chain DML through, plus <c>fired</c>, which
    /// takes one row per trigger body run carrying its
    /// <c>TRIGGER_NESTLEVEL()</c>.
    /// </summary>
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t1 (id int identity primary key, v int);
            create table t2 (id int identity primary key, v int);
            create table t3 (id int identity primary key, v int);
            create table fired (seq int identity primary key, name varchar(20), nest int);
            """);
        return simulation;
    }

    /// <summary>Every body run, in order, as <c>name:nestlevel</c>.</summary>
    private static object? FiredTrace(Simulation simulation) => simulation.ExecuteScalar(
        "select string_agg(name + ':' + cast(nest as varchar(2)), ',') within group (order by seq) from fired");

    // A trigger on t1 that logs its fire and inserts one more t1 row while
    // fewer than four exist — self-recursive, but bounded.
    private const string BoundedSelfInsertTrigger = """
        create trigger tr1 on t1 after insert
        as
        begin
            insert fired (name, nest) values ('tr1', trigger_nestlevel());
            if (select count(*) from t1) < 4
                insert t1 (v) values (99);
        end
        """;

    // t1's trigger writes t2, whose trigger only logs — the two-level AFTER
    // chain the nested-triggers option cuts.
    private const string ChainTrigger1 = """
        create trigger tr1 on t1 after insert
        as
        begin
            insert fired (name, nest) values ('tr1', trigger_nestlevel());
            insert t2 (v) values (1);
        end
        """;

    private const string ChainTrigger2 = """
        create trigger tr2 on t2 after insert
        as
            insert fired (name, nest) values ('tr2', trigger_nestlevel())
        """;

    // === RECURSIVE_TRIGGERS ===

    [TestMethod]
    public void SysDatabases_RecursiveTriggers_DefaultsOff()
        => IsFalse((bool)new Simulation().ExecuteScalar(
            "select is_recursive_triggers_on from sys.databases where name = db_name()")!);

    [TestMethod]
    public void SysDatabases_RecursiveTriggers_ReflectsAlterDatabase()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            alter database current set recursive_triggers on;
            select is_recursive_triggers_on from sys.databases where name = db_name()
            """)!);

    /// <summary>
    /// Off (the default), the body's insert on its own table reaches the heap
    /// without re-firing the trigger: two rows, one fire.
    /// </summary>
    [TestMethod]
    public void RecursiveTriggersOff_DirectRecursion_FiresOnce()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(BoundedSelfInsertTrigger, "insert t1 (v) values (1)");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t1"));
        AreEqual("tr1:1", FiredTrace(simulation));
    }

    /// <summary>
    /// On, the same body recurses until its own guard stops it, one nesting
    /// level deeper each time.
    /// </summary>
    [TestMethod]
    public void RecursiveTriggersOn_DirectRecursion_Repeats()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "alter database current set recursive_triggers on",
            BoundedSelfInsertTrigger,
            "insert t1 (v) values (1)");
        AreEqual(4, simulation.ExecuteScalar("select count(*) from t1"));
        AreEqual("tr1:1,tr1:2,tr1:3,tr1:4", FiredTrace(simulation));
    }

    /// <summary>
    /// Unbounded recursion runs into the 32-level nesting cap, and the whole
    /// statement rolls back — the trigger's own writes included.
    /// </summary>
    [TestMethod]
    public void RecursiveTriggersOn_Unbounded_RaisesNestingLimit()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "alter database current set recursive_triggers on",
            "create trigger tr1 on t1 after insert as insert t1 (v) values (99)");
        simulation.AssertSqlError(
            "insert t1 (v) values (1)",
            217,
            "Maximum stored procedure, function, trigger, or view nesting level exceeded (limit 32).");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t1"));
    }

    /// <summary>
    /// Only <em>direct</em> recursion is suppressed: a trigger reached again
    /// through another table's trigger fires, even though its outer frame is
    /// still on the stack.
    /// </summary>
    [TestMethod]
    public void RecursiveTriggersOff_IndirectRecursion_StillFires()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            """
            create trigger tr1 on t1 after insert
            as
            begin
                insert fired (name, nest) values ('tr1', trigger_nestlevel());
                if (select count(*) from t1) < 3
                    insert t2 (v) values (1);
            end
            """,
            """
            create trigger tr2 on t2 after insert
            as
            begin
                insert fired (name, nest) values ('tr2', trigger_nestlevel());
                insert t1 (v) values (2);
            end
            """,
            "insert t1 (v) values (1)");
        AreEqual("tr1:1,tr2:2,tr1:3,tr2:4,tr1:5", FiredTrace(simulation));
    }

    /// <summary>
    /// A stored procedure between the body and the DML doesn't launder the
    /// recursion — the innermost trigger frame is still the trigger's own.
    /// </summary>
    [TestMethod]
    public void RecursiveTriggersOff_DirectRecursionThroughProcedure_StillSuppressed()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "create procedure p_insert as insert t1 (v) values (77)",
            """
            create trigger tr1 on t1 after insert
            as
            begin
                insert fired (name, nest) values ('tr1', trigger_nestlevel());
                if (select count(*) from t1) < 4
                    exec p_insert;
            end
            """,
            "insert t1 (v) values (1)");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t1"));
        AreEqual("tr1:1", FiredTrace(simulation));
    }

    // === nested triggers ===

    [TestMethod]
    public void NestedTriggers_DefaultsOn()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select convert(int, value_in_use) from sys.configurations where configuration_id = 115"));

    /// <summary>
    /// With the option installed as 0, only the first AFTER level runs: the
    /// chain's second table takes its row but its trigger doesn't fire.
    /// </summary>
    [TestMethod]
    public void NestedTriggersOff_StopsAfterTriggerChain()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            ChainTrigger1,
            ChainTrigger2,
            "exec sp_configure 'nested triggers', 0; reconfigure;",
            "insert t1 (v) values (1)");
        AreEqual("tr1:1", FiredTrace(simulation));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t2"));
    }

    /// <summary>
    /// The staged value does nothing until <c>RECONFIGURE</c> installs it — the
    /// change shows in <c>sys.configurations.value</c> while
    /// <c>value_in_use</c>, which the trigger dispatcher reads, still says 1.
    /// </summary>
    [TestMethod]
    public void NestedTriggersOff_WithoutReconfigure_ChainStillFires()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            ChainTrigger1,
            ChainTrigger2,
            "exec sp_configure 'nested triggers', 0",
            "insert t1 (v) values (1)");
        AreEqual(1, simulation.ExecuteScalar(
            "select convert(int, value_in_use) from sys.configurations where configuration_id = 115"));
        AreEqual("tr1:1,tr2:2", FiredTrace(simulation));
    }

    /// <summary>
    /// The server option wins over the database one: with nesting off, a
    /// trigger can't re-fire itself even where <c>RECURSIVE_TRIGGERS</c> is on,
    /// because that re-fire is an AFTER trigger under an AFTER trigger.
    /// </summary>
    [TestMethod]
    public void NestedTriggersOff_DisablesDirectRecursion_DespiteRecursiveTriggersOn()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "alter database current set recursive_triggers on",
            BoundedSelfInsertTrigger,
            "exec sp_configure 'nested triggers', 0; reconfigure;",
            "insert t1 (v) values (1)");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t1"));
        AreEqual("tr1:1", FiredTrace(simulation));
    }

    /// <summary>
    /// Sibling triggers on one table are all first-level, so nesting off
    /// doesn't stop the second one.
    /// </summary>
    [TestMethod]
    public void NestedTriggersOff_SiblingTriggersOnOneTable_BothFire()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "create trigger tr_a on t1 after insert as insert fired (name, nest) values ('a', trigger_nestlevel())",
            "create trigger tr_b on t1 after insert as insert fired (name, nest) values ('b', trigger_nestlevel())",
            "exec sp_configure 'nested triggers', 0; reconfigure;",
            "insert t1 (v) values (1)");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from fired"));
        AreEqual(1, simulation.ExecuteScalar("select max(nest) from fired"));
    }

    /// <summary>
    /// INSTEAD OF triggers are exempt from the nesting rule: the chain runs
    /// through both of them, and the AFTER trigger at the end fires too —
    /// nothing but INSTEAD OF frames sit above it.
    /// </summary>
    [TestMethod]
    public void NestedTriggersOff_InsteadOfChain_NestsAndReachesAfterTrigger()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            """
            create trigger io1 on t1 instead of insert
            as
            begin
                insert fired (name, nest) values ('io1', trigger_nestlevel());
                insert t2 (v) values (1);
            end
            """,
            """
            create trigger io2 on t2 instead of insert
            as
            begin
                insert fired (name, nest) values ('io2', trigger_nestlevel());
                insert t3 (v) values (1);
            end
            """,
            "create trigger tr3 on t3 after insert as insert fired (name, nest) values ('tr3', trigger_nestlevel())",
            "exec sp_configure 'nested triggers', 0; reconfigure;",
            "insert t1 (v) values (1)");
        AreEqual("io1:1,io2:2,tr3:3", FiredTrace(simulation));
        // Both INSTEAD OF targets stayed empty; only the unintercepted t3 wrote.
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t1"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t2"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t3"));
    }

    /// <summary>
    /// The AFTER rule reads the whole stack, not just the frame above: an AFTER
    /// trigger's body still reaches an INSTEAD OF trigger, but the AFTER trigger
    /// one level below that stays suppressed — the first AFTER frame is still
    /// running.
    /// </summary>
    [TestMethod]
    public void NestedTriggersOff_AfterReachesInsteadOf_ButNotTheAfterBelowIt()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            ChainTrigger1,
            """
            create trigger io2 on t2 instead of insert
            as
            begin
                insert fired (name, nest) values ('io2', trigger_nestlevel());
                insert t3 (v) values (1);
            end
            """,
            "create trigger tr3 on t3 after insert as insert fired (name, nest) values ('tr3', trigger_nestlevel())",
            "exec sp_configure 'nested triggers', 0; reconfigure;",
            "insert t1 (v) values (1)");
        AreEqual("tr1:1,io2:2", FiredTrace(simulation));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t3"));
    }

    // === INSTEAD OF self-recursion ===

    /// <summary>
    /// An INSTEAD OF body's DML against its own target is processed as if the
    /// table had no INSTEAD OF trigger — it reaches the heap rather than
    /// re-entering the trigger, and <c>RECURSIVE_TRIGGERS ON</c> doesn't change
    /// that.
    /// </summary>
    [TestMethod]
    public void InsteadOf_SelfDml_ReachesHeap_EvenWithRecursiveTriggersOn()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches(
            "alter database current set recursive_triggers on",
            """
            create trigger io1 on t1 instead of insert
            as
            begin
                insert fired (name, nest) values ('io1', trigger_nestlevel());
                insert t1 (v) values (99);
            end
            """,
            "insert t1 (v) values (1)");
        AreEqual("io1:1", FiredTrace(simulation));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t1"));
    }
}
