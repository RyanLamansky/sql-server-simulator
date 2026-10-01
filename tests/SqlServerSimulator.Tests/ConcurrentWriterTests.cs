using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Sessions writing one table at once, each under the table's IX lock with
/// row X on what it writes, as real admits them: every write lands exactly
/// once at the address its statement reports, readers that run beside the
/// writers without row locks — NOLOCK and SNAPSHOT — never see a torn page,
/// and a SNAPSHOT transaction reads the same rows every time.
/// </summary>
[TestClass]
public sealed class ConcurrentWriterTests
{
    private const int Workers = 8;

    [TestMethod]
    [DataRow(false, false, DisplayName = "Heap")]
    [DataRow(true, false, DisplayName = "Clustered")]
    [DataRow(false, true, DisplayName = "Heap, versioned")]
    [DataRow(true, true, DisplayName = "Clustered, versioned")]
    public void MixedWriters_OnOneTable_KeepEveryRow(bool clustered, bool versioned)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table t (id int identity {(clustered ? "primary key" : "")}, w int not null, v int not null, pad varchar(400) not null, big varchar(max) null);
            create index ix_w on t (w);
            """);
        if (versioned)
            _ = simulation.ExecuteNonQuery("alter database simulated set allow_snapshot_isolation on");

        var expected = new Dictionary<int, int>[Workers];
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var writersDone = new ManualResetEventSlim();
        var readers = new[]
        {
            new Thread(() => Capture(failures, () => ReadNoLock(simulation, writersDone))),
            new Thread(() => Capture(failures, () => ReadSnapshot(simulation, writersDone, versioned))),
        };
        var writers = new Thread[Workers];
        for (var w = 0; w < Workers; w++)
        {
            var worker = w;
            expected[worker] = [];
            writers[worker] = new Thread(() => Capture(failures, () => Write(simulation, worker, expected[worker])));
        }

        foreach (var thread in readers)
            thread.Start();
        foreach (var thread in writers)
            thread.Start();
        foreach (var thread in writers)
            thread.Join();
        writersDone.Set();
        foreach (var thread in readers)
            thread.Join();

        if (!failures.IsEmpty)
            throw new AggregateException(failures);

        using var connection = simulation.CreateOpenConnection();
        for (var w = 0; w < Workers; w++)
        {
            var actual = new Dictionary<int, int>();
            using (var command = connection.CreateCommand("select id, v from t where w = @w", ("@w", w)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    actual.Add(reader.GetInt32(0), reader.GetInt32(1));
            }
            HasCount(expected[w].Count, actual, $"worker {w}'s row count");
            foreach (var (id, v) in expected[w])
                AreEqual(v, actual.GetValueOrDefault(id, -1), $"worker {w}'s row {id}");
        }
        AreEqual(expected.Sum(e => e.Count), simulation.ExecuteScalar("select count(*) from t"));
    }

    private static void Capture(System.Collections.Concurrent.ConcurrentQueue<Exception> failures, Action body)
    {
        try
        {
            body();
        }
        catch (Exception exception)
        {
            failures.Enqueue(exception);
        }
    }

    private static void Write(Simulation simulation, int worker, Dictionary<int, int> rows)
    {
        var random = new Random(worker);
        using var connection = simulation.CreateOpenConnection();
        for (var i = 0; i < 60; i++)
        {
            var v = (worker * 100_000) + (i * 10);
            switch (random.Next(rows.Count < 4 ? 3 : 8))
            {
                case 0:
                    AddEach(rows, Rows(connection, """
                        declare @t table (id int);
                        insert t (w, v, pad) output inserted.id into @t values (@w, @v, 'a');
                        insert t (w, v, pad) output inserted.id into @t values (@w, @v + 1, 'b');
                        select t.id, t.v from t join @t i on t.id = i.id;
                        """, worker, v), 2);
                    break;
                case 1:
                    AddEach(rows, Rows(connection, """
                        insert t (w, v, pad, big) output inserted.id, inserted.v
                        values (@w, @v, 'c', replicate(cast('L' as varchar(max)), 9000)), (@w, @v + 1, 'd', null), (@w, @v + 2, 'e', 'small');
                        """, worker, v), 3);
                    break;
                case 2:
                    AddEach(rows, Rows(connection, """
                        declare @m table (id int, p int);
                        merge t using (values (@v, 0), (@v + 1, 1)) as s (v, p) on 1 = 0
                        when not matched then insert (w, v, pad) values (@w, s.v, 'm')
                        output inserted.id, s.p into @m;
                        select t.id, t.v from t join @m m on t.id = m.id;
                        """, worker, v), 2);
                    break;
                case 3:
                    {
                        // Growing the row past its extent forwards it on a heap.
                        var id = Pick(rows, random);
                        AreEqual(1, Execute(connection, $"update t set v = v + 1, pad = replicate('x', {random.Next(50, 400)}) where id = @id", worker, id));
                        rows[id]++;
                        break;
                    }
                case 4:
                    {
                        var id = Pick(rows, random);
                        AreEqual(1, Execute(connection, "delete t where id = @id", worker, id));
                        _ = rows.Remove(id);
                        break;
                    }
                case 5:
                    {
                        var rolledBack = Rows(connection, """
                            begin tran;
                            insert t (w, v, pad, big) output inserted.id, inserted.v values (@w, @v, 'r', replicate(cast('R' as varchar(max)), 9000));
                            rollback;
                            """, worker, v);
                        HasCount(1, rolledBack);
                        AreEqual(0, Execute(connection, "select count(*) from t where id = @id", worker, rolledBack[0].Id, scalar: true));
                        break;
                    }
                case 6:
                    AreEqual(rows.Count, Execute(connection, "update t set v = v + 1 where w = @w", worker, 0));
                    foreach (var id in rows.Keys.ToArray())
                        rows[id]++;
                    break;
                default:
                    {
                        var id = Pick(rows, random);
                        AreEqual(1, Execute(connection, "merge t using (select @id as id) s on t.id = s.id when matched then update set v = v + 1, pad = 'merged';", worker, id));
                        rows[id]++;
                        break;
                    }
            }
        }
    }

    private static int Pick(Dictionary<int, int> rows, Random random) => rows.Keys.ElementAt(random.Next(rows.Count));

    private static void AddEach(Dictionary<int, int> rows, List<(int Id, int V)> added, int count)
    {
        HasCount(count, added, "rows the batch reads back by the ids its OUTPUT reported");
        foreach (var (id, v) in added)
            rows.Add(id, v);
    }

    private static List<(int Id, int V)> Rows(DbConnection connection, string text, int worker, int v)
    {
        using var command = connection.CreateCommand("set nocount on; " + text, ("@w", worker), ("@v", v));
        using var reader = command.ExecuteReader();
        var rows = new List<(int, int)>();
        do
        {
            while (reader.Read())
                rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }
        while (reader.NextResult());
        return rows;
    }

    private static int Execute(DbConnection connection, string text, int worker, int id, bool scalar = false)
    {
        using var command = connection.CreateCommand(text, ("@w", worker), ("@id", id));
        return scalar ? (int)command.ExecuteScalar()! : command.ExecuteNonQuery();
    }

    private static void ReadNoLock(Simulation simulation, ManualResetEventSlim writersDone)
    {
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("""
            select count(*), sum(cast(v as bigint)), max(len(pad)), sum(cast(len(big) as bigint)) from t with (nolock);
            select id, w, v from t with (nolock) where w = 3;
            """);
        while (!writersDone.IsSet)
        {
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                }
            }
            while (reader.NextResult());
        }
    }

    private static void ReadSnapshot(Simulation simulation, ManualResetEventSlim writersDone, bool versioned)
    {
        if (!versioned)
            return;
        using var connection = simulation.CreateOpenConnection();
        using (var setup = connection.CreateCommand("set transaction isolation level snapshot"))
            _ = setup.ExecuteNonQuery();
        using var command = connection.CreateCommand("""
            begin tran;
            select count(*), sum(cast(v as bigint)) from t;
            select count(*), sum(cast(v as bigint)) from t;
            commit;
            """);
        while (!writersDone.IsSet)
        {
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            var (count, sum) = (reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
            IsTrue(reader.NextResult());
            IsTrue(reader.Read());
            AreEqual(count, reader.GetInt32(0), "a snapshot's count read twice");
            AreEqual(sum, reader.IsDBNull(1) ? 0 : reader.GetInt64(1), "a snapshot's sum read twice");
        }
    }
}
