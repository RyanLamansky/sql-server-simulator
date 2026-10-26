using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A member written on a value — a method or a property, after a column, a
/// variable or any other expression — is judged by the receiver's type before
/// its name: a spatial, <c>hierarchyid</c> or <c>xml</c> receiver has its own
/// members and refuses any other, every other system type has none (Msg 258),
/// and an unqualified column's dotted name reads as a function's, missing
/// once the statement runs (Msg 4121). Each expectation is real's answer for
/// the same text (probed 2026-10-26 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class MemberReceiverTests
{
    private const string Setup = """
        create table u (id int, f float, n nvarchar(10), d decimal(5, 2), v varbinary(10), dt datetime2, b bit, j json, h hierarchyid, xm xml, loc geography);
        insert u (id, h, xm, loc, j) values (1, '/1/2/', '<a>5</a>', geography::Point(1, 2, 4326), '{"a":1}');
        create table flag (step int);
        """;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        return simulation;
    }

    [TestMethod]
    [DataRow("select cast(1 as int).STAsText()", 258, "Cannot call methods on int.")]
    [DataRow("select cast(1 as int).Lat", 258, "Cannot call methods on int.")]
    [DataRow("select (id).Lat from u", 258, "Cannot call methods on int.")]
    [DataRow("select abs(id).STAsText() from u", 258, "Cannot call methods on int.")]
    [DataRow("select cast(null as int).STAsText()", 258, "Cannot call methods on int.")]
    [DataRow("select (select 1).STAsText()", 258, "Cannot call methods on int.")]
    [DataRow("select cast(1 as int).value('.', 'int')", 258, "Cannot call methods on int.")]
    [DataRow("select cast(N'x' as nvarchar(10)).ToString()", 258, "Cannot call methods on nvarchar.")]
    [DataRow("declare @i int = 1; select @i.Lat", 258, "Cannot call methods on int.")]
    [DataRow("declare @i int = 1; select @i.value('.', 'int')", 258, "Cannot call methods on int.")]
    [DataRow("declare @i int = 1; select @i.STAsText()", 258, "Cannot call methods on int.")]
    [DataRow("select geography::Point(1, 2, 4326).GetLevel()", 6506, "Could not find method 'GetLevel' for type 'Microsoft.SqlServer.Types.SqlGeography' in assembly 'Microsoft.SqlServer.Types'")]
    [DataRow("select loc.value('.', 'int') from u", 6506, "Could not find method 'value' for type 'Microsoft.SqlServer.Types.SqlGeography' in assembly 'Microsoft.SqlServer.Types'")]
    [DataRow("select cast('/1/' as hierarchyid).STAsText()", 6506, "Could not find method 'STAsText' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'")]
    [DataRow("select h.value('.', 'int') from u", 6506, "Could not find method 'value' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'")]
    [DataRow("select cast('/1/' as hierarchyid).IsNull()", 6506, "Could not find method 'IsNull' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'")]
    [DataRow("select cast('/1/' as hierarchyid).Lat", 6592, "Could not find property or field 'Lat' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'.")]
    [DataRow("select h.Foo from u", 6592, "Could not find property or field 'Foo' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'.")]
    [DataRow("select h.ToString from u", 6592, "Could not find property or field 'ToString' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'.")]
    [DataRow("select cast('<a/>' as xml).STAsText()", 227, "\"STAsText\" is not a valid function, property, or field.")]
    [DataRow("select xm.GetLevel() from u", 227, "\"GetLevel\" is not a valid function, property, or field.")]
    [DataRow("select xm.Foo from u", 227, "\"Foo\" is not a valid function, property, or field.")]
    [DataRow("select xm.nodes from u", 227, "\"nodes\" is not a valid function, property, or field.")]
    [DataRow("select xm.Value from u", 227, "\"Value\" is not a valid function, property, or field.")]
    [DataRow("select xm.value from u", 8173, "Incorrect syntax was used to invoke the XML data type method 'value'.")]
    public void A_Member_Its_Receiver_Lacks(string sql, int number, string message)
        => Seeded().AssertSqlError(sql, number, message);

    [TestMethod]
    [DataRow("select t.id.Lat from u t", "int")]
    [DataRow("select t.f.Lat from u t", "float")]
    [DataRow("select t.n.Lat from u t", "nvarchar")]
    [DataRow("select t.d.Lat from u t", "decimal")]
    [DataRow("select t.v.Lat from u t", "varbinary")]
    [DataRow("select t.dt.Lat from u t", "datetime2")]
    [DataRow("select t.b.Lat from u t", "bit")]
    [DataRow("select t.j.Lat from u t", "json")]
    [DataRow("select t.j.modify('$.a', 2) from u t", "json")]
    [DataRow("select t.id.Lat() from u t", "int")]
    [DataRow("select t.id.STAsText() from u t", "int")]
    [DataRow("select t.id.value('.', 'int') from u t", "int")]
    [DataRow("select t.id.GetLevel() from u t", "int")]
    [DataRow("select t.id.Lat.Foo from u t", "int")]
    [DataRow("select u.id.Lat from u", "int")]
    [DataRow("select 1 from u t where t.id.Lat = 1", "int")]
    [DataRow("select 1 from u t order by t.id.Lat", "int")]
    [DataRow("declare @t table (id int); select t.id.Lat from @t t", "int")]
    [DataRow("select t.id.Lat from (select 1 id) t", "int")]
    [DataRow("update u set f = u.id.Lat", "int")]
    [DataRow("update u set f = 1 output inserted.id.Lat", "int")]
    [DataRow("insert u (id) output inserted.id.Lat values (2)", "int")]
    public void A_Source_Column_Without_Members_Raises258(string sql, string type)
        => Seeded().AssertSqlError(sql, 258, $"Cannot call methods on {type}.");

    [TestMethod]
    [DataRow("select t.nosuch.Lat from u t", 207, "Invalid column name 'nosuch'.")]
    [DataRow("select t.nosuch.Lat() from u t", 207, "Invalid column name 'nosuch'.")]
    [DataRow("select t.nosuch.STAsText() from u t", 207, "Invalid column name 'nosuch'.")]
    [DataRow("select x.id.Lat from u t", 4104, "The multi-part identifier \"x.id.Lat\" could not be bound.")]
    [DataRow("select id.Lat from u", 4104, "The multi-part identifier \"id.Lat\" could not be bound.")]
    [DataRow("select t.h.Foo from u t", 6592, "Could not find property or field 'Foo' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'.")]
    [DataRow("select t.h.GetLevel from u t", 6592, "Could not find property or field 'GetLevel' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'.")]
    [DataRow("select t.h.Foo() from u t", 6506, "Could not find method 'Foo' for type 'Microsoft.SqlServer.Types.SqlHierarchyId' in assembly 'Microsoft.SqlServer.Types'")]
    [DataRow("select t.xm.Foo from u t", 227, "\"Foo\" is not a valid function, property, or field.")]
    [DataRow("select t.xm.modify from u t", 8173, "Incorrect syntax was used to invoke the XML data type method 'modify'.")]
    public void A_Qualified_Member_Reports_What_Its_Column_Says(string sql, int number, string message)
        => Seeded().AssertSqlError(sql, number, message);

    [TestMethod]
    public void A_Hierarchyid_Has_The_IsNull_Property()
    {
        var simulation = Seeded();
        IsFalse((bool)simulation.ExecuteScalar("select h.IsNull from u")!);
        IsFalse((bool)simulation.ExecuteScalar("select t.h.IsNull from u t")!);
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select cast(null as hierarchyid).IsNull"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("declare @h hierarchyid; select @h.IsNull"));
        AreEqual("bit", simulation.ExecuteScalar("select sql_variant_property(cast('/1/' as hierarchyid).IsNull, 'BaseType')"));
    }

    /// <summary>
    /// An unqualified column's dotted call reads as a function's name, which
    /// real resolves only as the statement runs: the statements ahead of it
    /// run, the batch ends there, and an un-taken branch never raises it — as
    /// for a receiver naming no column at all.
    /// </summary>
    [TestMethod]
    [DataRow("id.STAsText()", "id", "id.STAsText")]
    [DataRow("id.GetLevel()", "id", "id.GetLevel")]
    [DataRow("id.value('.', 'int')", "id", "id.value")]
    [DataRow("j.modify('$.a', 1)", "j", "j.modify")]
    [DataRow("nosuch.STAsText()", "nosuch", "nosuch.STAsText")]
    [DataRow("nosuch.GetLevel()", "nosuch", "nosuch.GetLevel")]
    [DataRow("nosuch.value('.', 'int')", "nosuch", "nosuch.value")]
    [DataRow("x.nosuch.STAsText()", "x", "x.nosuch.STAsText")]
    public void An_Unqualified_Receivers_Call_Is_A_Missing_Function_As_It_Runs(string call, string column, string function)
    {
        var simulation = Seeded();
        simulation.AssertSqlError(
            $"insert flag values (1); select {call} from u t; insert flag values (2)",
            4121,
            $"Cannot find either column \"{column}\" or the user-defined function or aggregate \"{function}\", or the name is ambiguous.");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from flag"));
        AreEqual(2, simulation.ExecuteScalar($"if 1 = 0 select {call} from u t; select 2"));
    }

    [TestMethod]
    [DataRow("select t.id.Lat from u t")]
    [DataRow("select cast(1 as int).STAsText()")]
    [DataRow("select t.loc.GetLevel() from u t")]
    [DataRow("select xm.STAsText() from u")]
    public void A_Member_Refusal_Stops_The_Batch_As_It_Compiles(string statement)
    {
        var simulation = Seeded();
        _ = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery($"insert flag values (1); {statement}"));
        _ = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery($"if 1 = 0 {statement}; insert flag values (2)"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from flag"));
    }

    [TestMethod]
    [DataRow("select t.id.Lat from missing t")]
    [DataRow("select x.c.STAsText() from missing x")]
    [DataRow("select x.loc.GetLevel() from missing x")]
    [DataRow("select x.c.value('.', 'int') from missing x")]
    public void A_Statement_Reading_A_Missing_Table_Defers_Its_Member_Refusal(string statement)
        => AreEqual(2, Seeded().ExecuteScalar($"if 1 = 0 {statement}; select 2"));

    [TestMethod]
    public void A_Module_Binds_The_Call_As_Real_Does()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches("create procedure p as select id.STAsText() x from u");
        simulation.AssertSqlError("exec p", 4121, "Cannot find either column \"id\" or the user-defined function or aggregate \"id.STAsText\", or the name is ambiguous.");
        simulation.AssertSqlError("create view v as select id.STAsText() x from u", 4121, "Cannot find either column \"id\" or the user-defined function or aggregate \"id.STAsText\", or the name is ambiguous.");
        simulation.AssertSqlError("create view v as select t.id.STAsText() x from u t", 258, "Cannot call methods on int.");
        simulation.AssertSqlError("create function f() returns table as return select id.GetLevel() x from u", 4121, "Cannot find either column \"id\" or the user-defined function or aggregate \"id.GetLevel\", or the name is ambiguous.");
    }

    /// <summary>
    /// The first member refusal reports among the statement's name errors, in
    /// binding order, and one after any other error doesn't report at all.
    /// </summary>
    [TestMethod]
    [DataRow("select t.id.Lat, t.nosuch from u t", "258 207")]
    [DataRow("select t.nosuch, t.id.Lat from u t", "207")]
    [DataRow("select t.id.Lat, t.h.Foo from u t", "258")]
    [DataRow("select t.h.Foo, t.id.Lat from u t", "6592")]
    [DataRow("select t.xm.Foo, t.nosuch from u t", "227 207")]
    [DataRow("select t.id.Lat, t.nosuch, t.f.Lat from u t", "258 207")]
    [DataRow("select t.nosuch from u t where t.id.Lat = 1", "258 207")]
    [DataRow("select t.id.Lat from u t where t.nosuch = 1", "207")]
    public void A_Member_Refusal_Reports_As_A_Type_Check(string statement, string numbers)
    {
        var simulation = Seeded();
        var ex = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery(statement));
        AreEqual(numbers, string.Join(" ", ex.Errors.Select(error => error.Number)));
    }

    [TestMethod]
    public void A_Missing_Functions_Message_Names_Its_First_Part()
        => new Simulation().AssertSqlError(
            "select simulated.dbo.nosuchfn()",
            4121,
            "Cannot find either column \"simulated\" or the user-defined function or aggregate \"simulated.dbo.nosuchfn\", or the name is ambiguous.");

    private const string MergeHead = "merge m using (select geography::Point(1, 2, 4326) loc, 5 k) s on 1 = 0 when not matched then insert (id, x) values (s.k, s.loc.Lat) output ";

    [TestMethod]
    public void A_Merge_Output_Reads_Its_Sources_Spatial_Members()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table m (id int, x float, g geography)");
        using var reader = simulation.ExecuteReader(MergeHead + "s.loc.Lat, inserted.x, s.k, s.loc.STAsText();");
        IsTrue(reader.Read());
        AreEqual("1|1|5|POINT (2 1)", $"{reader.GetDouble(0).ToInvariantString()}|{reader.GetDouble(1).ToInvariantString()}|{reader.GetInt32(2)}|{reader.GetString(3)}");
    }

    [TestMethod]
    [DataRow("s.loc.Bogus;", 6592, "Could not find property or field 'Bogus' for type 'Microsoft.SqlServer.Types.SqlGeography' in assembly 'Microsoft.SqlServer.Types'.")]
    [DataRow("s.k.Lat;", 258, "Cannot call methods on int.")]
    [DataRow("k;", 207, "Invalid column name 'k'.")]
    [DataRow("id;", 207, "Invalid column name 'id'.")]
    [DataRow("inserted.nosuch;", 207, "Invalid column name 'nosuch'.")]
    [DataRow("s.nosuch;", 207, "Invalid column name 'nosuch'.")]
    [DataRow("x.k;", 4104, "The multi-part identifier \"x.k\" could not be bound.")]
    [DataRow("loc.STAsText();", 4121, "Cannot find either column \"loc\" or the user-defined function or aggregate \"loc.STAsText\", or the name is ambiguous.")]
    public void A_Merge_Output_Refuses_What_Real_Refuses(string output, int number, string message)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table m (id int, x float, g geography)");
        simulation.AssertSqlError(MergeHead + output, number, message);
    }
}
