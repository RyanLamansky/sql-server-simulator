using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>BACKUP</c> / <c>RESTORE</c> over the TDS endpoint as a test harness
/// drives them: application connections pooled in the database, the restore
/// issued from <c>master</c> on a connection of its own.
/// </summary>
[TestClass]
public sealed class BackupRestoreWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task HarnessSequence_RestoresUnderPooledApplicationConnections()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create database app");
        Wire.ExecInProc(simulation, "use app; create table t (id int primary key, v nvarchar(10)); insert t values (1, N'clean')");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var appConnectionString = Wire.PooledConnectionString(listener) + $";Initial Catalog=app;Application Name={Guid.NewGuid():N}";

        await using var master = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await Execute(master, "BACKUP DATABASE [app] TO  DISK = N'c:\\temp\\app-automated.bak' WITH NOFORMAT, INIT,  NAME = N'Automated Database Backup', SKIP, NOREWIND, NOUNLOAD,  STATS = 10");

        await using (var app = new SqlConnection(appConnectionString))
        {
            await app.OpenAsync(TestContext.CancellationToken);
            await Execute(app, "update t set v = N'dirty'");
        }

        await Execute(master, """
            DECLARE @kill nvarchar(max) = '';
            SELECT @kill = @kill + 'KILL ' + CONVERT(varchar(5), session_id) + ';'
            FROM sys.dm_exec_sessions
            WHERE database_id = DB_ID('app') AND session_id <> @@SPID AND is_user_process = 1;
            EXEC(@kill);
            """);
        await Execute(master, "ALTER DATABASE [app] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        await Execute(master, "ALTER DATABASE [app] SET RECOVERY SIMPLE");
        var messages = new List<int>();
        master.InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SqlError>().Select(error => error.Number));
        await Execute(master, "RESTORE DATABASE [app] FROM DISK = N'c:\\temp\\app-automated.bak' WITH FILE = 1, REPLACE, NOUNLOAD, STATS = 5");
        CollectionAssert.AreEqual(Enumerable.Repeat(3211, 20).Concat([4035, 4035, 3014]).ToArray(), messages.ToArray());
        await using (var state = new SqlCommand("SELECT state_desc FROM sys.databases WHERE name = @dbName", master))
        {
            _ = state.Parameters.AddWithValue("@dbName", "app");
            AreEqual("ONLINE", await state.ExecuteScalarAsync(TestContext.CancellationToken));
        }
        await Execute(master, "ALTER DATABASE [app] SET MULTI_USER");

        // The pool's connection was killed: it fails once, the next is new.
        object? value = null;
        for (var attempt = 0; attempt < 2 && value is null; attempt++)
        {
            try
            {
                await using var app = new SqlConnection(appConnectionString);
                await app.OpenAsync(TestContext.CancellationToken);
                await using var read = new SqlCommand("select v from t where id = 1", app);
                value = await read.ExecuteScalarAsync(TestContext.CancellationToken);
            }
            catch (SqlException)
            {
            }
        }
        AreEqual("clean", value);
    }

    [TestMethod]
    public async Task ApplicationReconnectingThroughoutTheSequence_DoesntBreakTheRestore()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create database app");
        Wire.ExecInProc(simulation, "use app; create table t (id int primary key, v nvarchar(10)); insert t values (1, N'clean')");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var appConnectionString = Wire.PooledConnectionString(listener).Replace("Max Pool Size=1", "Max Pool Size=5", StringComparison.Ordinal)
            + $";Initial Catalog=app;Application Name={Guid.NewGuid():N};Connect Retry Count=0";
        await using var master = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";Initial Catalog=master");
        await Execute(master, "BACKUP DATABASE [app] TO DISK = N'app.bak' WITH INIT");
        await using (var dirty = new SqlConnection(appConnectionString))
        {
            await dirty.OpenAsync(TestContext.CancellationToken);
            await Execute(dirty, "update t set v = N'dirty'");
        }

        // An application whose pool reopens and queries as fast as it can,
        // as an entity-cache monitor polling the database does.
        using var stop = new CancellationTokenSource();
        var poller = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var app = new SqlConnection(appConnectionString);
                    await app.OpenAsync(stop.Token);
                    await using var read = new SqlCommand("select v from t where id = 1", app);
                    _ = await read.ExecuteScalarAsync(stop.Token);
                }
                catch (SqlException)
                {
                    // Refused while the restore holds the database, as on real.
                }
                catch (OperationCanceledException)
                {
                }
            }
        }, TestContext.CancellationToken);

        await Task.Delay(100, TestContext.CancellationToken);
        await Execute(master, """
            DECLARE @kill nvarchar(max) = '';
            SELECT @kill = @kill + 'KILL ' + CONVERT(varchar(5), session_id) + ';'
            FROM sys.dm_exec_sessions
            WHERE database_id = DB_ID('app') AND session_id <> @@SPID AND is_user_process = 1;
            EXEC(@kill);
            """);
        await Execute(master, "ALTER DATABASE [app] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        await Execute(master, "ALTER DATABASE [app] SET RECOVERY SIMPLE");
        await Task.Delay(100, TestContext.CancellationToken);
        await Execute(master, "RESTORE DATABASE [app] FROM DISK = N'app.bak' WITH FILE = 1, REPLACE, NOUNLOAD, STATS = 5");
        await Execute(master, "ALTER DATABASE [app] SET MULTI_USER");
        await stop.CancelAsync();
        await poller;

        // The poller's last refused login can leave its pool in SqlClient's
        // blocking period, which rethrows that error for a few seconds
        // whatever the server would answer now, so the check reads through a
        // connection of its own.
        await using var after = new SqlConnection(appConnectionString + ";Pooling=false");
        await after.OpenAsync(TestContext.CancellationToken);
        await using var check = new SqlCommand("select v from t where id = 1", after);
        AreEqual("clean", await check.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task LoginNamingADatabaseAnotherSessionHoldsSingleUser_IsMsg4060()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create database app");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var master = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";Initial Catalog=master");
        await Execute(master, "ALTER DATABASE [app] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        var error = await ThrowsExactlyAsync<SqlException>(async () => await Wire.OpenAsync(listener, TestContext.CancellationToken, ";Initial Catalog=app;ConnectRetryInterval=1"));
        AreEqual(4060, error.Number);
    }

    /// <summary>
    /// While <c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c> settles the
    /// sessions it ended, a login naming the database fails at once with a
    /// lone Msg 18456, which SqlClient neither retries in the open nor masks.
    /// An application pool meeting it there enters its blocking period rather
    /// than reaching the restore behind it, whose refusal would hold the
    /// pool's next connection in SqlClient's ten-second login retry.
    /// </summary>
    [TestMethod]
    public async Task LoginWhileRollbackImmediateSettles_IsALoneMsg18456()
    {
        var simulation = new Simulation { RollbackSettleMillis = 1500 };
        Wire.ExecInProc(simulation, "create database app");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var victim = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";Initial Catalog=app");
        await using var master = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";Initial Catalog=master");
        var alter = Execute(master, "ALTER DATABASE [app] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        await Task.Delay(300, TestContext.CancellationToken);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await ThrowsExactlyAsync<SqlException>(async () => await Wire.OpenAsync(listener, TestContext.CancellationToken, ";Initial Catalog=app;ConnectRetryCount=0"));
        IsLessThan(1000, started.ElapsedMilliseconds);
        CollectionAssert.AreEqual(new[] { 18456 }, error.Errors.Cast<SqlError>().Select(entry => entry.Number).ToArray());
        AreEqual("Login failed for user 'sa'.", error.Message);
        await alter;
        await Execute(master, "ALTER DATABASE [app] SET MULTI_USER");
        await using var after = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";Initial Catalog=app");
    }

    [TestMethod]
    public async Task Refusal_ArrivesAsItsErrorThenMsg3013()
    {
        var simulation = new Simulation { RestoreExclusiveAccessWaitMillis = 200 };
        Wire.ExecInProc(simulation, "create database app; backup database app to disk = N'app.bak'");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var holder = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";Initial Catalog=app");
        await using var master = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var restore = new SqlCommand("restore database app from disk = N'app.bak' with replace", master);
        var error = await ThrowsExactlyAsync<SqlException>(async () => await restore.ExecuteNonQueryAsync(TestContext.CancellationToken));
        CollectionAssert.AreEqual(new[] { 3101, 3013 }, error.Errors.Cast<SqlError>().Select(entry => entry.Number).ToArray());
    }

    private async Task Execute(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }
}
