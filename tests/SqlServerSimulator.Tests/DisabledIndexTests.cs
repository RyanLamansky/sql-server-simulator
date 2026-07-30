using System.Data.Common;
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
    private static string ErrorNumber(DbException exception) => (string)exception.Data["HelpLink.EvtID"]!;

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
                """, 1505).Message);

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
        var exception = Throws<DbException>(() => simulation.ExecuteNonQuery(statement));
        AreEqual("8655", ErrorNumber(exception));
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
        var exception = Throws<DbException>(() => simulation.ExecuteNonQuery("select count(*) from t"));
        AreEqual("8655", ErrorNumber(exception));
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
}
