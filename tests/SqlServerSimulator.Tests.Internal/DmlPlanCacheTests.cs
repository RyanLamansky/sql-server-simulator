using System.Data.Common;
using System.Runtime.CompilerServices;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which top-level DML statements run from a cached plan, observed through
/// <see cref="Simulation.DmlPlanHits"/> and <see cref="Simulation.DmlPlanRecordings"/>:
/// the EF Core modification shapes replay, statement by statement, while the
/// shapes whose parse holds something per execution or per session re-parse.
/// Whether a replay reports what a fresh parse reports is
/// <c>DmlPlanReplayTests</c>' (public API) to show.
/// </summary>
[TestClass]
public sealed class DmlPlanCacheTests
{
    private const string Setup = """
        create table p (id int primary key);
        create table t (id int identity primary key, name nvarchar(20) not null, v int not null, pid int null references p(id));
        insert p values (1);
        insert t (name, v) values (N'a', 1), (N'b', 2);
        """;

    private static (Simulation Simulation, DbConnection Connection) Open()
    {
        var simulation = new Simulation();
        var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, Setup);
        return (simulation, connection);
    }

    private static void Execute(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            _ = command.Parameters.Add(parameter);
        }
        using var reader = command.ExecuteReader();
        while (reader.NextResult())
        {
        }
    }

    private static object? Scalar(DbConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// Runs <paramref name="before"/> (batches split on <c>go</c> lines), then
    /// <paramref name="text"/> three times, and returns the replays it made.
    /// </summary>
    private static long ReplaysOverThreeRuns(string text, string? before = null, params (string Name, object Value)[] parameters)
    {
        var (simulation, connection) = Open();
        using (connection)
        {
            foreach (var batch in before?.Split("\ngo\n") ?? [])
                Execute(connection, batch);
            var hits = simulation.DmlPlanHits;
            for (var run = 0; run < 3; run++)
                Execute(connection, text, parameters);
            return simulation.DmlPlanHits - hits;
        }
    }

    [TestMethod]
    public void EfInsert_ReplaysAfterItsFirstRun()
        => AreEqual(2, ReplaysOverThreeRuns(
            "SET IMPLICIT_TRANSACTIONS OFF;\nSET NOCOUNT ON;\nINSERT INTO [t] ([name], [v])\nOUTPUT INSERTED.[id]\nVALUES (@p0, @p1);",
            parameters: [("@p0", "x"), ("@p1", 1)]));

    [TestMethod]
    public void EfUpdateBatch_ReplaysEachStatement()
        => AreEqual(4, ReplaysOverThreeRuns(
            "SET NOCOUNT ON;\nUPDATE [t] SET [v] = @p0\nOUTPUT 1\nWHERE [id] = @p1;\nUPDATE [t] SET [v] = @p2\nOUTPUT 1\nWHERE [id] = @p3;",
            parameters: [("@p0", 5), ("@p1", 1), ("@p2", 6), ("@p3", 2)]));

    [TestMethod]
    public void EfDelete_Replays()
        => AreEqual(2, ReplaysOverThreeRuns("SET NOCOUNT ON;\nDELETE FROM [t]\nOUTPUT 1\nWHERE [id] = @p0;", parameters: [("@p0", 99)]));

    [TestMethod]
    public void EfTriggerShape_ReplaysTheUpdateAndNotTheSelect()
        => AreEqual(2, ReplaysOverThreeRuns(
            "SET NOCOUNT ON;\nUPDATE [t] SET [v] = @p0\nWHERE [id] = @p1;\nSELECT @@ROWCOUNT;",
            "create trigger t_after on t after update as return",
            ("@p0", 5), ("@p1", 1)));

    [TestMethod]
    public void EfInsertIntoTableVariable_Reparses()
        => AreEqual(0, ReplaysOverThreeRuns("""
            SET NOCOUNT ON;
            DECLARE @inserted0 TABLE ([id] int);
            INSERT INTO [t] ([name], [v])
            OUTPUT INSERTED.[id]
            INTO @inserted0
            VALUES (@p0, @p1);
            SELECT [i].[id] FROM @inserted0 i;
            """, parameters: [("@p0", "x"), ("@p1", 1)]));

    [TestMethod]
    public void ClientOutputOnATriggeredTable_Reparses()
        => AreEqual(0, ReplaysOverThreeRuns("update t set v = 3 output inserted.v where id = @id", "create trigger t_after on t after update as return\ngo\ndisable trigger t_after on t", ("@id", 1)));

    [TestMethod]
    public void Subquery_Reparses()
        => AreEqual(0, ReplaysOverThreeRuns("update t set v = (select max(v) from t) where id = @id", parameters: ("@id", 1)));

    [TestMethod]
    public void InsertSelect_Reparses()
        => AreEqual(0, ReplaysOverThreeRuns("insert into t (name, v) select name, v from t where id = @id", parameters: ("@id", 1)));

    [TestMethod]
    public void TempTableTarget_Reparses()
        => AreEqual(0, ReplaysOverThreeRuns("update #w set v = @v", "create table #w (v int); insert #w values (1)", ("@v", 2)));

    [TestMethod]
    public void InsideABlock_Reparses()
        => AreEqual(0, ReplaysOverThreeRuns("if @v > 0 begin update t set v = @v where id = 1 end", parameters: ("@v", 2)));

    [TestMethod]
    public void UnderAnotherIsolationLevel_Reparses()
        => AreEqual(0, ReplaysOverThreeRuns("update t set v = @v where id = 1", "set transaction isolation level serializable", ("@v", 2)));

    [TestMethod]
    public void AfterASetOptionTheKeyNames_TheStatementReparses()
        => AreEqual(0, ReplaysOverThreeRuns("set ansi_nulls off; update t set v = @v where id = 1", parameters: ("@v", 2)));

    [TestMethod]
    public void UnderAnImpersonatedPrincipal_Reparses()
        => AreEqual(0, ReplaysOverThreeRuns(
            "update t set v = @v where id = 1",
            "create user u without login; grant update, select on t to u; execute as user = 'u'",
            ("@v", 2)));

    [TestMethod]
    public void SchemaChange_ReparsesOnceThenReplaysAgain()
    {
        var (simulation, connection) = Open();
        using (connection)
        {
            const string text = "update t set v = @v where id = 1";
            Execute(connection, text, ("@v", 1));
            Execute(connection, text, ("@v", 2));
            AreEqual(1, simulation.DmlPlanHits);
            Execute(connection, "alter table t add w int null");
            Execute(connection, text, ("@v", 3));
            AreEqual(1, simulation.DmlPlanHits);
            Execute(connection, text, ("@v", 4));
            AreEqual(2, simulation.DmlPlanHits);
        }
    }

    [TestMethod]
    public void FreeProcCache_DropsThePlans()
    {
        var (simulation, connection) = Open();
        using (connection)
        {
            const string text = "delete t where id = @id";
            Execute(connection, text, ("@id", 99));
            Execute(connection, "dbcc freeproccache with no_infomsgs");
            Execute(connection, text, ("@id", 99));
            AreEqual(0, simulation.DmlPlanHits);
            Execute(connection, text, ("@id", 99));
            AreEqual(1, simulation.DmlPlanHits);
        }
    }

    [TestMethod]
    public void DifferentParameterTypes_KeepSeparatePlans()
    {
        var (simulation, connection) = Open();
        using (connection)
        {
            const string text = "update t set name = @n where id = 1";
            Execute(connection, text, ("@n", "x"));
            Execute(connection, text, ("@n", 5));
            AreEqual(0, simulation.DmlPlanHits);
            AreEqual("5", Scalar(connection, "select name from t where id = 1"));
        }
    }

    // Not inlined, so no caller frame keeps the connection reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SessionToken LeakRecordingConnection(Simulation simulation)
    {
#pragma warning disable CA2000 // Abandoning the connection undisposed is the scenario under test.
        var connection = simulation.CreateDbConnection();
#pragma warning restore CA2000
        connection.Open();
        Execute(connection, "begin transaction; update t set v = 99 where id = @id", ("@id", 1));
        return connection.Session;
    }

    [TestMethod]
    public void CachedPlan_DoesNotKeepItsRecordingConnectionAlive()
    {
        var (simulation, observer) = Open();
        using (observer)
        {
            var recordings = simulation.DmlPlanRecordings;
            var session = LeakRecordingConnection(simulation);
            AreEqual(recordings + 1, simulation.DmlPlanRecordings);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            AreEqual(1, simulation.ReclaimAbandonedSessions());
            IsTrue(session.Reclaimed);
            Execute(observer, "update t set v = 99 where id = @id", ("@id", 1));
            AreEqual(99, Scalar(observer, "select v from t where id = 1"));
        }
    }
}
