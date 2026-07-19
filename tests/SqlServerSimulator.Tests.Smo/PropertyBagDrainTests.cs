using Microsoft.SqlServer.Management.Smo;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The SMO property-bag drain — the exact operation the API sweep runs and the
/// dominant source of its engine errors. SMO loads an object's default-init
/// property set with a single catalog query; one missing column fails that
/// whole bag query (Msg 207 Invalid column name) and every property of every
/// object of that type errors. These tests drain the bag for a Table,
/// StoredProcedure, and Sequence and assert no property surfaces the
/// missing-column failure, plus that the previously-absent columns
/// (<c>sys.tables.is_replicated</c> → [Replicated],
/// <c>sys.procedures.is_auto_executed</c> → [Startup],
/// <c>sys.sequences.precision</c>/<c>scale</c> → [NumericPrecision]/[NumericScale])
/// read their probed values.
/// </summary>
[TestClass]
public sealed class PropertyBagDrainTests
{
    private static Server server = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _) => server = SmoFixture.NewServer();

    [ClassCleanup]
    public static void ClassCleanup() => server.ConnectionContext.Disconnect();

    private static Database FixtureDatabase => server.Databases[SmoFixture.DatabaseName];

    [TestMethod]
    public void Table_PropertyBag_DrainsWithoutMissingColumn()
    {
        var customers = FixtureDatabase.Tables["Customers", "Sales"];
        IsNotNull(customers);
        var badColumns = DrainForMissingColumns(customers);
        IsEmpty(badColumns);
        IsFalse((bool)customers.Properties["Replicated"].Value!);
    }

    [TestMethod]
    public void StoredProcedure_PropertyBag_DrainsWithoutMissingColumn()
    {
        var proc = FixtureDatabase.StoredProcedures["GetCustomerCount", "Sales"];
        IsNotNull(proc);
        var badColumns = DrainForMissingColumns(proc);
        IsEmpty(badColumns);
        IsFalse((bool)proc.Properties["Startup"].Value!);
    }

    [TestMethod]
    public void Sequence_PropertyBag_DrainsWithoutMissingColumn()
    {
        var sequence = FixtureDatabase.Sequences["OrderNumber", "Sales"];
        IsNotNull(sequence);
        var badColumns = DrainForMissingColumns(sequence);
        IsEmpty(badColumns);
        // int-typed sequence → precision 10, scale 0.
        AreEqual(10, (int)sequence.Properties["NumericPrecision"].Value!);
        AreEqual(0, (int)sequence.Properties["NumericScale"].Value!);
    }

    /// <summary>
    /// Drains every property's <c>Value</c> (the sweep's exact operation) and
    /// returns the names of any that surface a missing-column error — the
    /// signature of a failed property-bag query. Unrelated failures (an
    /// unmodeled DMV behind an expensive computed property loaded by its own
    /// query, e.g. Table.DataSpaceUsed) are intentionally tolerated: only the
    /// shared bag query is under test here.
    /// </summary>
    private static List<string> DrainForMissingColumns(SqlSmoObject smoObject)
    {
        var badColumns = new List<string>();
        var props = smoObject.Properties;
        for (var i = 0; i < props.Count; i++)
        {
            Property property;
            try { property = props.GetPropertyObject(i, true); }
            catch { continue; }
            try { _ = props[i].Value; }
            catch (Exception ex) when (IsMissingColumn(ex)) { badColumns.Add(property.Name); }
            catch { /* unmodeled-feature failures are not this test's concern */ }
        }
        return badColumns;
    }

    private static bool IsMissingColumn(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("Invalid column name", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
