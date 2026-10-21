using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An autocommit <c>ALTER TABLE … SWITCH</c> or <c>TRUNCATE … WITH
/// (PARTITIONS …)</c> gives back the off-row chains of the rows it took from
/// a table as it commits, as a <c>DELETE</c> does, which SQL can't see.
/// </summary>
[TestClass]
public sealed class PartitionRowMoveReclamationTests
{
    [TestMethod]
    [DataRow("alter table t switch partition 1 to t2 partition 1")]
    [DataRow("truncate table t with (partitions (1))")]
    public void AutocommitRowMove_FreesTheSourcesChainsForReuse(string change)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Run(connection, "create partition function pf (int) as range left for values (2); create partition scheme ps as partition pf all to ([primary])");
        Run(connection, "create table t (id int primary key, v varchar(max)) on ps (id); create table t2 (id int primary key, v varchar(max)) on ps (id)");
        Run(connection, "insert t select value, replicate(cast('x' as varchar(max)), 9000) from generate_series(1, 4)");
        var heap = simulation.Databases["simulated"].Schemas["dbo"].HeapTables["t"].Heap;
        var lobPages = heap.LobPages.Count;
        Run(connection, change);
        Run(connection, "insert t select value, replicate(cast('y' as varchar(max)), 9000) from generate_series(1, 2)");
        HasCount(lobPages, heap.LobPages);
    }

    private static void Run(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }
}
