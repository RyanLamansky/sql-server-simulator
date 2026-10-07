using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A spatial column's members where no query scope is installed — an UPDATE's
/// or MERGE's SET list, where <c>col.STSrid = …</c> is a mutator, an OUTPUT
/// clause, a computed column and a CHECK constraint — plus the spatial
/// aggregates over an untyped NULL, and an invalid instance's serialization
/// and <c>IsValidDetailed()</c> report, against answers probed from SQL
/// Server 2025 (2026-10-06).
/// </summary>
[TestClass]
public sealed class SpatialColumnMemberTests
{
    private const string PointTable = """
        create table pt (id int, loc geography, g geometry, x float);
        insert pt values (1, geography::Point(10, 20, 4326), geometry::Point(1, 2, 0), 0), (2, null, null, 0);
        """;

    [TestMethod]
    public void Set_STSrid_Restamps_The_Column()
        => AreEqual("4269|5|11|POINT (20 10)", new Simulation().ExecuteScalar(PointTable + """
            update pt set loc.STSrid = 4269, g.STSrid = 4 where id = 1;
            update pt set x = loc.Lat + 1, g.STSrid += 1 where id = 1;
            select concat(loc.STSrid, '|', g.STSrid, '|', x, '|', loc.ToString()) from pt where id = 1
            """));

