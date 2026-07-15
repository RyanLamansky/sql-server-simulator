using Microsoft.SqlServer.Management.Smo;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SMO's Script-As → CREATE: the definition text SSMS generates from the
/// simulator's catalog surface. Assertions target the load-bearing lines
/// (StringAssert.Contains), not a brittle full-text match — the exact
/// whitespace / index-option boilerplate is SMO's, not the simulator's.
/// </summary>
[TestClass]
public sealed class ScriptAsTests
{
    private static Server server = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _) => server = SmoFixture.NewServer();

    [ClassCleanup]
    public static void ClassCleanup() => server.ConnectionContext.Disconnect();

    private static Database FixtureDatabase => server.Databases[SmoFixture.DatabaseName];

    private static string Script(Table table, ScriptingOptions options) =>
        string.Join('\n', table.Script(options).Cast<string>());

    [TestMethod]
    public void RichTable_CreateScript_ContainsLoadBearingLines()
    {
        var customers = FixtureDatabase.Tables["Customers", "Sales"];
        IsNotNull(customers);

        var script = Script(customers, new ScriptingOptions
        {
            DriAll = true,
            Indexes = true,
            Triggers = true,
            ExtendedProperties = true,
        });

        // Schema-qualified CREATE TABLE header.
        Assert.Contains("CREATE TABLE [Sales].[Customers]", script);
        // A representative column with type + nullability.
        Assert.Contains("[CustomerName] [nvarchar](100)", script);
        Assert.Contains("NOT NULL", script);
        // Identity clause.
        Assert.Contains("IDENTITY(1,1)", script);
        // Primary key.
        Assert.Contains("CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED", script);
        // Foreign key with its references clause (cross-schema).
        Assert.Contains("CONSTRAINT [FK_Customers_People] FOREIGN KEY", script);
        Assert.Contains("REFERENCES [Application].[People] ([PersonID])", script);
        // Nonclustered index including its INCLUDE list.
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Customers_Name]", script);
        Assert.Contains("INCLUDE([CreditLimit])", script);
        // Named default constraint.
        Assert.Contains("CONSTRAINT [DF_Customers_Discount]", script);
        Assert.Contains("DEFAULT", script);
        // Check constraint.
        Assert.Contains("CONSTRAINT [CK_Customers_CreditLimit]", script);
        Assert.Contains("CHECK", script);
    }

    [TestMethod]
    public void TemporalTable_CreateScript_EmitsSystemVersioning()
    {
        var employeeRoles = FixtureDatabase.Tables["EmployeeRoles", "Application"];
        IsNotNull(employeeRoles);

        var script = Script(employeeRoles, new ScriptingOptions { DriAll = true });

        Assert.Contains("GENERATED ALWAYS AS ROW START", script);
        Assert.Contains("GENERATED ALWAYS AS ROW END", script);
        Assert.Contains("PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])", script);
        Assert.Contains("SYSTEM_VERSIONING = ON", script);
        Assert.Contains("HISTORY_TABLE = [Application].[EmployeeRolesHistory]", script);
    }
}
