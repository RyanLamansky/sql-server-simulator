using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>DROP INDEX</c> over XML and spatial indexes, and the neighbors that
/// care whether one exists. Probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class XmlAndSpatialIndexDropTests
{
    private const string Setup = """
        create table t (id int constraint pk primary key, x xml, g geometry);
        create primary xml index px on t (x);
        create xml index sx on t (x) using xml index px for value;
        create spatial index gx on t (g) with (bounding_box = (0, 0, 10, 10));
        """;

    [TestMethod]
    public void DroppingAPrimaryXmlIndex_TakesItsSecondariesAlong()
        => AreEqual("pk|gx", new Simulation().ExecuteScalar(Setup + """
            drop index px on t;
            select string_agg(name, '|') within group (order by index_id) from sys.indexes where object_id = object_id('t')
            """));

    [TestMethod]
    public void DroppingEachKind_LeavesTheOthers()
        => AreEqual("0:1:0", new Simulation().ExecuteScalar(Setup + """
            drop index sx on t; drop index gx on t;
            select concat((select count(*) from sys.xml_indexes where secondary_type is not null), ':', (select count(*) from sys.xml_indexes), ':', (select count(*) from sys.spatial_indexes))
            """));

    [TestMethod]
    public void ARecreatedIndex_TakesOnePastTheHighestId()
        => AreEqual("py:256001|px2:256002|sx2:256003", new Simulation().ExecuteScalar("""
            create table t (id int primary key, x xml, y xml);
            create primary xml index px on t (x); create primary xml index py on t (y);
            create xml index sx on t (x) using xml index px for value;
            drop index px on t;
            create primary xml index px2 on t (x);
            create xml index sx2 on t (x) using xml index px2 for path;
            select string_agg(concat(name, ':', index_id), '|') within group (order by index_id) from sys.xml_indexes
            """));

    [TestMethod]
    public void RollingBack_RestoresTheIndex()
        => AreEqual(3, new Simulation().ExecuteScalar(Setup + """
            begin tran; drop index px on t; drop index gx on t; rollback;
            select (select count(*) from sys.xml_indexes) + (select count(*) from sys.spatial_indexes)
            """));

    /// <summary>Msg 3749 is raised while compiling, so nothing ahead of it in the batch runs.</summary>
    [TestMethod]
    [DataRow("px")]
    [DataRow("gx")]
    public void TheOldTableDotIndexForm_IsRefusedWhileCompiling(string index)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup + "create table v (a int)");
        sim.AssertSqlError($"insert v values (1); drop index if exists t.{index}", 3749, $"Cannot drop XML Index 't.{index}' using old 'Table.Index' syntax, use 'Index ON Table' syntax instead.");
        AreEqual(0, sim.ExecuteScalar("select count(*) from v"));
    }

    [TestMethod]
    public void ThePrimaryKey_CannotBeDroppedUnderAnXmlOrSpatialIndex()
        => AreEqual(3727, new Simulation().AssertSqlError(Setup + "alter table t drop constraint pk", 3734).Errors[1].Number);

    [TestMethod]
    public void ADroppedIndexIsGone_ForAnotherDrop()
        => _ = new Simulation().AssertSqlError(Setup + "drop index gx on t; drop index gx on t", 3701);

    [TestMethod]
    public void ATemporaryTable_TakesAnXmlIndex()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table #t (id int primary key, x xml); insert #t values (1, '<a b="1"/>');
            create primary xml index px on #t (x);
            create xml index sx on #t (x) using xml index px for value;
            drop index sx on #t;
            select x.value('(/a/@b)[1]', 'int') from #t
            """));

    [TestMethod]
    [DataRow("create primary xml index px on nope (x)", 1088, 201)]
    [DataRow("create xml index sx on nope (x) using xml index px for path", 1088, 201)]
    [DataRow("create spatial index sx on nope (g)", 1088, 202)]
    [DataRow("create fulltext catalog c as default; create fulltext index on nope (a) key index pk", 208, 49)]
    [DataRow("create fulltext catalog c as default; create table #t (id int not null constraint pk primary key, a nvarchar(10)); create fulltext index on #t (a) key index pk", 208, 48)]
    public void AMissingTarget_RaisesItsKindsState(string statement, int number, int state)
        => AreEqual(state, new Simulation().AssertSqlError(statement, number).State);

    [TestMethod]
    [DataRow("drop index pk on t", "t.pk")]
    [DataRow("drop index t.pk", "t.pk")]
    [DataRow("drop index pk on dbo.t", "dbo.t.pk")]
    public void AKeyBackingIndex_IsNamedAsWritten(string statement, string name)
        => Contains($"index '{name}'", new Simulation().AssertSqlError("create table t (id int constraint pk primary key); " + statement, 3723).Message);

    [TestMethod]
    public void AKeyBackingIndexRefusal_EndsTheBatchAndRollsBack()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int constraint pk primary key); create table v (a int)");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "begin tran; insert v values (1); drop index pk on t; insert v values (2)";
        _ = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
        command.CommandText = "select concat(@@trancount, ':', (select count(*) from v))";
        AreEqual("0:0", command.ExecuteScalar());
    }
}
