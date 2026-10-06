using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The sparse column set, <c>xml COLUMN_SET FOR ALL_SPARSE_COLUMNS</c>: read
/// as XML over the sparse columns, written through to them, and standing in
/// for them in <c>SELECT *</c>. Every expectation probed 2026-10-06 against
/// SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SparseColumnSetTests
{
    private const string Table = "create table t (id int, a int sparse null, b varchar(10) sparse null, cs xml column_set for all_sparse_columns);";

    [TestMethod]
    public void Read_RendersEachNonNullSparseColumn_NullWhenNone()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Table, "insert t (id, a, b) values (1, 5, 'x'), (2, null, null), (3, null, 'q&<')");
        AreEqual("<a>5</a><b>x</b>", sim.ExecuteScalar("select cast(cs as nvarchar(max)) from t where id = 1"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select cs from t where id = 2"));
        AreEqual("<b>q&amp;&lt;</b>", sim.ExecuteScalar("select cast(cs as nvarchar(max)) from t where id = 3"));
    }

    [TestMethod]
    public void SelectStar_ShowsTheSetInPlaceOfItsColumns()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Table);
        using var reader = sim.ExecuteReader("select * from t");
        AreEqual(2, reader.FieldCount);
        AreEqual("cs", reader.GetName(1));
    }

    [TestMethod]
    public void Catalog_MarksTheSetAndItsColumns()
        => AreEqual("id:0:0:0,a:1:0:0,b:1:0:0,cs:0:1:0|1|0", new Simulation().ExecuteScalar($"""
            {Table}
            select concat(string_agg(concat(name, ':', cast(is_sparse as int), ':', cast(is_column_set as int), ':', cast(is_computed as int)), ',') within group (order by column_id),
                '|', columnproperty(object_id('t'), 'cs', 'IsColumnSet'), '|', (select count(*) from sys.computed_columns where object_id = object_id('t')))
            from sys.columns where object_id = object_id('t')
            """));

    [TestMethod]
    public void Insert_WritesTheNamedColumns_CaseInsensitively()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Table, "insert t (id, cs) values (1, '<A>7</A>'), (2, '<b>yy</b><a>1</a>'), (3, null), (4, ''), (5, '<a/>'), (6, '<b></b>')");
        AreEqual("1:7:,2:1:yy,3::,4::,5:0:,6::", sim.ExecuteScalar("select string_agg(concat(id, ':', a, ':', b), ',') within group (order by id) from t"));
        AreEqual("<b />", sim.ExecuteScalar("select cast(cs as nvarchar(max)) from t where id = 6"));
    }

    [TestMethod]
    public void InsertWithoutAColumnList_FillsTheSet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Table, "insert t values (5, '<b>z</b>')");
        AreEqual("z", sim.ExecuteScalar("select b from t"));
    }

    [TestMethod]
    public void Update_ReplacesEverySparseColumn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Table, "insert t (id, a, b) values (1, 5, 'x')", "update t set cs = '<b>new</b>'");
        AreEqual("|new", sim.ExecuteScalar("select concat(a, '|', b) from t"));
        sim.ExecuteBatches("update t set a = 9");
        AreEqual("<a>9</a><b>new</b>", sim.ExecuteScalar("select cast(cs as nvarchar(max)) from t"));
    }

    [TestMethod]
    public void Merge_WritesThroughTheSet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Table, "insert t (id, a) values (1, 1)",
            "merge t using (select 1 id, '<a>9</a>' x) s on t.id = s.id when matched then update set cs = s.x;");
        AreEqual(9, sim.ExecuteScalar("select a from t"));
    }

    [TestMethod]
    [DataRow("insert t (id, a, cs) values (1, 5, '<a>1</a>')", 360)]
    [DataRow("update t set a = 1, cs = '<a>1</a>'", 360)]
    [DataRow("insert t (id, cs) values (1, '<zz>1</zz>')", 1911)]
    [DataRow("insert t (id, cs) values (1, '<a>abc</a>')", 9532)]
    [DataRow("insert t (id, cs) values (1, '<a>1</a><a>2</a>')", 9525)]
    [DataRow("insert t (id, cs) values (1, 'text')", 9524)]
    [DataRow("insert t (id, cs) values (1, '<a>1<x/></a>')", 9524)]
    [DataRow("insert t (id, cs) values (1, '<a q=\"1\">2</a>')", 9530)]
    public void Write_Refusals(string statement, int number)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(Table);
        _ = sim.AssertSqlError(statement, number);
    }

    [TestMethod]
    [DataRow("create table t (id int, a int sparse null, cs xml column_set for all_sparse_columns, cs2 xml column_set for all_sparse_columns)", 1732)]
    [DataRow("create table t (id int, a int sparse null, cs int column_set for all_sparse_columns)", 1733)]
    [DataRow("create table t (id int, a int sparse null, cs xml not null column_set for all_sparse_columns)", 102)]
    [DataRow("create table t (id int, a int sparse null); alter table t add cs xml column_set for all_sparse_columns", 1734)]
    public void Declaration_Refusals(string statement, int number)
        => _ = new Simulation().AssertSqlError(statement, number);

    [TestMethod]
    public void ASparseColumnAddedLater_JoinsTheSet()
        => AreEqual("<c>2020-01-02T03:04:05</c>", new Simulation().ExecuteScalar("""
            create table t (id int, a int sparse null, cs xml column_set for all_sparse_columns);
            alter table t add c datetime sparse null;
            insert t (id, c) values (1, '2020-01-02T03:04:05');
            select cast(cs as nvarchar(max)) from t
            """));
}
