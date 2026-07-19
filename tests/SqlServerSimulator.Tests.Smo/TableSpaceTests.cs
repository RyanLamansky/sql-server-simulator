using Microsoft.SqlServer.Management.Smo;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The Table space-usage surface — SMO's <c>DataSpaceUsed</c> / <c>IndexSpaceUsed</c>
/// properties and the Database <c>Size</c> / <c>SpaceAvailable</c> pages. These
/// are the computed properties SMO loads with their own catalog queries (not the
/// shared property-bag), each of which reads <c>master.dbo.spt_values</c> (type
/// 'E' number 1 → 8 KB page size), <c>sys.dm_db_partition_stats</c> (IndexSpaceUsed),
/// and — in a never-taken but compile-bound memory-optimized arm —
/// <c>sys.dm_db_xtp_table_memory_stats</c>. Before those surfaces were modeled
/// the whole statement failed (Msg 208) and every property errored. All values
/// must be non-negative and internally consistent.
/// </summary>
[TestClass]
public sealed class TableSpaceTests
{
    private static Server server = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _) => server = SmoFixture.NewServer();

    [ClassCleanup]
    public static void ClassCleanup() => server.ConnectionContext.Disconnect();

    private static Database FixtureDatabase => server.Databases[SmoFixture.DatabaseName];

    [TestMethod]
    public void Table_DataAndIndexSpaceUsed_RetrievableAndNonNegative()
    {
        // Customers has a clustered PK plus two nonclustered indexes, so
        // IndexSpaceUsed exercises the multi-partition dm_db_partition_stats sum.
        var customers = FixtureDatabase.Tables["Customers", "Sales"];
        IsNotNull(customers);
        IsGreaterThanOrEqualTo(0.0, (double)customers.DataSpaceUsed);
        IsGreaterThanOrEqualTo(0.0, (double)customers.IndexSpaceUsed);
    }

    [TestMethod]
    public void Table_HeapWithNoSecondaryIndex_HasZeroIndexSpace()
    {
        // People has only a clustered PK — no separate index storage, so
        // IndexSpaceUsed computes to zero (Σ used − base data = 0).
        var people = FixtureDatabase.Tables["People", "Application"];
        IsNotNull(people);
        IsGreaterThanOrEqualTo(0.0, (double)people.DataSpaceUsed);
        AreEqual(0.0, (double)people.IndexSpaceUsed);
    }

    [TestMethod]
    public void Database_SizeAndSpaceAvailable_RetrievableAndNonNegative()
    {
        var db = FixtureDatabase;
        db.Refresh();
        IsGreaterThan(0.0, db.Size);
        IsGreaterThanOrEqualTo(0.0, db.SpaceAvailable);
    }
}
