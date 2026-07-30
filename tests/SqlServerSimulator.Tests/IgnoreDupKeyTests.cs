using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>IGNORE_DUP_KEY</c>: an INSERT whose row would duplicate the key drops that
/// row and the statement carries on, instead of raising Msg 2601 / 2627. Covers
/// where the option may be declared, where real refuses it, the severity-0
/// Msg 3604 that rides the info-message stream once per statement, the paths that
/// keep raising (UPDATE, MERGE), <c>sys.indexes.ignore_dup_key</c>, and
/// <c>ALTER INDEX … SET</c>. Every behavior here was probed against SQL Server
/// 2025 — see <c>docs/claude/constraints.md</c>.
/// </summary>
[TestClass]
public sealed class IgnoreDupKeyTests
{
    /// <summary>
    /// Runs <paramref name="commandText"/> and returns the info messages it
    /// raised, so the once-per-statement Msg 3604 can be counted rather than
    /// merely detected.
    /// </summary>
    private static List<SimulatedError> InfoMessagesFrom(Simulation simulation, string setup, string commandText)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using (var setupCommand = connection.CreateCommand())
        {
            setupCommand.CommandText = setup;
            _ = setupCommand.ExecuteNonQuery();
        }

        var errors = new List<SimulatedError>();
        connection.InfoMessage += (_, e) => errors.AddRange(e.Errors);
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        _ = command.ExecuteNonQuery();
        return errors;
    }

    private static string ErrorNumber(DbException exception) => (string)exception.Data["HelpLink.EvtID"]!;

    // --- the skip itself ---

    [TestMethod]
    public void UniqueIndex_DuplicateRow_IsSkippedAndTheRestInserted()
        => AreEqual("1,2,3", new Simulation().ExecuteScalar("""
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            insert t values (1);
            insert t values (2),(1),(3);
            select string_agg(cast(id as varchar(9)), ',') from t
            """));

    [TestMethod]
    public void SkippedRow_IsExcludedFromRowCount()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            insert t values (1)
            """);
        // Two of the three offered rows land, so both the rows-affected the
        // client sees and @@ROWCOUNT report 2 — probe-confirmed.
        AreEqual(2, simulation.ExecuteNonQuery("insert t values (2),(1),(3)"));
        AreEqual(2, simulation.ExecuteScalar("insert t values (4),(1),(5); select @@rowcount"));
    }

    [TestMethod]
    public void SkippedRow_LeavesErrorNumberZero()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            insert t values (1);
            insert t values (1);
            select @@error
            """));

    [TestMethod]
    public void DuplicateWithinTheValuesList_IsSkipped()
        => AreEqual("1,2", new Simulation().ExecuteScalar("""
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            insert t values (1),(1),(2);
            select string_agg(cast(id as varchar(9)), ',') from t
            """));

    [TestMethod]
    public void InsertSelect_SkipsDuplicates()
        => AreEqual("1,2,3,4,5", new Simulation().ExecuteScalar("""
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            insert t values (1),(2);
            insert t select value from generate_series(1, 5);
            select string_agg(cast(id as varchar(9)), ',') from t
            """));

    [TestMethod]
    public void SecondNull_IsSkipped()
        // UNIQUE treats NULLs as equal, so the second NULL is the duplicate the
        // option drops rather than raises on.
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            insert t values (null);
            insert t values (null),(1);
            select count(*) from t
            """));

    [TestMethod]
    public void SkippedRow_ProducesNoOutputRow()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            declare @seen table (id int);
            insert t values (1);
            insert t output inserted.id into @seen values (2),(1),(3);
            select count(*) from @seen
            """));

    [TestMethod]
    public void SkippedRow_StillConsumesItsIdentityValue()
        // Probe-confirmed: the dropped row burns its identity value, so the
        // surviving rows are 1 and 3 rather than 1 and 2.
        => AreEqual("1:1,3:2", new Simulation().ExecuteScalar("""
            create table t (id int identity(1,1), u int not null);
            create unique index ux on t(u) with (ignore_dup_key = on);
            insert t (u) values (1);
            insert t (u) values (1),(2);
            select string_agg(concat(id, ':', u), ',') within group (order by id) from t
            """));

    [TestMethod]
    public void SkippedRow_DoesNotReachAnAfterTrigger()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table t (u int not null);
            create table audit (u int not null);
            create unique index ux on t(u) with (ignore_dup_key = on);
            insert t values (1)
            """,
            "create trigger tr_t on t after insert as insert audit (u) select u from inserted");
        AreEqual(2, simulation.ExecuteScalar("""
            insert t values (1),(2);
            select u from audit
            """));
    }

    // --- Msg 3604, once per statement ---

    [TestMethod]
    public void ThreeSkippedRows_RaiseOneMessage()
    {
        var errors = InfoMessagesFrom(
            new Simulation(),
            """
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            insert t values (1),(2),(3)
            """,
            "insert t values (1),(2),(3),(4)");
        HasCount(1, errors);
        AreEqual(3604, errors[0].Number);
        AreEqual("Duplicate key was ignored.", errors[0].Message);
        // Severity 0, not the 10 an ordinary informational RAISERROR carries.
        AreEqual(0, errors[0].Class);
        AreEqual(0, errors[0].State);
    }

    [TestMethod]
    public void NoSkippedRow_RaisesNoMessage()
    {
        var errors = InfoMessagesFrom(
            new Simulation(),
            """
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on)
            """,
            "insert t values (7),(8)");
        IsEmpty(errors);
    }

    // --- where the option may be declared ---

    [TestMethod]
    public void PrimaryKeyConstraint_HonorsTheOption()
        => AreEqual("1,2", new Simulation().ExecuteScalar("""
            create table t (id int not null, constraint pk_t primary key (id) with (ignore_dup_key = on));
            insert t values (1);
            insert t values (1),(2);
            select string_agg(cast(id as varchar(9)), ',') from t
            """));

    [TestMethod]
    public void UniqueConstraint_HonorsTheOption()
        => AreEqual("1,2", new Simulation().ExecuteScalar("""
            create table t (id int, constraint uq_t unique (id) with (ignore_dup_key = on));
            insert t values (1);
            insert t values (1),(2);
            select string_agg(cast(id as varchar(9)), ',') from t
            """));

    [TestMethod]
    public void InlinePrimaryKey_AcceptsAndHonorsTheOption()
        // The inline column form takes the same WITH clause as the table-level
        // one — real accepts it, and the simulator used to raise Msg 102.
        => AreEqual("1,2", new Simulation().ExecuteScalar("""
            create table t (id int not null primary key with (ignore_dup_key = on));
            insert t values (1);
            insert t values (1),(2);
            select string_agg(cast(id as varchar(9)), ',') from t
            """));

    [TestMethod]
    public void InlineUnique_AcceptsAndHonorsTheOption()
        => AreEqual("1,2", new Simulation().ExecuteScalar("""
            create table t (id int unique with (ignore_dup_key = on));
            insert t values (1);
            insert t values (1),(2);
            select string_agg(cast(id as varchar(9)), ',') from t
            """));

    [TestMethod]
    public void AlterTableAddConstraint_HonorsTheOption()
        => AreEqual("1,2", new Simulation().ExecuteScalar("""
            create table t (id int not null);
            alter table t add constraint uq_t unique (id) with (ignore_dup_key = on);
            insert t values (1);
            insert t values (1),(2);
            select string_agg(cast(id as varchar(9)), ',') from t
            """));

    [TestMethod]
    public void OptionOff_KeepsRaising()
        => new Simulation().AssertSqlError("""
            create table t (id int not null, constraint pk_t primary key (id) with (ignore_dup_key = off));
            insert t values (1);
            insert t values (1)
            """, 2627);

    [TestMethod]
    public void OptionAppliesPerIndex_NotPerTable()
    {
        // A lenient index and a strict one on the same table: a row duplicating
        // only the lenient key is dropped, one duplicating the strict key raises.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int);
            create unique index ux_a on t(a) with (ignore_dup_key = on);
            create unique index ux_b on t(b);
            insert t values (1, 1)
            """);
        AreEqual(0, simulation.ExecuteNonQuery("insert t values (1, 2)"));
        var exception = Throws<DbException>(() => simulation.ExecuteNonQuery("insert t values (2, 1)"));
        AreEqual("2601", ErrorNumber(exception));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t"));
    }

    // --- paths that keep raising ---

    [TestMethod]
    public void Update_IntoDuplicate_StillRaises()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            insert t values (1),(2);
            update t set id = 1 where id = 2
            """, 2601);

    [TestMethod]
    public void Merge_NotMatchedInsert_StillRaises()
        // The downgrade is the INSERT statement's alone: MERGE's insert action
        // raises Msg 2601 on real even against an IGNORE_DUP_KEY index.
        => new Simulation().AssertSqlError("""
            create table t (id int, v int);
            create unique index ux on t(id) with (ignore_dup_key = on);
            insert t values (1, 10);
            merge t as target using (values (1, 99), (2, 20)) as source (id, v) on target.v = source.v
            when not matched then insert (id, v) values (source.id, source.v);
            """, 2601);

    // --- declarations real refuses ---

    [TestMethod]
    public void NonUniqueIndex_RaisesMsg1916()
        => AreEqual(
            "CREATE INDEX options nonunique and ignore_dup_key are mutually exclusive.",
            new Simulation().AssertSqlError("""
                create table t (id int);
                create index ix on t(id) with (ignore_dup_key = on)
                """, 1916).Message);

    [TestMethod]
    public void NonUniqueIndex_RejectionPrecedesNameResolution()
        // Probe-confirmed: Msg 1916 is a statement-shape check that fires ahead
        // of the missing-table error a bare CREATE INDEX would give.
        => _ = new Simulation().AssertSqlError("create index ix on nosuchtable(id) with (ignore_dup_key = on)", 1916);

    [TestMethod]
    public void FilteredUniqueIndex_RaisesMsg10618()
        => AreEqual(
            "Cannot create filtered index 'ux' on table 't' because the statement sets the IGNORE_DUP_KEY option to ON. "
            + "Rewrite the statement so that it does not use the IGNORE_DUP_KEY option.",
            new Simulation().AssertSqlError("""
                create table t (id int);
                create unique index ux on t(id) where id > 0 with (ignore_dup_key = on)
                """, 10618).Message);

    [TestMethod]
    public void IndexedView_RaisesMsg1990()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table src (id int not null, v int not null)",
            "create view vw with schemabinding as select id, count_big(*) as c from dbo.src group by id");
        AreEqual(
            "Cannot define an index on a view with ignore_dup_key index option. Remove ignore_dup_key option and "
            + "verify that view definition does not allow duplicates, or do not index view.",
            simulation.AssertSqlError("create unique clustered index ux_vw on vw(id) with (ignore_dup_key = on)", 1990).Message);
    }

    // --- sys.indexes ---

    [TestMethod]
    public void SysIndexes_ProjectsTheFlag()
        => AreEqual("ix_plain=0 pk_t=1 ux_lenient=1 ux_strict=0", new Simulation().ExecuteScalar("""
            create table t (id int not null, a int, b int, c int, constraint pk_t primary key (id) with (ignore_dup_key = on));
            create unique index ux_lenient on t(a) with (ignore_dup_key = on);
            create unique index ux_strict on t(b);
            create index ix_plain on t(c);
            select string_agg(concat(name, '=', ignore_dup_key), ' ') within group (order by name)
            from sys.indexes where object_id = object_id('t') and name is not null
            """));

    // --- ALTER INDEX … SET ---

    [TestMethod]
    public void AlterIndexSet_TurnsTheOptionOn()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u);
            alter index ux on t set (ignore_dup_key = on);
            insert t values (1, 1);
            insert t values (2, 1);
            select count(*) from t
            """));

    [TestMethod]
    public void AlterIndexSet_TurnsTheOptionOffAgain()
        => new Simulation().AssertSqlError("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u) with (ignore_dup_key = on);
            alter index ux on t set (ignore_dup_key = off);
            insert t values (1, 1);
            insert t values (2, 1)
            """, 2601);

    [TestMethod]
    public void AlterIndexSet_MultipleOptions_ReadsOnlyTheOneThatMatters()
        // FILLFACTOR is a reserved keyword where the other option names are
        // ordinary identifiers, so the list has to read names off raw source.
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u);
            alter index ux on t set (allow_row_locks = on, ignore_dup_key = on, statistics_norecompute = off, fillfactor = 70);
            select ignore_dup_key from sys.indexes where name = 'ux'
            """)!);

    [TestMethod]
    public void AlterIndexSet_NonUniqueIndex_RaisesMsg1915()
        // A different number and wording from CREATE's Msg 1916, both probed.
        => AreEqual(
            "Cannot alter a non-unique index with ignore_dup_key index option. Index 'ix' is non-unique.",
            new Simulation().AssertSqlError("""
                create table t (id int, n int);
                create index ix on t(n);
                alter index ix on t set (ignore_dup_key = on)
                """, 1915).Message);

    [TestMethod]
    public void AlterIndexSet_FilteredIndex_RaisesMsg10618WithAlterVerb()
        => AreEqual(
            "Cannot alter filtered index 'ux' on table 't' because the statement sets the IGNORE_DUP_KEY option to ON. "
            + "Rewrite the statement so that it does not use the IGNORE_DUP_KEY option.",
            new Simulation().AssertSqlError("""
                create table t (id int, f int);
                create unique index ux on t(f) where f > 0;
                alter index ux on t set (ignore_dup_key = on)
                """, 10618).Message);

    [TestMethod]
    public void AlterIndexSet_ConstraintBackedIndex_RaisesMsg1979()
        // Real accepts the option in a constraint's own declaration but refuses
        // to change it afterwards.
        => AreEqual(
            "Cannot use index option ignore_dup_key to alter index 'pk_t' as it enforces a primary or unique constraint.",
            new Simulation().AssertSqlError("""
                create table t (id int not null, constraint pk_t primary key (id));
                alter index pk_t on t set (ignore_dup_key = on)
                """, 1979).Message);

    [TestMethod]
    public void AlterIndexAll_AbortsOnTheConstraintBackedIndex()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null, u int, constraint pk_t primary key (id));
            create unique index ux on t(u);
            alter index all on t set (ignore_dup_key = on)
            """, 1979);

    [TestMethod]
    public void AlterIndexAll_WithoutTheOption_SucceedsOverConstraints()
        // A SET that never mentions IGNORE_DUP_KEY has nothing to refuse, so ALL
        // sweeps a constraint-bearing table cleanly and leaves the flags alone.
        => AreEqual("pk_t=0 ux=0", new Simulation().ExecuteScalar("""
            create table t (id int not null, u int, constraint pk_t primary key (id));
            create unique index ux on t(u);
            alter index all on t set (allow_row_locks = on);
            select string_agg(concat(name, '=', ignore_dup_key), ' ') within group (order by name)
            from sys.indexes where object_id = object_id('t') and name is not null
            """));

    [TestMethod]
    public void AlterIndex_MissingIndex_RaisesMsg2727()
        => AreEqual("Cannot find index 'nope'.", new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            alter index nope on t set (ignore_dup_key = on)
            """, 2727).Message);

    [TestMethod]
    public void AlterIndex_MissingTable_RaisesMsg1088()
        => AreEqual(
            "Cannot find the object \"nosuchtable\" because it does not exist or you do not have permissions.",
            new Simulation().AssertSqlError("alter index ux on nosuchtable set (ignore_dup_key = on)", 1088).Message);

    [TestMethod]
    public void AlterIndex_UnknownOption_RaisesMsg155()
        => AreEqual(
            "'no_such_option' is not a recognized ALTER INDEX option.",
            new Simulation().AssertSqlError("""
                create table t (id int not null primary key);
                alter index t on t set (no_such_option = on)
                """, 155).Message);

    [TestMethod]
    public void AlterIndex_NonOnOffValue_RaisesMsg102()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null, u int);
            create unique index ux on t(u);
            alter index ux on t set (ignore_dup_key = maybe)
            """, 102);

    [TestMethod]
    public void AlterIndex_EmptyOptionList_RaisesMsg102()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null, u int);
            create unique index ux on t(u);
            alter index ux on t set ()
            """, 102);

    [TestMethod]
    public void AlterIndex_ReorganizeForm_IsNotModeled()
    {
        // SET / DISABLE / REBUILD ship (the latter two in DisabledIndexTests);
        // the rest of the ALTER INDEX grammar doesn't.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int not null, u int);
            create unique index ux on t(u)
            """);
        var exception = Throws<NotSupportedException>(() => simulation.ExecuteNonQuery("alter index ux on t reorganize"));
        Contains("REORGANIZE", exception.Message);
    }
}
