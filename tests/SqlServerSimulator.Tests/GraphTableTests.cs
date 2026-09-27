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
    public void EdgeConstraint_HelpConstraint()
    {
        using var reader = Seeded(Constrained).ExecuteReader("exec sp_helpconstraint 'L', 'nomsg'");
        IsTrue(reader.Read());
        AreEqual("EDGE CONSTRAINT", reader.GetString(0));
        AreEqual("No Action", reader.GetString(2));
        AreEqual("CONNECTION (simulated.dbo.Person TO simulated.dbo.City, simulated.dbo.Person TO simulated.dbo.Person)", reader.GetString(6));
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
    public void ShortestPath_Refusals(string query, int number) => _ = Seeded(Paths).AssertSqlError(query, number);
}
