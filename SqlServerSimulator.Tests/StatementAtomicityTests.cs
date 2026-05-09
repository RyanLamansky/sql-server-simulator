using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for SQL Server's statement-level atomicity in auto-commit mode:
/// when a single statement (multi-row INSERT, multi-row UPDATE, MERGE)
/// fails mid-execution, the partial writes are rolled back. Probe-confirmed
/// against SQL Server 2025 (2026-05-08): a multi-row INSERT whose third row
/// violates a constraint leaves zero rows behind, not two. Identity and
/// rowversion counters intentionally stay outside the rollback — they keep
/// advancing even when the writes that consumed them are undone.
/// </summary>
[TestClass]
public sealed class StatementAtomicityTests
{
    private static int CountRows(DbConnection conn, string table) =>
        (int)conn.CreateCommand($"select count(*) from {table}").ExecuteScalar()!;

    [TestMethod]
    public void MultiRowInsert_PrimaryKeyViolation_RollsBackEntireStatement()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int primary key, val int)").ExecuteNonQuery();

        // First row inserts (id=1); second row violates PK. SQL Server rolls
        // back the entire statement → 0 rows. Without statement atomicity the
        // simulator would have left (1, 100) behind.
        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("insert t values (1, 100), (1, 200), (3, 300)").ExecuteNonQuery());
        AreEqual(0, CountRows(connection, "t"));
    }

    [TestMethod]
    public void MultiRowInsert_CheckViolation_RollsBackEntireStatement()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int, val int check (val < 100))").ExecuteNonQuery();

        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("insert t values (1, 50), (2, 150), (3, 80)").ExecuteNonQuery());
        AreEqual(0, CountRows(connection, "t"));
    }

    [TestMethod]
    public void MultiRowInsert_NotNullViolation_RollsBackEntireStatement()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int, val int not null)").ExecuteNonQuery();

        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("insert t values (1, 10), (2, null), (3, 30)").ExecuteNonQuery());
        AreEqual(0, CountRows(connection, "t"));
    }

    // Setting id = id + 100 to one row at a time would fail if id=1 collides with id=101 etc.
    // Bulk update: set id = 99 for both rows where id < 3 — both target the same key.
    [TestMethod]
    public void MultiRowUpdate_KeyCollisionMidBatch_RollsBackEntireStatement()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int primary key, val int);
            insert t values (1, 10), (2, 20), (3, 30)
            """).ExecuteNonQuery();

        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("update t set id = 99 where id < 3").ExecuteNonQuery());

        // All three pre-update rows visible; no row got the new id=99.
        var rows = new List<(int, int)>();
        using var reader = connection.CreateCommand("select id, val from t order by id").ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (1, 10), (2, 20), (3, 30) }, rows);
    }

    // DELETE doesn't have constraint failures — it just tombstones rows. This test
    // confirms the undo log doesn't accidentally undo successful deletes (the log
    // discards on success).
    [TestMethod]
    public void MultiRowDelete_NeverFails_AllRowsRemoved()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int);
            insert t values (1), (2), (3);
            delete from t where id < 3
            """).ExecuteNonQuery();
        AreEqual(1, CountRows(connection, "t"));
    }

    // EF SaveChanges multi-row shape: MERGE INTO target USING (VALUES …) ... WHEN
    // NOT MATCHED THEN INSERT. A constraint violation on the second VALUES row
    // should leave zero rows in the target.
    [TestMethod]
    public void Merge_WhenNotMatchedInsert_PartialFailure_RollsBackBatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int primary key, val int);
            insert t values (5, 500)
            """).ExecuteNonQuery();

        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand(
                "merge into t using (values (1, 10), (5, 50), (3, 30)) as src(id, val) " +
                "on t.id = src.id " +
                "when not matched then insert (id, val) values (src.id, src.val);").ExecuteNonQuery());

        // The PK collision on (5, 50) should roll back the (1, 10) insert too.
        var rows = new List<(int, int)>();
        using var reader = connection.CreateCommand("select id, val from t order by id").ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { (5, 500) }, rows);
    }

    // Probe-confirmed against SQL Server 2025: identity advances even when the
    // inserts that consumed values are rolled back.
    [TestMethod]
    public void IdentityCounter_AdvancesEvenOnRollback()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int identity(1,1) primary key, val int check (val < 100));
            insert t (val) values (50)
            """).ExecuteNonQuery();

        // Second insert violates CHECK; statement rolls back. Identity should
        // still have advanced (the value 2 is "consumed" and gapped).
        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("insert t (val) values (200)").ExecuteNonQuery());

        // Third insert: id should be 3, not 2 (matching SQL Server's gap behavior).
        _ = connection.CreateCommand("insert t (val) values (60)").ExecuteNonQuery();
        var ids = new List<int>();
        using var reader = connection.CreateCommand("select id from t order by id").ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids);
    }

    // Same probe-confirmed semantic for rowversion: the database-scoped counter
    // advances even when the inserts are rolled back.
    [TestMethod]
    public void RowVersion_AdvancesEvenOnRollback()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int primary key, rv rowversion);
            insert t (id) values (1)
            """).ExecuteNonQuery();
        var rv1 = (byte[])connection.CreateCommand("select rv from t where id = 1").ExecuteScalar()!;

        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("insert t values (1, 0x0000000000000000)").ExecuteNonQuery());

        _ = connection.CreateCommand("insert t (id) values (3)").ExecuteNonQuery();
        var rv3 = (byte[])connection.CreateCommand("select rv from t where id = 3").ExecuteScalar()!;

        // Big-endian 8-byte counter; rv3 should be > rv1 + 1 (rolled-back insert consumed a value).
        var v1 = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(rv1);
        var v3 = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(rv3);
        IsGreaterThan(v1 + 1, v3, $"Expected gap in rowversion (v1={v1}, v3={v3}); rolled-back insert should consume a value.");
    }

    // After a rolled-back statement, subsequent statements still work — the heap
    // is in a consistent state.
    [TestMethod]
    public void RolledBackInsert_DoesNotPersist_ButTableRemainsUsable()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int primary key);
            insert t values (1)
            """).ExecuteNonQuery();

        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("insert t values (2), (1), (3)").ExecuteNonQuery());

        // A subsequent legitimate insert succeeds.
        _ = connection.CreateCommand("insert t values (4)").ExecuteNonQuery();
        var ids = new List<int>();
        using var reader = connection.CreateCommand("select id from t order by id").ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 4 }, ids);
    }
}
