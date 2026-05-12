namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for views: CREATE / DROP, FROM-clause invocation, column-
/// list rename, body grammar (single SELECT, no FROM allowed, JOIN /
/// aggregate / set ops), catalog-view surface (sys.views, sys.objects 'V'
/// rows, sys.columns output projection, INFORMATION_SCHEMA.VIEWS,
/// INFORMATION_SCHEMA.TABLES VIEW rows), and the error paths probe-confirmed
/// against SQL Server 2025 (Msg 4511 / 4506 / 8158 / 8159 / 1033 / 208).
/// </summary>
[TestClass]
public sealed class ViewTests
{
    private static Simulation WithT1()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table dbo.t1 (id int identity primary key, label varchar(20), tag varchar(10));
            insert dbo.t1 (label, tag) values ('a','x'), ('b','x'), ('c','y'), ('d','y')
            """);
        return simulation;
    }

    [TestMethod]
    public void Create_And_Select_BasicView()
    {
        using var reader = WithT1().ExecuteReader("""
            create view dbo.v as select id, label from dbo.t1;
            select id, label from dbo.v order by id
            """);
        var rows = new List<(int id, string label)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        Assert.HasCount(4, rows);
        Assert.AreEqual((1, "a"), rows[0]);
        Assert.AreEqual((4, "d"), rows[3]);
    }

    [TestMethod]
    public void View_With_Column_Rename_List()
        => Assert.AreEqual(1, WithT1().ExecuteScalar("""
            create view dbo.v(the_id, the_label) as select id, label from dbo.t1;
            select the_id from dbo.v order by the_id
            """));

    [TestMethod]
    public void View_Rename_TooFew_Raises_Msg8158()
        => WithT1().AssertSqlError("create view dbo.v(a) as select id, label from dbo.t1", 8158);

    [TestMethod]
    public void View_Rename_TooMany_Raises_Msg8159()
        => WithT1().AssertSqlError("create view dbo.v(a, b, c) as select id, label from dbo.t1", 8159);

    [TestMethod]
    public void View_Unnamed_Projection_Raises_Msg4511()
    {
        var ex = WithT1().AssertSqlError("create view dbo.v as select id, label + tag from dbo.t1", 4511);
        Assert.Contains("column 2", ex.Message);
    }

    [TestMethod]
    public void View_Duplicate_Columns_Raises_Msg4506()
    {
        var ex = WithT1().AssertSqlError("create view dbo.v as select id, id from dbo.t1", 4506);
        Assert.Contains("'id'", ex.Message);
    }

    [TestMethod]
    public void View_Body_With_OrderBy_Without_Top_Raises_Msg1033()
        => WithT1().AssertSqlError("create view dbo.v as select id, label from dbo.t1 order by id", 1033);

    [TestMethod]
    public void View_Body_With_Top_And_OrderBy_Works()
    {
        using var reader = WithT1().ExecuteReader("""
            create view dbo.v as select top 2 id, label from dbo.t1 order by id;
            select id from dbo.v order by id
            """);
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        Assert.HasCount(2, ids);
    }

    [TestMethod]
    public void View_Body_With_Join()
    {
        using var reader = WithT1().ExecuteReader("""
            create table dbo.t2 (id int, note varchar(10));
            insert dbo.t2 values (1, 'first'), (2, 'second');
            create view dbo.v as select a.id, a.label, b.note from dbo.t1 a inner join dbo.t2 b on a.id = b.id;
            select id, label, note from dbo.v order by id
            """);
        var rows = new List<(int, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        Assert.HasCount(2, rows);
        Assert.AreEqual((1, "a", "first"), rows[0]);
    }

    [TestMethod]
    public void View_Body_With_Aggregate_And_GroupBy()
    {
        using var reader = WithT1().ExecuteReader("""
            create view dbo.v as select tag, count(*) as cnt from dbo.t1 group by tag;
            select tag, cnt from dbo.v order by tag
            """);
        var rows = new List<(string, int)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { ("x", 2), ("y", 2) }, rows);
    }

    [TestMethod]
    public void View_With_Schemabinding_Encryption_Parse_And_Ignore()
        => Assert.AreEqual(1, WithT1().ExecuteScalar("""
            create view dbo.v with schemabinding, encryption as select id from dbo.t1;
            select count(*) from dbo.v where id = 1
            """));

    [TestMethod]
    public void View_With_Check_Option_Records_Bit_In_SysViews()
        => Assert.IsTrue((bool)WithT1().ExecuteScalar("""
            create view dbo.v as select id, label from dbo.t1 where tag = 'x' with check option;
            select with_check_option from sys.views where name = 'v'
            """)!);

    [TestMethod]
    public void Unqualified_View_Name_In_From_Resolves_Via_Dbo()
        => Assert.AreEqual(4, WithT1().ExecuteScalar("""
            create view dbo.v as select id from dbo.t1;
            select count(*) from v
            """));

    [TestMethod]
    public void View_On_View()
        => Assert.AreEqual(1, WithT1().ExecuteScalar("""
            create view dbo.v1 as select id, label from dbo.t1;
            create view dbo.v2 as select id as new_id from dbo.v1;
            select min(new_id) from dbo.v2
            """));

    [TestMethod]
    public void Drop_View_Removes_It()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id from dbo.t1;
            drop view dbo.v
            """);
        _ = simulation.AssertSqlError("select id from dbo.v", 208);
    }

    [TestMethod]
    public void Drop_View_IfExists_NoOps()
        => new Simulation().ExecuteNonQuery("drop view if exists dbo.does_not_exist");

    [TestMethod]
    public void Drop_View_Missing_Raises_Msg3701_WithViewWording()
    {
        var ex = new Simulation().AssertSqlError("drop view dbo.does_not_exist", 3701);
        Assert.Contains("view 'dbo.does_not_exist'", ex.Message);
    }

    [TestMethod]
    public void Recursive_View_Raises_Msg208_AtCreate()
        => new Simulation().AssertSqlError("create view dbo.v as select id from dbo.v", 208);

    [TestMethod]
    public void View_Name_Collision_With_Table_Raises_Msg2714()
        => WithT1().AssertSqlError("create view dbo.t1 as select id from dbo.t1", 2714);

    [TestMethod]
    public void View_Body_NoFrom_Works()
    {
        using var reader = new Simulation().ExecuteReader("""
            create view dbo.v as select 1 as x, 'hi' as y;
            select x, y from dbo.v
            """);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual("hi", reader.GetString(1));
    }

    [TestMethod]
    public void SysObjects_HasViewRow_WithTypeV()
    {
        using var reader = WithT1().ExecuteReader("""
            create view dbo.v as select id from dbo.t1;
            select name, type, type_desc from sys.objects where name = 'v'
            """);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("v", reader.GetString(0));
        Assert.AreEqual("V ", reader.GetString(1));
        Assert.AreEqual("VIEW", reader.GetString(2));
    }

    [TestMethod]
    public void SysViews_Emits_ViewRows()
    {
        using var reader = WithT1().ExecuteReader("""
            create view dbo.v1 as select id from dbo.t1;
            create view dbo.v_check as select id from dbo.t1 where id > 0 with check option;
            select name, with_check_option from sys.views where name like 'v%'
            """);
        var rows = new Dictionary<string, bool>();
        while (reader.Read())
            rows[reader.GetString(0)] = reader.GetBoolean(1);
        Assert.HasCount(2, rows);
        Assert.IsFalse(rows["v1"]);
        Assert.IsTrue(rows["v_check"]);
    }

    [TestMethod]
    public void SysColumns_Emits_ViewOutputProjection()
    {
        using var reader = WithT1().ExecuteReader("""
            create view dbo.v as select id, label from dbo.t1;
            select name, column_id from sys.columns where object_id = object_id('dbo.v', 'V') order by column_id
            """);
        var cols = new List<(string name, int id)>();
        while (reader.Read())
            cols.Add((reader.GetString(0), reader.GetInt32(1)));
        CollectionAssert.AreEqual(new[] { ("id", 1), ("label", 2) }, cols);
    }

    [TestMethod]
    public void InformationSchema_Views_Emits_ViewDefinition()
    {
        using var reader = WithT1().ExecuteReader("""
            create view dbo.v as select id from dbo.t1;
            select table_name, check_option, is_updatable from information_schema.views where table_name = 'v'
            """);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("v", reader.GetString(0));
        Assert.AreEqual("NONE", reader.GetString(1));
        Assert.AreEqual("NO", reader.GetString(2));
    }

    [TestMethod]
    public void InformationSchema_Tables_Includes_Views_With_TableType_View()
    {
        using var reader = WithT1().ExecuteReader("""
            create view dbo.v as select id from dbo.t1;
            select table_name, table_type from information_schema.tables where table_name in ('t1', 'v') order by table_name
            """);
        var rows = new List<(string name, string type)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        CollectionAssert.AreEqual(new[] { ("t1", "BASE TABLE"), ("v", "VIEW") }, rows);
    }

    [TestMethod]
    public void ObjectId_With_VFilter_Resolves_Views_Only()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("create view dbo.v as select id from dbo.t1");

        Assert.IsNotNull(simulation.ExecuteScalar("select object_id('dbo.v', 'V')"));
        Assert.AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('dbo.v', 'U')"));
        Assert.AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('dbo.t1', 'V')"));
        Assert.IsNotNull(simulation.ExecuteScalar("select object_id('dbo.t1', 'U')"));
    }

    [TestMethod]
    public void View_WithAlias_RebindsColumnQualifier()
    {
        using var reader = WithT1().ExecuteReader("""
            create view dbo.v as select id, label from dbo.t1;
            select c.id, c.label from dbo.v as c where c.id = 1
            """);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual("a", reader.GetString(1));
    }

    [TestMethod]
    public void Insert_Through_View_Raises_NotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(() => WithT1().ExecuteNonQuery("""
            create view dbo.v as select id, label from dbo.t1;
            insert dbo.v(label) values ('z')
            """));
        Assert.Contains("Updatable-view DML", ex.Message);
    }

    [TestMethod]
    public void Update_Through_View_Raises_NotSupported()
        => Assert.Throws<NotSupportedException>(() => WithT1().ExecuteNonQuery("""
            create view dbo.v as select id, label from dbo.t1;
            update dbo.v set label = 'new' where id = 1
            """));

    [TestMethod]
    public void Delete_Through_View_Raises_NotSupported()
        => Assert.Throws<NotSupportedException>(() => WithT1().ExecuteNonQuery("""
            create view dbo.v as select id, label from dbo.t1;
            delete dbo.v where id = 1
            """));
}
