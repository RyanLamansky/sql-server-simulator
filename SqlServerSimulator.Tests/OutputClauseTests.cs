using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Direct-SQL coverage for the <c>INSERT ... OUTPUT INSERTED.&lt;col&gt;</c>
/// clause and the narrowly-shaped <c>MERGE INTO ... USING (VALUES) ON 1 = 0
/// WHEN NOT MATCHED THEN INSERT ... OUTPUT</c> form that EF Core emits for
/// multi-row SaveChanges. Behavior pinned here; EF-side coverage lives in
/// <c>EFCoreIdentity</c>.
/// </summary>
[TestClass]
public class OutputClauseTests
{
    [TestMethod]
    public void InsertOutput_SingleRowProjectsGeneratedIdentity()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, name nvarchar(20) not null )");

        using var reader = simulation
            .CreateCommand("insert into t (name) output inserted.id values ('a')")
            .ExecuteReader();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.IsFalse(reader.Read());
    }

    [TestMethod]
    public void InsertOutput_MultipleColumnsAndAlias()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, name nvarchar(20) not null )");

        using var reader = simulation
            .CreateCommand("insert into t (name) output inserted.id as NewId, inserted.name values ('a')")
            .ExecuteReader();

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
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, name nvarchar(20) not null )");

        using var reader = simulation
            .CreateCommand("insert into t (name) output inserted.id values ('a'), ('b'), ('c')")
            .ExecuteReader();

        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ids);
    }

    [TestMethod]
    public void InsertOutput_OnNonIdentityColumns_StillProjects()
    {
        // OUTPUT isn't identity-specific — it streams whatever columns the
        // caller asks for from the inserted row.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( a int, b int )");

        using var reader = simulation
            .CreateCommand("insert into t output inserted.b, inserted.a values (1, 2)")
            .ExecuteReader();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.AreEqual(1, reader.GetInt32(1));
    }

    [TestMethod]
    public void InsertOutput_ExpressionInProjection_Evaluates()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, name nvarchar(20) not null )");

        using var reader = simulation
            .CreateCommand("insert into t (name) output inserted.id + 100 as Bumped values ('only')")
            .ExecuteReader();

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Bumped", reader.GetName(0));
        Assert.AreEqual(101, reader.GetInt32(0));
    }

    [TestMethod]
    public void InsertOutput_UnprefixedColumnReference_RaisesMsg4104()
    {
        // Real SQL Server: identifiers in OUTPUT must be prefixed with INSERTED
        // or DELETED (or a MERGE source alias). Bare/destination-prefixed
        // references fail with Msg 4104 "could not be bound".
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( a int )");

        var ex = Assert.Throws<DbException>(() =>
            simulation.ExecuteScalar("insert into t output a values (1)"));
        Assert.Contains("could not be bound", ex.Message);
    }

    [TestMethod]
    public void InsertOutput_DeletedReference_RaisesMsg4104()
    {
        // INSERT has no DELETED rows; referencing DELETED.col fails like real SQL Server.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int )");

        var ex = Assert.Throws<DbException>(() =>
            simulation.ExecuteScalar("insert into t output deleted.id values (1)"));
        Assert.Contains("could not be bound", ex.Message);
    }

    [TestMethod]
    public void InsertOutput_PersistsRowsInTable()
    {
        // OUTPUT shouldn't change INSERT's persistence semantics — the rows
        // are still inserted, just with the projection streamed back.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, name nvarchar(20) not null )");

        using (var reader = simulation
            .CreateCommand("insert into t (name) output inserted.id values ('a'), ('b')")
            .ExecuteReader())
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
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, name nvarchar(20) not null )");

        var affected = simulation.ExecuteNonQuery("""
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
        // EF Core's multi-row insert pattern: OUTPUT projects both INSERTED.id
        // (the auto-generated key) and a positional column carried through the
        // source alias so EF can stitch generated keys back to original entities.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, name nvarchar(20) not null )");

        using var reader = simulation
            .CreateCommand("""
                merge into t using (values ('a', 0), ('b', 1)) as i (name, _Position) on 1 = 0
                when not matched then insert (name) values (i.name)
                output inserted.id, i._Position;
                """)
            .ExecuteReader();

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
        // EF Core emits "MERGE [target]" with no INTO; real SQL Server accepts
        // both forms. The simulator must follow.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, v int not null )");

        var affected = simulation.ExecuteNonQuery("""
            merge t using (values (10)) as i (v) on 1 = 0
            when not matched then insert (v) values (i.v);
            """);
        Assert.AreEqual(1, affected);
    }

    [TestMethod]
    public void Merge_WhenMatchedFires_RaisesNotSupported()
    {
        // The simulator parses WHEN MATCHED syntactically but throws if the ON
        // predicate ever steers a source row into that branch. EF never does
        // (its predicate is literal `1 = 0`), but a hand-written MERGE that
        // does should fail loudly so the gap is visible.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int, v int )");

        _ = Assert.Throws<NotSupportedException>(() => simulation.ExecuteNonQuery("""
            merge t using (values (1, 99)) as i (id, v) on 1 = 1
            when matched then update set v = i.v
            when not matched then insert (id, v) values (i.id, i.v);
            """));
    }

    [TestMethod]
    public void Merge_FirstSourceTuple_DeterminesAliasSchema()
    {
        // Source-alias column types come from the first VALUES tuple — used
        // for OUTPUT planning. A literal 0 here is int; OUTPUT picks that up
        // even though all real SQL Server cares about at runtime is the value.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, v int not null )");

        using var reader = simulation
            .CreateCommand("""
                merge t using (values (10, 0), (20, 1), (30, 2)) as i (v, ord) on 1 = 0
                when not matched then insert (v) values (i.v)
                output inserted.id, i.ord;
                """)
            .ExecuteReader();
        var pairs = new List<(int Id, int Ord)>();
        while (reader.Read())
            pairs.Add((reader.GetInt32(0), reader.GetInt32(1)));
        pairs.Sort((a, b) => a.Ord.CompareTo(b.Ord));
        CollectionAssert.AreEqual(new[] { (1, 0), (2, 1), (3, 2) }, pairs);
    }

    [TestMethod]
    public void Merge_UpdatesScopeIdentity()
    {
        // SCOPE_IDENTITY/@@IDENTITY behave the same after MERGE-driven inserts.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int identity(1, 1) not null, v int not null )");

        _ = simulation.ExecuteNonQuery("""
            merge t using (values (10), (20)) as i (v) on 1 = 0
            when not matched then insert (v) values (i.v);
            """);

        Assert.AreEqual(2m, simulation.ExecuteScalar<decimal>("select scope_identity()"));
    }
}
