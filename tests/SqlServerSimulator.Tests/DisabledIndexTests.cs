using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER INDEX … DISABLE</c> / <c>… REBUILD</c>: disabling takes an index out
/// of service so its uniqueness stops being enforced, and REBUILD puts it back —
/// re-validating whatever accumulated meanwhile. A disabled <b>clustered</b> index
/// goes further and locks the table (Msg 8655), because on real the clustered
/// index is the table's storage. Everything here was probed against SQL Server
/// 2025 — see <c>docs/claude/indexes.md</c>.
/// </summary>
[TestClass]
public sealed class DisabledIndexTests
{

    // --- a disabled nonclustered unique index stops being enforced ---

    [TestMethod]
    public void DisabledUniqueIndex_StopsEnforcingUniqueness()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u);
            insert t values (1, 1);
            alter index ux on t disable;
            insert t values (2, 1);
            select count(*) from t
            """));

    [TestMethod]
    public void DisabledUniqueIndex_LeavesTheTableReadable()
        // Only a *clustered* disabled index locks the table; a nonclustered one
        // leaves it fully usable, including a predicate the index would have served.
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u);
            insert t values (1, 1);
            alter index ux on t disable;
            select u from t where u = 1
            """));

    [TestMethod]
    public void SysIndexes_ProjectsIsDisabled()
        => AreEqual("ux_off=0 ux_on=1", new Simulation().ExecuteScalar("""
            create table t (id int not null, a int, b int);
            create unique index ux_on on t(a);
            create unique index ux_off on t(b);
            alter index ux_on on t disable;
            select string_agg(concat(name, '=', is_disabled), ' ') within group (order by name)
            from sys.indexes where object_id = object_id('t') and name is not null
            """));

    // --- REBUILD restores enforcement, re-validating on the way ---

    [TestMethod]
    public void Rebuild_RestoresEnforcement()
        => new Simulation().AssertSqlError("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u);
            insert t values (1, 1);
            alter index ux on t disable;
            alter index ux on t rebuild;
            insert t values (2, 1)
            """, 2601);

    [TestMethod]
    public void Rebuild_WithDuplicatesAccumulatedWhileDisabled_RaisesMsg1505()
        => AreEqual(
            "The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name "
            + "'dbo.t' and the index name 'ux'. The duplicate key value is (1).",
            new Simulation().AssertSqlError("""
                create table t (id int not null, u int not null);
                create unique index ux on t(u);
                insert t values (1, 1);
                alter index ux on t disable;
                insert t values (2, 1);
                alter index ux on t rebuild
                """, 1505).Errors[0].Message);

    [TestMethod]
    public void Rebuild_AfterTheDuplicateIsRemoved_Succeeds()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u);
            insert t values (1, 1);
            alter index ux on t disable;
            insert t values (2, 1);
            delete from t where id = 2;
            alter index ux on t rebuild;
            select cast(is_disabled as int) from sys.indexes where name = 'ux'
            """));

    [TestMethod]
    public void Rebuild_OfAnIndexThatWasNeverDisabled_IsANoOp()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u);
            insert t values (1, 1);
            alter index ux on t rebuild;
            select count(*) from t
            """));

    [TestMethod]
    public void Rebuild_AcceptsPartitionAllAndAWithClause()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u);
            insert t values (1, 1);
            alter index ux on t rebuild partition = all with (online = off);
            select count(*) from t
            """));

    // --- constraint-backed indexes ---

    [TestMethod]
    public void DisabledNonclusteredUniqueConstraint_StopsEnforcing_TableStaysUsable()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key clustered, u int not null, constraint uq_t unique nonclustered (u));
            insert t values (1, 1);
            alter index uq_t on t disable;
            insert t values (2, 1);
            select count(*) from t
            """));

    [TestMethod]
    public void RebuiltUniqueConstraint_RaisesMsg2627NotMsg2601()
        // Enforcement comes back through the constraint, so the violation carries
        // the constraint's wording rather than a unique index's.
        => new Simulation().AssertSqlError("""
            create table t (id int not null primary key clustered, u int not null, constraint uq_t unique nonclustered (u));
            insert t values (1, 1);
            alter index uq_t on t disable;
            alter index uq_t on t rebuild;
            insert t values (2, 1)
            """, 2627);

    // --- a disabled clustered index locks the table ---

    [TestMethod]
    public void DisabledClusteredPrimaryKey_LocksTheTableForReads()
        => AreEqual(
            "The query processor is unable to produce a plan because the index 'pk_t' on table or view 't' is disabled.",
            new Simulation().AssertSqlError("""
                create table t (id int not null, constraint pk_t primary key (id));
                insert t values (1);
                alter index pk_t on t disable;
                select count(*) from t
                """, 8655).Message);

    [TestMethod]
    [DataRow("insert t values (2)")]
    [DataRow("update t set id = 5")]
    [DataRow("delete from t")]
    [DataRow("merge t as target using (values (9)) as source (id) on target.id = source.id when not matched then insert (id) values (source.id);")]
    public void DisabledClusteredPrimaryKey_LocksTheTableForWrites(string statement)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int not null, constraint pk_t primary key (id));
            insert t values (1);
            alter index pk_t on t disable
            """);
        var exception = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery(statement));
        AreEqual(8655, exception.Number);
    }

    [TestMethod]
    public void RebuildingTheClusteredIndex_UnlocksTheTable()
        // The lockout is recoverable, which is why DDL isn't gated.
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null, constraint pk_t primary key (id));
            insert t values (1);
            alter index pk_t on t disable;
            alter index pk_t on t rebuild;
            select count(*) from t
            """));

    [TestMethod]
    public void DisabledNonclusteredIndex_DoesNotLockTheTable()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key clustered, a int);
            create unique index ux_a on t(a);
            insert t values (1, 1);
            alter index ux_a on t disable;
            select count(*) from t
            """));

    // --- ALL, and operations a disabled index refuses ---

    [TestMethod]
    public void AlterIndexAll_Disable_TakesTheClusteredKeyWithIt()
    {
        // Every real table's PRIMARY KEY defaults clustered, so ALL DISABLE is
        // how a table usually ends up locked.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int not null primary key, a int);
            create unique index ux_a on t(a);
            insert t values (1, 1);
            alter index all on t disable
            """);
        var exception = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery("select count(*) from t"));
        AreEqual(8655, exception.Number);
        // ALL REBUILD is the way back.
        _ = simulation.ExecuteNonQuery("alter index all on t rebuild");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void SetOnADisabledIndex_RaisesMsg1973()
        => AreEqual(
            "Cannot perform the specified operation on disabled index 'ux' on table 't'.",
            new Simulation().AssertSqlError("""
                create table t (id int not null, u int);
                create unique index ux on t(u);
                alter index ux on t disable;
                alter index ux on t set (ignore_dup_key = on)
                """, 1973).Message);

    [TestMethod]
    public void ADisabledIndex_CanStillBeDropped()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int not null, u int);
            create unique index ux on t(u);
            alter index ux on t disable;
            drop index ux on t;
            select count(*) from sys.indexes where name = 'ux'
            """));

    // --- the IGNORE_DUP_KEY interaction that started this ---

    [TestMethod]
    public void DisablingAnIgnoreDupKeyIndex_StopsTheSkipEntirely()
        // While disabled there is no uniqueness check at all, so the duplicate is
        // stored rather than skipped — no Msg 3604, and the row counts.
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u) with (ignore_dup_key = on);
            insert t values (1, 1);
            alter index ux on t disable;
            insert t values (2, 1);
            select count(*) from t
            """));

    [TestMethod]
    public void RebuildingKeepsTheIgnoreDupKeySetting()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null, u int not null);
            create unique index ux on t(u) with (ignore_dup_key = on);
            insert t values (1, 1);
            alter index ux on t disable;
            alter index ux on t rebuild;
            insert t values (2, 1);
            select count(*) from t
            """));

    /// <summary>
    /// An index hint naming a disabled index is Msg 315 by name and Msg 316 by
    /// id (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void IndexHint_OnADisabledIndex_IsRefused()
    {
        const string setup = "create table t (id int primary key, a int); create index ix_a on t (a); alter index ix_a on t disable; ";
        new Simulation().AssertSqlError(
            setup + "select id from dbo.t with (index(ix_a))",
            315,
            "Index \"ix_a\" on table \"dbo.t\" (specified in the FROM clause) is disabled or resides in a filegroup which is not online.");
        new Simulation().AssertSqlError(
            setup + "select id from dbo.t with (index(2))",
            316,
            "The index ID 2 on table \"dbo.t\" (specified in the FROM clause) is disabled or resides in a filegroup which is not online.");
    }

    // --- XML, spatial and JSON indexes (probed 2026-10-20) ---

    private const string ExtendedIndexes = """
        create table t (a int not null primary key, x xml, g geometry, j json, b int);
        create index n on t (b);
        create primary xml index xp on t (x);
        create xml index xv on t (x) using xml index xp for value;
        create spatial index sp on t (g) with (bounding_box = (0, 0, 10, 10));
        create json index ji on t (j);
        """;

    private static List<string> Warnings(Simulation simulation, string sql)
    {
        var messages = new List<string>();
        using var connection = simulation.CreateOpenConnection();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static error => $"{error.Number}:{error.Message}"));
        _ = connection.CreateCommand(sql).ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    public void DisablingTheClusteredIndex_TakesEveryIndexKindWithIt()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(ExtendedIndexes);
        var nodes = simulation.ExecuteScalar("select name from sys.internal_tables where internal_type = 202");
        CollectionAssert.AreEqual(new[]
        {
            "3750:Warning: Index 'n' on table 't' was disabled as a result of disabling the clustered index on the table.",
            $"3750:Warning: Index 'xv' on table '{nodes}' was disabled as a result of disabling the clustered index on the table.",
            "3750:Warning: Index 'xp' on table 't' was disabled as a result of disabling the clustered index on the table.",
            "3750:Warning: Index 'sp' on table 't' was disabled as a result of disabling the clustered index on the table.",
            "3750:Warning: Index 'ji' on table 't' was disabled as a result of disabling the clustered index on the table.",
        }, Warnings(simulation, "alter index all on t disable"));
        AreEqual(6, simulation.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('t') and is_disabled = 1"));
        simulation.AssertSqlError("alter index n on t rebuild", 1987, "Cannot alter nonclustered index 'n' on table 't' because its clustered index is disabled.");
        simulation.AssertSqlError("alter index xp on t set (allow_row_locks = off)", 1987, "Cannot alter primary xml index 'xp' on table 't' because its clustered index is disabled.");
        simulation.AssertSqlError("alter index sp on t reorganize", 1987, "Cannot alter EXTENDED INDEX 'sp' on table 't' because its clustered index is disabled.");
        simulation.AssertSqlError("alter index ji on t rebuild", 1987, "Cannot alter JSON index 'ji' on table 't' because its clustered index is disabled.");
        simulation.AssertSqlError("alter index xv on t rebuild", 1987, "Cannot alter XML INDEX 'xv' on table 't' because its primary xml index is disabled.");
        // A spatial or JSON index keeps a rebuild of every index from running.
        _ = simulation.AssertSqlError("alter index all on t rebuild", 8655);
        AreEqual(6, simulation.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('t') and is_disabled = 1"));
        _ = simulation.ExecuteNonQuery("drop index sp on t; drop index ji on t; alter index all on t rebuild");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('t') and is_disabled = 1"));
    }

    [TestMethod]
    public void XmlIndex_TakesTheAlterIndexForms()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int not null primary key, x xml);
            insert t values (1, '<r><a>1</a></r>');
            create primary xml index xp on t (x);
            create xml index xv on t (x) using xml index xp for value;
            create xml index xh on t (x) using xml index xp for path;
            alter index xv on t disable;
            """);
        var nodes = simulation.ExecuteScalar("select name from sys.internal_tables where internal_type = 202");
        CollectionAssert.AreEqual(new[] { $"3750:Warning: Index 'xh' on table '{nodes}' was disabled as a result of disabling the clustered index on the table." },
            Warnings(simulation, "alter index xp on t disable"));
        // The documents still read, and the table still takes rows.
        AreEqual(1, simulation.ExecuteScalar("insert t values (2, '<r/>'); select x.value('(/r/a)[1]', 'int') from t where a = 1"));
        simulation.AssertSqlError("alter index xp on t reorganize", 1973, "Cannot perform the specified operation on disabled index 'xp' on table 't'.");
        // Rebuilding the primary brings back only the primary.
        AreEqual("xp:0:70:1,xv:1:0:0,xh:1:0:0", simulation.ExecuteScalar("""
            alter index xp on t rebuild with (fillfactor = 70, pad_index = on);
            select string_agg(concat(name, ':', cast(is_disabled as int), ':', fill_factor, ':', cast(is_padded as int)), ',') within group (order by index_id) from sys.indexes where object_id = object_id('t') and type = 3
            """));
        AreEqual("0", simulation.ExecuteScalar("alter index xv on t rebuild; alter index xp on t set (allow_page_locks = off); select concat(allow_page_locks, '') from sys.indexes where name = 'xp'"));
        simulation.AssertSqlError("alter index xp on t reorganize", 1943, $"The index \"xp\" on table \"{nodes}\" cannot be reorganized because page level locking is disabled.");
        simulation.AssertSqlError("alter index xp on t rebuild partition = 1", 7731, "Cannot specify partition number in Alter index statement to rebuild or reorganize a partition of XML INDEX 'xp'.");
        simulation.AssertSqlError("alter index xp on t rebuild with (online = on)", 153, "Invalid usage of the option ONLINE in the ALTER INDEX REBUILD statement.");
        simulation.AssertSqlError("alter index xv on t rebuild with (data_compression = none)", 153, "Invalid usage of the option data_compression in the ALTER INDEX REBUILD statement.");
        simulation.AssertSqlError("alter index xp on t set (optimize_for_sequential_key = on)", 153, "Invalid usage of the option OPTIMIZE_FOR_SEQUENTIAL_KEY in the ALTER INDEX SET statement.");
        _ = simulation.ExecuteNonQuery("alter index xp on t rebuild with (online = off, ignore_dup_key = off, xml_compression = on, sort_in_tempdb = on, maxdop = 1)");
    }
}