    [TestMethod]
    public void Set_STSrid_Through_An_Alias_A_View_And_A_Merge()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(PointTable, "create view v as select id, g as h from pt");
        _ = sim.ExecuteNonQuery("update p set g.STSrid = 7 from pt p where id = 1");
        AreEqual(7, sim.ExecuteScalar("select g.STSrid from pt where id = 1"));
        _ = sim.ExecuteNonQuery("update v set h.STSrid = 8 where id = 1");
        AreEqual(8, sim.ExecuteScalar("select g.STSrid from pt where id = 1"));
        _ = sim.ExecuteNonQuery("merge pt as a using (select 1 k) s on a.id = s.k when matched then update set g.STSrid = 9;");
        AreEqual(9, sim.ExecuteScalar("select g.STSrid from pt where id = 1"));
        AreEqual("9|10", sim.ExecuteScalar("declare @o table (d int, i int); update pt set g.STSrid = 10 output deleted.g.STSrid, inserted.g.STSrid into @o where id = 1; select concat(d, '|', i) from @o"));
    }

    [TestMethod]
    [DataRow("update pt set loc.Lat = 5", 6595)]
    [DataRow("update pt set g.Foo = 5", 6592)]
    [DataRow("update pt set g.stsrid = 5", 6592)]
    [DataRow("update pt set g.STAsText = 5", 6592)]
    [DataRow("update pt set g.STAsText()", 6201)]
    [DataRow("update pt set g.STSrid(1)", 6506)]
    [DataRow("update pt set g.STSrid() = 2", 102)]
    [DataRow("update pt set g.STSrid = 8, g.STSrid = 9", 264)]
    [DataRow("update pt set g.STSrid = 1 where id = 2", 5302)]
    [DataRow("update pt set g.STSrid = null", 6522)]
    [DataRow("update pt set loc.STSrid = 1 where id = 1", 6522)]
    [DataRow("update p set p.g.STSrid = 7 from pt p", 4104)]
    public void Set_On_A_Spatial_Member_Refuses_What_Real_Refuses(string update, int number)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(PointTable);
        _ = sim.AssertSqlError(update, number);
    }

    [TestMethod]
    public void A_Row_Count_Top_Stops_Before_A_Later_Rows_Set_Value()
        => AreEqual(55, new Simulation().ExecuteScalar(PointTable + """
            update top (1) pt set g.STSrid = 55;
            select g.STSrid from pt where id = 1
            """));

    [TestMethod]
    public void Computed_Columns_And_Checks_Read_The_Property()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table c (lat as loc.Lat, loc geography check (loc.Lat > 0), srid as loc.STSrid persisted, constraint ck_long check (loc.Long < 90));
            insert c values (geography::Point(10, 20, 4326));
            alter table c add lng as loc.Long;
            """);
        AreEqual("10|4326|20", sim.ExecuteScalar("select concat(lat, '|', srid, '|', lng) from c"));
        AreEqual("([loc].[Lat])", sim.ExecuteScalar("select definition from sys.computed_columns where name = 'lat'"));
        _ = sim.AssertSqlError("insert c values (geography::Point(-10, 20, 4326))", 547);
        _ = sim.AssertSqlError("create table c2 (a int, loc geography, b as loc.a)", 6592);
        // A qualifier naming the table itself is the column reading.
        AreEqual(3, new Simulation().ExecuteScalar("create table x7 (a int, x7 geography, b as x7.a); insert x7 (a) values (3); select b from x7"));
    }

    [TestMethod]
    public void Aggregates_Over_An_Untyped_Null_Answer_Null()
        => IsNull(new Simulation().ExecuteScalar("select geometry::UnionAggregate(null).STAsText()") as string);

    [TestMethod]
    // An invalid instance keeps its isValid bit clear, and a geography one is
    // version 2, which MinDbCompatibilityLevel reports as 110.
    [DataRow("geography::STGeomFromText('LINESTRING(0 0, 1 1, 0 0)', 4326)", "0xE610000002000300000000000000000000000000000000000000000000000000F03F000000000000F03F0000000000000000000000000000000001000000010000000001000000FFFFFFFF0000000002", 110)]
    [DataRow("geography::STGeomFromText('LINESTRING(0 0, 0 0)', 4326)", "0xE610000002100000000000000000000000000000000000000000000000000000000000000000", 110)]
    [DataRow("geometry::STGeomFromText('LINESTRING(0 0, 1 1, 0 0)', 0)", "0x0000000001000300000000000000000000000000000000000000000000000000F03F000000000000F03F0000000000000000000000000000000001000000010000000001000000FFFFFFFF0000000002", 100)]
    [DataRow("geography::STGeomFromText('LINESTRING(0 0, 1 1, 2 0)', 4326)", "0xE610000001040300000000000000000000000000000000000000000000000000F03F000000000000F03F0000000000000000000000000000004001000000010000000001000000FFFFFFFF0000000002", 100)]
    public void Serialization_Follows_Validity(string instance, string bytes, int level)
        => AreEqual($"{bytes}|{level}", new Simulation().ExecuteScalar($"declare @g {instance[..instance.IndexOf(':')]} = {instance}; select concat(convert(varchar(max), cast(@g as varbinary(max)), 1), '|', @g.MinDbCompatibilityLevel())"));
    [TestMethod]
    [DataRow("geometry", "LINESTRING(0 0, 0 0)", "24406: Not valid because curve (1) degenerates to a point.")]
    [DataRow("geometry", "LINESTRING(0 0, 5 8, 1 1, 5 8, 5 8)", "24406: Not valid because curve (2) degenerates to a point.")]
    [DataRow("geography", "LINESTRING(0 0, 2 0, 1 0, 3 0)", "24413: Not valid because of two overlapping edges in curve (1).")]
    [DataRow("geometry", "POLYGON((0 0, 1 1, 1 0, 0 1, 0 0))", "24404: Not valid because polygon ring (1) intersects itself or some other ring.")]
    [DataRow("geography", "POLYGON((0 0, 1 1, 1 0, 0 1, 0 0))", "24409: Not valid because some portion of polygon ring (1) lies in the interior of a polygon.")]
    [DataRow("geometry", "POLYGON((0 0, 3 0, 3 3, 0 3, 0 0, 1 1, 2 1, 2 2, 1 2, 1 1, 0 0))", "24404: Not valid because polygon ring (1) intersects itself or some other ring.")]
    [DataRow("geography", "POLYGON((0 0, 3 0, 3 3, 0 3, 0 0, 1 1, 2 1, 2 2, 1 2, 1 1, 0 0))", "24413: Not valid because of two overlapping edges in curve (1).")]
    [DataRow("geometry", "POLYGON((0 0, 3 0, 3 3, 0 3, 0 0, 1 1, 2 1, 1 1, 0 0))", "24413: Not valid because of two overlapping edges in curve (1).")]
    [DataRow("geography", "POLYGON((0 0, 2 0, 1 1, 2 2, 0 2, 1 1, 0 0))", "24404: Not valid because polygon ring (1) intersects itself or some other ring.")]
    [DataRow("geography", "POLYGON((0 0, 3 0, 3 3, 0 3, 3 0, 3 -1, 0 -1, 0 0))", "24409: Not valid because some portion of polygon ring (1) lies in the interior of a polygon.")]
    public void IsValidDetailed_Names_The_Rule_A_Single_Figure_Breaks(string type, string wkt, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {type}::STGeomFromText('{wkt}', {(type == "geography" ? 4326 : 0)}).IsValidDetailed()"));
}
