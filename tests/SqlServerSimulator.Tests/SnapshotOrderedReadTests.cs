using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>READ_COMMITTED_SNAPSHOT</c> or <c>SNAPSHOT</c> read ordered by an
/// index answers from its snapshot — the committed rows, at the keys they held
/// — whether it walks the index in order or sorts, while other sessions move
/// keys, insert and delete.
/// </summary>
[TestClass]
public sealed class SnapshotOrderedReadTests
{
    private static Simulation WithNames()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database d;
            alter database d set read_committed_snapshot on;
            alter database d set allow_snapshot_isolation on;
            """);
        _ = simulation.ExecuteNonQuery("""
            use d;
            create table t (id int primary key, name nvarchar(20) not null);
            create index ix on t (name);
            insert t values (1, N'b'), (2, N'd'), (3, N'f'), (4, N'h');
            """);
        return simulation;
    }

    private static DbConnection Open(Simulation simulation)
    {
        var connection = simulation.CreateOpenConnection();
        connection.ChangeDatabase("d");
        return connection;
    }

    private static string Names(DbConnection connection, string sql)
    {
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add($"{reader.GetInt32(0).ToInvariantString()}:{reader.GetString(1)}");
        return string.Join(",", names);
    }

    [TestMethod]
    public void AnRcsiRead_SeesTheCommittedOrder_PastAnotherSessionsUncommittedWrites()
    {
        var simulation = WithNames();
        using var writer = Open(simulation);
        using var reader = Open(simulation);
        _ = writer.CreateCommand("begin tran; update t set name = N'a' where id = 3; delete t where id = 1; insert t values (5, N'c')").ExecuteNonQuery();
        AreEqual("1:b,2:d,3:f", Names(reader, "select top (3) id, name from t order by name"));
        AreEqual("4:h,3:f", Names(reader, "select top (2) id, name from t order by name desc"));
        _ = writer.CreateCommand("commit").ExecuteNonQuery();
        AreEqual("3:a,5:c,2:d", Names(reader, "select top (3) id, name from t order by name"));
    }

    [TestMethod]
    public void ASnapshotTransaction_KeepsItsOrder_AfterOthersCommitKeyChanges()
    {
        var simulation = WithNames();
        using var writer = Open(simulation);
        using var reader = Open(simulation);
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran").ExecuteNonQuery();
        AreEqual("1:b,2:d,3:f,4:h", Names(reader, "select id, name from t order by name offset 0 rows"));
        _ = writer.CreateCommand("update t set name = N'a' where id = 4; delete t where id = 2; insert t values (6, N'e')").ExecuteNonQuery();
        AreEqual("1:b,2:d,3:f,4:h", Names(reader, "select id, name from t order by name offset 0 rows"));
        AreEqual("2:d,3:f", Names(reader, "select id, name from t where name > N'c' and name < N'g' order by name"));
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
        AreEqual("4:a,1:b,6:e,3:f", Names(reader, "select id, name from t order by name"));
    }

    [TestMethod]
    public void AStreamingRcsiRead_ReadsOnAtItsSnapshot_WhileAnotherSessionCommits()
    {
        var simulation = WithNames();
        using var writer = Open(simulation);
        using var reader = Open(simulation);
        using var rows = reader.CreateCommand("select id, name from t order by name").ExecuteReader();
        IsTrue(rows.Read());
        AreEqual("b", rows.GetString(1));
        _ = writer.CreateCommand("update t set name = N'a' where id = 4; update t set name = N'z' where id = 2; delete t where id = 3; insert t values (7, N'e')").ExecuteNonQuery();
        var rest = new List<string>();
        while (rows.Read())
            rest.Add($"{rows.GetInt32(0).ToInvariantString()}:{rows.GetString(1)}");
        AreEqual("2:d,3:f,4:h", string.Join(",", rest));
    }
}
