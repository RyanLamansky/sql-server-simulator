using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Node and edge tables: <c>AS NODE</c> / <c>AS EDGE</c> and their internal
/// columns, the JSON identifiers the pseudo-columns read and write, edge
/// constraints, <c>MATCH</c>, and the six identifier functions.
/// Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class GraphTableTests
{
    private const string Graph = """
        create table Person (id int primary key, name varchar(20)) as node;
        create table City (id int, name varchar(20)) as node;
        create table Likes (w int) as edge;
        create table LivesIn as edge;
        insert Person values (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd');
        insert City values (10, 'x'), (20, 'y');
        insert Likes select p1.$node_id, p2.$node_id, p1.id * 10 + p2.id from Person p1, Person p2 where p2.id = p1.id + 1 and p1.id < 3;
        insert Likes select p1.$node_id, p2.$node_id, 99 from Person p1, Person p2 where p1.id = 3 and p2.id = 1;
        insert LivesIn select p.$node_id, c.$node_id from Person p, City c where (p.id % 2 = 1 and c.id = 10) or (p.id % 2 = 0 and c.id = 20);
        """;

    private static Simulation Seeded(params ReadOnlySpan<string> more)
    {
        var sim = new Simulation();
        sim.ExecuteBatches([Graph, .. more]);
        return sim;
    }

    private static string Rows(Simulation sim, string query)
    {
        using var reader = sim.ExecuteReader(query);
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return string.Join(";", rows);
    }

    private static string Columns(Simulation sim, string query)
    {
        using var reader = sim.ExecuteReader(query);
        return string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => System.Text.RegularExpressions.Regex.Replace(reader.GetName(i), "_[0-9A-F]{32}$", "_*")));
    }

    [TestMethod]
    [DataRow("Person", "graph_id_*|bigint|1|GRAPH_ID|True;$node_id_*|nvarchar|2|GRAPH_ID_COMPUTED|False;id|int|NULL|NULL|False;name|varchar|NULL|NULL|False")]
    [DataRow("Likes", "graph_id_*|bigint|1|GRAPH_ID|True;$edge_id_*|nvarchar|2|GRAPH_ID_COMPUTED|False;from_obj_id_*|int|4|GRAPH_FROM_OBJ_ID|True;from_id_*|bigint|3|GRAPH_FROM_ID|True;$from_id_*|nvarchar|5|GRAPH_FROM_ID_COMPUTED|False;to_obj_id_*|int|7|GRAPH_TO_OBJ_ID|True;to_id_*|bigint|6|GRAPH_TO_ID|True;$to_id_*|nvarchar|8|GRAPH_TO_ID_COMPUTED|False;w|int|NULL|NULL|False")]
    [DataRow("LivesIn", "graph_id_*|bigint|1|GRAPH_ID|True;$edge_id_*|nvarchar|2|GRAPH_ID_COMPUTED|False;from_obj_id_*|int|4|GRAPH_FROM_OBJ_ID|True;from_id_*|bigint|3|GRAPH_FROM_ID|True;$from_id_*|nvarchar|5|GRAPH_FROM_ID_COMPUTED|False;to_obj_id_*|int|7|GRAPH_TO_OBJ_ID|True;to_id_*|bigint|6|GRAPH_TO_ID|True;$to_id_*|nvarchar|8|GRAPH_TO_ID_COMPUTED|False")]
    public void Catalog_InternalColumns(string table, string expected) => AreEqual(expected, System.Text.RegularExpressions.Regex.Replace(Rows(Seeded(), $"""
        select c.name, type_name(c.user_type_id), c.graph_type, c.graph_type_desc, c.is_hidden
        from sys.columns c where c.object_id = object_id('{table}') and c.is_computed = 0 order by c.column_id
        """), "_[0-9A-F]{32}", "_*"));

    [TestMethod]
    [DataRow("select count(*) from sys.tables where is_node = 1", 2)]
    [DataRow("select count(*) from sys.tables where is_edge = 1", 2)]
    [DataRow("select count(*) from sys.computed_columns", 0)]
    [DataRow("select max_length from sys.columns where object_id = object_id('Likes') and graph_type = 5", (short)2000)]
    [DataRow("select is_nullable from sys.columns where object_id = object_id('Likes') and graph_type = 5", true)]
    [DataRow("select is_nullable from sys.columns where object_id = object_id('Person') and graph_type = 2", false)]
    [DataRow("select columnproperty(object_id('Person'), (select name from sys.columns where object_id = object_id('Person') and graph_type = 2), 'IsComputed')", 1)]
    [DataRow("select index_id from sys.indexes where object_id = object_id('Person') and name like 'GRAPH_UNIQUE_INDEX_%'", 2)]
    [DataRow("select is_unique from sys.indexes where object_id = object_id('Likes') and name like 'GRAPH_UNIQUE_INDEX_%'", true)]
    public void Catalog_Scalars(string query, object expected) => AreEqual(expected, Seeded().ExecuteScalar(query));

    [TestMethod]
    [DataRow("select * from Person", "$node_id_*,id,name")]
    [DataRow("select * from Likes", "$edge_id_*,$from_id_*,$to_id_*,w")]
    [DataRow("select $node_id, id from Person", "$node_id_*,id")]
    [DataRow("select $NODE_ID as n from Person", "n")]
    [DataRow("select * from Person p1, Likes l, Person p2 where 1 = 1", "$node_id_*,id,name,$edge_id_*,$from_id_*,$to_id_*,w,$node_id_*,id,name")]
    [DataRow("select * from Person p1, Likes l, Person p2 where match(p1-(l)->p2)", "$edge_id_*,$from_id_*,$to_id_*,w,$node_id_*,id,name,$node_id_*,id,name")]
    [DataRow("select * from City c, LivesIn v, Person p where match(p-(v)->c)", "$edge_id_*,$from_id_*,$to_id_*,$node_id_*,id,name,$node_id_*,id,name")]
    [DataRow("select * from City c, Likes l, Person p1, Person p2 where match(p1-(l)->p2)", "$node_id_*,id,name,$edge_id_*,$from_id_*,$to_id_*,w,$node_id_*,id,name,$node_id_*,id,name")]
    public void Select_ColumnNames(string query, string expected) => AreEqual(expected, Columns(Seeded(), query));

    [TestMethod]
    [DataRow("select $node_id from Person where id = 1", """{"type":"node","schema":"dbo","table":"Person","id":0}""")]
    [DataRow("select p.$node_id from Person p where id = 4", """{"type":"node","schema":"dbo","table":"Person","id":3}""")]
    [DataRow("select $edge_id from Likes where w = 23", """{"type":"edge","schema":"dbo","table":"Likes","id":1}""")]
    [DataRow("select $to_id from LivesIn v where $from_id = (select $node_id from Person where id = 2)", """{"type":"node","schema":"dbo","table":"City","id":1}""")]
    [DataRow("select id from Person where $node_id = '{\"type\":\"node\",\"schema\":\"dbo\",\"table\":\"Person\",\"id\":2}'", 3)]
    [DataRow("exec sp_rename 'City', 'Town'; select $to_id from LivesIn where $from_id = (select $node_id from Person where id = 1)", """{"type":"node","schema":"dbo","table":"Town","id":0}""")]
    [DataRow("create table [we\"ird\\t] (a int) as node; insert [we\"ird\\t] values (1); select $node_id from [we\"ird\\t]", """{"type":"node","schema":"dbo","table":"we\"ird\\t","id":0}""")]
    [DataRow("drop table City; select $to_id from LivesIn where $from_id = (select $node_id from Person where id = 1)", null)]
    public void PseudoColumn_Values(string query, object? expected) => AreEqual(expected ?? DBNull.Value, Seeded().ExecuteScalar(query));

    [TestMethod]
    public void PseudoColumn_ReachesThroughAView() =>
        AreEqual("""{"type":"node","schema":"dbo","table":"Person","id":2}""", Seeded("create view v as select * from Likes").ExecuteScalar("select $from_id from v where w = 99"));

    [TestMethod]
    [DataRow("select $node_id from Person, City", 209)]
    [DataRow("select $node_id from Likes", 207)]
    [DataRow("select $from_id from Person", 207)]
    [DataRow("create table plain (a int); select $node_id from plain", 207)]
    [DataRow("update Person set $node_id = 'x'", 271)]
    [DataRow("update Likes set $from_id = 'x'", 271)]
    [DataRow("create table E1 as node", 102)]
    [DataRow("create table E2 () as node", 102)]
    [DataRow("create table #n (a int) as node", 13914)]
    [DataRow("declare @t table (a int) as node", 156)]
    public void Errors_Compile(string batch, int number) => _ = Seeded().AssertSqlError(batch, number);

    [TestMethod]
    [DataRow("select name from sys.columns where object_id = object_id('Person') and graph_type = 1", "insert Person ({0}, id) values (5, 9)", 13908)]
    [DataRow("select name from sys.columns where object_id = object_id('Likes') and graph_type = 3", "update Likes set {0} = 7", 13908)]
    [DataRow("select name from sys.columns where object_id = object_id('Person') and graph_type = 1", "alter table Person drop column {0}", 13913)]
    [DataRow("select name from sys.columns where object_id = object_id('Person') and graph_type = 2", "alter table Person drop column [{0}]", 13913)]
    [DataRow("select name from sys.columns where object_id = object_id('Likes') and graph_type = 3", "alter table Likes alter column {0} int", 13913)]
    public void Errors_InternalColumns(string nameQuery, string statement, int number)
    {
        var sim = Seeded();
        _ = sim.AssertSqlError(string.Format(System.Globalization.CultureInfo.InvariantCulture, statement, sim.ExecuteScalar(nameQuery)), number);
    }

    [TestMethod]
    public void GraphIds_CountLikeRealsCounter()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table P (id int) as node;
            insert P values (1), (2);
            insert P ($node_id, id) values ('{"type":"node","schema":"dbo","table":"P","id":7}', 3);
            insert P (id) values (4);
            delete P where id = 4;
            insert P (id) values (5);
            begin tran; insert P (id) values (6); rollback;
            insert P (id) values (7);
            truncate table P;
            insert P (id) values (8);
            """);
        AreEqual(12L, sim.ExecuteScalar("select graph_id_from_node_id($node_id) from P"));
    }

    [TestMethod]
    [DataRow("insert Person ($node_id, id) values ('{\"type\":\"node\",\"schema\":\"dbo\",\"table\":\"Person\",\"id\":0}', 9)", 2601)]
    [DataRow("insert Person ($node_id, id) values ('garbage', 9)", 13921)]
    [DataRow("insert Person ($node_id, id) values (NULL, 9)", 13921)]
    [DataRow("insert Person ($node_id, id) values ('{\"type\":\"node\",\"schema\":\"dbo\",\"table\":\"City\",\"id\":8}', 9)", 13921)]
    [DataRow("insert Likes ($edge_id, $from_id, $to_id) values ('x', (select $node_id from Person where id = 1), (select $node_id from Person where id = 1))", 13921)]
    [DataRow("insert Likes (w) values (1)", 515)]
    [DataRow("insert Likes default values", 515)]
    [DataRow("insert Likes values ('garbage', (select $node_id from Person where id = 1), 1)", 515)]
    [DataRow("insert Likes values ('{\"type\":\"node\",\"schema\":\"dbo\",\"table\":\"Person\",\"id\":1.5}', (select $node_id from Person where id = 1), 1)", 515)]
    [DataRow("insert Likes values ('{\"type\":\"node\",\"schema\":\"dbo\",\"table\":\"Person\",\"id\":\"1\"}', (select $node_id from Person where id = 1), 1)", 515)]
    [DataRow("insert Likes values ('{\"type\":\"node\",\"schema\":\"dbo\",\"table\":\"Person\",\"id\":1,\"x\":1}', (select $node_id from Person where id = 1), 1)", 515)]
    [DataRow("insert Likes values ((select $edge_id from Likes where w = 12), (select $node_id from Person where id = 1), 1)", 515)]
    [DataRow("insert Likes values ('{\"type\":\"node\",\"schema\":\"dbo\",\"table\":\"Nope\",\"id\":1}', (select $node_id from Person where id = 1), 1)", 515)]
    [DataRow("insert Person values (1, 'x', 2)", 213)]
    public void Insert_Refusals(string statement, int number) => _ = Seeded().AssertSqlError(statement, number);

    [TestMethod]
    public void Insert_NotNullNamesTheHiddenObjectIdColumn() =>
        Assert.StartsWith("Cannot insert the value NULL into column 'to_obj_id_", Seeded().AssertSqlError("insert Likes ($from_id, w) values ((select $node_id from Person where id = 1), 1)", 515).Errors[0].Message);

    [TestMethod]
    [DataRow("""{"schema":"dbo","table":"Person","id":1,"type":"node"}""", 1L)]
    [DataRow("""{ "type" : "node", "schema" : "dbo", "table" : "person", "id" : 2 }""", 2L)]
    [DataRow("""{"TYPE":"NODE","schema":"DBO","table":"Person","id":-5}""", -5L)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":3,"id":77}""", 77L)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":99}""", 99L)]
    public void Insert_WrittenEndpointIsLenientJson(string endpoint, long expected) =>
        AreEqual(expected, Seeded().ExecuteScalar($"""
            insert Likes values ('{endpoint}', (select $node_id from Person where id = 1), 500);
            select graph_id_from_node_id($from_id) from Likes where w = 500
            """));

    [TestMethod]
    public void Insert_ExplicitEdgeIdAndMergeAndOutput()
    {
        var sim = Seeded();
        AreEqual("""{"type":"node","schema":"dbo","table":"Person","id":4}""", sim.ExecuteScalar("insert Person (id) output inserted.$node_id values (5)"));
        _ = sim.ExecuteNonQuery("""
            insert Likes ($edge_id, $from_id, $to_id, w) values ('{"type":"edge","schema":"dbo","table":"Likes","id":40}', (select $node_id from Person where id = 1), (select $node_id from Person where id = 2), 7);
            merge Likes as t using (select (select $node_id from Person where id = 4) f, (select $node_id from Person where id = 1) tt) s on 1 = 0
                when not matched then insert ($from_id, $to_id, w) values (s.f, s.tt, 8);
            """);
        AreEqual("7:40;8:41", Rows(sim, "select concat(w, ':', graph_id_from_edge_id($edge_id)) from Likes where w in (7, 8) order by w"));
    }

    [TestMethod]
    [DataRow("select concat(p1.name, p2.name, l.w) from Person p1, Likes l, Person p2 where match(p1-(l)->p2) order by 1", "ab12;bc23;ca99")]
    [DataRow("select concat(p1.name, p2.name) from Person p1, Likes l, Person p2 where match(p2<-(l)-p1) order by 1", "ab;bc;ca")]
    [DataRow("select concat(p1.name, p2.name, p3.name) from Person p1, Likes l1, Person p2, Likes l2, Person p3 where match(p1-(l1)->p2-(l2)->p3) order by 1", "abc;bca;cab")]
    [DataRow("select concat(p1.name, p2.name, p3.name) from Person p1, Likes l1, Person p2, Likes l2, Person p3 where match(p1-(l1)->p2 and p2-(l2)->p3) order by 1", "abc;bca;cab")]
    [DataRow("select concat(p1.name, p2.name, c.name) from Person p1, Likes l1, Person p2, LivesIn li, City c where match(p1-(l1)->p2-(li)->c) order by 1", "aby;bcx;cax")]
    [DataRow("select concat(p1.name, p2.name, c.name) from Person p1, Likes l1, Person p2, LivesIn li, LivesIn li2, City c where match(p1-(l1)->p2-(li)->c<-(li2)-p1) order by 1", "cax")]
    [DataRow("select concat(p1.name, p2.name) from Person p1, Likes l, Person p2 where p1.id > 1 and match(p1-(l)->p2) order by 1", "bc;ca")]
    [DataRow("select count(*) from Person p1, Likes l where match(p1-(l)->p1)", "0")]
    [DataRow("select concat(Person.name, City.name) from Person, LivesIn, City where match(Person-(LivesIn)->City) order by 1", "ax;by;cx;dy")]
    [DataRow("select count(*) from (select * from Person) p1, Likes l, Person p2 where match(p1-(l)->p2)", "3")]
    [DataRow("with x as (select * from Likes) select count(*) from Person p1, x, Person p2 where match(p1-(x)->p2)", "3")]
    [DataRow("select count(*) from Person p1, Likes l, Person p2 where case when match(p1-(l)->p2) then 1 else 0 end = 1", "3")]
    [DataRow("select count(*) from Person where exists (select 1 from Person p1, Likes l, Person p2 where match(p1-(l)->p2) and p1.id = Person.id)", "3")]
    public void Match_Rows(string query, string expected) => AreEqual(expected, Rows(Seeded(), query));

    [TestMethod]
    public void Match_DrivesUpdateAndDelete()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("update p1 set name = 'z' from Person p1, Likes l, Person p2 where match(p1-(l)->p2) and p2.id = 1");
        AreEqual("z", sim.ExecuteScalar("select name from Person where id = 3"));
        _ = sim.ExecuteNonQuery("delete l from Person p1, Likes l, Person p2 where match(p1-(l)->p2) and p1.id = 1");
        AreEqual(2, sim.ExecuteScalar("select count(*) from Likes"));
    }

    [TestMethod]
    [DataRow("select 1 from Person p1, Likes l, Person p2 where match(p1-(l)->p2) or 1 = 1", 13905)]
    [DataRow("select 1 from Person p1, Likes l, Person p2 where not match(p1-(l)->p2)", 13905)]
    [DataRow("select 1 from Person p1, Likes l, Person p2 where 1 = 0 or (match(p1-(l)->p2) and 2 = 2)", 13905)]
    [DataRow("select 1 from Person p1, Likes l, Likes p2 where match(p1-(l)->p2)", 13901)]
    [DataRow("select 1 from Person p1, Person l, Person p2 where match(p1-(l)->p2)", 13902)]
    [DataRow("select 1 from Person p1, Likes l where match(p1-(l)->zz)", 13900)]
    [DataRow("select 1 from Person p1, Likes l, Person p2, Person p3 where match(p1-(l)->p2 and p2-(l)->p3)", 13903)]
    [DataRow("select 1 from Person p1, Likes l, Person p2 where match(p1-(l)->p2) and match(p1-(l)->p2)", 13903)]
    [DataRow("select 1 from Person p1 join Likes l on 1 = 1, Person p2 where match(p1-(l)->p2)", 13920)]
    [DataRow("select 1 from Person p1 cross join Likes l cross join Person p2 where match(p1-(l)->p2)", 13920)]
    [DataRow("select 1 from Person p1 join Likes l on match(p1-(l)->p1)", 13920)]
    [DataRow("select match(p1-(l)->p2) from Person p1, Likes l, Person p2", 102)]
    [DataRow("select count(*) from Person p1, Likes l, Person p2 group by p1.id having match(p1-(l)->p2)", 102)]
    [DataRow("select 1 from Person p1, Likes l, Person p2 where match(p1->p2)", 102)]
    [DataRow("select 1 from Person p1, Likes l, Person p2 where match(p1<-(l)->p2)", 102)]
    [DataRow("select 1 from Person p1, Likes l, Person p2 where match(p1-(l)-p2)", 102)]
    [DataRow("select 1 from Person p1, Likes l, Person p2 where match(dbo.p1-(l)->p2)", 102)]
    [DataRow("select 1 from Person p1, Likes l, Person p2 where match(p1-(l)->p2 or p1-(l)->p2)", 156)]
    public void Match_Refusals(string query, int number) => _ = Seeded().AssertSqlError(query, number);

    [TestMethod]
    public void Match_ReportsEveryMisboundIdentifier()
    {
        var ex = Seeded().AssertSqlError("select 1 from Person p1, Likes l, Person p2 where match(p1-(p2)->l)", 13902);
        AreEqual(2, ex.Errors.Count);
        AreEqual(13901, ex.Errors[1].Number);
    }

    [TestMethod]
    [DataRow("select graph_id_from_node_id($node_id) from Person where id = 3", 2L)]
    [DataRow("select object_id_from_node_id($node_id) - object_id('Person') from Person where id = 3", 0)]
    [DataRow("select object_id_from_edge_id($edge_id) - object_id('Likes') from Likes where w = 12", 0)]
    [DataRow("select graph_id_from_edge_id($edge_id) from Likes where w = 23", 1L)]
    [DataRow("select object_id_from_node_id($to_id) - object_id('City') from LivesIn where graph_id_from_edge_id($edge_id) = 0", 0)]
    [DataRow("select node_id_from_parts(object_id('Person'), 5)", """{"type":"node","schema":"dbo","table":"Person","id":5}""")]
    [DataRow("select node_id_from_parts(object_id('Person'), -1)", """{"type":"node","schema":"dbo","table":"Person","id":-1}""")]
    [DataRow("select node_id_from_parts(cast(object_id('Person') as bigint), cast(1 as tinyint))", """{"type":"node","schema":"dbo","table":"Person","id":1}""")]
    [DataRow("select edge_id_from_parts(object_id('Likes'), 9)", """{"type":"edge","schema":"dbo","table":"Likes","id":9}""")]
    [DataRow("select node_id_from_parts(object_id('Likes'), 5)", null)]
    [DataRow("select edge_id_from_parts(object_id('Person'), 5)", null)]
    [DataRow("select node_id_from_parts(12345, 5)", null)]
    [DataRow("select node_id_from_parts(null, 5)", null)]
    [DataRow("select graph_id_from_node_id($edge_id) from Likes where w = 12", null)]
    [DataRow("select object_id_from_edge_id($from_id) from Likes where w = 12", null)]
    [DataRow("select graph_id_from_node_id(null)", null)]
    [DataRow("select sql_variant_property(node_id_from_parts(object_id('Person'), 5), 'MaxLength')", 2000)]
    public void Functions_Values(string query, object? expected) => AreEqual(expected ?? DBNull.Value, Seeded().ExecuteScalar(query));

    [TestMethod]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":3}""", 3L)]
    [DataRow("""{"TYPE":"NODE","schema":"DBO","table":"person","id":3}""", 3L)]
    [DataRow("""{"type":"node","schema":"dbo","table":"\u0050erson","id":3}""", 3L)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person ","id":3}""", 3L)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":-0}""", 0L)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":9223372036854775807}""", long.MaxValue)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":9223372036854775808}""", null)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":03}""", null)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":3.0}""", null)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":1e2}""", null)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":"3"}""", null)]
    [DataRow("""{ "type":"node","schema":"dbo","table":"Person","id":3}""", null)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":3} """, null)]
    [DataRow("""{"schema":"dbo","table":"Person","id":3,"type":"node"}""", null)]
    [DataRow("""{"type":"node","schema":"dbo","table":"Person","id":3,"id":4}""", null)]
    [DataRow("""{"type":"node","schema":"sys","table":"Person","id":3}""", null)]
    [DataRow("""{"type":"node","schema":"dbo","table":"[Person]","id":3}""", null)]
    [DataRow("""{"type":"edge","schema":"dbo","table":"Likes","id":3}""", null)]
    public void Functions_ReadOnlyTheRenderedShape(string identifier, object? expected) =>
        AreEqual(expected ?? DBNull.Value, Seeded().ExecuteScalar($"select graph_id_from_node_id(N'{identifier}')"));

    [TestMethod]
    [DataRow("select node_id_from_parts('abc', 5)", 8116)]
    [DataRow("select node_id_from_parts(1.5, 5)", 8116)]
    [DataRow("select node_id_from_parts(object_id('Person'), 9999999999999)", 8116)]
    [DataRow("select node_id_from_parts(object_id('Person'), cast(1 as bit))", 8116)]
    [DataRow("select node_id_from_parts(object_id('Person'), cast(1 as sql_variant))", 8116)]
    [DataRow("select graph_id_from_node_id(5)", 8116)]
    [DataRow("select graph_id_from_node_id(cast('x' as varbinary(10)))", 8116)]
    [DataRow("select graph_id_from_node_id(cast('<a/>' as xml))", 8116)]
    [DataRow("select node_id_from_parts(object_id('Person'))", 174)]
    [DataRow("select graph_id_from_node_id('a', 'b')", 174)]
    [DataRow("select edge_id_from_parts()", 174)]
    public void Functions_Refusals(string query, int number) => _ = Seeded().AssertSqlError(query, number);

    private const string Constrained = """
        create table X (id int) as node;
        insert X values (100);
        create table L (w int, constraint ec connection (Person to City, Person to Person) on delete cascade) as edge;
        create table L2 (constraint ec2 connection (Person to City)) as edge;
        """;

    [TestMethod]
    public void EdgeConstraint_Catalog()
    {
        var sim = Seeded(Constrained, "create table L3 (connection (City to Person)) as edge");
        AreEqual("ec|CASCADE|False;ec2|NO_ACTION|False;EC__L3__|NO_ACTION|True", Rows(sim, """
            select case is_system_named when 1 then left(name, 8) else name end, delete_referential_action_desc, is_system_named
            from sys.edge_constraints order by object_id
            """));
        AreEqual("ec|1|Person|City;ec|2|Person|Person;ec2|1|Person|City", Rows(sim, """
            select object_name(object_id), clause_number, object_name(from_object_id), object_name(to_object_id)
            from sys.edge_constraint_clauses where object_name(object_id) in ('ec', 'ec2') order by object_id, clause_number
            """));
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.objects where type = 'EC'"));
        AreEqual("ec", sim.ExecuteScalar("select object_name(object_id('ec'))"));
    }

    [TestMethod]
    [DataRow("insert L select p.$node_id, x.$node_id, 1 from Person p, X x where p.id = 1", "INSERT statement conflicted with the EDGE constraint \"ec\"")]
    [DataRow("insert L select c.$node_id, p.$node_id, 1 from Person p, City c where p.id = 1 and c.id = 10", "INSERT statement conflicted with the EDGE constraint \"ec\"")]
    [DataRow("insert L2 values ('{\"type\":\"node\",\"schema\":\"dbo\",\"table\":\"Person\",\"id\":55}', (select $node_id from City where id = 10))", "INSERT statement conflicted with the EDGE constraint \"ec2\"")]
    [DataRow("insert L2 select p.$node_id, c.$node_id from Person p, City c where p.id = 2 and c.id = 10; delete Person where id = 2", "DELETE statement conflicted with the EDGE REFERENCE constraint \"ec2\"")]
    [DataRow("insert L2 select p.$node_id, c.$node_id from Person p, City c where p.id = 2 and c.id = 10; merge Person using (values (2)) s(id) on Person.id = s.id when matched then delete;", "DELETE statement conflicted with the EDGE REFERENCE constraint \"ec2\"")]
    [DataRow("alter table L add constraint ec3 connection (X to X)", null)]
    public void EdgeConstraint_Enforcement(string statement, string? message)
    {
        var sim = Seeded(Constrained);
        if (message is null)
        {
            sim.ExecuteBatches(statement);
            _ = sim.AssertSqlError("insert L select p.$node_id, c.$node_id, 1 from Person p, City c where p.id = 1 and c.id = 10", 547);
            return;
        }
        Assert.Contains(message, sim.AssertSqlError(statement, 547).Errors[0].Message);
    }

    [TestMethod]
    public void EdgeConstraint_CascadeDeletesEdgesWithoutConstraintKeepsThem()
    {
        var sim = Seeded(Constrained);
        _ = sim.ExecuteNonQuery("""
            insert L select p.$node_id, q.$node_id, 1 from Person p, Person q where q.id = p.id + 1;
            delete Person where id = 2;
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from L"));
        AreEqual(3, sim.ExecuteScalar("select count(*) from Likes"));
    }

    [TestMethod]
    [DataRow("drop table City", 13934)]
    [DataRow("truncate table City", 13944)]
    [DataRow("create table L4 (connection (Person to plainT)) as edge", 13931)]
    [DataRow("create table L5 (connection (Person to Likes)) as edge", 13933)]
    [DataRow("create table N2 (id int, connection (Person to Person)) as node", 13930)]
    [DataRow("create table T2 (id int, connection (Person to Person))", 13930)]
    [DataRow("create table L8 (connection (Person to Person) on delete set null) as edge", 156)]
    [DataRow("create table L9 (connection (Person to Person) on update cascade) as edge", 156)]
    [DataRow("insert L select p.$node_id, x.$node_id, 1 from Person p, X x where p.id = 1; alter table L add constraint e connection (Person to City)", 547)]
    public void EdgeConstraint_Refusals(string statement, int number) => _ = Seeded(Constrained).AssertSqlError(statement, number);

    [TestMethod]
    public void EdgeConstraint_DropAndNoCheck()
    {
        var sim = Seeded(Constrained);
        _ = sim.ExecuteNonQuery("""
            alter table L drop constraint ec;
            insert L select p.$node_id, x.$node_id, 1 from Person p, X x where p.id = 1;
            alter table L with nocheck add constraint e connection (Person to City);
            drop table L2;
            """);
        AreEqual("e|True", Rows(sim, "select name, is_not_trusted from sys.edge_constraints"));
    }

    [TestMethod]
    public void EdgeConstraint_NoCheckAndCheck()
    {
        var sim = Seeded(Constrained);
        _ = sim.ExecuteNonQuery("alter table L2 nocheck constraint ec2; insert L2 select p.$node_id, x.$node_id from Person p, X x where p.id = 1");
        AreEqual("ec2|True|True|1|1", Rows(sim, "select name, is_disabled, is_not_trusted, objectproperty(object_id, 'CnstIsDisabled'), objectproperty(object_id, 'CnstIsNotTrusted') from sys.edge_constraints where name = 'ec2'"));
        _ = sim.ExecuteNonQuery("alter table L2 check constraint ec2");
        AreEqual("False|True", Rows(sim, "select is_disabled, is_not_trusted from sys.edge_constraints where name = 'ec2'"));
        var ex = sim.AssertSqlError("alter table L2 with check check constraint ec2", 547);
        Contains("The ALTER TABLE statement conflicted with the EDGE constraint \"ec2\".", ex.Message);
        _ = sim.ExecuteNonQuery("delete L2; alter table L2 with check check constraint all");
        AreEqual("False|False", Rows(sim, "select is_disabled, is_not_trusted from sys.edge_constraints where name = 'ec2'"));
    }

    [TestMethod]
    public void EdgeConstraint_DisabledNeitherRefusesNorCascadesANodeDelete()
    {
        var sim = Seeded(Constrained, "insert L select p.$node_id, c.$node_id, 1 from Person p, City c where p.id = 1 and c.id = 10");
        _ = sim.ExecuteNonQuery("alter table L nocheck constraint all; delete City where id = 10");
        AreEqual(1, sim.ExecuteScalar("select count(*) from L"));
        _ = sim.AssertSqlError("truncate table City", 13944);
        AreEqual("Disabled", Rows(sim, "exec sp_helpconstraint 'L', 'nomsg'").Split('|')[4]);
    }

    [TestMethod]
    [DataRow("select [$node_id] from Person", "$node_id")]
    [DataRow("select p.[$node_id] from Person p", "$node_id")]
    [DataRow("select \"$from_id\" from Likes", "$from_id")]
    public void PseudoColumn_DelimitedIsAnOrdinaryName(string query, string name) =>
        Seeded().AssertSqlError(query, 207, $"Invalid column name '{name}'.");

    [TestMethod]
    [DataRow("select {0} from Person")]
    [DataRow("select p.{0} from Person p")]
    [DataRow("select id from Person where {0} = 0")]
    [DataRow("select id from Person order by {0}")]
    [DataRow("select count({0}) from Person")]
    public void InternalColumn_NamedInAQuery_Raises(string shape)
    {
        var sim = Seeded();
        var graphId = (string)sim.ExecuteScalar("select name from sys.columns where object_id = object_id('Person') and column_id = 1")!;
        sim.AssertSqlError(string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, graphId), 13908, $"Cannot access internal graph column '{graphId}'.");
    }

    [TestMethod]
    public void InternalColumn_PseudoColumnByFullNameReads()
    {
        var sim = Seeded();
        var nodeId = (string)sim.ExecuteScalar("select name from sys.columns where object_id = object_id('Person') and column_id = 2")!;
        AreEqual("""{"type":"node","schema":"dbo","table":"Person","id":0}""", sim.ExecuteScalar($"select [{nodeId}] from Person where id = 1"));
    }

    [TestMethod]
    public void EdgeConstraint_HelpConstraint()
    {
        using var reader = Seeded(Constrained).ExecuteReader("exec sp_helpconstraint 'L', 'nomsg'");
        IsTrue(reader.Read());
        AreEqual("EDGE CONSTRAINT", reader.GetString(0));
        AreEqual("No Action", reader.GetString(2));
        AreEqual("CONNECTION (simulated.dbo.Person TO simulated.dbo.City, simulated.dbo.Person TO simulated.dbo.Person)", reader.GetString(6));
    }

    /// <summary>A node table's sp_helpconstraint closes with the edge constraints naming it, sorted.</summary>
    [TestMethod]
    public void EdgeConstraint_HelpConstraintOfTheNode()
    {
        using var reader = Seeded(Constrained).ExecuteReader("exec sp_helpconstraint 'City', 'nomsg'");
        List<string> last = [];
        do
        {
            last.Clear();
            while (reader.Read())
                last.Add(reader.GetString(0));
        } while (reader.NextResult());
        AreEqual("simulated.dbo.L: ec|simulated.dbo.L2: ec2", string.Join("|", last));
    }

    private const string Paths = """
        create table N (id int, name varchar(20), d decimal(5, 2)) as node;
        create table E (w int, m money) as edge;
        insert N values (1, 'a', 1.5), (2, 'b', 2.25), (3, 'c', 3), (4, 'd', 4), (5, 'e', 5), (6, 'f', 6);
        insert E select a.$node_id, b.$node_id, a.id * 10 + b.id, a.id * 1.5 from N a, N b
            where a.id = 1 and b.id = 2 or a.id = 2 and b.id = 3 or a.id = 3 and b.id = 4 or a.id = 1 and b.id = 3 or a.id = 4 and b.id = 1 or a.id = 5 and b.id = 6;
        """;

    [TestMethod]
    [DataRow("select string_agg(n2.name, '->') within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id = 1 order by 1", "b;c;c->d;c->d->a")]
    [DataRow("select string_agg(n2.name, '->') within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2){1,2})) and n1.id = 1 order by 1", "b;c;c->d")]
    [DataRow("select string_agg(n2.name, '->') within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(<-(e)-n2)+)) and n1.id = 1 order by 1", "d;d->c;d->c->a;d->c->b")]
    [DataRow("select concat(n1.name, ':', string_agg(n2.name, '->') within group (graph path)) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id in (2, 5) order by 1", "b:c;b:c->d;b:c->d->a;b:c->d->a->b;e:f")]
    [DataRow("select concat(last_value(n2.name) within group (graph path), ':', count(n2.id) within group (graph path), ':', sum(e.w) within group (graph path)) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id = 1 order by 1", "a:3:88;b:1:12;c:1:13;d:2:47")]
    [DataRow("select concat(avg(e.w) within group (graph path), ':', avg(n2.d) within group (graph path), ':', max(e.m) within group (graph path), ':', min(n2.name) within group (graph path)) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id = 1 order by 1", "12:2.250000:1.50:b;13:3.000000:1.50:c;23:3.500000:4.50:c;29:2.833333:6.00:a")]
    [DataRow("with q as (select n1.name n, last_value(n2.name) within group (graph path) dest from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))) select n from q where dest = 'a' order by 1", "a;b;c;d")]
    [DataRow("create table G (x int) as edge; insert G select a.$node_id, b.$node_id, 7 from N a, N b where a.id = 4 and b.id = 6; select concat(string_agg(n2.name, '->') within group (graph path), ':', x.name) from N n1, E for path e, N for path n2, G g, N x where match(shortest_path(n1(-(e)->n2)+) and last_node(n2)-(g)->x) and n1.id = 1", "c->d:f")]
    [DataRow("select count(*) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))", "17")]
    [DataRow("update E set w = null where w = 13; select string_agg(cast(e.w as varchar), ',') within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id = 1 order by 1", "NULL;12;34;34,41")]
    [DataRow("select last_value(n2.name) within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+) and last_node(n2) = last_node(n2)) and n1.id = 1 order by 1", "a;b;c;d")]
    public void ShortestPath_Rows(string query, string expected) => AreEqual(expected, Rows(Seeded(Paths), query));

    [TestMethod]
    [DataRow("select string_agg(n2.name, '') within group (graph path) s, count(n2.id) within group (graph path) c, sum(e.w) within group (graph path) w, avg(n2.d) within group (graph path) d, last_value(n2.name) within group (graph path) l", "varchar(8000),int,int,decimal(38,6),varchar(20)")]
    public void ShortestPath_ResultTypes(string projection, string expected)
    {
        using var reader = Seeded(Paths).ExecuteReader($"exec sp_describe_first_result_set N'{projection.Replace("'", "''", StringComparison.Ordinal)} from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))'");
        var types = new List<string>();
        while (reader.Read())
            types.Add(reader.GetString(reader.GetOrdinal("system_type_name")));
        AreEqual(expected, string.Join(",", types));
    }

    [TestMethod]
    [DataRow("select n1.name, n2.name from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))", 13961)]
    [DataRow("select n1.name from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n2.id = 3", 13961)]
    [DataRow("select n1.name from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) order by n2.name", 13961)]
    [DataRow("select n1.name from N n1, E e, N n2 where match(shortest_path(n1(-(e)->n2)+))", 13948)]
    [DataRow("select n1.name from N n1, E for path e, N for path n2 where match(n1-(e)->n2)", 13949)]
    [DataRow("select string_agg(n1.name, '->') within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))", 13954)]
    [DataRow("select string_agg(n2.name, '->') within group (graph path) from N n2", 13952)]
    [DataRow("select count(*) within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))", 102)]
    [DataRow("select count(distinct n2.id) within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))", 102)]
    [DataRow("select 1 from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2){0,2}))", 13942)]
    [DataRow("select 1 from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2){1,1}))", 13943)]
    [DataRow("select 1 from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)*))", 102)]
    [DataRow("select (select count(*) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)))", 13957)]
    [DataRow("select 1 from N n1, E for path e, N for path n2, N n3 where match(shortest_path(n1(-(e)->n2)+) and last_node(n2) = n3)", 102)]
    public void ShortestPath_Refusals(string query, int number) => _ = Seeded(Paths).AssertSqlError(query, number);

    // ---- probed 2026-10-05 against SQL Server 2025 ----

    [TestMethod]
    [DataRow("select p.name from Person p where not exists (select 1 from Likes k, Person p2 where match(p-(k)->p2)) order by 1", "d")]
    [DataRow("select p.name, (select count(*) from Likes k, Person p2 where match(p-(k)->p2)) from Person p order by 1", "a|1;b|1;c|1;d|0")]
    public void Match_In_A_Subquery_Binds_An_Outer_Node(string query, string expected)
        => AreEqual(expected, Rows(Seeded(), query));

    [TestMethod]
    public void Single_Table_Writes_Read_The_Pseudo_Columns()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteNonQuery("delete Likes where $from_id = (select $node_id from Person where id = 3)"));
        AreEqual(1, sim.ExecuteNonQuery("update Person set name = 'z' where $node_id = (select $node_id from Person where id = 4)"));
        AreEqual("2|z", Rows(sim, "select (select count(*) from Likes), (select name from Person where id = 4)"));
    }

    [TestMethod]
    [DataRow("create table E (constraint Person connection (Person to City)) as edge", 2714, 5)]
    [DataRow("create table E (constraint ec connection (Person to City)) as edge; create table E2 (constraint ec connection (Person to City)) as edge", 2714, 5)]
    [DataRow("create table P2 (id int primary key nonclustered) as node with (memory_optimized = on)", 13910, 1)]
    [DataRow("create table P2 (id int primary key, vf datetime2 generated always as row start, vt datetime2 generated always as row end, period for system_time (vf, vt)) as node with (system_versioning = on)", 13912, 1)]
    [DataRow("alter table Person drop column $node_id", 102, 1)]
    [DataRow("alter table Person alter column $node_id nvarchar(10)", 102, 1)]
    [DataRow("alter table Person as edge", 156, 1)]
    public void Graph_Ddl_Refusals(string statement, int number, int state)
        => AreEqual(state, Seeded().AssertSqlError(statement, number).State);

    [TestMethod]
    public void Edge_Constraint_Names_The_Target_First_And_A_Drop_Names_The_Table_As_Written()
    {
        var sim = Seeded();
        AreEqual("Edge constraint 'ec' references invalid table 'Y'.  Table could not be found.", sim.AssertSqlError("create table E (constraint ec connection (X to Y)) as edge", 13931).Errors[0].Message);
        sim.ExecuteBatches("create table E (constraint ec connection (Person to City)) as edge");
        AreEqual("Could not drop node table 'dbo.City' because it is referenced by an edge constraint.", sim.AssertSqlError("drop table dbo.City", 13934).Message);
    }

    [TestMethod]
    public void Node_Id_Keys_An_Index_Or_Key_As_The_Graph_Id()
    {
        var sim = Seeded(
            "create table Q (id int, constraint pkq primary key ($node_id)) as node",
            "create unique index ux on City ($node_id)");
        AreEqual("graph_id_*;graph_id_*", System.Text.RegularExpressions.Regex.Replace(Rows(sim, """
            select c.name from sys.index_columns ic join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
            join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id
            where i.name in ('pkq', 'ux') order by i.name
            """), "_[0-9A-F]{32}", "_*"));
    }

    [TestMethod]
    public void A_Partitioned_Node_Table_Keeps_Its_Graph_Index_On_Primary()
    {
        var sim = Seeded(
            "create partition function pf (int) as range for values (10)",
            "create partition scheme ps as partition pf all to ([primary])",
            "create table P2 (id int) as node on ps(id)");
        AreEqual("PRIMARY;ps", Rows(sim, "select ds.name from sys.indexes i join sys.data_spaces ds on ds.data_space_id = i.data_space_id where i.object_id = object_id('P2') order by i.index_id desc"));
    }

    // ---- probed 2026-10-11 against SQL Server 2025 ----

    private const string Mixed = """
        create table M (k int) as node;
        create table F (z int) as edge;
        insert M values (100), (200), (300);
        insert F select a.$node_id, b.$node_id, a.k + b.id from M a, N b where a.k = 100 and b.id in (1, 2) or a.k = 200 and b.id = 5;
        insert F select b.$node_id, a.$node_id, b.id from M a, N b where a.k = 300 and b.id in (2, 3);
        """;

    [TestMethod]
    [DataRow("select string_agg(concat(n2.name, '/', n3.name), '->') within group (graph path) from N n1, E for path e1, N for path n2, E for path e2, N for path n3 where match(shortest_path(n1(-(e1)->n2-(e2)->n3)+)) and n1.id = 1", "b/c;c/d;b/c->d/a;c/d->a/b")]
    [DataRow("select count(*) from N n1, E for path e1, N for path n2, E for path e2, N for path n3 where match(shortest_path(n1(-(e1)->n2-(e2)->n3){1,2}))", "14")]
    [DataRow("select last_value(n2.name) within group (graph path) from N n1, E for path e1, N for path n2, E for path e2, N for path n3 where match(shortest_path(n1(<-(e1)-n2<-(e2)-n3)+)) and n1.id = 1", "d;b;a;c")]
    [DataRow("select sum(e1.w) within group (graph path), sum(e2.w) within group (graph path), count(e1.w) within group (graph path), avg(n2.d) within group (graph path) from N n1, E for path e1, N for path n2, E for path e2, N for path n3 where match(shortest_path(n1(-(e1)->n2-(e2)->n3)+)) and n1.id = 4", "41|12|1|1.500000;41|13|1|1.500000;75|54|2|2.750000;64|46|2|2.250000")]
    [DataRow("select n1.name, string_agg(concat(m.k, '/', n2.name), '->') within group (graph path) from N n1, F for path f1, M for path m, F for path f2, N for path n2 where match(shortest_path(n1(<-(f1)-m-(f2)->n2)+)) order by 1, 2", "a|100/a;a|100/b;b|100/a;b|100/b;e|200/e")]
    [DataRow("select n1.name, string_agg(m.k, '->') within group (graph path) from N n1, E for path e, N for path n2, F for path f, M for path m where match(shortest_path(n1(-(e)->n2<-(f)-m)+)) order by 1", "a|100;d|100")]
    [DataRow("select n1.name, string_agg(n2.name, '->') within group (graph path), n3.name, string_agg(n4.name, '->') within group (graph path) from N n1, E for path e1, N for path n2, N n3, E for path e2, N for path n4 where match(shortest_path(n1(-(e1)->n2)+) and shortest_path(n3(-(e2)->n4)+) and last_node(n2) = last_node(n4)) and n1.id = 1 and n3.id = 2 order by 2", "a|b|b|c->d->a->b;a|c|b|c;a|c->d|b|c->d;a|c->d->a|b|c->d->a")]
    [DataRow("select count(*) from N n1, E for path e1, N for path n2, N n3, E for path e2, N for path n4 where match(shortest_path(n1(-(e1)->n2)+) and shortest_path(n3(-(e2)->n4)+) and last_node(n2) = last_node(n4))", "65")]
    [DataRow("select count(*) from N n1, E for path e1, N for path n2, E for path e2, N for path n4 where match(shortest_path(n1(-(e1)->n2)+)) and match(shortest_path(n1(-(e2)->n4)+))", "65")]
    [DataRow("select count(*) from N n1, E for path e1, N for path n2, E for path e2, N for path n4 where match(shortest_path(last_node(n2)(-(e2)->n4)+) and shortest_path(n1(-(e1)->n2)+))", "64")]
    [DataRow("select count(*) from N n1, E for path e1, N for path n2 where match(shortest_path(n1(-(e1)->n2)+)) or n1.id = 99", "17")]
    public void ShortestPath_Repeated_Sections_And_Several_Paths(string query, string expected) => AreEqual(expected, Rows(Seeded(Paths, Mixed), query));

    [TestMethod]
    [DataRow("select n1.name, string_agg(n2.name, '->') within group (graph path) from (select * from N where id < 3) n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id = 1", "a|b;a|c;a|c->d;a|c->d->a")]
    [DataRow("with c as (select * from N) select n1.name, string_agg(n2.name, '->') within group (graph path) from c n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id = 1", "a|b;a|c;a|c->d;a|c->d->a")]
    [DataRow("select n1.name, string_agg(n2.name, '->') within group (graph path) from N n1, (select * from E where w <> 13) for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id = 1", "a|b;a|b->c;a|b->c->d;a|b->c->d->a")]
    [DataRow("select n1.name, string_agg(n2.name, '->') within group (graph path), count(n2.id) within group (graph path) from N n1, E for path e, (select * from N where id <> 3) for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id = 1", "a|b|1;a|d|1;a|d->a|2")]
    public void ShortestPath_Reads_Through_Derived_Sources(string query, string expected) => AreEqual(expected, Rows(Seeded(Paths), query));

    [TestMethod]
    public void ShortestPath_Walks_Through_A_Missing_Node()
        => AreEqual("a|?|NULL;a|c|3;a|c->d|4;a|c->d->a|1", Rows(Seeded(Paths, "delete N where id = 2"), """
            select n1.name, string_agg(isnull(n2.name, '?'), '->') within group (graph path), last_value(n2.id) within group (graph path)
            from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.id = 1
            """));

    [TestMethod]
    [DataRow("s p q t", "s>p s>q p>t q>t", "p;q;p>t")]
    [DataRow("s q p t", "s>p s>q p>t q>t", "q;p;q>t")]
    [DataRow("s a b x y t", "s>a s>b a>y b>x x>t y>t", "a;b;b>x;a>y;b>x>t")]
    public void ShortestPath_Reports_Each_Depth_In_Graph_Id_Order(string nodes, string edges, string expected)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table XN (name varchar(10)) as node; create table XE as edge",
            string.Join(";", nodes.Split(' ').Select(node => $"insert XN values ('{node}')")),
            string.Join(";", edges.Split(' ').Select(edge => $"insert XE select a.$node_id, b.$node_id from XN a, XN b where a.name = '{edge[0]}' and b.name = '{edge[2]}'")));
        AreEqual(expected, Rows(sim, "select string_agg(n2.name, '>') within group (graph path) from XN n1, XE for path e, XN for path n2 where match(shortest_path(n1(-(e)->n2)+)) and n1.name = 's'"));
    }

    [TestMethod]
    [DataRow("select string_agg(concat(n2.name, n4.name), ',') within group (graph path) from N n1, E for path e1, N for path n2, N n3, E for path e2, N for path n4 where match(shortest_path(n1(-(e1)->n2)+) and shortest_path(n3(-(e2)->n4)+))", 13953, 1)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, E e2, N n4 where match(shortest_path(n1(-(e1)->n2)+) and n2-(e2)->n4)", 13906, 1)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, E for path e2, N for path n4 where match(shortest_path(n1(-(e1)->n2)+) and shortest_path(n2(-(e2)->n4)+))", 13965, 1)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, N n3 where match(shortest_path(n1(-(e1)->n2)+) and last_node(n2) = last_node(n3))", 13965, 7)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, N n3 where match(shortest_path(n1(-(e1)->n2)+) and last_node(n3) = last_node(n2))", 13962, 6)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, F for path f, M for path m where match(shortest_path(n1(-(e1)->n2)+) and shortest_path(n1(<-(f)-m)+) and last_node(n2) = last_node(m))", 13964, 2)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2 where match(shortest_path(n1(-(e1)->n2)+) and shortest_path(n1(-(e1)->n2)+))", 13950, 1)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, E for path e2 where match(shortest_path(n1(-(e1)->n2-(e2)->n1)+))", 13948, 1)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, M for path m where match(shortest_path(n1(-(e1)->n2-(e1)->m)+))", 13903, 1)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2 where match(shortest_path(last_node(n1)(-(e1)->n2)+))", 13962, 2)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, N n3, E e2, N x where match(shortest_path(n1(-(e1)->n2)+) and last_node(n3)-(e2)->x)", 13962, 4)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, N n3, E e2, N x where match(shortest_path(n1(-(e1)->n2)+) and x-(e2)->last_node(n3))", 13962, 5)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, E e2, N x where match(shortest_path(n1(-(e1)->n2)+) and last_node(e1)-(e2)->x)", 13962, 4)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2 where match(last_node(n2) = last_node(n2))", 13962, 11)]
    [DataRow("select 1 from N n1, E for path e1, N for path n2, E for path e2 where match(shortest_path(n1(-(e1)->n2)+) and shortest_path(last_node(n2)(-(e2)->last_node(n2))+))", 102, 1)]
    [DataRow("select 1 from (select top 2 * from N order by id desc) n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))", 13940, 1)]
    public void ShortestPath_Refusals_Of_Paths_And_Last_Nodes(string query, int number, int state)
        => AreEqual(state, Seeded(Paths, Mixed).AssertSqlError(query, number).State);

    [TestMethod]
    public void ShortestPath_Refuses_Every_Path_Column_Clause_By_Clause()
    {
        var ex = Seeded(Paths).AssertSqlError("""
            select max(n2.d) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) and e.w = 1
            group by n2.id having min(e.m) > 0 order by max(n2.name)
            """, 13961);
        AreEqual("n2.name|n2.id|e.m|e.w|n2.d", string.Join("|", ex.Errors.Cast<SimulatedError>().Select(error => error.Message.Split('\'')[1])));
        AreEqual(2, Seeded(Paths).AssertSqlError("select string_agg(n2.name, '->') within group (order by n2.name) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))", 13961).Errors.Count);
    }

    [TestMethod]
    [DataRow("select 1 from E k, N p1, E k2 where match(k-(p1)->k2)", "13902 p1|13901 k|13901 k2")]
    [DataRow("select 1 from N a, E e, N b, E f, N c where match(a-(b)->c-(e)->f)", "13902 b")]
    [DataRow("select 1 from (select distinct * from N) n1, (select distinct * from E) e, N n2 where match(n1-(e)->n2)", "13940 e|13940 n1")]
    [DataRow("select 1 from N p1 cross apply (select * from E) k, N p2 where match(p1-(k)->p2)", "13920 k|13920 p1")]
    [DataRow("select 1 from N p cross apply (select p2.name from E k, N p2 where match(p-(k)->p2)) q", "13920 p")]
    [DataRow("select 1 from N p, E k cross apply (select p2.name from N p2 where match(p-(k)->p2)) q", "13920 k")]
    public void Match_Reports_Binding_Refusals_In_Reals_Order(string query, string expected)
    {
        var ex = Seeded(Paths).AssertSqlError(query, int.Parse(expected[..5], System.Globalization.CultureInfo.InvariantCulture));
        AreEqual(expected, string.Join("|", ex.Errors.Cast<SimulatedError>().Select(error => $"{error.Number} {error.Message.Split('\'')[1]}")));
    }

    [TestMethod]
    public void Match_Refusal_Under_A_Deferred_Compile_Ends_The_Batch()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("create table A (id int) as node; create table B as edge; select 1 from A a1 join B b on 1 = 1, A a2 where match(a1-(b)->a2); create table Z (id int)", 13920);
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('Z')"));
    }

    [TestMethod]
    public void Index_On_An_Endpoint_Keys_Its_Object_And_Graph_Ids()
    {
        var sim = Seeded(
            "create index ix on Likes ($from_id desc) include ($to_id)",
            "create table K2 (w int, constraint uq unique ($from_id, $to_id)) as edge",
            "create statistics st on Likes ($to_id)");
        AreEqual("from_obj_id_*|1|True|False;from_id_*|2|True|False;to_obj_id_*|0|False|True;to_id_*|0|False|True", System.Text.RegularExpressions.Regex.Replace(Rows(sim, """
            select c.name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column from sys.index_columns ic join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
            where ic.object_id = object_id('Likes') and ic.index_id = (select index_id from sys.indexes where name = 'ix') order by ic.index_column_id
            """), "_[0-9A-F]{32}", "_*"));
        AreEqual(4, sim.ExecuteScalar("select count(*) from sys.index_columns where object_id = object_id('K2') and index_id = (select index_id from sys.indexes where name = 'uq')"));
        AreEqual("to_obj_id_*;to_id_*", System.Text.RegularExpressions.Regex.Replace(Rows(sim, "select col_name(object_id('Likes'), column_id) from sys.stats_columns where object_id = object_id('Likes') and stats_id = (select stats_id from sys.stats where name = 'st') order by stats_column_id"), "_[0-9A-F]{32}", "_*"));
    }

    [TestMethod]
    public void View_Over_A_Graph_Table_Lists_Its_Hidden_Graph_Columns()
    {
        var sim = Seeded(
            "create view V1 as select $node_id nid, id from Person",
            "create view V2 as select w, $from_id f from Likes",
            "create view V3 as select top 2 id from Person",
            "create view V4 as select id from V3",
            "create view V5 as select p.id from Person p, City c");
        AreEqual("nid|2|False;id|NULL|False;graph_id_*|1|True;obj_id_*|9|True", System.Text.RegularExpressions.Regex.Replace(Rows(sim,
            "select name, graph_type, is_hidden from sys.columns where object_id = object_id('V1') order by column_id"), "_[0-9A-F]{32}", "_*"));
        AreEqual("w;f;graph_id_*;from_obj_id_*;from_id_*;to_obj_id_*;to_id_*", System.Text.RegularExpressions.Regex.Replace(Rows(sim,
            "select column_name from information_schema.columns where table_name = 'V2' order by ordinal_position"), "_[0-9A-F]{32}", "_*"));
        AreEqual("3|0|0", Rows(sim, "select (select count(*) from sys.columns where object_id = object_id('V3')), (select count(*) from sys.columns where object_id = object_id('V4') and is_hidden = 1), (select count(*) from sys.columns where object_id = object_id('V5') and is_hidden = 1)"));
        AreEqual("GRAPH_IV_NODE_OBJ_ID", sim.ExecuteScalar("select graph_type_desc from sys.columns where object_id = object_id('V1') and graph_type = 9"));
    }

    [TestMethod]
    public void Edge_Constraint_On_A_Non_Edge_Table_Stops_The_Batch_While_It_Compiles()
    {
        var sim = Seeded();
        AreEqual(1750, sim.AssertSqlError("create table Z (id int); if 1 = 0 create table T1 (a int, constraint ec connection (Person to City))", 13930).Errors[1].Number);
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('Z')"));
        AreEqual(0, sim.AssertSqlError("create table Z (id int); alter table Person add constraint ec connection (Person to City)", 13930).State);
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('Z')"));
        _ = sim.ExecuteNonQuery("begin try exec('create table T1 (a int, constraint ec connection (Person to City))') end try begin catch end catch; create table Z (id int)");
        AreNotEqual(DBNull.Value, sim.ExecuteScalar("select object_id('Z')"));
    }

    [TestMethod]
    [DataRow("grant select (id) on Person to u", "select $node_id from Person", "$node_id_*")]
    [DataRow("grant select (id) on Person to u", "select $node_id, id from Person", "$node_id_*")]
    [DataRow("grant select (id) on Person to u", "select id from Person order by $node_id", "$node_id_*")]
    [DataRow("grant select (w) on Likes to u", "select $from_id, w from Likes", "$from_id_*")]
    [DataRow("grant select on Person to u; deny select (name) on Person to u", "select $node_id from Person", null)]
    [DataRow("grant select on Person to u; deny select (id) on Person to u", "select $node_id from Person", null)]
    public void Pseudo_Column_Read_Asks_Select_On_Itself(string grants, string query, string? denied)
    {
        var sim = Seeded("create user u without login", grants);
        var statement = $"execute as user = 'u'; {query}; revert";
        if (denied is null)
        {
            _ = sim.ExecuteScalar(statement);
            return;
        }
        var ex = sim.AssertSqlError(statement, 230);
        AreEqual(1, ex.Errors.Count);
        StartsWith(denied.TrimEnd('*'), ex.Errors[0].Message.Split('\'')[1]);
    }

    [TestMethod]
    [DataRow("merge Likes l using Person p1 join Person p2 on p1.id + 2 = p2.id on match(p1-(l)->p2) when not matched then insert values (p1.$node_id, p2.$node_id, 13) when matched then update set w = 0; select w from Likes order by w", "12;13;13;23;99")]
    [DataRow("merge Likes l using Person p1 cross join Person p2 on match(p1-(l)->p2) when matched then update set w = w + 100; select w from Likes order by w", "112;123;199")]
    [DataRow("merge Likes l using Person p1 join Person p2 on 1 = 1 on match(p1-(l)->p2) when not matched by source then delete when matched then update set w = 5 when not matched by target then insert values (p1.$node_id, p2.$node_id, 7); select w, count(*) from Likes group by w order by w", "5|3;7|13")]
    [DataRow("merge Likes l using Person p1 join Person p2 on 1 = 1 on match(p1-(l)->p2) or 1 = 0 when matched then update set w = 6; select count(*) from Likes where w = 6", "3")]
    [DataRow("merge Likes l using (Person p1 cross join Person p2) on match(p2<-(l)-p1) and p1.id = 1 when matched then delete output deleted.w, p1.name, p2.name;", "12|a|b")]
    [DataRow("merge Likes l using Person p1 on match(p1-(l)->p1) when matched then delete; select count(*) from Likes", "3")]
    [DataRow("merge Likes Likes using Person p1 join Person p2 on 1 = 1 on match(p1-(Likes)->p2) when matched then delete; select count(*) from Likes", "0")]
    public void Merge_Binds_A_Match_Over_Its_Target_And_Joined_Source(string statement, string expected) => AreEqual(expected, Rows(Seeded(), statement));

    [TestMethod]
    [DataRow("merge Person using Likes l join Person p2 on 1 = 1 on match(Person-(l)->p2) when matched then update set name = 'z';", 13920)]
    [DataRow("merge Likes l using Person p1 left join Person p2 on p2.id = p1.id + 1 on match(p1-(l)->p2) when matched then delete;", 13920)]
    [DataRow("merge Likes l using Person p1 join Person p2 on p1.id = p2.id - 1 on match(p1-(l)->p2) when matched then update set w = id;", 209)]
    [DataRow("merge Likes l using Person p1 join Person p2 on 1 = 1 on match(p1-(l)->p2 and p2-(l)->p1) when matched then delete;", 13903)]
    [DataRow("merge Likes l using Person p1 join Person p2 on 1 = 1 on match(p1-(x)->p2) when matched then delete;", 13900)]
    [DataRow("merge Likes l using Person p1, Person p2 on match(p1-(l)->p2) when matched then delete;", 102)]
    public void Merge_Match_Refusals(string statement, int number) => _ = Seeded().AssertSqlError(statement, number);

    [TestMethod]
    public void Merge_Match_Over_An_Opaque_Derived_Source_Names_It_For_Each_Position()
        => AreEqual(2, Seeded().AssertSqlError("""
            merge Likes using (select p1.$node_id f, p2.$node_id t from Person p1, Person p2 where p1.id = 2 and p2.id = 4) s
            on match(s-(Likes)->s) when not matched then insert ($from_id, $to_id) values (s.f, s.t);
            """, 13940).Errors.Count);

    private const string Chain = """
        create table N (id int, name varchar(10)) as node;
        create table E (w int) as edge;
        create table M (id int, tag varchar(10)) as node;
        insert N values (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd');
        insert M values (10, 'm');
        insert E select (select $node_id from N where id = 1), (select $node_id from N where id = 2), 12;
        insert E select (select $node_id from N where id = 2), (select $node_id from N where id = 3), 23;
        insert E select (select $node_id from N where id = 3), (select $node_id from N where id = 4), 34;
        insert E select (select $node_id from N where id = 1), (select $node_id from M where id = 10), 110;
        """;

    private static Simulation Chained(params ReadOnlySpan<string> more)
    {
        var sim = new Simulation();
        sim.ExecuteBatches([Chain, "create view vN as select name from N", "create view vE as select w from E", .. more]);
        return sim;
    }

    [TestMethod]
    [DataRow("select n1.name, n2.name from (select name from N) n1, E e, N n2 where match(n1-(e)->n2) order by 1, 2", "a|b;b|c;c|d")]
    [DataRow("select n1.name, e.w, n2.name from vN n1, vE e, vN n2 where match(n1-(e)->n2) order by 1, 2", "a|12|b;b|23|c;c|34|d")]
    [DataRow("with c as (select name from N where id < 3) select n1.name, n2.name from c n1, E e, c n2 where match(n1-(e)->n2) order by 1, 2", "a|b")]
    [DataRow("select n1.x, n2.name from (select name as x, $node_id as nid from N) n1, E e, N n2 where match(n1-(e)->n2) order by 1, 2", "a|b;b|c;c|d")]
    [DataRow("select n1.name, n2.name from (select name from N where id > 1) n1, E e, N n2 where match(n1-(e)->n2) order by 1, 2", "b|c;c|d")]
    [DataRow("select n1.name, n2.name, e.w from N n1, (select w from E where w > 20) e, N n2 where match(n1-(e)->n2) order by 1, 2", "b|c|23;c|d|34")]
    [DataRow("select n1.name, n2.name from (select name from (select name, id from N) i) n1, E e, N n2 where match(n1-(e)->n2) order by 1, 2", "a|b;b|c;c|d")]
    public void Match_ReadsThroughABodyThatProjectsNoPseudoColumn(string query, string expected) => AreEqual(expected, Rows(Chained(), query));

    [TestMethod]
    public void Match_ThroughABodyThatProjectsNoPseudoColumn_StarSkipsTheIdentity()
        => AreEqual("$edge_id_*,$from_id_*,$to_id_*,w,name,$node_id_*,id,name", Columns(Chained(), "select * from (select name from N) n1, E e, N n2 where match(n1-(e)->n2)"));

    [TestMethod]
    [DataRow("select n1.name, e.w, n2.name, n2.id from N n1, E e, N n2 where match(n1-(e)->n2) order by e.w", "a|12|b|2;b|23|NULL|NULL;NULL|34|d|4")]
    [DataRow("select n1.name, e.w, n2.name from N n1, E e, N n2 where match(n1-(e)->n2) and n2.name is null", "b|23|NULL")]
    [DataRow("select n1.name, e.w, n2.name, f.w, n3.name from N n1, E e, N n2, E f, N n3 where match(n1-(e)->n2-(f)->n3) order by e.w, f.w", "a|12|b|23|NULL;b|23|NULL|34|d")]
    [DataRow("select e.w, f.w from N n1, E e, N n2, E f, N n3 where match(n1-(e)->n2 and n2-(f)->n3) order by e.w, f.w", "12|23;23|34")]
    [DataRow("select e.w, n2.name, n2.$node_id from N n1, E e, N n2 where match(n1-(e)->n2) and n2.id is null", "23|NULL|NULL")]
    [DataRow("select count(*) from N n1, E e, M n2 where match(n1-(e)->n2)", "1")]
    public void Match_NullExtendsANodeItsTableNoLongerHolds(string query, string expected) => AreEqual(expected, Rows(Chained("delete N where id = 3"), query));

    [TestMethod]
    [DataRow("select n1.name, count(*) from N n1, E for path e, N for path n2 where not match(shortest_path(n1(-(e)->n2)+)) group by n1.name order by 1", "a|12;b|12;c|12;d|12")]
    [DataRow("select e.w, count(*) from N n1, E for path e, N for path n2 where not match(shortest_path(n1(-(e)->n2)+)) group by e.w order by 1", "12|16;23|16;34|16")]
    [DataRow("select e.w, count(*) from N n1, E for path e, N for path n2 where not match(shortest_path(n1(<-(e)-n2)+)) group by e.w order by 1", "12|16;23|16;34|16")]
    [DataRow("select count(*) from N n1, E for path e, N for path n2 where not not match(shortest_path(n1(-(e)->n2)+))", "48")]
    [DataRow("select count(*) from N n1, E for path e, N for path n2 where not match(shortest_path(n1(-(e)->n2)+)) or 1 = 0", "48")]
    [DataRow("select count(*) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+)) or 1 = 0", "6")]
    [DataRow("select count(*) from N n1, E for path e, N for path n2", "64")]
    [DataRow("select count(*) from N n1, E for path e, N for path n2 where n1.id = 1", "16")]
    public void NotMatch_AppliesNoShortestPath(string query, string expected) => AreEqual(expected, Rows(Chained(), query));

    [TestMethod]
    public void NotMatch_ShortestPath_LeavesForPathSourcesUnused()
    {
        var sim = Chained();
        _ = sim.AssertSqlError("select count(*) from N n1, E for path e, N for path n2, E e3, N n3 where not match(shortest_path(n1(-(e)->n2)+)) and match(n1-(e3)->n3)", 13949);
        sim.AssertSqlError("select n1.name, string_agg(n2.name, '>') within group (graph path) from N n1, E for path e, N for path n2 where not match(shortest_path(n1(-(e)->n2)+))", 13952,
            "No columns in the aggregate 'string_agg' WITHIN GROUP(GRAPH PATH) reference a recursive path.");
    }

    [TestMethod]
    public void RowLevelSecurity_OverAGraphTable()
    {
        var sim = Chained(
            "create function dbo.fn(@id int) returns table with schemabinding as return select 1 ok where @id <> 3",
            "create function dbo.fe(@w int) returns table with schemabinding as return select 1 ok where @w <> 23",
            "create security policy p add filter predicate dbo.fn(id) on dbo.N with (state = on)");
        const string Path = "select n1.name, string_agg(n2.name, '>') within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))";

        // A hidden node NULL-pads the MATCH, and refuses a walk naming the path's node.
        AreEqual("a|12|b;b|23|NULL;NULL|34|d", Rows(sim, "select n1.name, e.w, n2.name from N n1, E e, N n2 where match(n1-(e)->n2) order by e.w"));
        sim.AssertSqlError(Path, 4104, "The multi-part identifier \"n2\" could not be bound.");
        AreEqual(3, sim.AssertSqlError(Path + " and 1 = 0", 4104).State);
        AreEqual(3, sim.AssertSqlError("select n1.name, count(e.w) within group (graph path) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))", 4104).State);
        AreEqual("27", Rows(sim, "select count(*) from N n1, E for path e, N for path n2 where not match(shortest_path(n1(-(e)->n2)+))"));

        // Over the edge table too the node is named first, and alone the edge at state 2.
        _ = sim.ExecuteNonQuery("alter security policy p add filter predicate dbo.fe(w) on dbo.E");
        AreEqual(3, sim.AssertSqlError(Path, 4104).State);
        _ = sim.ExecuteNonQuery("alter security policy p drop filter predicate on dbo.N");
        AreEqual("a|12|b;c|34|d", Rows(sim, "select n1.name, e.w, n2.name from N n1, E e, N n2 where match(n1-(e)->n2) order by e.w"));
        sim.AssertSqlError(Path, 4104, "The multi-part identifier \"e\" could not be bound.");
        AreEqual(2, sim.AssertSqlError(Path, 4104).State);
        _ = sim.ExecuteNonQuery("alter security policy p with (state = off)");
        AreEqual("6", Rows(sim, "select count(*) from N n1, E for path e, N for path n2 where match(shortest_path(n1(-(e)->n2)+))"));
    }
}
