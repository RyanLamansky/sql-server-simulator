using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>TRUNCATE TABLE</c>: row removal, identity-counter reset,
/// transaction rollback semantics (rollback restores both rows AND the
/// pre-truncate identity counter — distinct from the simulator's general
/// "identity bypasses the log" rule that applies to INSERT). Errors and
/// skip-mode interaction. Probed against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class TruncateTableTests
{
    [TestMethod]
    public void Truncate_RemovesAllRows()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1), (2), (3);
            truncate table t;
            select count(*) from t
            """));

    [TestMethod]
    public void Truncate_ResetsIdentityToSeed()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int identity(1,1), v int);
            insert t (v) values (10), (20), (30);
            truncate table t;
            insert t (v) values (99);
            select id from t
            """));

    /// <summary>
    /// Custom seed (8, 3) — first insert after TRUNCATE gets the seed (8),
    /// not the next-after-prior-max. Probe-confirmed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void Truncate_ResetsIdentityToCustomSeed()
        => AreEqual(8, new Simulation().ExecuteScalar("""
            create table t (id int identity(8, 3), v int);
            insert t (v) values (10), (20);
            truncate table t;
            insert t (v) values (99);
            select id from t
            """));

    [TestMethod]
    public void Truncate_OnEmptyTable_Succeeds()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int);
            truncate table t;
            select count(*) from t
            """));

    [TestMethod]
    public void Truncate_OnTempTable_Works()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table #foo (id int);
            insert #foo values (1), (2);
            truncate table #foo;
            select count(*) from #foo
            """));

    [TestMethod]
    public void Truncate_ThreePartName_Resolves()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1);
            truncate table simulated.dbo.t;
            select count(*) from t
            """));

    [TestMethod]
    public void Truncate_SetsRowCountToZero()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int);
            insert t values (1), (2), (3);
            truncate table t;
            select @@rowcount as rc
            """);
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    [TestMethod]
    public void Truncate_NonExistentTable_Msg4701()
        => new Simulation().AssertSqlError(
            "truncate table does_not_exist",
            4701,
            "Cannot find the object \"does_not_exist\" because it does not exist or you do not have permissions.");

    [TestMethod]
    public void Truncate_NonExistentTempTable_Msg4701()
        => new Simulation().AssertSqlError(
            "truncate table #does_not_exist",
            4701);

    [TestMethod]
    public void Truncate_WithWhere_SyntaxError()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("""
            create table t (id int);
            truncate table t where id = 1
            """));
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
    }

    // ---- Transaction rollback ----
    // Probe-confirmed: ROLLBACK after TRUNCATE inside BEGIN TRAN restores
    // both the row data AND the identity counter.

    [TestMethod]
    public void Truncate_InTransaction_RollbackRestoresRows()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1), (2), (3);
            begin tran;
            truncate table t;
            rollback;
            select count(*) from t
            """));

    [TestMethod]
    public void Truncate_InTransaction_RollbackRestoresIdentity()
    {
        // After ROLLBACK, identity counter is back to where it was — the
        // next INSERT after re-inserting still continues from the prior
        // high-water mark.
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int identity(1,1), v int);
            insert t (v) values (10), (20), (30);
            begin tran;
            truncate table t;
            rollback;
            insert t (v) values (99);
            select id from t order by id
            """);
        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, ids);
    }

    [TestMethod]
    public void Truncate_InTransaction_Commit_TruncationPersists()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1), (2), (3);
            begin tran;
            truncate table t;
            commit;
            select count(*) from t
            """));

    [TestMethod]
    public void Truncate_InTransaction_InsertThenRollback_FullRestore()
    {
        // ROLLBACK undoes both the post-TRUNCATE INSERT and the TRUNCATE
        // itself (LIFO: INSERT undo first, then TRUNCATE undo). Final state:
        // the original three rows.
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int);
            insert t values (1), (2), (3);
            begin tran;
            truncate table t;
            insert t values (99);
            rollback;
            select id from t order by id
            """);
        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ids);
    }

    // ---- Skip-mode ----

    [TestMethod]
    public void Truncate_InUntakenIf_TableUntouched()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1), (2), (3);
            if 1=0 truncate table t;
            select count(*) from t
            """));

    [TestMethod]
    public void Truncate_InUntakenIf_MissingTableNotChecked()
    {
        // Skip-mode gates the name resolution too — a TRUNCATE in an
        // un-taken branch against a non-existent table doesn't surface
        // Msg 4701.
        _ = new Simulation().ExecuteNonQuery("if 1=0 truncate table does_not_exist");
    }
}
