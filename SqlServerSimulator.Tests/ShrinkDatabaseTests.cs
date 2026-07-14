using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-surface tests for <c>DBCC SHRINKDATABASE</c> / <c>DBCC SHRINKFILE</c>.
/// The commands reclaim memory by trimming dead trailing pages; these assert the
/// observable contract — SHRINKDATABASE returns no result set, SHRINKFILE returns
/// the probe-confirmed per-file report row, the statements leave data intact, and
/// an unknown database name raises Msg 2520. The high-water-mark drop itself is
/// covered by <c>ShrinkTests</c> in the internal suite (it needs <c>Heap</c> access).
/// </summary>
[TestClass]
public sealed class ShrinkDatabaseTests
{
    [TestMethod]
    public void ShrinkFile_ReturnsDocumentedReportRow()
    {
        using var reader = new Simulation().ExecuteReader("dbcc shrinkfile (1, 0)");

        AreEqual(6, reader.FieldCount);
        AreEqual("DbId", reader.GetName(0));
        AreEqual("FileId", reader.GetName(1));
        AreEqual("CurrentSize", reader.GetName(2));
        AreEqual("MinimumSize", reader.GetName(3));
        AreEqual("UsedPages", reader.GetName(4));
        AreEqual("EstimatedPages", reader.GetName(5));
        AreEqual(typeof(short), reader.GetFieldType(0));
        AreEqual(typeof(int), reader.GetFieldType(1));

        IsTrue(reader.Read());
        // database_id 5: the simulated user database (master is 1).
        AreEqual((short)5, reader.GetInt16(0));
        AreEqual(1, reader.GetInt32(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void ShrinkDatabase_ReturnsNoResultSet()
    {
        using var reader = new Simulation().ExecuteReader("dbcc shrinkdatabase (simulated)");
        AreEqual(0, reader.FieldCount);
        IsFalse(reader.HasRows);
    }

    [TestMethod]
    public void ShrinkDatabase_RunsAndPreservesData()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, v varchar(7000) not null);
            insert t values (1, replicate('a', 7000)), (2, replicate('b', 7000));
            delete from t where id = 2;
            dbcc shrinkdatabase (simulated)
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
        AreEqual("aaaa", (string)sim.ExecuteScalar("select left(v, 4) from t where id = 1")!);
        // Reuse after a shrink still works.
        _ = sim.ExecuteNonQuery("insert t values (3, replicate('c', 7000))");
        AreEqual(2, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void ShrinkDatabase_WithNoInfoMsgs_Parses()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int);
            insert t values (1);
            dbcc shrinkdatabase (simulated) with no_infomsgs;
            select count(*) from t
            """));

    [TestMethod]
    public void ShrinkFile_WithTruncateOnlyOption_Parses()
    {
        // SHRINKFILE emits its report row even mid-batch, so drive it through
        // ExecuteNonQuery (which ignores result sets) and assert data intact.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int);
            insert t values (1);
            dbcc shrinkfile (1, 0, truncateonly)
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void ShrinkDatabase_UnknownName_Msg2520()
    {
        var ex = new Simulation().AssertSqlError("dbcc shrinkdatabase (no_such_db)", 2520);
        Contains("Could not find database 'no_such_db'", ex.Message);
    }
}
