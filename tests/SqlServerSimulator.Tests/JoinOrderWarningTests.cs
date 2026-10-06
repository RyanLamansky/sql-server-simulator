using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// When Msg 8625 — the join order a local join hint enforces — goes out: as
/// real compiles each hinted statement, so ahead of everything a batch runs,
/// once per compile rather than per execution (probed 2026-10-06 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class JoinOrderWarningTests
{
    private const string Hinted = "select * from a inner hash join b on a.id = b.id";

    private static (Simulation Simulation, SimulatedDbConnection Connection, List<string> Stream) Session()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table a (id int); create table b (id int); insert a values (1); insert b values (1)");
        var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var stream = new List<string>();
        connection.InfoMessage += (_, e) => stream.AddRange(e.Errors.Select(error => $"{error.Number}@{error.LineNumber}"));
        return (simulation, connection, stream);
    }

    private static List<string> Run(SimulatedDbConnection connection, List<string> stream, string batch)
    {
        stream.Clear();
        using var reader = connection.CreateCommand(batch).ExecuteReader();
        do
        {
            stream.Add("rs");
            while (reader.Read())
            {
            }
        }
        while (reader.NextResult());
        return [.. stream];
    }

    [TestMethod]
    public void EveryHintedStatement_WarnsAheadOfTheBatch()
    {
        var (_, connection, stream) = Session();
        using (connection)
            CollectionAssert.AreEqual(new[] { "8625@1", "8625@3", "rs", "rs", "rs" }, Run(connection, stream, $"{Hinted};\nselect 2;\n{Hinted}"));
    }

    [TestMethod]
    public void ARepeatedBatch_RunsOnItsCachedPlan()
    {
        var (_, connection, stream) = Session();
        using (connection)
        {
            CollectionAssert.AreEqual(new[] { "8625@1", "rs" }, Run(connection, stream, Hinted));
            CollectionAssert.AreEqual(new[] { "rs" }, Run(connection, stream, Hinted));
        }
    }

    [TestMethod]
    public void ALoop_WarnsOnce()
    {
        var (_, connection, stream) = Session();
        using (connection)
            _ = ContainsSingle(entry => entry.StartsWith("8625", StringComparison.Ordinal), Run(connection, stream, $"declare @i int = 0; while @i < 3 begin {Hinted}; set @i += 1; end"));
    }

    [TestMethod]
    public void AStatementTheCompileDefers_WarnsAsItRuns()
    {
        var (_, connection, stream) = Session();
        using (connection)
            CollectionAssert.AreEqual(new[] { "rs", "8625@3", "rs" }, Run(connection, stream, "select 1;\ncreate table c (id int);\nselect * from c inner hash join b on c.id = b.id"));
    }

    [TestMethod]
    public void Recompile_WarnsEveryRun()
    {
        var (_, connection, stream) = Session();
        using (connection)
        {
            CollectionAssert.AreEqual(new[] { "rs", "8625@2", "rs" }, Run(connection, stream, $"select 1;\n{Hinted} option (recompile)"));
            CollectionAssert.AreEqual(new[] { "rs", "8625@2", "rs" }, Run(connection, stream, $"select 1;\n{Hinted} option (recompile)"));
        }
    }

    [TestMethod]
    public void AProcedure_WarnsAtItsFirstCompile()
    {
        var (simulation, connection, stream) = Session();
        using (connection)
        {
            _ = simulation.ExecuteNonQuery($"create procedure p as {Hinted}");
            _ = ContainsSingle(entry => entry.StartsWith("8625", StringComparison.Ordinal), Run(connection, stream, "exec p; exec p"));
            AreEqual(0, Run(connection, stream, "exec p").Count(entry => entry.StartsWith("8625", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public void AView_WarnsOnTheReferencingStatementsLine_NotAtCreate()
    {
        var (_, connection, stream) = Session();
        using (connection)
        {
            CollectionAssert.AreEqual(Array.Empty<string>(), Run(connection, stream, "create view v as select a.id from a inner hash join b on a.id = b.id").Where(entry => entry != "rs").ToArray());
            CollectionAssert.AreEqual(new[] { "8625@3", "rs", "rs" }, Run(connection, stream, "select 1;\n\nselect * from v"));
        }
    }

    [TestMethod]
    public void NoExec_StillWarns()
    {
        var (_, connection, stream) = Session();
        using (connection)
        {
            _ = connection.CreateCommand("set noexec on").ExecuteNonQuery();
            _ = ContainsSingle(entry => entry.StartsWith("8625", StringComparison.Ordinal), Run(connection, stream, Hinted));
            _ = connection.CreateCommand("set noexec off").ExecuteNonQuery();
        }
    }

    [TestMethod]
    public void DynamicSql_WarnsOncePerText()
    {
        var (_, connection, stream) = Session();
        using (connection)
            _ = ContainsSingle(entry => entry.StartsWith("8625", StringComparison.Ordinal), Run(connection, stream, $"exec('{Hinted}'); exec('{Hinted}')"));
    }
}
