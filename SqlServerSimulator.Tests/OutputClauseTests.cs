namespace SqlServerSimulator;

/// <summary>
/// Direct-SQL coverage for <c>INSERT ... OUTPUT INSERTED.&lt;col&gt;</c> and
/// the narrowly-shaped <c>MERGE INTO ... USING (VALUES) ON 1 = 0 WHEN NOT
/// MATCHED THEN INSERT ... OUTPUT</c> form that EF Core emits for multi-row
/// SaveChanges.
/// </summary>
[TestClass]
public class OutputClauseTests
{
    [TestMethod]
    public void InsertOutput_SingleRowProjectsGeneratedIdentity()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t ( id int identity(1, 1) not null, name nvarchar(20) not null );
            insert t (name) output inserted.id values ('a')
            """).ExecuteReader();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    public void InsertOutput_MultipleColumnsAndAlias()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t ( id int identity(1, 1) not null, name nvarchar(20) not null );
            insert t (name) output inserted.id as NewId, inserted.name values ('a')
            """).ExecuteReader();

        Assert.AreEqual(2, reader.FieldCount);
        Assert.AreEqual("NewId", reader.GetName(0));
        Assert.AreEqual("name", reader.GetName(1));

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual("a", reader.GetString(1));
    }

    [TestMethod]
    public void InsertOutput_MultiRowYieldsOneOutputRowPerInsertedRow()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t ( id int identity(1, 1) not null, name nvarchar(20) not null );
            insert t (name) output inserted.id values ('a'), ('b'), ('c')
            """).ExecuteReader();

        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ids);
    }

    [TestMethod]
    public void InsertOutput_OnNonIdentityColumns_StillProjects()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t ( a int, b int );
            insert t output inserted.b, inserted.a values (1, 2)
            """).ExecuteReader();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.AreEqual(1, reader.GetInt32(1));
    }

    [TestMethod]
    public void InsertOutput_ExpressionInProjection_Evaluates()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t ( id int identity(1, 1) not null, name nvarchar(20) not null );
            insert t (name) output inserted.id + 100 as Bumped values ('only')
            """).ExecuteReader();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Bumped", reader.GetName(0));
        Assert.AreEqual(101, reader.GetInt32(0));
    }

    [TestMethod]
    public void InsertOutput_UnprefixedColumnReference_RaisesMsg4104()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t ( a int );
            insert t output a values (1)
            """, 4104);
        Assert.Contains("could not be bound", ex.Message);
    }

    [TestMethod]
    public void InsertOutput_DeletedReference_RaisesMsg4104()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t ( id int );
            insert t output deleted.id values (1)
            """, 4104);
        Assert.Contains("could not be bound", ex.Message);
    }

    [TestMethod]
    public void InsertOutput_PersistsRowsInTable()
    {
        var simulation = new Simulation();
        using (var reader = simulation.CreateCommand("""
            create table t ( id int identity(1, 1) not null, name nvarchar(20) not null );
            insert t (name) output inserted.id values ('a'), ('b')
            """).ExecuteReader())
        {
            while (reader.Read()) { }
        }

        using var verify = simulation.CreateCommand("select id, name from t").ExecuteReader();
        var rows = new List<(int, string)>();
        while (verify.Read())
            rows.Add((verify.GetInt32(0), verify.GetString(1)));
        rows.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        CollectionAssert.AreEqual(new[] { (1, "a"), (2, "b") }, rows);
    }

    [TestMethod]
    public void Merge_ON1Equals0_NotMatched_InsertsAllSourceRows()
    {
        var simulation = new Simulation();
        var affected = simulation.ExecuteNonQuery("""
            create table t ( id int identity(1, 1) not null, name nvarchar(20) not null );
            merge into t using (values ('a'), ('b'), ('c')) as i (name) on 1 = 0
            when not matched then insert (name) values (i.name);
            """);

        Assert.AreEqual(3, affected);

        using var reader = simulation.CreateCommand("select id, name from t").ExecuteReader();
        var rows = new List<(int, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1)));
        rows.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        CollectionAssert.AreEqual(new[] { (1, "a"), (2, "b"), (3, "c") }, rows);
    }

    [TestMethod]
    public void Merge_OutputProjectsInsertedAndSourceColumns()
    {
        // EF Core's multi-row insert pattern: OUTPUT projects INSERTED.id (auto-key) plus a positional
        // column from the source alias so EF can stitch generated keys back to original entities.
        using var reader = new Simulation().CreateCommand("""
            create table t ( id int identity(1, 1) not null, name nvarchar(20) not null );
            merge into t using (values ('a', 0), ('b', 1)) as i (name, _Position) on 1 = 0
            when not matched then insert (name) values (i.name)
            output inserted.id, i._Position;
            """).ExecuteReader();

        Assert.AreEqual("id", reader.GetName(0));
        Assert.AreEqual("_Position", reader.GetName(1));

        var rows = new List<(int Id, int Pos)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        rows.Sort((x, y) => x.Pos.CompareTo(y.Pos));
        Assert.HasCount(2, rows);
        Assert.AreEqual(0, rows[0].Pos);
        Assert.AreEqual(1, rows[1].Pos);
        Assert.AreEqual(1, rows[0].Id);
        Assert.AreEqual(2, rows[1].Id);
    }

    [TestMethod]
    public void Merge_WithoutInto_AcceptsEFsForm()
    {
        // EF Core emits "MERGE [target]" with no INTO; SQL Server accepts both.
        var affected = new Simulation().ExecuteNonQuery("""
            create table t ( id int identity(1, 1) not null, v int not null );
            merge t using (values (10)) as i (v) on 1 = 0
            when not matched then insert (v) values (i.v);
            """);
        Assert.AreEqual(1, affected);
    }

    [TestMethod]
    public void Merge_AutoPopulatesRowVersionOnNotMatchedInsert()
    {
        // Regression: standalone INSERT auto-bumped rowversion; the parallel MERGE path missed it.
        var simulation = new Simulation();
        var affected = simulation.ExecuteNonQuery("""
            create table t (id int primary key, name nvarchar(50), rv rowversion);
            merge t using (values (1, 'a'), (2, 'b')) as s (id, name) on 1 = 0
            when not matched then insert (id, name) values (s.id, s.name);
            """);
        Assert.AreEqual(2, affected);

        using var reader = simulation.ExecuteReader("select id, rv from t order by id");
        Assert.IsTrue(reader.Read());
        var row1 = (byte[])reader.GetValue(1);
        Assert.IsTrue(reader.Read());
        var row2 = (byte[])reader.GetValue(1);
        Assert.AreNotEqual(BitConverter.ToString(row1), BitConverter.ToString(row2));
    }

    // MERGE INSERT branch must refuse explicit values for rowversion (mirrors INSERT's Msg 273).
    [TestMethod]
    public void Merge_ExplicitRowVersionInColumnList_RaisesMsg273()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int primary key, rv rowversion);
            merge t using (values (1, 0x0000000000000001)) as s (id, rv) on 1 = 0
            when not matched then insert (id, rv) values (s.id, s.rv);
            """, 273);

    // Simulator parses WHEN MATCHED but throws if the ON predicate ever steers a source row there.
    [TestMethod]
    public void Merge_WhenMatchedFires_RaisesNotSupported()
        => _ = Assert.Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery("""
            create table t ( id int, v int );
            merge t using (values (1, 99)) as i (id, v) on 1 = 1
            when matched then update set v = i.v
            when not matched then insert (id, v) values (i.id, i.v);
            """));

    [TestMethod]
    public void Merge_FirstSourceTuple_DeterminesAliasSchema()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t ( id int identity(1, 1) not null, v int not null );
            merge t using (values (10, 0), (20, 1), (30, 2)) as i (v, ord) on 1 = 0
            when not matched then insert (v) values (i.v)
            output inserted.id, i.ord;
            """).ExecuteReader();
        var pairs = new List<(int Id, int Ord)>();
        while (reader.Read())
            pairs.Add((reader.GetInt32(0), reader.GetInt32(1)));
        pairs.Sort((a, b) => a.Ord.CompareTo(b.Ord));
        CollectionAssert.AreEqual(new[] { (1, 0), (2, 1), (3, 2) }, pairs);
    }

    [TestMethod]
    public void Merge_UpdatesScopeIdentity()
        => Assert.AreEqual(2m, new Simulation().ExecuteScalar<decimal>("""
            create table t ( id int identity(1, 1) not null, v int not null );
            merge t using (values (10), (20)) as i (v) on 1 = 0
            when not matched then insert (v) values (i.v);
            select scope_identity()
            """));
}
