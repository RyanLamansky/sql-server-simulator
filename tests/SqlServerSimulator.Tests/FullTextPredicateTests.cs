using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The simulator doesn't model full-text search — apps that issue
/// CONTAINS / FREETEXT predicates or CONTAINSTABLE / FREETEXTTABLE rowset
/// functions get an explicit <see cref="NotSupportedException"/> at parse
/// time rather than a silent miss-as-match. These tests pin that
/// loud-failure behavior.
/// </summary>
[TestClass]
public sealed class FullTextPredicateTests
{
    private static Simulation BuildSim()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.doc (id int, body nvarchar(max))");
        return sim;
    }

    [TestMethod]
    public void Contains_Predicate_Raises_NotSupported()
    {
        var ex = ThrowsExactly<NotSupportedException>(() =>
            BuildSim().ExecuteScalar("select count(*) from dbo.doc where contains(body, 'hello')"));
        Contains("CONTAINS", ex.Message);
    }

    [TestMethod]
    public void FreeText_Predicate_Raises_NotSupported()
    {
        var ex = ThrowsExactly<NotSupportedException>(() =>
            BuildSim().ExecuteScalar("select count(*) from dbo.doc where freetext(body, 'hello')"));
        Contains("FREETEXT", ex.Message);
    }

    [TestMethod]
    public void ContainsTable_Rowset_Raises_NotSupported()
    {
        var ex = ThrowsExactly<NotSupportedException>(() =>
            BuildSim().ExecuteScalar("select count(*) from containstable(dbo.doc, body, 'hello') as t"));
        Contains("CONTAINSTABLE", ex.Message);
    }

    [TestMethod]
    public void FreeTextTable_Rowset_Raises_NotSupported()
    {
        var ex = ThrowsExactly<NotSupportedException>(() =>
            BuildSim().ExecuteScalar("select count(*) from freetexttable(dbo.doc, body, 'hello') as t"));
        Contains("FREETEXTTABLE", ex.Message);
    }
}
