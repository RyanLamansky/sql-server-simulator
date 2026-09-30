using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>GROUP BY ALL</c>: every group the source produces is kept whatever the
/// <c>WHERE</c> does to its rows, and the aggregates read only the rows the
/// <c>WHERE</c> keeps — plus real's refusals around it (probed 2026-09-30
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class GroupByAllTests
{
    private const string NullEliminated = "Warning: Null value is eliminated by an aggregate or other SET operation.";

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (g int, h int, v int);
            insert t values (1, 1, 10), (1, 2, 20), (2, 1, 30), (3, null, null), (null, 1, 5), (2, 2, null)
            """);
        return simulation;
    }

    private static List<string> Rows(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
        {
            var fields = new string[reader.FieldCount];
            for (var i = 0; i < fields.Length; i++)
                fields[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            rows.Add(string.Join("|", fields));
        }
        return rows;
    }

    [TestMethod]
    public void KeepsEveryGroup_AggregatingOnlyTheRowsTheWhereKeeps()
        => CollectionAssert.AreEqual(
            new[] { "NULL|0|0|NULL|NULL|NULL", "1|1|1|20|20|20", "2|1|1|30|30|30", "3|0|0|NULL|NULL|NULL" },
            Rows(Seeded(), "select g, count(*), count(v), sum(v), max(v), avg(v) from t where v > 15 group by all g order by g"));

    [TestMethod]
    public void WithoutAWhere_IsAPlainGroupBy()
        => CollectionAssert.AreEqual(
            new[] { "NULL|1", "1|2", "2|2", "3|1" },
            Rows(Seeded(), "select g, count(*) from t group by all g order by g"));

    [TestMethod]
    public void HavingReadsTheFilteredAggregates()
        => CollectionAssert.AreEqual(
            new[] { "NULL|0", "3|0" },
            Rows(Seeded(), "select g, count(*) from t where v > 15 group by all g having count(*) = 0 order by g"));

    [TestMethod]
    public void DistinctAggregatesAndAnEmptyWhere()
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            new[] { "NULL|0", "1|1", "2|1", "3|0" },
            Rows(simulation, "select g, count(distinct v) from t where v > 15 group by all g order by g"));
        CollectionAssert.AreEqual(
            new[] { "NULL|NULL|0", "1|NULL|0", "2|NULL|0", "3|NULL|0" },
            Rows(simulation, "select g, min(v), count_big(*) from t where 1 = 0 group by all g order by g"));
    }

    /// <summary>A row the <c>WHERE</c> drops never evaluates an aggregate's operand.</summary>
    [TestMethod]
    public void AFilteredRowNeverEvaluatesTheOperand()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table d (g int, v int); insert d values (1, 0), (2, 20)");
        CollectionAssert.AreEqual(new[] { "1|NULL", "2|0" }, Rows(simulation, "select g, sum(10 / v) from d where v > 15 group by all g order by g"));
    }

    /// <summary>The groups are those of the joined rows, before the <c>WHERE</c>.</summary>
    [TestMethod]
    public void GroupsComeFromTheJoinedRows()
        => CollectionAssert.AreEqual(
            new[] { "1|2", "2|2", "3|0" },
            Rows(Seeded(), "select a.g, count(*) from t a join t b on a.g = b.g where a.v > 15 group by all a.g order by a.g"));

    /// <summary>
    /// Without an <c>ORDER BY</c> the groups leave in the order a plain
    /// <c>GROUP BY</c>'s would — two keys with an aggregate sort by the second
    /// key first.
    /// </summary>
    [TestMethod]
    public void RowOrderFollowsThePlainGroupBy()
        => CollectionAssert.AreEqual(
            new[] { "3|NULL|0|NULL", "NULL|1|0|NULL", "1|1|0|NULL", "2|1|1|30", "1|2|1|20", "2|2|0|NULL" },
            Rows(Seeded(), "select g, h, count(*), sum(v) from t where v > 15 group by all g, h"));

    [TestMethod]
    public void NestedQueryShapes()
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            new[] { "NULL|0", "3|0" },
            Rows(simulation, "select * from (select g, count(*) c from t where v > 15 group by all g) d where c = 0 order by g"));
        AreEqual(0, simulation.ExecuteScalar("select (select count(*) from t where v > 100 group by all g having g = 1)"));
        _ = simulation.ExecuteNonQuery("create view v1 as select g, count(*) c from t where v > 15 group by all g");
        AreEqual(4, simulation.ExecuteScalar("select count(*) from v1"));
    }

    [TestMethod]
    public void EmptyGroupingSetAlone_IsOneGroup()
        => AreEqual(0, Seeded().ExecuteScalar("select count(*) from t where v > 100 group by all ()"));

    [TestMethod]
    [DataRow("select g, count(*) from t where v > 15 group by all g with cube")]
    [DataRow("select g, count(*) from t group by all g with rollup")]
    [DataRow("select g, count(*) from t group by all rollup(g)")]
    [DataRow("select g, count(*) from t group by all cube(g)")]
    [DataRow("select count(*) from t group by all grouping sets (())")]
    [DataRow("select g, count(*) from t group by all g, ()")]
    [DataRow("select g, count(*) from t group by all (), g")]
    [DataRow("select count(*) from t group by all (), ()")]
    public void GroupingSetConstructs_Msg1028(string sql)
        => Seeded().AssertSqlError(sql, 1028, "The CUBE, ROLLUP, and GROUPING SETS constructs are not allowed in a GROUP BY ALL clause.");

    /// <summary>
    /// Msg 1028 is raised while the batch parses, at the token after the
    /// grouping list, or at the legacy form's <c>ROLLUP</c>; it outranks a name
    /// that doesn't resolve and fires in a branch that never runs.
    /// </summary>
    [TestMethod]
    public void Msg1028_IsAParseError()
    {
        var simulation = Seeded();
        AreEqual(6, simulation.AssertSqlError("select g, count(*) from t\nwhere v > 1\ngroup by all\ng,\ncube(g)\n;", 1028).LineNumber);
        AreEqual(6, simulation.AssertSqlError("select g, count(*) from t\nwhere v > 1\ngroup by all\ng\nwith\nrollup\n;", 1028).LineNumber);
        _ = simulation.AssertSqlError("select g, count(*) from nosuch where nocolumn > 1 group by all g with cube", 1028);
        _ = simulation.AssertSqlError("select 1; if 1 = 0 select g, count(*) from t group by all cube(g)", 1028);
        var both = simulation.AssertSqlError("select from; select g, count(*) from t group by all g with rollup; select from", 156);
        CollectionAssert.AreEqual(new[] { 156, 1028 }, both.Errors.Select(error => error.Number).ToArray());
    }

    [TestMethod]
    public void GroupByAllWithNothingAfterIt_Msg102()
        => Seeded().AssertSqlError("select count(*) from t group by all;", 102);

    [TestMethod]
    [DataRow("create view v2 with schemabinding as\nselect g, count_big(*) c from dbo.t\ngroup by\nall g")]
    [DataRow("create function f() returns table with schemabinding as return\nselect g from dbo.t where nosuch > 1\ngroup by\nall g")]
    [DataRow("create function f() returns int with schemabinding as begin\nreturn (select top 1 count(*) from dbo.t\ngroup by\nall g) end")]
    public void SchemaBoundBody_Msg1054(string sql)
    {
        var error = Seeded().AssertSqlError(sql, 1054);
        AreEqual("Syntax 'ALL' is not allowed in schema-bound objects.", error.Errors[0].Message);
        AreEqual(8, error.State);
        AreEqual(4, error.LineNumber);
    }

    private static Simulation WithLinkedServer()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.r (g int, v int); insert r values (1, 10), (2, 30)");
        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver @server = 'OTHER', @srvproduct = 'SQL Server'; create table l (g int, v int); insert l values (1, 10), (2, 30)");
        return local;
    }

    /// <summary>
    /// A query whose own FROM reads a linked server can't combine GROUP BY ALL
    /// with a WHERE: Msg 7417 while the batch compiles, so nothing in the batch
    /// runs, a dead branch raises it and a TRY doesn't catch it.
    /// </summary>
    [TestMethod]
    [DataRow("select g, count(*) from OTHER.simulated.dbo.r where v > 15 group by all g")]
    [DataRow("select a.g, count(*) from l a join OTHER.simulated.dbo.r b on a.g = b.g where a.v > 15 group by all a.g")]
    [DataRow("select g, count(*) from (select * from OTHER.simulated.dbo.r) d where v > 15 group by all g")]
    [DataRow("select g, count(*) from openquery(OTHER, 'select g, v from simulated.dbo.r') d where v > 15 group by all g")]
    [DataRow("select 1; if 1 = 0 select g, count(*) from OTHER.simulated.dbo.r where v > 15 group by all g")]
    [DataRow("begin try select g, count(*) from OTHER.simulated.dbo.r where v > 15 group by all g end try begin catch select 0 end catch")]
    public void RemoteSourceWithAWhere_Msg7417(string sql)
        => WithLinkedServer().AssertSqlError(sql, 7417, "GROUP BY ALL is not supported in queries that access remote tables if there is also a WHERE clause in the query.");

    [TestMethod]
    public void RemoteSourceWithoutAWhere_OrOnlyInASubquery_Runs()
    {
        var simulation = WithLinkedServer();
        CollectionAssert.AreEqual(new[] { "1|1", "2|1" }, Rows(simulation, "select g, count(*) from OTHER.simulated.dbo.r group by all g order by g"));
        CollectionAssert.AreEqual(
            new[] { "1|0", "2|1" },
            Rows(simulation, "select g, count(*) from l where v in (select v from OTHER.simulated.dbo.r where v > 15) group by all g order by g"));
    }

    private static (SimulatedDbConnection Connection, List<string> Log) Open(string setup)
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        _ = connection.CreateCommand(setup).ExecuteNonQuery();
        var log = new List<string>();
        connection.InfoMessage += (_, e) => log.Add($"{e.Errors[0].Number}: {e.Message}");
        return (connection, log);
    }

    /// <summary>
    /// Over a WHERE, real sends Msg 8153 whenever a row reached the grouping
    /// and the query holds an aggregate that reports a skipped NULL —
    /// <c>COUNT(*)</c> too, and even when every row passed the WHERE.
    /// </summary>
    [TestMethod]
    [DataRow("select g, count(*) from t where v > 15 group by all g", true)]
    [DataRow("select g, count(*) from t where 1 = 1 group by all g", true)]
    [DataRow("select g, sum(v) from t where v > 5 group by all g", true)]
    [DataRow("select g from t where v > 5 group by all g having count(*) > 0", true)]
    [DataRow("select g, count(*) from t group by all g", false)]
    [DataRow("select g, string_agg(cast(v as varchar(9)), ',') from t where v > 15 group by all g", false)]
    [DataRow("select g, grouping(g) from t where v > 5 group by all g", false)]
    [DataRow("select g, count(*) from e where v > 1 group by all g", false)]
    [DataRow("set ansi_warnings off; select g, count(*) from t where v > 15 group by all g", false)]
    [DataRow("select count(*) where 1 = 0 group by all ()", false)]
    [DataRow("declare @x int = 0; select count(*) where @x = 1 group by all ()", true)]
    public void NullEliminatedWarning(string sql, bool warns)
    {
        var (connection, log) = Open("create table t (g int, v int); insert t values (1, 10), (1, 20), (2, 30); create table e (g int, v int)");
        _ = connection.CreateCommand(sql).ExecuteNonQuery();
        AreEqual(warns ? 1 : 0, log.Count(entry => entry == "8153: " + NullEliminated));
    }
}
