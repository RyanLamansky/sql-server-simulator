using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server 2025's preview vector surface: <c>CREATE VECTOR INDEX</c>, the
/// read-only table it leaves, <c>VECTOR_SEARCH</c> and the <c>float16</c>
/// base type, each behind the <c>PREVIEW_FEATURES</c> scoped configuration.
/// Every expectation was probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class VectorSearchTests
{
    /// <summary>A simulation whose default database has the preview switch on.</summary>
    private static Simulation Preview()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database scoped configuration set preview_features = on");
        return sim;
    }

    /// <summary>A preview simulation holding <c>t</c> — five 2-dimension rows, one NULL — indexed for <paramref name="metric"/>.</summary>
    private static Simulation Indexed(string metric = "euclidean")
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery($"""
            create table t (id int not null primary key, v vector(2), x int);
            insert t values (1, '[1,0]', 10), (2, '[0,1]', 20), (3, '[1,1]', 30), (4, '[1,0.5]', 40), (5, null, 50);
            create vector index vx on t(v) with (metric = '{metric}')
            """);
        return sim;
    }

    private static void AssertError(Simulation sim, string commandText, int number, byte state, string message)
    {
        var ex = sim.AssertSqlError(commandText, number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual(state, ex.Errors[0].State);
    }

    private static string Search(Simulation sim, string query) =>
        string.Join(";", sim.ExecuteReader(query).EnumerateRecords().Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.GetValue(0)}:{r.GetValue(1)}")));

    [TestMethod]
    public void CreateVectorIndex_WithoutPreview_IsUnknownObjectType()
        => AssertError(new Simulation(), """
            create table t (id int not null primary key, v vector(2));
            create vector index vx on t(v) with (metric = 'cosine')
            """, 343, 2, "Unknown object type 'vector' used in a CREATE, DROP, or ALTER statement.");

    [TestMethod]
    public void CreateVectorIndex_CatalogRows()
    {
        var sim = Indexed("cosine");
        AreEqual("vx", sim.ExecuteScalar("select name from sys.indexes where object_id = object_id('t') and type = 8 and type_desc = 'VECTOR' and index_id = 1152000"));
        AreEqual("DiskANN", sim.ExecuteScalar("select vector_index_type from sys.vector_indexes"));
        AreEqual("COSINE", sim.ExecuteScalar("select distance_metric from sys.vector_indexes"));
        AreEqual("""{"StartId":"3", "L":"48", "M":"8", "R":"48"}""", sim.ExecuteScalar("select build_parameters from sys.vector_indexes"));
        AreEqual("2|0|0", sim.ExecuteScalar("select concat_ws('|', column_id, key_ordinal, cast(is_included_column as int)) from sys.index_columns where index_id = 1152000"));
        AreEqual(1152000, sim.ExecuteScalar("select indexproperty(object_id('t'), 'vx', 'IndexID')"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.stats where object_id = object_id('t') and stats_id > 1"));
        using var reader = sim.ExecuteReader("exec sp_helpindex 't'");
        AreEqual(1, reader.EnumerateRecords().Count());
    }

    [TestMethod]
    public void CreateVectorIndex_ReportsJoinOrderWarning()
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, v vector(2))");
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        _ = connection.CreateCommand("create vector index vx on t(v) with (metric = 'dot')").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { 8625 }, messages);
    }

    [TestMethod]
    [DataRow("euclidean", "30")]
    [DataRow("cosine", "40")]
    [DataRow("dot", "40")]
    public void StartId_IsTheVectorNearestTheMean(string metric, string startId)
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery($"""
            create table t (id int not null primary key, v vector(2));
            insert t values (10, '[1,0]'), (20, '[0,1]'), (30, '[1,1]'), (40, '[5,5]'), (50, '[-1,0]');
            create vector index vx on t(v) with (metric = '{metric}')
            """);
        AreEqual(startId, sim.ExecuteScalar("select json_value(build_parameters, '$.StartId') from sys.vector_indexes"));
    }

    [TestMethod]
    [DataRow("(1, '[1,0]'), (2, '[1,0]'), (3, '[1,0]')", "2")]
    [DataRow("(1, '[5,0]'), (2, '[0,0]'), (3, '[10,0]')", "1")]
    [DataRow("(1, '[4,0]'), (2, '[0,0]'), (3, '[10,0]'), (4, '[6,0]')", "1")]
    [DataRow("(1, null)", "0")]
    public void StartId_Ties(string rows, string startId)
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery($"""
            create table t (id int not null primary key, v vector(2));
            insert t values {rows};
            create vector index vx on t(v) with (metric = 'euclidean')
            """);
        AreEqual(startId, sim.ExecuteScalar("select json_value(build_parameters, '$.StartId') from sys.vector_indexes"));
    }

    [TestMethod]
    [DataRow("create vector index vx on t(v)", 102, "Incorrect syntax near ')'.")]
    [DataRow("create vector index vx on t(v) with (type = 'diskann')", 153, "Invalid usage of the option metric in the CREATE VECTOR INDEX statement.")]
    [DataRow("create vector index vx on t(v) with (metric = 'manhattan')", 102, "Incorrect syntax near 'manhattan'.")]
    [DataRow("create vector index vx on t(v) with (metric = cosine)", 102, "Incorrect syntax near 'cosine'.")]
    [DataRow("create vector index vx on t(v) with (metric = 'cosine', type = 'hnsw')", 102, "Incorrect syntax near 'hnsw'.")]
    [DataRow("create vector index vx on t(v) with (metric = 'cosine', maxdop = -1)", 304, "'-1' is out of range for index/statistics option 'maxdop'. See sp_configure option 'max degree of parallelism' for valid values.")]
    [DataRow("create vector index vx on t(v) with (metric = 'cosine', maxdop = 32768)", 304, "'32768' is out of range for index/statistics option 'maxdop'. See sp_configure option 'max degree of parallelism' for valid values.")]
    [DataRow("create vector index vx on t(v) with (metric = 'cosine', fillfactor = 50)", 155, "'fillfactor' is not a recognized CREATE VECTOR INDEX option.")]
    [DataRow("create vector index vx on t(v) with (metric = 'cosine', foo = 1)", 155, "'foo' is not a recognized CREATE VECTOR INDEX option.")]
    [DataRow("create vector index vx on t(x) with (metric = 'cosine')", 42215, "Could not create the vector index on the column 'x' on table 't', because it is not of type vector.")]
    [DataRow("create vector index vx on t(zz) with (metric = 'cosine')", 1911, "Column name 'zz' does not exist in the target table, index or view.")]
    [DataRow("create vector index vx on nosuch(v) with (metric = 'cosine')", 1088, "Cannot find the object \"nosuch\" because it does not exist or you do not have permissions.")]
    [DataRow("create vector index vx on h(v) with (metric = 'cosine')", 42217, "Table 'h' must have a clustered primary key on a single 4 byte INT column to create a vector index.")]
    [DataRow("create vector index vx on b(v) with (metric = 'cosine')", 42217, "Table 'b' must have a clustered primary key on a single 4 byte INT column to create a vector index.")]
    [DataRow("create vector index vx on #tmp(v) with (metric = 'cosine')", 42220, "Cannot create the vector index on temp objects. '#tmp' is identified as a temp object.")]
    [DataRow("create vector index pk on t(v) with (metric = 'cosine')", 1913, "The operation failed because an index or statistics with name 'pk' already exists on table 't'.")]
    [DataRow("begin tran; create vector index vx on t(v) with (metric = 'cosine')", 574, "CREATE VECTOR INDEX statement cannot be used inside a user transaction.")]
    [DataRow("set ansi_nulls off; create vector index vx on t(v) with (metric = 'cosine')", 1934, "CREATE VECTOR INDEX failed because the following SET options have incorrect settings: 'ANSI_NULLS'. Verify that SET options are correct for use with indexed views and/or indexes on computed columns and/or filtered indexes and/or query notifications and/or XML data type methods and/or spatial index operations.")]
    public void CreateVectorIndex_Refusals(string statement, int number, string message)
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null constraint pk primary key, v vector(2), x int);
            create table h (id int not null, v vector(2));
            create table b (id bigint not null primary key, v vector(2));
            """);
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table #tmp (id int not null primary key, v vector(2))").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand(statement).ExecuteNonQuery());
        AreEqual(number, ex.Number);
        AreEqual(message, ex.Errors[0].Message);
    }

    [TestMethod]
    public void CreateVectorIndex_OnePerColumn_OnePerTableColumnPair()
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, v vector(2), w vector(2));
            create vector index vx on t(v) with (metric = 'cosine');
            create vector index vw on t(w) with (metric = 'dot', type = 'DiskANN ', maxdop = 1) on [primary]
            """);
        AreEqual("vw:1152001,vx:1152000", sim.ExecuteScalar("select string_agg(concat(name, ':', index_id), ',') within group (order by name) from sys.vector_indexes"));
        _ = sim.AssertSqlError("create vector index vy on t(v) with (metric = 'euclidean')", 42230);
    }

    [TestMethod]
    [DataRow("insert t values (9, '[0,0]', 0)")]
    [DataRow("update t set x = 1 where id = 99")]
    [DataRow("delete t where 1 = 0")]
    [DataRow("merge t using (select 1 a) s on t.id = s.a when matched then update set x = 5;")]
    [DataRow("if 1 = 0 insert t values (9, '[0,0]', 0)")]
    public void VectorIndex_MakesTheTableReadOnly_WhileCompiling(string write)
    {
        var sim = Indexed();
        AssertError(sim, $"select 1; {write}", 42231, 1, "Data modification statement failed because table 't' has a vector index on it.");
        AreEqual(5, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void VectorIndex_ReadOnly_WaitsForItsStatement_InABatchWithDdl()
    {
        var sim = Indexed();
        // A batch holding DDL is optimized statement by statement, so an
        // untaken write is never refused and a taken one only when it runs.
        AreEqual(2, sim.ExecuteScalar("create table zz (a int); if 1 = 0 insert t values (9, '[0,0]', 0); select 2"));
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand("select 1; create table yy (a int); insert t values (9, '[0,0]', 0); select 2");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(1, reader.GetValue(0));
        AreEqual(42231, Throws<SimulatedSqlException>(() => reader.NextResult()).Number);
    }

    [TestMethod]
    public void VectorIndex_ReadOnly_EndsTheBatch_AndCanBeCaught()
    {
        var sim = Indexed();
        AreEqual(42231, sim.ExecuteScalar("begin try exec ('insert t values (9, ''[0,0]'', 0)') end try begin catch select error_number() end catch"));
        _ = sim.AssertSqlError("exec ('insert t values (9, ''[0,0]'', 0)'); select 2", 42231);
        AssertError(sim, "truncate table t", 42232, 1, "TRUNCATE TABLE statement failed because table 't' has a vector index on it.");
    }

    [TestMethod]
    public void VectorIndex_CascadeIntoIt_IsState3()
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery("""
            create table p (id int primary key);
            create table c (id int not null primary key, v vector(2), pid int references p(id) on delete cascade);
            insert p values (1); insert c values (1, '[1,0]', 1);
            create vector index vc on c(v) with (metric = 'cosine')
            """);
        AssertError(sim, "delete p", 42231, 3, "Data modification statement failed because table 'c' has a vector index on it.");
        AreEqual(1, sim.ExecuteScalar("select count(*) from c"));
    }

    [TestMethod]
    public void VectorIndex_ModuleWritingTheTable_FailsWhenItRuns()
    {
        var sim = Indexed();
        sim.ExecuteBatches("create procedure p as insert t values (9, '[0,0]', 0)");
        var ex = sim.AssertSqlError("exec p", 42231);
        AreEqual("p", ex.Errors[0].Procedure);
    }

    [TestMethod]
    public void DropIndex_MakesTheTableWritableAgain()
    {
        var sim = Indexed();
        AssertError(sim, "drop index t.vx", 3766, 1, "Cannot drop vector index 't.vx' using old 'Table.Index' syntax, use 'Index ON Table' syntax instead.");
        _ = sim.ExecuteNonQuery("drop index vx on t");
        AreEqual(6, sim.ExecuteScalar("insert t values (9, '[0,0]', 0); select count(*) from t"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.vector_indexes"));
    }

    [TestMethod]
    [DataRow("alter index vx on t rebuild", 42250)]
    [DataRow("alter index vx on t disable", 42250)]
    [DataRow("alter index all on t rebuild", 42250)]
    [DataRow("alter table t drop column v", 5074)]
    [DataRow("alter table t alter column v vector(2) not null", 5074)]
    [DataRow("alter table t drop constraint pk", 3768)]
    [DataRow("exec sp_rename 't.vx', 'vy', 'INDEX'", 290)]
    public void VectorIndex_RefusesChanges(string statement, int number)
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null constraint pk primary key, v vector(2), x int);
            create vector index vx on t(v) with (metric = 'cosine')
            """);
        _ = sim.AssertSqlError(statement, number);
        AreEqual("vx", sim.ExecuteScalar("select name from sys.vector_indexes"));
        _ = sim.ExecuteNonQuery("alter table t drop column x");
    }

    [TestMethod]
    [DataRow("euclidean", "1:0.20000000298023224;4:0.30000001192092896;3:0.800000011920929")]
    [DataRow("cosine", "1:0.019419312477111816;4:0.0352361798286438;3:0.1679496169090271")]
    [DataRow("dot", "3:-1.2000000476837158;4:-1.100000023841858;1:-1")]
    public void VectorSearch_ReturnsTheNearestInDistanceOrder(string metric, string expected)
        => AreEqual(expected, Search(Indexed(metric), $"""
            declare @q vector(2) = '[1,0.2]';
            select a.id, s.distance from vector_search(table = t as a, column = v, similar_to = @q, metric = '{metric}', top_n = 3) as s
            """));

    [TestMethod]
    public void VectorSearch_DistanceIsVectorDistance()
        => AreEqual(0, Indexed().ExecuteScalar("""
            declare @q vector(2) = '[0.3,0.9]';
            select count(*) from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 10) as s
            where s.distance <> vector_distance('euclidean', a.v, @q)
            """));

    [TestMethod]
    public void VectorSearch_TiesFollowTheKey()
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, v vector(2));
            insert t values (5, '[1,0]'), (3, '[1,0]'), (9, '[0,1]'), (1, '[1,0]'), (7, '[0,1]');
            create vector index vx on t(v) with (metric = 'cosine')
            """);
        AreEqual("1:0;3:0;5:0;7:1;9:1", Search(sim, """
            declare @q vector(2) = '[1,0]';
            select a.id, s.distance from vector_search(table = t as a, column = v, similar_to = @q, metric = 'cosine', top_n = 10) s
            """));
    }

    [TestMethod]
    public void VectorSearch_Star_PutsDistanceFirst()
    {
        using var reader = Indexed().ExecuteReader("""
            declare @q vector(2) = '[1,0]';
            select * from vector_search(table = t, column = v, similar_to = @q, metric = 'euclidean', top_n = 1)
            """);
        AreEqual("distance,id,v,x", string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
    }

    [TestMethod]
    public void VectorSearch_ComposesAsTwoSources()
    {
        var sim = Indexed();
        _ = sim.ExecuteNonQuery("create table q (qid int, qv vector(2)); insert q values (1, '[1,0]'), (2, '[0,1]'), (3, null)");
        // WHERE filters the TOP_N the search returned.
        AreEqual("4:0.30000001192092896;3:0.800000011920929", Search(sim, """
            declare @q vector(2) = '[1,0.2]';
            select a.id, s.distance from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 3) s where a.x > 10
            """));
        AreEqual("1:1;1:4;2:2;2:3;3:", Search(sim, """
            select q.qid, a.id from q outer apply vector_search(table = t as a, column = v, similar_to = q.qv, metric = 'euclidean', top_n = 2) s
            order by q.qid, s.distance
            """));
        AreEqual("1:1;2:", Search(sim, """
            declare @q vector(2) = '[1,0.2]';
            select q.qid, a.id from q left join vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) s on a.id = q.qid
            where q.qid < 3
            """));
        AreEqual(2, sim.ExecuteScalar("""
            select count(*) from q cross apply vector_search(table = t as a, column = v, similar_to = q.qv, metric = 'euclidean', top_n = q.qid) s
            where q.qid = 2
            """));
    }

    [TestMethod]
    public void VectorSearch_SelectInto_DistanceIsNotNullFloat()
        => AreEqual("id:int:0,distance:float:0", Indexed().ExecuteScalar("""
            declare @q vector(2) = '[1,0]';
            select a.id, s.distance into #r from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 2) s;
            select string_agg(concat(name, ':', type_name(system_type_id), ':', cast(is_nullable as int)), ',') within group (order by column_id)
            from tempdb.sys.columns where object_id = object_id('tempdb..#r')
            """));

    [TestMethod]
    [DataRow("0", "")]
    [DataRow("@zero", "")]
    [DataRow("@big", "1;4;3;2")]
    public void VectorSearch_TopN(string topN, string expected)
        => AreEqual(expected, string.Join(";", Indexed().ExecuteReader($"""
            declare @q vector(2) = '[1,0.2]', @zero int = 0, @big bigint = 3000000000;
            select a.id from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = {topN}) s
            """).EnumerateRecords().Select(r => r.GetValue(0))));

    [TestMethod]
    [DataRow("similar_to = @q, metric = 'euclidean', top_n = @null", 1014, "A TOP or FETCH clause contains an invalid value.")]
    [DataRow("similar_to = @q, metric = 'euclidean', top_n = 2.5", 1060, "The number of rows provided for a TOP or FETCH clauses row count parameter must be an integer.")]
    [DataRow("similar_to = @q, metric = 'euclidean', top_n = @dec", 1060, "The number of rows provided for a TOP or FETCH clauses row count parameter must be an integer.")]
    [DataRow("similar_to = @q, metric = 'euclidean', top_n = -1", 102, "Incorrect syntax near '-'.")]
    [DataRow("similar_to = @q, metric = 'euclidean', top_n = 1 + 1", 102, "Incorrect syntax near '+'.")]
    [DataRow("similar_to = @q, metric = 'euclidean'", 102, "Incorrect syntax near 'TOP_N'.")]
    [DataRow("similar_to = @q, top_n = 2", 102, "Incorrect syntax near 'top_n'.")]
    [DataRow("similar_to = @q, metric = @m, top_n = 2", 102, "Incorrect syntax near '@m'.")]
    [DataRow("similar_to = @q, metric = 'euclidean', top_n = 2,", 102, "Incorrect syntax near ')'.")]
    [DataRow("similar_to = cast(@q as vector(2)), metric = 'euclidean', top_n = 2", 102, "Incorrect syntax near '('.")]
    [DataRow("similar_to = '[1,0]', metric = 'euclidean', top_n = 2", 8116, "Argument data type varchar is invalid for argument 3 of vector_distance function.")]
    [DataRow("similar_to = @q3, metric = 'euclidean', top_n = 2", 42204, "The vector dimensions 2 and 3 do not match.")]
    [DataRow("similar_to = @q, metric = 'cosine', top_n = 2", 42227, "Cannot find a vector index with metric 'cosine' on column 'v'.")]
    [DataRow("similar_to = x, metric = 'euclidean', top_n = 2", 207, "Invalid column name 'x'.")]
    public void VectorSearch_ArgumentRefusals(string arguments, int number, string message)
        => AssertError(Indexed(), $"""
            declare @q vector(2) = '[1,0]', @q3 vector(3) = '[1,0,0]', @null int, @dec decimal(5, 1) = 2, @m varchar(10) = 'euclidean';
            select a.id from vector_search(table = t as a, column = v, {arguments}) s
            """, number, number == 42204 ? (byte)3 : (byte)1, message);

    [TestMethod]
    [DataRow("table = nosuch, column = v", 208, 240, "Invalid object name 'nosuch'.")]
    [DataRow("table = vw, column = v", 42217, 4, "Table '' must have a clustered primary key on a single 4 byte INT column to create a vector index.")]
    [DataRow("table = t, column = zz", 207, 20, "Invalid column name 'zz'.")]
    [DataRow("table = t, column = x", 42226, 1, "The column 'x' is not of vector type. Vector search cannot be performed on a non-vector column.")]
    [DataRow("table = u, column = v", 42227, 1, "Cannot find a vector index with metric 'euclidean' on column 'v'.")]
    [DataRow("table = t, column = t.v", 102, 1, "Incorrect syntax near '.'.")]
    [DataRow("table = @tv, column = v", 102, 1, "Incorrect syntax near '@tv'.")]
    public void VectorSearch_SourceRefusals(string arguments, int number, int state, string message)
    {
        var sim = Indexed();
        sim.ExecuteBatches("create view vw as select id, v from t", "create table u (id int not null primary key, v vector(2))");
        AssertError(sim, $"""
            declare @q vector(2) = '[1,0]';
            declare @tv table (id int);
            select 1 from vector_search({arguments}, similar_to = @q, metric = 'euclidean', top_n = 2) s
            """, number, (byte)state, message);
    }

    [TestMethod]
    [DataRow("select s.id from vector_search(table = t, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) s", 207)]
    [DataRow("select a.distance from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) s", 207)]
    [DataRow("select 1 from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) a", 1011)]
    [DataRow("select 1 from vector_search(table = t, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) t", 1012)]
    [DataRow("select 1 from t join vector_search(table = t, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) s on 1 = 1", 1013)]
    [DataRow("select 1 from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) s (d)", 102)]
    [DataRow("select 1 from vector_search(column = v, table = t, similar_to = @q, metric = 'euclidean', top_n = 1) s", 102)]
    public void VectorSearch_NameScopes(string query, int number)
        => _ = Indexed().AssertSqlError($"declare @q vector(2) = '[1,0]'; {query}", number);

    [TestMethod]
    public void VectorSearch_WithoutPreview_IsASyntaxError()
    {
        var sim = Indexed();
        _ = sim.ExecuteNonQuery("alter database scoped configuration set preview_features = off");
        AssertError(sim, """
            declare @q vector(2) = '[1,0]';
            select 1 from vector_search(table = t, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) s
            """, 156, 1, "Incorrect syntax near the keyword 'table'.");
        _ = sim.AssertSqlError("insert t values (9, '[0,0]', 0)", 42231);
    }

    [TestMethod]
    public void VectorSearch_InAProcedure_RepeatsFromThePlanCache()
    {
        var sim = Indexed();
        sim.ExecuteBatches("""
            create procedure p @q vector(2) as
            select a.id from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) s
            """);
        AreEqual(2, sim.ExecuteScalar("exec p '[0,0.9]'"));
        AreEqual(1, sim.ExecuteScalar("exec p '[0.9,0]'"));
    }

    [TestMethod]
    [DataRow(8, "[1,2,3,4,5,6,7,8]", "[0.3,0.1,0.7,0.2,0.9,0.11,0.13,0.17]", 0.38045626878738403)]
    [DataRow(9, "[1,2,3,4,5,6,7,8,9]", "[0.3,0.1,0.7,0.2,0.9,0.11,0.13,0.17,0.5]", 0.31315070390701294)]
    public void CosineKernel_IsBitExact(int dimensions, string a, string b, double expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select vector_distance('cosine', cast('{a}' as vector({dimensions})), cast('{b}' as vector({dimensions})))"));

    [TestMethod]
    public void Float16_WithoutPreview_IsNotRecognized()
        => AssertError(new Simulation(), "declare @v vector(3, float16)", 195, 1, "'float16' is not a recognized vector base type.");

    [TestMethod]
    [DataRow("[0.1,1.0009765625,65504,-65504,6e-8,1e-8,-0,2049,2051,65519.99,1e-5]",
        "[9.9975586e-002,1.0009766e+000,6.5504000e+004,-6.5504000e+004,5.9604645e-008,0.0000000e+000,0.0000000e+000,2.0480000e+003,2.0520000e+003,6.5504000e+004,1.0013580e-005]")]
    public void Float16_RoundsToHalfPrecision(string input, string expected)
    {
        var dimensions = input.Count(c => c == ',') + 1;
        AreEqual($"{expected}|{8 + (2 * dimensions)}", Preview().ExecuteScalar($"declare @v vector({dimensions}, float16) = '{input}'; select concat(cast(@v as varchar(max)), '|', datalength(@v))"));
    }

    [TestMethod]
    [DataRow("select cast('[65520]' as vector(1, float16))", 42241, 2, "Input JSON contains out-of-range values for float16.")]
    [DataRow("select cast('[1e39]' as vector(1, float16))", 42241, 1, "Input JSON contains out-of-range values for float16.")]
    [DataRow("declare @x vector(3997, float16)", 2717, 2, "The size (3997) given to the type 'vector' exceeds the maximum allowed (3996).")]
    [DataRow("create table w (v vector(3997, float16))", 2717, 5, "The size (3997) given to the column 'v' exceeds the maximum allowed (3996).")]
    [DataRow("select 1; select cast(@b as vector(2, float16))", 42238, 1, "Conversion of vector from data type float32 to float16 is not allowed.")]
    [DataRow("select 1; set @a = @b", 42238, 1, "Conversion of vector from data type float32 to float16 is not allowed.")]
    [DataRow("select coalesce(@b, @a)", 42238, 1, "Conversion of vector from data type float16 to float32 is not allowed.")]
    [DataRow("select 1; select vector_norm(@a, 'norm2')", 42246, 1, "vector_norm function does not support vector with base type float16.")]
    [DataRow("select vector_normalize(@a, 'norm2')", 42246, 1, "vector_normalize function does not support vector with base type float16.")]
    [DataRow("select vector_distance('cosine', @a, @b)", 42243, 1, "VECTOR_DISTANCE function does not support different base types for vector arguments.")]
    public void Float16_Refusals(string statement, int number, int state, string message)
        => AssertError(Preview(), $"declare @a vector(2, float16) = '[1,2]', @b vector(2) = '[1,2]'; {statement}", number, (byte)state, message);

    [TestMethod]
    public void Float16_DistancesWidenToFloat32()
        => AreEqual(0, Preview().ExecuteScalar("""
            declare @a vector(9, float16) = '[0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9]', @b vector(9, float16) = '[1.1,-0.2,0.35,0.4,-0.5,0.6,7.7,0.8,0.19]';
            declare @c vector(9) = cast(cast(@a as varchar(max)) as vector(9)), @d vector(9) = cast(cast(@b as varchar(max)) as vector(9));
            select case when vector_distance('cosine', @a, @b) = vector_distance('cosine', @c, @d)
                and vector_distance('euclidean', @a, @b) = vector_distance('euclidean', @c, @d)
                and vector_distance('dot', @a, @b) = vector_distance('dot', @c, @d) then 0 else 1 end
            """));

    [TestMethod]
    public void Float16_Catalog()
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery("create table t (id int, v vector(3, float16))");
        AreEqual("14|3|1|float16", sim.ExecuteScalar("select concat_ws('|', max_length, vector_dimensions, vector_base_type, vector_base_type_desc) from sys.columns where name = 'v'"));
        AreEqual("float16", sim.ExecuteScalar("select vectorproperty(cast('[1]' as vector(1, float16)), 'BaseType')"));
        AreEqual("varchar(max)", sim.ExecuteScalar("select system_type_name from sys.dm_exec_describe_first_result_set(N'select v from t', null, 0)"));
    }

    [TestMethod]
    public void Float16_VectorSearch()
    {
        var sim = Preview();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, v vector(2, float16));
            insert t values (10, '[1,0]'), (20, '[0,1]'), (30, '[1,1]');
            create vector index vx on t(v) with (metric = 'euclidean')
            """);
        AreEqual("10", sim.ExecuteScalar("select json_value(build_parameters, '$.StartId') from sys.vector_indexes"));
        AreEqual("30:0.10009765625", Search(sim, """
            declare @q vector(2, float16) = '[1,0.9]';
            select a.id, s.distance from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) s
            """));
        _ = sim.AssertSqlError("""
            declare @q vector(2) = '[1,0.9]';
            select a.id from vector_search(table = t as a, column = v, similar_to = @q, metric = 'euclidean', top_n = 1) s
            """, 42243);
    }
}
