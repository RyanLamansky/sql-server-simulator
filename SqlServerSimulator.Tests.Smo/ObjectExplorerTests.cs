using Microsoft.SqlServer.Management.Smo;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The Object-Explorer surface: SMO enumerating databases, tables, and a rich
/// table's child collections (columns / indexes / FKs / triggers), plus the
/// views and stored-procedures folders. This is the shape SSMS populates its
/// tree from.
/// </summary>
[TestClass]
public sealed class ObjectExplorerTests
{
    private static Server server = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _) => server = SmoFixture.NewServer();

    [ClassCleanup]
    public static void ClassCleanup() => server.ConnectionContext.Disconnect();

    private static Database FixtureDatabase => server.Databases[SmoFixture.DatabaseName];

    [TestMethod]
    public void Databases_ContainsFixtureDatabase()
    {
        IsTrue(server.Databases.Contains(SmoFixture.DatabaseName));
    }

    [TestMethod]
    public void Tables_CountMatchesFixture()
    {
        // Five declared tables plus the auto-created temporal history sibling
        // (EmployeeRolesHistory), which SMO lists as its own table.
        AreEqual(6, FixtureDatabase.Tables.Count);
    }

    [TestMethod]
    public void RichTable_ChildCollectionCountsMatchFixture()
    {
        var customers = FixtureDatabase.Tables["Customers", "Sales"];
        IsNotNull(customers);

        AreEqual(8, customers.Columns.Count);
        // Clustered PK + IX_Customers_Name (INCLUDE) + IX_Customers_Active (filtered).
        AreEqual(3, customers.Indexes.Count);
        AreEqual(1, customers.ForeignKeys.Count);
        AreEqual(1, customers.Triggers.Count);
    }

    [TestMethod]
    public void Views_EnumerateAndContainFixtureView()
    {
        // Enumerating .Count must not throw (exercises the sys.all_views path).
        IsGreaterThanOrEqualTo(1, FixtureDatabase.Views.Count);
        IsNotNull(FixtureDatabase.Views["CustomerSummary", "Sales"]);
    }

    [TestMethod]
    public void StoredProcedures_EnumerateAndContainFixtureProc()
    {
        IsGreaterThanOrEqualTo(1, FixtureDatabase.StoredProcedures.Count);
        IsNotNull(FixtureDatabase.StoredProcedures["GetCustomerCount", "Sales"]);
    }

    [TestMethod]
    public void ExtendedProperties_SurfaceOnTableAndColumn()
    {
        var people = FixtureDatabase.Tables["People", "Application"];
        IsNotNull(people);
        IsGreaterThanOrEqualTo(1, people.ExtendedProperties.Count);
        IsNotNull(people.ExtendedProperties["MS_Description"]);

        var fullName = people.Columns["FullName"];
        IsNotNull(fullName);
        IsGreaterThanOrEqualTo(1, fullName.ExtendedProperties.Count);
    }
}
