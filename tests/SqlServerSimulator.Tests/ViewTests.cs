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
        var simulation = WithT1();
        simulation.ExecuteBatches(
            """
            create table dbo.t2 (id int, note varchar(10));
            insert dbo.t2 values (1, 'first'), (2, 'second');
            """,
            "create view dbo.v as select a.id, a.label, b.note from dbo.t1 a inner join dbo.t2 b on a.id = b.id");
        using var reader = simulation.ExecuteReader("select id, label, note from dbo.v order by id");
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
    {
        var simulation = WithT1();
        simulation.ExecuteBatches(
            "create view dbo.v1 as select id, label from dbo.t1",
            "create view dbo.v2 as select id as new_id from dbo.v1");
        Assert.AreEqual(1, simulation.ExecuteScalar("select min(new_id) from dbo.v2"));
    }

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
        var simulation = WithT1();
        simulation.ExecuteBatches(
            "create view dbo.v1 as select id from dbo.t1",
            "create view dbo.v_check as select id from dbo.t1 where id > 0 with check option");
        using var reader = simulation.ExecuteReader("select name, with_check_option from sys.views where name like 'v%'");
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
    public void Insert_Through_Simple_View_Writes_To_Base()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label, tag from dbo.t1;
            insert dbo.v(label, tag) values ('e','z')
            """);
        Assert.AreEqual("e", simulation.ExecuteScalar("select label from dbo.t1 where tag = 'z'"));
    }

    [TestMethod]
    public void Insert_Through_View_Without_ColumnList_UsesImplicitProjection()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label, tag from dbo.t1;
            insert dbo.v values ('e','z')
            """);
        Assert.AreEqual("e", simulation.ExecuteScalar("select label from dbo.t1 where tag = 'z'"));
    }

    [TestMethod]
    public void Update_Through_Simple_View_Writes_To_Base()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label from dbo.t1;
            update dbo.v set label = 'renamed' where id = 1
            """);
        Assert.AreEqual("renamed", simulation.ExecuteScalar("select label from dbo.t1 where id = 1"));
    }

    [TestMethod]
    public void Delete_Through_Simple_View_Writes_To_Base()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label from dbo.t1;
            delete dbo.v where id = 1
            """);
        Assert.AreEqual(0, simulation.ExecuteScalar("select count(*) from dbo.t1 where id = 1"));
    }

    [TestMethod]
    public void Filtered_View_Update_Limits_To_Visible_Rows()
    {
        var simulation = WithT1();
        // Filter view to tag='x' rows only. UPDATE through view must affect
        // only the visible rows (label='a','b'), not the hidden ones.
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label from dbo.t1 where tag = 'x';
            update dbo.v set label = 'Q'
            """);
        using var reader = simulation.ExecuteReader("select label from dbo.t1 order by id");
        var labels = new List<string>();
        while (reader.Read())
            labels.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "Q", "Q", "c", "d" }, labels);
    }

    [TestMethod]
    public void Filtered_View_Delete_Limits_To_Visible_Rows()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label from dbo.t1 where tag = 'x';
            delete dbo.v
            """);
        Assert.AreEqual(2, simulation.ExecuteScalar("select count(*) from dbo.t1"));
    }

    [TestMethod]
    public void Filtered_View_Insert_Without_CheckOption_Allows_OutOfView_Row()
    {
        // Probe-confirmed against SQL Server 2025: a view's WHERE filters
        // reads but does NOT constrain INSERTs unless WITH CHECK OPTION is
        // specified. The row is in the base but invisible through the view.
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label, tag from dbo.t1 where tag = 'x';
            insert dbo.v(label, tag) values ('outsider','z')
            """);
        Assert.AreEqual("z", simulation.ExecuteScalar("select tag from dbo.t1 where label = 'outsider'"));
        Assert.IsNull(simulation.ExecuteScalar("select count(*) from dbo.v where label = 'outsider'") is int n && n > 0 ? (object?)"visible" : null);
    }

    [TestMethod]
    public void Insert_Through_CheckOption_View_Violates_Raises_Msg550()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("create view dbo.v as select id, label, tag from dbo.t1 where tag = 'x' with check option");
        var ex = simulation.AssertSqlError("insert dbo.v(label,tag) values ('bad','z')", 550);
        Assert.Contains("CHECK OPTION", ex.Message);
        Assert.AreEqual(0, simulation.ExecuteScalar("select count(*) from dbo.t1 where label = 'bad'"));
    }

    [TestMethod]
    public void Insert_Through_CheckOption_View_Honoring_Predicate_Succeeds()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label, tag from dbo.t1 where tag = 'x' with check option;
            insert dbo.v(label,tag) values ('good','x')
            """);
        Assert.AreEqual("good", simulation.ExecuteScalar("select label from dbo.v where label = 'good'"));
    }

    [TestMethod]
    public void Update_Through_CheckOption_View_Moving_Row_Out_Raises_Msg550()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("create view dbo.v as select id, label, tag from dbo.t1 where tag = 'x' with check option");
        _ = simulation.AssertSqlError("update dbo.v set tag = 'z' where id = 1", 550);
        Assert.AreEqual("x", simulation.ExecuteScalar("select tag from dbo.t1 where id = 1"));
    }

    [TestMethod]
    public void Insert_Through_Aggregate_View_Raises_Msg4403()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("create view dbo.v as select tag, count(*) as cnt from dbo.t1 group by tag");
        var ex = simulation.AssertSqlError("insert dbo.v(tag,cnt) values ('q',1)", 4403);
        Assert.Contains("'dbo.v'", ex.Message);
        Assert.Contains("aggregates", ex.Message);
    }

    [TestMethod]
    public void Update_Through_Distinct_View_Raises_Msg4403()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("create view dbo.v as select distinct tag from dbo.t1");
        _ = simulation.AssertSqlError("update dbo.v set tag='q'", 4403);
    }

    [TestMethod]
    public void Delete_Through_Aggregate_View_Raises_Msg4403()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("create view dbo.v as select tag, count(*) as cnt from dbo.t1 group by tag");
        _ = simulation.AssertSqlError("delete dbo.v where tag = 'x'", 4403);
    }

    [TestMethod]
    public void Insert_Touching_Derived_Column_Raises_Msg4406()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("create view dbo.v as select id, label, len(label) as label_len from dbo.t1");
        var ex = simulation.AssertSqlError("insert dbo.v(label, label_len) values ('zz', 99)", 4406);
        Assert.Contains("'dbo.v'", ex.Message);
        Assert.Contains("derived or constant field", ex.Message);
    }

    [TestMethod]
    public void Insert_OnDerived_View_Touching_Only_Direct_Cols_Succeeds()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label, len(label) as label_len from dbo.t1;
            insert dbo.v(label) values ('newrow')
            """);
        Assert.AreEqual("newrow", simulation.ExecuteScalar("select label from dbo.t1 where label='newrow'"));
    }

    [TestMethod]
    public void Update_Setting_Derived_Column_Raises_Msg4406()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("create view dbo.v as select id, label, len(label) as label_len from dbo.t1");
        _ = simulation.AssertSqlError("update dbo.v set label_len = 7 where id = 1", 4406);
    }

    [TestMethod]
    public void Delete_Through_Derived_View_Works()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label, len(label) as label_len from dbo.t1;
            delete dbo.v where id = 1
            """);
        Assert.AreEqual(0, simulation.ExecuteScalar("select count(*) from dbo.t1 where id = 1"));
    }

    /// <summary>
    /// INSERT through a JOIN view is Msg 4405 for a column list spanning two
    /// base tables (and for the implicit list, which spans all), while a list
    /// naming a single base table's columns writes that table — the routing
    /// lives in <see cref="JoinViewInsertTests"/>.
    /// </summary>
    [TestMethod]
    public void Insert_Through_Join_View_Raises_Msg4405()
    {
        var simulation = WithT1();
        simulation.ExecuteBatches(
            "create table dbo.t2 (id int, owner varchar(10))",
            "create view dbo.v as select a.id, a.label, b.owner from dbo.t1 a inner join dbo.t2 b on a.id = b.id");
        var ex = simulation.AssertSqlError("insert dbo.v(label, owner) values ('x', 'y')", 4405);
        Assert.Contains("'dbo.v'", ex.Message);
        Assert.Contains("multiple base tables", ex.Message);
        Assert.AreEqual(1, simulation.ExecuteNonQuery("insert dbo.v(owner) values ('y')"));
        Assert.AreEqual(1, simulation.ExecuteScalar("select count(*) from dbo.t2 where owner = 'y'"));
    }

    [TestMethod]
    public void Chain_View_On_View_Update_Composes_Visibility()
    {
        var simulation = WithT1();
        simulation.ExecuteBatches(
            "create view dbo.v1 as select id, label, tag from dbo.t1 where tag = 'x'",
            "create view dbo.v2 as select id, label, tag from dbo.v1 where id > 1");
        _ = simulation.ExecuteNonQuery("update dbo.v2 set label = 'CHAINED'");
        // Only the row with tag='x' AND id>1 should be touched. That's id=2, label='b'.
        Assert.AreEqual("CHAINED", simulation.ExecuteScalar("select label from dbo.t1 where id = 2"));
        Assert.AreEqual("a", simulation.ExecuteScalar("select label from dbo.t1 where id = 1"));
        Assert.AreEqual("c", simulation.ExecuteScalar("select label from dbo.t1 where id = 3"));
    }

    [TestMethod]
    public void Chain_View_With_CheckOption_Cascades_Up_The_Chain()
    {
        var simulation = WithT1();
        // v_chain1 has CHECK OPTION on tag='x'; v_chain2 has CHECK OPTION on n>1
        // (where n isn't a column in t1 so use a different table for clarity).
        simulation.ExecuteBatches(
            "create view dbo.v1 as select id, label, tag from dbo.t1 where tag = 'x' with check option",
            "create view dbo.v2 as select id, label, tag from dbo.v1 where id > 0 with check option");
        // INSERT through v2 with tag='y' violates v1's CHECK OPTION via the chain.
        _ = simulation.AssertSqlError("insert dbo.v2(label, tag) values ('chk_fail', 'y')", 550);
        Assert.AreEqual(0, simulation.ExecuteScalar("select count(*) from dbo.t1 where label = 'chk_fail'"));
        // INSERT honoring the predicate succeeds.
        _ = simulation.ExecuteNonQuery("insert dbo.v2(label, tag) values ('chk_ok', 'x')");
        Assert.AreEqual("chk_ok", simulation.ExecuteScalar("select label from dbo.t1 where label = 'chk_ok'"));
    }

    [TestMethod]
    public void View_With_ColumnRename_Maps_To_Base_Correctly()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v(my_id, my_label, my_tag) as select id, label, tag from dbo.t1;
            insert dbo.v(my_label, my_tag) values ('renamed','q');
            update dbo.v set my_label = 'updated_q' where my_tag = 'q'
            """);
        Assert.AreEqual("updated_q", simulation.ExecuteScalar("select label from dbo.t1 where tag = 'q'"));
    }

    [TestMethod]
    public void Insert_Through_View_Auto_Generates_Identity()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("""
            create view dbo.v as select id, label from dbo.t1;
            insert dbo.v(label) values ('auto')
            """);
        // Identity column kicked in: the new row has the next id (5).
        Assert.AreEqual(5, simulation.ExecuteScalar("select id from dbo.t1 where label = 'auto'"));
    }

    [TestMethod]
    public void Update_Through_View_Identity_Column_Raises_Msg8102()
    {
        var simulation = WithT1();
        _ = simulation.ExecuteNonQuery("create view dbo.v as select id, label from dbo.t1");
        _ = simulation.AssertSqlError("update dbo.v set id = 99 where id = 1", 8102);
    }
}
