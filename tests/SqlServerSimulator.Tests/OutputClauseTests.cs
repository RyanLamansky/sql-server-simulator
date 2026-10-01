using System.Data.Common;

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
    public void InsertOutput_UnprefixedColumnReference_RaisesMsg207()
        => new Simulation().AssertSqlError("""
            create table t ( a int );
            insert t output a values (1)
            """, 207, "Invalid column name 'a'.");

    [TestMethod]
    [DataRow("insert t output inserted.foo values (1)")]
    [DataRow("update t set a = 1 output inserted.foo")]
    [DataRow("delete t output deleted.foo")]
    public void Output_PseudoTableUnknownColumn_RaisesMsg207(string statement)
        => new Simulation().AssertSqlError($"create table t (a int); {statement}", 207, "Invalid column name 'foo'.");

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

    [TestMethod]
    public void InsertOutput_InsertedStar_ExpandsAllColumns()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t (id int identity, v int, c char(2) default 'AB');
            insert t (v) output inserted.* values (10), (20)
            """).ExecuteReader();

        Assert.AreEqual(3, reader.FieldCount);
        Assert.AreEqual("id", reader.GetName(0));
        Assert.AreEqual("v", reader.GetName(1));
        Assert.AreEqual("c", reader.GetName(2));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual(10, reader.GetInt32(1));
        Assert.AreEqual("AB", reader.GetString(2));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.AreEqual(20, reader.GetInt32(1));
    }

    [TestMethod]
    public void InsertOutput_StarMixedWithExtra_AppendsAfter()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t (id int identity, v int);
            insert t (v) output inserted.*, 'tag' as note values (5)
            """).ExecuteReader();

        Assert.AreEqual(3, reader.FieldCount);
        Assert.AreEqual("id", reader.GetName(0));
        Assert.AreEqual("v", reader.GetName(1));
        Assert.AreEqual("note", reader.GetName(2));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual(5, reader.GetInt32(1));
        Assert.AreEqual("tag", reader.GetString(2));
    }

    [TestMethod]
    public void UpdateOutput_InsertedAndDeletedStar_BothExpand()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t (id int primary key, v int);
            insert t values (1, 10), (2, 20);
            update t set v = v + 100
            output inserted.*, deleted.*
            where id = 1
            """).ExecuteReader();

        Assert.AreEqual(4, reader.FieldCount);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual(110, reader.GetInt32(1));
        Assert.AreEqual(1, reader.GetInt32(2));
        Assert.AreEqual(10, reader.GetInt32(3));
    }

    [TestMethod]
    public void DeleteOutput_DeletedStar_ExpandsAllColumns()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t (id int primary key, v int, n nvarchar(10));
            insert t values (1, 10, 'a'), (2, 20, 'b');
            delete t output deleted.* where id = 1
            """).ExecuteReader();

        Assert.AreEqual(3, reader.FieldCount);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual(10, reader.GetInt32(1));
        Assert.AreEqual("a", reader.GetString(2));
    }

    [TestMethod]
    public void DeleteOutput_InsertedStar_RaisesMsg4104()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int primary key, v int);
            delete t output inserted.* where id = 1
            """, 4104);

    [TestMethod]
    public void InsertOutput_DeletedStar_RaisesMsg4104()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int primary key, v int);
            insert t output deleted.* values (1, 10)
            """, 4104);

    [TestMethod]
    public void InsertOutput_StarInto_MatchesTargetColumnCount()
    {
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "create table t (id int identity, v int); create table audit (id int, v int)";
            _ = setup.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "insert t (v) output inserted.* into audit values (50)";
            _ = ins.ExecuteNonQuery();
        }
        using var verify = conn.CreateCommand();
        verify.CommandText = "select id, v from audit";
        using var reader = verify.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual(50, reader.GetInt32(1));
    }

    [TestMethod]
    public void MergeOutput_InsertedAndDeletedStar_NullForUnmatchedSide()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t (id int primary key, v int);
            insert t values (1, 10);
            merge t as tg
            using (values (1, 100), (2, 200)) as s (id, v) on tg.id = s.id
            when matched then update set v = s.v
            when not matched by target then insert (id, v) values (s.id, s.v)
            output $action, inserted.*, deleted.*;
            """).ExecuteReader();

        Assert.AreEqual(5, reader.FieldCount);
        var rows = new List<(string action, int? insId, int? insV, int? delId, int? delV)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }
        rows.Sort((a, b) => (a.insId ?? 0).CompareTo(b.insId ?? 0));
        Assert.HasCount(2, rows);
        Assert.AreEqual("UPDATE", rows[0].action);
        Assert.AreEqual(1, rows[0].insId);
        Assert.AreEqual(100, rows[0].insV);
        Assert.AreEqual(1, rows[0].delId);
        Assert.AreEqual(10, rows[0].delV);
        Assert.AreEqual("INSERT", rows[1].action);
        Assert.AreEqual(2, rows[1].insId);
        Assert.AreEqual(200, rows[1].insV);
        Assert.IsNull(rows[1].delId);
        Assert.IsNull(rows[1].delV);
    }

    [TestMethod]
    public void MergeOutput_SourceAliasStar_ExpandsSourceColumns()
    {
        using var reader = new Simulation().CreateCommand("""
            create table t (id int primary key, v int);
            merge t
            using (values (5, 50)) as s (id, v) on t.id = s.id
            when not matched by target then insert (id, v) values (s.id, s.v)
            output s.*;
            """).ExecuteReader();

        Assert.AreEqual(2, reader.FieldCount);
        Assert.AreEqual("id", reader.GetName(0));
        Assert.AreEqual("v", reader.GetName(1));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(5, reader.GetInt32(0));
        Assert.AreEqual(50, reader.GetInt32(1));
    }

    [TestMethod]
    public void Output_StarWithUnknownQualifier_Msg4104()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int primary key, v int);
            insert t output foo.* values (1, 10)
            """, 4104);

    // === MERGE … OUTPUT … INTO ===
    //
    // MERGE reached this by sharing the one projection type; its own fork
    // never had an INTO branch, so the clause used to be left unconsumed and
    // the statement died on the trailing-semicolon check (Msg 10713).

    [TestMethod]
    public void MergeOutputInto_TableVariable_CapturesRowsAndActionPerBranch()
    {
        var sim = new Simulation();
        Assert.AreEqual("1=UPDATE,2=INSERT", sim.ExecuteScalar("""
            create table m (id int primary key, v int);
            insert m values (1, 10);
            declare @o table (id int, act varchar(10));
            merge m using (values (1, 99), (2, 20)) as s (id, v) on m.id = s.id
            when matched then update set v = s.v
            when not matched then insert (id, v) values (s.id, s.v)
            output inserted.id, $action into @o;
            select string_agg(cast(id as varchar) + '=' + act, ',') within group (order by id) from @o
            """));
    }

    [TestMethod]
    public void MergeOutputInto_RealTableWithColumnList_Writes()
    {
        var sim = new Simulation();
        Assert.AreEqual("3,30,INSERT", sim.ExecuteScalar("""
            create table m (id int primary key, v int);
            create table sink (a int, b int, act varchar(10));
            merge m using (values (3, 30)) as s (id, v) on m.id = s.id
            when not matched then insert (id, v) values (s.id, s.v)
            output inserted.id, inserted.v, $action into sink (a, b, act);
            select cast(a as varchar) + ',' + cast(b as varchar) + ',' + act from sink
            """));
    }

    /// <summary>DELETED and the source alias resolve into the target too.</summary>
    [TestMethod]
    public void MergeOutputInto_ResolvesDeletedAndSourceAlias()
    {
        var sim = new Simulation();
        Assert.AreEqual("10|77", sim.ExecuteScalar("""
            create table m (id int primary key, v int);
            insert m values (1, 10);
            declare @o table (oldv int, srcv int);
            merge m using (values (1, 77)) as s (id, v) on m.id = s.id
            when matched then update set v = s.v
            output deleted.v, s.v into @o;
            select cast(oldv as varchar) + '|' + cast(srcv as varchar) from @o
            """));
    }

    /// <summary>
    /// An INTO target consumes the rows, so the MERGE is a non-query — it must
    /// not leave an empty result set behind the way it did before the
    /// suppression was shared.
    /// </summary>
    [TestMethod]
    public void MergeOutputInto_YieldsNoResultSet()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table m (id int primary key, v int)");
        using var reader = sim.ExecuteReader("""
            declare @o table (id int);
            merge m using (values (1, 10)) as s (id, v) on m.id = s.id
            when not matched then insert (id, v) values (s.id, s.v)
            output inserted.id into @o;
            """);
        Assert.AreEqual(0, reader.FieldCount);
    }

    /// <summary>Without INTO, the rows still come back to the client.</summary>
    [TestMethod]
    public void MergeOutputWithoutInto_StillReturnsRows()
    {
        var sim = new Simulation();
        Assert.AreEqual(4, sim.ExecuteScalar("""
            create table m (id int primary key, v int);
            merge m using (values (4, 40)) as s (id, v) on m.id = s.id
            when not matched then insert (id, v) values (s.id, s.v)
            output inserted.id;
            """));
    }

    /// <summary>
    /// And INTO is the legal way to emit OUTPUT against a triggered target,
    /// which Msg 334 otherwise forbids — the combination that had no
    /// expressible form until MERGE gained INTO.
    /// </summary>
    [TestMethod]
    public void MergeOutputInto_IsAllowedAgainstATriggeredTarget()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table m (id int primary key, v int)",
            "create trigger tr on m after insert as begin set nocount on; end");
        Assert.AreEqual(6, sim.ExecuteScalar("""
            declare @o table (id int);
            merge m using (values (6, 60)) as s (id, v) on m.id = s.id
            when not matched then insert (id, v) values (s.id, s.v)
            output inserted.id into @o;
            select id from @o
            """));
    }

    /// <summary>
    /// OUTPUT INTO a column list measures the projection against it the way
    /// INSERT … SELECT does: Msg 121 for more items, Msg 120 for fewer (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("insert t (v) output inserted.id, inserted.v into u (a) values (1)", 121)]
    [DataRow("insert t (v) output inserted.v into u (a, b) values (1)", 120)]
    public void OutputIntoAColumnList_MeasuresTheProjectionAgainstIt(string statement, int number)
        => new Simulation().AssertSqlError("create table t (id int identity, v int); create table u (a int, b int); " + statement, number);

    /// <summary>
    /// Every result set <paramref name="sql"/> returns, each as its column
    /// names then its rows, values joined by <c>|</c>.
    /// </summary>
    private static string ResultSets(DbConnection connection, string sql)
    {
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        List<string> sets = [];
        do
        {
            List<string> lines = [string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName))];
            while (reader.Read())
                lines.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            sets.Add(string.Join(";", lines));
        }
        while (reader.NextResult());
        return string.Join(" / ", sets);
    }

    /// <summary>
    /// A client <c>OUTPUT</c> may follow an <c>OUTPUT … INTO</c>: the statement
    /// writes the target and returns the second clause's rows (probed
    /// 2026-10-01 against SQL Server 2025 for all four verbs).
    /// </summary>
    [TestMethod]
    [DataRow(
        "declare @log table (a int); insert t output inserted.a into @log output inserted.a, 'client' as c values (1), (2); select @@rowcount r; select a from @log",
        "a|c;1|client;2|client / r;2 / a;1;2")]
    [DataRow(
        "create table logt (a int, b int default 9); insert t output inserted.a into logt (a) output inserted.a select 7 union all select 8; select a, b from logt",
        "a;7;8 / a|b;7|9;8|9")]
    [DataRow(
        "declare @log table (a int); insert t values (1), (2); update t set a = a + 1 output inserted.a into @log output deleted.a, inserted.a; select @@rowcount r; select a from @log order by a",
        "a|a;1|2;2|3 / r;2 / a;2;3")]
    [DataRow(
        "declare @log table (a int); insert t values (1), (2), (3); delete t output deleted.a into @log output deleted.a where a > 1; select a from @log order by a; select a from t",
        "a;2;3 / a;2;3 / a;1")]
    [DataRow(
        "declare @log table (a int, act nvarchar(10)); insert t values (11); merge t using (values (11), (50)) s (x) on t.a = s.x when matched then update set a = 12 when not matched then insert (a) values (s.x) output inserted.a, $action into @log output $action, inserted.a, s.x, deleted.a; select @@rowcount r; select a, act from @log order by a",
        "$action|a|x|a;UPDATE|12|11|11;INSERT|50|50|NULL / r;2 / a|act;12|UPDATE;50|INSERT")]
    public void OutputInto_ThenAClientOutput_WritesTheTargetAndReturnsTheRows(string sql, string expected)
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (a int)").ExecuteNonQuery();
        Assert.AreEqual(expected, ResultSets(connection, sql));
    }

    [TestMethod]
    public void OutputInto_ThenAClientOutput_ThroughAViewAndAProcedure()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int)",
            "create view v as select a from t",
            "create table logt (a int)",
            "create procedure p as insert t output inserted.a into logt output inserted.a * 10 x values (4)");
        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual("a|;21|v", ResultSets(connection, "declare @log table (a int); insert v output inserted.a into @log output inserted.a, 'v' values (21)"));
        Assert.AreEqual("x;40", ResultSets(connection, "exec p"));
        Assert.AreEqual("x;40", ResultSets(connection, "exec p"));
        Assert.AreEqual(2, connection.CreateCommand("select count(*) from logt").ExecuteScalar());
    }

    /// <summary>
    /// Only that order parses: a second <c>OUTPUT</c> after a client one is
    /// read as its column alias, a second <c>INTO</c> is the syntax error at
    /// the keyword, and a third clause at its <c>OUTPUT</c>.
    /// </summary>
    [TestMethod]
    [DataRow("insert t output inserted.a output inserted.a into @log values (3)", "inserted")]
    [DataRow("insert t output inserted.a as x output inserted.a into @log values (3)", "output")]
    [DataRow("insert t output inserted.a into @log (a) output inserted.a into @log values (3)", "into")]
    [DataRow("insert t output inserted.a into @log output inserted.a output inserted.a values (3)", "inserted")]
    [DataRow("insert t output inserted.a into @log output * values (3)", "*")]
    public void OutputClauses_InAnyOtherShape_AreSyntaxErrors(string statement, string near)
    {
        var error = new Simulation().AssertSqlError("create table t (a int); declare @log table (a int); " + statement, near == "into" ? 156 : 102);
        Assert.Contains($"'{near}'", error.Message);
    }

    /// <summary>
    /// The client clause is what a triggered target refuses (Msg 334), and a
    /// function body refuses the statement as it refuses any write (Msg 443).
    /// </summary>
    [TestMethod]
    public void TheClientClause_IsRefusedWhereAClientOutputIs()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int)", "create trigger trg on t after insert as select 1 x where 1 = 0");
        _ = simulation.AssertSqlError("declare @log table (a int); insert t output inserted.a into @log output inserted.a values (1)", 334);
        _ = simulation.AssertSqlError(
            "create function f() returns @r table (a int) as begin declare @log table (a int); insert @r output inserted.a into @log output inserted.a values (1); return end",
            443);
    }

    /// <summary>
    /// An <c>OUTPUT … INTO</c> target's PRIMARY KEY / UNIQUE keys are enforced
    /// as an INSERT's are — a duplicate ends the statement and takes back its
    /// own writes, two NULLs collide, and <c>IGNORE_DUP_KEY</c> drops the row
    /// (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("k int primary key", "inserted.k", "(1), (1)", 2627, 0)]
    [DataRow("k int null unique", "null", "(7), (8)", 2627, 0)]
    [DataRow("k int not null, unique (k) with (ignore_dup_key = on)", "inserted.k", "(1), (1), (2)", 0, 2)]
    public void OutputIntoAKeyedTable_EnforcesItsKeys(string target, string projection, string values, int error, int landed)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ({target}); create table s (k int)");
        var statement = $"insert s output {projection} into t values {values}";
        if (error != 0)
            _ = simulation.AssertSqlError(statement, error);
        else
            _ = simulation.ExecuteNonQuery(statement);
        Assert.AreEqual(landed, simulation.ExecuteScalar("select count(*) from t"));
    }
}
