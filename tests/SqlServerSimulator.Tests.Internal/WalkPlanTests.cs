using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which <c>SELECT</c> statements of a batch compiling for the first time run
/// on the batch compile walk's parse of them rather than a second parse,
/// observed through <see cref="Simulation.WalkPlanReplays"/>: a plain query
/// does, while one whose parse sends what a compile sends, reads what belongs
/// to the session, or runs after the batch changed what it would parse under,
/// parses as it runs. Whether the run reports what a parse of its own would is
/// the public suites' to show.
/// </summary>
[TestClass]
public sealed class WalkPlanTests
{
    private const string Setup = """
        create table t (id int primary key, v int not null);
        insert t values (1, 1), (2, 2);
        create table u (id int primary key, t_id int not null);
        insert u values (10, 1);
        """;

    private static void Execute(DbConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        using var reader = command.ExecuteReader();
        while (reader.NextResult())
        {
        }
    }

    /// <summary>
    /// Runs <see cref="Setup"/> and <paramref name="before"/> (batches split on
    /// <c>go</c> lines), then <paramref name="text"/> once, returning the walk's
    /// parses its run replayed.
    /// </summary>
    private static long WalkReplays(string text, string before = "")
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        foreach (var batch in before.Split("\ngo\n", StringSplitOptions.RemoveEmptyEntries))
            Execute(connection, batch);
        var replays = simulation.WalkPlanReplays;
        Execute(connection, text);
        return simulation.WalkPlanReplays - replays;
    }

    [TestMethod]
    [DataRow("select v from t where id = 1")]
    [DataRow("select t.v, u.id from t join u on u.t_id = t.id where t.id = 1 order by u.id")]
    [DataRow("select count(*) from t where v in (select t_id from u)")]
    [DataRow("select v from t where id = 1; select id from u")]
    public void APlainQuery_RunsOnTheWalksParse(string text) => AreEqual(text.Split(';').Length, WalkReplays(text));

    [TestMethod]
    [DataRow("select t.v from t inner hash join u on u.t_id = t.id", DisplayName = "A join hint, whose warning a compile sends")]
    [DataRow("select v from t with (nolock)", DisplayName = "A table hint")]
    [DataRow("select v from t option (maxdop 1)", DisplayName = "A query hint")]
    [DataRow("select dbo.f(v) from t", DisplayName = "A user function, which a compile inlines")]
    [DataRow("select * from #s", DisplayName = "A temporary table")]
    [DataRow("select 1", DisplayName = "A query reading no table")]
    [DataRow("select v into t2 from t", DisplayName = "SELECT … INTO")]
    public void AQueryACompileSettlesMore_ParsesAsItRuns(string text) =>
        AreEqual(0, WalkReplays(text, "create function f (@v int) returns int as begin return @v end\ngo\ncreate table #s (a int)"));

    [TestMethod]
    [DataRow("set transaction isolation level serializable; select v from t", DisplayName = "An isolation level the cache stays out of")]
    [DataRow("create table w (a int); select v from t", DisplayName = "A schema change ahead of it")]
    [DataRow("set ansi_nulls off; select v from t where v = null", DisplayName = "A setting the cache key took")]
    public void AQueryTheBatchChangedTheGroundUnder_ParsesAsItRuns(string text) => AreEqual(0, WalkReplays(text));

    /// <summary>
    /// The walk's parse serves the statement's first run, which files it in the
    /// plan cache as its own parse would have been, so the loop's later
    /// iterations replay the cached plan.
    /// </summary>
    [TestMethod]
    public void AQueryInALoop_RunsOnTheWalksParseOnce_ThenOnTheCachedPlan()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        var (replays, hits) = (simulation.WalkPlanReplays, simulation.SelectPlanHits);
        Execute(connection, "declare @i int = 0; while @i < 3 begin select v from t where id = 1; set @i += 1 end");
        AreEqual((1, 2), (simulation.WalkPlanReplays - replays, simulation.SelectPlanHits - hits));
    }

    /// <summary>
    /// The walk parses a query in a branch the run doesn't take, which the run
    /// never files in the plan cache, so a later run taking the branch parses it
    /// as it would have.
    /// </summary>
    [TestMethod]
    public void AQueryInABranchNotTaken_IsLeftToTheRunThatTakesIt()
    {
        const string Batch = "declare @x int = (select count(*) from u); if @x > 1 select v from t where id = 2";
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        var (replays, recordings) = (simulation.WalkPlanReplays, simulation.SelectPlanRecordings);
        Execute(connection, Batch);
        AreEqual(0, simulation.WalkPlanReplays - replays);
        AreEqual(0, simulation.SelectPlanRecordings - recordings);

        Execute(connection, "insert u values (11, 2)");
        Execute(connection, Batch);
        AreEqual(1, simulation.SelectPlanRecordings - recordings);
    }
}
