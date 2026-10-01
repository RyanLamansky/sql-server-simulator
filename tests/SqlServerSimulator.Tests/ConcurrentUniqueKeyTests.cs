using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Sessions racing to write the same not-yet-present key into a PRIMARY KEY,
/// a UNIQUE constraint or a unique index: exactly one write of each key lands
/// and every other raises Msg 2627 / 2601, whichever path wrote it — as real's
/// second writer waits on the key the first one is entering and then finds it.
/// </summary>
[TestClass]
public sealed class ConcurrentUniqueKeyTests
{
    private const int Workers = 8;
    private const int Keys = 120;

    // Every key goes to a row (a, b); a single-column key leaves b at 0.
    private static readonly string[] TableShapes =
    [
        "create table t (a int not null, b int not null, w int not null, i int not null, constraint uq unique (a))",
        "create table t (a int not null, b int not null, w int not null, i int not null, constraint pk primary key clustered (a))",
        "create table t (a int not null, b int not null, w int not null, i int not null); create unique index ux on t (a)",
        "create table t (a int not null, b int not null, w int not null, i int not null, constraint pk primary key (a, b))",
    ];

    public enum Shape
    {
        Insert,
        MultiRowInsert,
        InsertSelect,
        Merge,
        Update,
        Bulk,
    }

    [TestMethod]
    [DataRow(0, Shape.Insert, DisplayName = "Heap UNIQUE, INSERT")]
    [DataRow(1, Shape.Insert, DisplayName = "Clustered PK, INSERT")]
    [DataRow(2, Shape.Insert, DisplayName = "Unique index, INSERT")]
    [DataRow(3, Shape.Insert, DisplayName = "Composite PK, INSERT")]
    [DataRow(0, Shape.MultiRowInsert, DisplayName = "Heap UNIQUE, multi-row INSERT")]
    [DataRow(1, Shape.MultiRowInsert, DisplayName = "Clustered PK, multi-row INSERT")]
    [DataRow(2, Shape.MultiRowInsert, DisplayName = "Unique index, multi-row INSERT")]
    [DataRow(3, Shape.MultiRowInsert, DisplayName = "Composite PK, multi-row INSERT")]
    [DataRow(0, Shape.InsertSelect, DisplayName = "Heap UNIQUE, INSERT … SELECT")]
    [DataRow(1, Shape.InsertSelect, DisplayName = "Clustered PK, INSERT … SELECT")]
    [DataRow(2, Shape.InsertSelect, DisplayName = "Unique index, INSERT … SELECT")]
    [DataRow(3, Shape.InsertSelect, DisplayName = "Composite PK, INSERT … SELECT")]
    [DataRow(0, Shape.Merge, DisplayName = "Heap UNIQUE, MERGE")]
    [DataRow(1, Shape.Merge, DisplayName = "Clustered PK, MERGE")]
    [DataRow(2, Shape.Merge, DisplayName = "Unique index, MERGE")]
    [DataRow(3, Shape.Merge, DisplayName = "Composite PK, MERGE")]
    [DataRow(0, Shape.Update, DisplayName = "Heap UNIQUE, key-changing UPDATE")]
    [DataRow(1, Shape.Update, DisplayName = "Clustered PK, key-changing UPDATE")]
    [DataRow(2, Shape.Update, DisplayName = "Unique index, key-changing UPDATE")]
    [DataRow(3, Shape.Update, DisplayName = "Composite PK, key-changing UPDATE")]
    [DataRow(0, Shape.Bulk, DisplayName = "Heap UNIQUE, BULK INSERT")]
    [DataRow(1, Shape.Bulk, DisplayName = "Clustered PK, BULK INSERT")]
    [DataRow(2, Shape.Bulk, DisplayName = "Unique index, BULK INSERT")]
    [DataRow(3, Shape.Bulk, DisplayName = "Composite PK, BULK INSERT")]
    public void RacingWritersOfOneKey_LandItOnce(int table, Shape shape)
    {
        var composite = table == 3;
        var simulation = new Simulation { OpenBulkFile = path => new MemoryStream(BulkFile(path)) };
        _ = simulation.ExecuteNonQuery(TableShapes[table]);
        if (shape == Shape.Update)
        {
            // Each worker parks one row per key and races the others to move
            // its row into that key.
            var parked = new StringBuilder();
            for (var w = 0; w < Workers; w++)
            {
                for (var k = 0; k < Keys; k++)
                    _ = parked.Append(parked.Length == 0 ? "insert t values " : ", ").Append($"({1_000_000 + (w * Keys) + k}, 0, {w}, {k})");
            }
            _ = simulation.ExecuteNonQuery(parked.ToString());
        }

        var step = shape == Shape.MultiRowInsert ? 2 : 1;
        Race(simulation, Keys / step, (connection, worker, round) =>
        {
            var k = round * step;
            var (a, b) = KeyOf(k, composite);
            var (a2, b2) = KeyOf(k + 1, composite);
            ExpectDuplicateOrSuccess(connection, shape switch
            {
                Shape.Insert => "insert t values (@a, @b, @w, 0)",
                Shape.MultiRowInsert => "insert t values (@a, @b, @w, 0), (@a2, @b2, @w, 0)",
                Shape.InsertSelect => "insert t select a, b, @w, 0 from (values (@a, @b)) v (a, b)",
                Shape.Merge => "merge t using (values (@a, @b)) s (a, b) on t.a = s.a and t.b = s.b when not matched then insert values (s.a, s.b, @w, 0);",
                Shape.Update => "update t set a = @a, b = @b where w = @w and i = @k",
                _ => $"bulk insert t from '{a}.{b}.{worker}.txt'",
            }, ("@a", a), ("@b", b), ("@a2", a2), ("@b2", b2), ("@w", worker), ("@k", k));
        });

        AreEqual(Keys, simulation.ExecuteScalar("select count(*) from t where a < 1000000"));
        AreEqual(Keys, simulation.ExecuteScalar("select count(*) from (select distinct a, b from t where a < 1000000) d"));
    }

