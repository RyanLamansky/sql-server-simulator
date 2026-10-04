using System.Data.Common;
using System.Diagnostics;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A plan-cache entry is one plan replayed by every session that sends the same
/// text, so each replay must run as the session executing it: its locks, its
/// lock timeout, its snapshot, its parameters and its session options — never
/// those of the session that happened to compile the plan first.
/// </summary>
[TestClass]
public sealed class PlanCacheSessionTests
{
    private static Simulation WithTable()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int primary key, v int); insert t values (1, 1), (2, 2)");
        return simulation;
    }

    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        return command.ExecuteScalar();
    }

    private static void Exec(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        _ = command.ExecuteNonQuery();
    }

    private static int SqlErrorNumber(DbConnection connection, string sql)
        => Throws<SimulatedSqlException>(() => Scalar(connection, sql)).Number;

    [TestMethod]
    public void DisposedCompiler_ReplayMeetingAnotherSessionsLock_TimesOut()
    {
        var simulation = WithTable();
        using (var compiler = simulation.CreateOpenConnection())
            AreEqual(2, Scalar(compiler, "select count(*) from t"));

        using var writer = simulation.CreateOpenConnection();
        Exec(writer, "begin tran; update t set v = 10 where id = 1");
        using var reader = simulation.CreateOpenConnection();
        Exec(reader, "set lock_timeout 50");
        AreEqual(1222, SqlErrorNumber(reader, "select count(*) from t"));
    }

    [TestMethod]
    public void Replay_DoesNotReadPastTheCompilersOwnRowLocks()
    {
        var simulation = WithTable();
        using var compiler = simulation.CreateOpenConnection();
        Exec(compiler, "begin tran; update t set v = 10 where id = 1");
        AreEqual(12, Scalar(compiler, "select sum(v) from t"));

        using var reader = simulation.CreateOpenConnection();
        Exec(reader, "set lock_timeout 50");
        AreEqual(1222, SqlErrorNumber(reader, "select sum(v) from t"));
    }

    [TestMethod]
    public void Replay_DoesNotReadPastATableLockHeldByAnotherSession()
    {
        var simulation = WithTable();
        AreEqual(2, simulation.ExecuteScalar("select count(*) from t"));

        using var writer = simulation.CreateOpenConnection();
        Exec(writer, "begin tran; insert t with (tablockx) values (3, 3)");
        using var reader = simulation.CreateOpenConnection();
        Exec(reader, "set lock_timeout 50");
        AreEqual(1222, SqlErrorNumber(reader, "select count(*) from t"));
    }

    [TestMethod]
    public void Replay_UsesTheReplayingSessionsLockTimeout()
    {
        var simulation = WithTable();
        using var compiler = simulation.CreateOpenConnection();
        Exec(compiler, "set lock_timeout 30000");
        AreEqual(2, Scalar(compiler, "select count(*) from t"));

        using var writer = simulation.CreateOpenConnection();
        Exec(writer, "begin tran; update t set v = 10 where id = 1");
        using var reader = simulation.CreateOpenConnection();
        Exec(reader, "set lock_timeout 0");
        var clock = Stopwatch.StartNew();
        AreEqual(1222, SqlErrorNumber(reader, "select count(*) from t"));
        IsLessThan(15_000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    public void Replay_HonorsNowait()
    {
        var simulation = WithTable();
        using var compiler = simulation.CreateOpenConnection();
        AreEqual(2, Scalar(compiler, "select count(*) from t with (nowait)"));

        using var writer = simulation.CreateOpenConnection();
        Exec(writer, "begin tran; update t set v = 10 where id = 1");
        using var reader = simulation.CreateOpenConnection();
        Exec(reader, "set lock_timeout 30000");
        var clock = Stopwatch.StartNew();
        AreEqual(1222, SqlErrorNumber(reader, "select count(*) from t with (nowait)"));
        IsLessThan(15_000, clock.ElapsedMilliseconds);
    }

    [TestMethod]
    public void Replay_ReleasesItsStatementScopedUpdateLock()
    {
        var simulation = WithTable();
        using var compiler = simulation.CreateOpenConnection();
        AreEqual(1, Scalar(compiler, "select v from t with (updlock) where id = 1"));
        using var replayer = simulation.CreateOpenConnection();
        AreEqual(1, Scalar(replayer, "select v from t with (updlock) where id = 1"));

        using var writer = simulation.CreateOpenConnection();
        Exec(writer, "set lock_timeout 50");
        Exec(writer, "update t set v = 3 where id = 1");
        AreEqual(3, Scalar(writer, "select v from t where id = 1"));
    }

    [TestMethod]
    public void Replay_HoldsItsUpdateLockForItsOwnTransaction()
    {
        var simulation = WithTable();
        AreEqual(1, simulation.ExecuteScalar("select v from t with (updlock) where id = 1"));

        using var holder = simulation.CreateOpenConnection();
        Exec(holder, "begin tran");
        AreEqual(1, Scalar(holder, "select v from t with (updlock) where id = 1"));
        AreEqual(1, Scalar(holder, """
            select count(*) from sys.dm_tran_locks
            where request_session_id = @@spid and request_mode = 'U' and resource_type = 'KEY'
            """));
        using var writer = simulation.CreateOpenConnection();
        Exec(writer, "set lock_timeout 50");
        AreEqual(1222, SqlErrorNumber(writer, "update t set v = 3 where id = 1"));
        Exec(holder, "commit");
        Exec(writer, "update t set v = 3 where id = 1");
    }

    [TestMethod]
    public void Replay_UnderReadCommittedSnapshot_ReadsTheCurrentStatementsSnapshot()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("alter database simulated set read_committed_snapshot on");
        AreEqual(3, simulation.ExecuteScalar("select sum(v) from t"));
        _ = simulation.ExecuteNonQuery("insert t values (3, 3)");
        AreEqual(6, simulation.ExecuteScalar("select sum(v) from t"));
    }

    [TestMethod]
    public void Replay_UnderReadCommittedSnapshot_SkipsAnotherSessionsUncommittedRow()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("alter database simulated set read_committed_snapshot on");
        AreEqual(3, simulation.ExecuteScalar("select sum(v) from t"));
        using var writer = simulation.CreateOpenConnection();
        Exec(writer, "begin tran; insert t values (3, 3)");
        AreEqual(3, simulation.ExecuteScalar("select sum(v) from t"));
        Exec(writer, "commit");
        AreEqual(6, simulation.ExecuteScalar("select sum(v) from t"));
    }

    [TestMethod]
    public void Replay_ForSystemTimeAsOfParameter_ReadsEachExecutionsValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table h (id int primary key, v int,
                vf datetime2 generated always as row start, vt datetime2 generated always as row end,
                period for system_time (vf, vt))
            with (system_versioning = on);
            insert h (id, v) values (1, 1)
            """);
        using var connection = simulation.CreateOpenConnection();
        using (var past = connection.CreateCommand("select count(*) from h for system_time as of @at", ("@at", new DateTime(2000, 1, 1))))
            AreEqual(0, past.ExecuteScalar());
        using var present = connection.CreateCommand("select count(*) from h for system_time as of @at", ("@at", new DateTime(9999, 1, 1)));
        AreEqual(1, present.ExecuteScalar());
    }

    [TestMethod]
    public void Replay_MasksForTheReplayingPrincipal()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table m (id int primary key, c varchar(10) masked with (function = 'default()'));
            insert m values (1, 'secret');
            create user u without login;
            grant select on m to u
            """);
        AreEqual("secret", simulation.ExecuteScalar("select c from m"));
        using var restricted = simulation.CreateOpenConnection();
        Exec(restricted, "execute as user = 'u'");
        AreEqual("xxxx", Scalar(restricted, "select c from m"));
        AreEqual("secret", simulation.ExecuteScalar("select c from m"));
    }

    [TestMethod]
    public void Replay_InAnotherDatabase_ReadsThatDatabasesTable()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("create database d2");
        _ = simulation.ExecuteNonQuery("use d2; create table t (id int primary key, v int); insert t values (1, 100)");
        AreEqual(3, simulation.ExecuteScalar("select sum(v) from t"));
        using var other = simulation.CreateOpenConnection();
        Exec(other, "use d2");
        AreEqual(100, Scalar(other, "select sum(v) from t"));
    }

    /// <summary>
    /// The result of <paramref name="query"/> on a session configured by
    /// <paramref name="setup"/>, when another session with <paramref name="compilerSetup"/>
    /// compiled the same text first — which must equal the result with no
    /// plan to replay.
    /// </summary>
    private static void AssertReplayMatchesFreshParse(string compilerSetup, string setup, string query)
    {
        const string Seed = """
            create table s (id int primary key, d datetime, v int, n int null, c varchar(10), m decimal(10, 4));
            insert s values (1, '2024-01-07', 7, null, 'abc', 1.2345)
            """;
        static string Read(DbConnection connection, string query)
        {
            try
            {
                using var reader = connection.CreateCommand(query).ExecuteReader();
                var values = new List<string>();
                while (reader.Read())
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                        values.Add(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "");
                }

                return string.Join("|", values);
            }
            catch (SimulatedSqlException error)
            {
                return $"Msg {error.Number}";
            }
        }

        var fresh = new Simulation();
        _ = fresh.ExecuteNonQuery(Seed);
        using var freshConnection = fresh.CreateOpenConnection();
        if (setup.Length != 0)
            Exec(freshConnection, setup);
        var expected = Read(freshConnection, query);

        var shared = new Simulation();
        _ = shared.ExecuteNonQuery(Seed);
        using (var compiler = shared.CreateOpenConnection())
        {
            if (compilerSetup.Length != 0)
                Exec(compiler, compilerSetup);
            _ = Read(compiler, query);
        }
        using var replayer = shared.CreateOpenConnection();
        if (setup.Length != 0)
            Exec(replayer, setup);
        AreEqual(expected, Read(replayer, query), $"{compilerSetup} → {setup}: {query}");
    }

    [TestMethod]
    [DataRow("set datefirst 1", "select datepart(weekday, d) from s")]
    [DataRow("set datefirst 1", "select @@datefirst from s")]
    [DataRow("set language French", "select datename(month, d) + datename(weekday, d) from s")]
    [DataRow("set language French", "select count(*) from s where d = '07/01/2024'")]
    [DataRow("set language German", "select count(*) from s where d > 'Januar 1, 2024'")]
    [DataRow("set language us_english", "select format(d, 'D') from s")]
    [DataRow("set dateformat dmy", "select count(*) from s where d = '07/01/2024'")]
    [DataRow("set dateformat dmy", "select cast(c as varchar) + cast(cast('01/02/2024' as datetime) as varchar(30)) from s")]
    [DataRow("set ansi_nulls off", "select count(*) from s where n = null")]
    [DataRow("set concat_null_yields_null off", "select c + null from s")]
    [DataRow("set arithabort off; set ansi_warnings off", "select v / 0 from s")]
    [DataRow("set arithabort off; set ansi_warnings off", "select count(*) from s where v / 0 is null")]
    [DataRow("set ansi_warnings off", "select cast(c as varchar(2)) from s")]
    [DataRow("set numeric_roundabort on", "select cast(m as decimal(5, 2)) from s")]
    [DataRow("set textsize 1", "select cast(c as varchar(max)) from s")]
    [DataRow("set fmtonly on", "select v from s")]
    [DataRow("set rowcount 0", "select @@rowcount from s")]
    [DataRow("begin tran", "select @@trancount + xact_state() from s")]
    [DataRow("set context_info 0x01", "select context_info() from s")]
    [DataRow("exec sp_set_session_context 'k', 'v'", "select session_context(N'k') from s")]
    [DataRow("create table #x (i int)", "select object_id('tempdb..#x') from s")]
    [DataRow("create table #x (i int); insert #x values (1)", "select (select count(*) from tempdb.sys.objects where object_id = object_id('tempdb..#x')) from s")]
    [DataRow("create user u without login; grant select on s to u; execute as user = 'u'", "select user_name() + '|' + cast(is_member('db_owner') as varchar) from s")]
    [DataRow("create user u without login; grant select on s to u; execute as user = 'u'", "select has_perms_by_name('s', 'OBJECT', 'UPDATE') from s")]
    [DataRow("create user u without login; execute as user = 'u'", "select v from s")]
    public void Replay_UnderADifferentSessionSetting_MatchesAFreshParse(string setup, string query)
    {
        AssertReplayMatchesFreshParse("", setup, query);
        AssertReplayMatchesFreshParse(setup, "", query);
    }
}
