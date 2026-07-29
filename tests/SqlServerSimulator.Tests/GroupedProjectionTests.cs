using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Grouped-query resolution and DISTINCT interaction. The recurring defect
/// here was qualifier-blindness: a name was matched on its leaf alone, so a
/// join bringing a same-named column into scope silently bound the wrong one
/// — no error, just wrong values or wrong order. Expected values are what
/// SQL Server 2025 returned for the same statement.
/// </summary>
[TestClass]
public sealed class GroupedProjectionTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table bk (id int, name varchar(40), pub_id int, price decimal(9,2))",
            "create table pb (id int, name varchar(40))",
            "insert pb values (1,'Apress'), (2,'Zeta')",
            "insert bk values (1,'Guide',1,30.00), (2,'Other',2,10.00)");
        return sim;
    }

    private static List<string> Rows(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var rows = new List<string>();
        while (reader.Read())
        {
            var parts = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                parts.Add($"{reader.GetValue(i)}");
            rows.Add(string.Join("|", parts));
        }

        return rows;
    }

    /// <summary>
    /// A grouped projection reads the qualified column it names. Matching on
    /// the leaf made `pb.name` bind to the `bk.name` grouping key.
    /// </summary>
    [TestMethod]
    public void GroupedProjection_QualifiedColumn_BindsToItsOwnTable()
    {
        var sim = Seeded();
        AreEqual(
            "Guide|Apress",
            string.Join(",", Rows(sim,
                "select bk.name, pb.name as publisher_name "
                + "from bk join pb on bk.pub_id = pb.id where bk.id = 1 "
                + "group by bk.id, bk.name, pb.name")));

        // Also with an aggregate present, which is the shape an ORM annotate
        // produces (the aggregate's own value is asserted elsewhere).
        AreEqual(
            "Guide|Apress",
            string.Join(",", Rows(sim,
                "select bk.name, pb.name as publisher_name "
                + "from bk join pb on bk.pub_id = pb.id where bk.id = 1 "
                + "group by bk.id, bk.name, pb.name having avg(bk.price) > 0")));
    }

    /// <summary>
    /// The same rule in a grouped ORDER BY: ordering by the joined table's
    /// column must not bind to the projected column of the same leaf name.
    /// </summary>
    [TestMethod]
    public void GroupedOrderBy_QualifiedTerm_BindsToItsOwnTable()
    {
        var sim = Seeded();
        // Publisher order is Apress, Zeta → Guide, Other. Binding to bk.name
        // would give Guide, Other as well, so the fixture makes them differ:
        // by bk.name it is Guide, Other; by pb.name it is Guide, Other too —
        // so order descending to separate them.
        AreEqual(
            "Other,Guide",
            string.Join(",", Rows(sim,
                "select bk.name from bk join pb on bk.pub_id = pb.id "
                + "group by bk.name, pb.name order by pb.name desc")));
    }

    /// <summary>
    /// DISTINCT applies to the grouped projection, and before any row limit.
    /// Grouping alone doesn't imply distinct output — the projection can be
    /// narrower than the grouping key, which is how `.dates()` collapses one
    /// row per record to one row per distinct year.
    /// </summary>
    [TestMethod]
    public void Distinct_OverGroupedProjection_Dedupes()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dt (id int, pubdate date)",
            "insert dt values (1,'2008-01-01'), (2,'2008-06-01'), (3,'1991-01-01')");

        AreEqual("2008,1991", string.Join(",", Rows(sim, "select distinct year(pubdate) from dt group by id, pubdate")));
        // Without DISTINCT the grouped rows stand.
        AreEqual("2008,2008,1991", string.Join(",", Rows(sim, "select year(pubdate) from dt group by id, pubdate")));
    }

    /// <summary>
    /// Under DISTINCT an ORDER BY term may name the source column behind a
    /// projected one rather than its output alias — the spelling an ORM leaves
    /// when it aliases every output positionally.
    /// </summary>
    [TestMethod]
    public void Distinct_OrderByNamesSourceColumnBehindAnAlias_IsAccepted()
    {
        var sim = Seeded();
        AreEqual(
            "1,2",
            string.Join(",", Rows(sim,
                "select distinct bk.id as Col1, pb.name as Col5 from bk join pb on bk.pub_id = pb.id order by pb.name asc")
                .ConvertAll(r => r.Split('|')[0])));

        // A term naming nothing in the select list is still Msg 145.
        _ = sim.AssertSqlError(
            "select distinct bk.id as Col1 from bk join pb on bk.pub_id = pb.id order by pb.name asc",
            145);
    }
}