    [TestMethod]
    [DataRow("constraint uq unique (k)", DisplayName = "NULL key, UNIQUE constraint")]
    [DataRow("index ux unique (k)", DisplayName = "NULL key, unique index")]
    public void RacingInsertsOfNull_LandOne(string key)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t (k int null, w int not null, {key})");
        Race(simulation, Keys, (connection, worker, round) =>
        {
            ExpectDuplicateOrSuccess(connection, "insert t values (null, @w)", ("@w", worker));
            IsLessThanOrEqualTo(1, (int)simulation.ExecuteScalar("select count(*) from t")!);
            using var delete = connection.CreateCommand("delete t where w = @w", ("@w", worker));
            _ = delete.ExecuteNonQuery();
        });
    }

    [TestMethod]
    [DataRow("constraint pk primary key (k) with (ignore_dup_key = on)", DisplayName = "IGNORE_DUP_KEY, PRIMARY KEY")]
    [DataRow("index ux unique (k) with (ignore_dup_key = on)", DisplayName = "IGNORE_DUP_KEY, unique index")]
    public void RacingInsertsUnderIgnoreDupKey_LandEachKeyOnce(string key)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t (k int not null, w int not null, {key})");
        Race(simulation, Keys, (connection, worker, k) =>
        {
            using var command = connection.CreateCommand("insert t values (@k, @w)", ("@k", k), ("@w", worker));
            _ = command.ExecuteNonQuery();
        });
        AreEqual(Keys, simulation.ExecuteScalar("select count(*) from t"));
        AreEqual(Keys, simulation.ExecuteScalar("select count(distinct k) from t"));
    }

    private static (int A, int B) KeyOf(int k, bool composite) => composite ? (k / 4, k % 4) : (k, 0);

    private static byte[] BulkFile(string path)
    {
        var parts = path[..^4].Split('.');
        return Encoding.UTF8.GetBytes($"{parts[0]}\t{parts[1]}\t{parts[2]}\t0\n");
    }

    private static void ExpectDuplicateOrSuccess(DbConnection connection, string text, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        using var command = connection.CreateCommand(text, parameters);
        try
        {
            _ = command.ExecuteNonQuery();
        }
        catch (SimulatedSqlException exception) when (exception.Number is 2601 or 2627)
        {
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> for every round on every worker's own
    /// connection, the workers starting each round together so their writes
    /// of one key overlap.
    /// </summary>
    private static void Race(Simulation simulation, int rounds, Action<DbConnection, int, int> body)
    {
        var failures = new ConcurrentQueue<Exception>();
        using var round = new Barrier(Workers);
        var threads = new Thread[Workers];
        for (var w = 0; w < Workers; w++)
        {
            var worker = w;
            threads[w] = new Thread(() =>
            {
                try
                {
                    using var connection = simulation.CreateOpenConnection();
                    for (var r = 0; r < rounds; r++)
                    {
                        round.SignalAndWait();
                        body(connection, worker, r);
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                    round.RemoveParticipant();
                }
            });
            threads[w].Start();
        }
        foreach (var thread in threads)
            thread.Join();
        if (!failures.IsEmpty)
            throw new AggregateException(failures);
    }
}
