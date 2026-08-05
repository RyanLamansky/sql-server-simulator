using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards the per-<see cref="Simulation"/> plan cache, which stores the
/// <see cref="Parser.Selection"/> sequence of a batch whose every top-level
/// statement is a SELECT (the EF-query shape, and any batch of several).
/// Every parse-and-replay path here is observable through the internal
/// <see cref="Simulation.PlanCacheHits"/> / <see cref="Simulation.PlanCacheMisses"/>
/// / <see cref="Simulation.PlanCacheCount"/> counters, which gate on cache-key
/// matching plus schema-version validation; the cache is bypassed entirely for
/// batches carrying a statement kind with no re-executable plan (DML, SET,
/// DDL, control flow), referencing a #temp / ##gtemp / table-variable, or
/// whose parameters' DbType can't be inferred — TVP structured binding.
/// Statement kinds the cache declines still reuse their tokenization; see
/// <see cref="TokenMemoTests"/>.
/// </summary>
[TestClass]
public sealed class PlanCacheTests
{
    private static (Simulation Sim, SimulatedDbConnection Connection) OpenWithTable()
    {
        var sim = new Simulation();
        var connection = sim.CreateDbConnection();
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            create table t (id int not null primary key, val int not null);
            insert t values (1, 10), (2, 20), (3, 30);
            """;
        _ = setup.ExecuteNonQuery();
        return (sim, connection);
    }

    private static int RunCount(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = 0;
        while (reader.Read())
            rows++;
        return rows;
    }

    [TestMethod]
    public void RepeatedQuery_SecondCallHits()
    {
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var hitsBefore = sim.PlanCacheHits;
            var missesBefore = sim.PlanCacheMisses;
            AreEqual(3, RunCount(connection, "select val from t"));
            // First call: cache miss, entry added.
            AreEqual(missesBefore + 1, sim.PlanCacheMisses);
            AreEqual(hitsBefore, sim.PlanCacheHits);
            AreEqual(3, RunCount(connection, "select val from t"));
            // Second call: cache hit, no parse / no entry add.
            AreEqual(hitsBefore + 1, sim.PlanCacheHits);
            AreEqual(missesBefore + 1, sim.PlanCacheMisses);
        }
    }

    [TestMethod]
    public void DistinctCommandText_DistinctEntries()
    {
        // Different WHERE clauses produce different CommandTexts → different
        // cache entries. Neither hits the other; both get cached for replay.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var entriesBefore = sim.PlanCacheCount;
            AreEqual(1, RunCount(connection, "select val from t where id = 1"));
            AreEqual(1, RunCount(connection, "select val from t where id = 2"));
            // Both queries cached separately.
            AreEqual(entriesBefore + 2, sim.PlanCacheCount);
            // Replays now hit, not miss.
            var hitsBefore = sim.PlanCacheHits;
            AreEqual(1, RunCount(connection, "select val from t where id = 1"));
            AreEqual(1, RunCount(connection, "select val from t where id = 2"));
            AreEqual(hitsBefore + 2, sim.PlanCacheHits);
        }
    }

    [TestMethod]
    public void DdlInvalidatesCache()
    {
        // The schema-version stamp on each entry is compared at lookup against
        // the live SchemaVersion. CREATE INDEX bumps the version, so the next
        // call against the same CommandText hits a stale entry → re-parse,
        // overwrite the entry under the new version.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            AreEqual(3, RunCount(connection, "select val from t"));
            var hitsAfterFirst = sim.PlanCacheHits;
            using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = "create index ix_t_val on t (val)";
                _ = ddl.ExecuteNonQuery();
            }
            AreEqual(3, RunCount(connection, "select val from t"));
            // Stale entry → miss, even though the CommandText is identical.
            AreEqual(hitsAfterFirst, sim.PlanCacheHits);
            // Third call hits the freshly-cached entry.
            AreEqual(3, RunCount(connection, "select val from t"));
            AreEqual(hitsAfterFirst + 1, sim.PlanCacheHits);
        }
    }

    [TestMethod]
    public void TempTableReference_NotCached()
    {
        // A #temp table binding captures a session-local HeapTable instance;
        // a cross-session replay would project the wrong table. The dispatch
        // sets HasSessionScopedReference on TryResolveTable's temp-table
        // path, and the promotion check declines.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "select * into #scratch from t";
                _ = setup.ExecuteNonQuery();
            }
            var entriesBefore = sim.PlanCacheCount;
            AreEqual(3, RunCount(connection, "select val from #scratch"));
            // No cache entry added for the temp-table-referencing query.
            AreEqual(entriesBefore, sim.PlanCacheCount);
            // Second call also misses (nothing was added the first time).
            var missesBefore = sim.PlanCacheMisses;
            AreEqual(3, RunCount(connection, "select val from #scratch"));
            AreEqual(missesBefore + 1, sim.PlanCacheMisses);
        }
    }

    [TestMethod]
    public void TableVariableReference_NotCached()
    {
        // A @t table-variable binding is per-BATCH, so any cached plan would
        // be unusable in another batch (the variable doesn't even exist).
        // The dispatch flags the resolution at the @t path; the promotion
        // check sees the flag and declines.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var entriesBefore = sim.PlanCacheCount;
            using var command = connection.CreateCommand();
            command.CommandText = """
                declare @v table (id int);
                insert @v values (1);
                select id from @v;
                """;
            _ = RunReaderCount(command);
            AreEqual(entriesBefore, sim.PlanCacheCount);
        }

        static int RunReaderCount(SimulatedDbCommand command)
        {
            using var reader = command.ExecuteReader();
            var rows = 0;
            while (reader.Read())
                rows++;
            return rows;
        }
    }

    /// <summary>
    /// Runs a batch, returning each statement's rows as a list of
    /// first-column values — enough to compare a cached replay against an
    /// uncached run statement for statement.
    /// </summary>
    private static List<List<object>> RunBatch(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var sets = new List<List<object>>();
        do
        {
            var rows = new List<object>();
            while (reader.Read())
                rows.Add(reader.GetValue(0));
            sets.Add(rows);
        }
        while (reader.NextResult());
        return sets;
    }

    [TestMethod]
    public void MultiSelectBatch_CachesAsOneSequence()
    {
        // Two top-level SELECTs in one CommandText cache as the sequence they
        // are: one entry, and the replay reproduces both result sets in order.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var entriesBefore = sim.PlanCacheCount;
            var hitsBefore = sim.PlanCacheHits;
            const string sql = "select val from t; select id from t;";
            var first = RunBatch(connection, sql);
            AreEqual(entriesBefore + 1, sim.PlanCacheCount);
            AreEqual(hitsBefore, sim.PlanCacheHits);

            var replayed = RunBatch(connection, sql);
            AreEqual(hitsBefore + 1, sim.PlanCacheHits);
            AreEqual(entriesBefore + 1, sim.PlanCacheCount);
            HasCount(2, replayed);
            CollectionAssert.AreEqual(first[0], replayed[0]);
            CollectionAssert.AreEqual(first[1], replayed[1]);
        }
    }

    [TestMethod]
    public void TrailingSemicolon_Caches()
    {
        // The end-of-batch probe tolerates separators, so the terminated form
        // every client that punctuates its statements emits caches like the
        // bare one.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var hitsBefore = sim.PlanCacheHits;
            AreEqual(3, RunCount(connection, "select val from t;"));
            AreEqual(hitsBefore, sim.PlanCacheHits);
            AreEqual(3, RunCount(connection, "select val from t;"));
            AreEqual(hitsBefore + 1, sim.PlanCacheHits);
        }
    }

    [TestMethod]
    public void MixedBatch_SelectThenInsert_NotCached()
    {
        // A statement kind with no re-executable plan among the batch's
        // statements declines the whole batch: the top-level statement count
        // outruns the collected plan sequence.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var entriesBefore = sim.PlanCacheCount;
            _ = RunBatch(connection, "select val from t; insert t values (4, 40);");
            _ = RunBatch(connection, "select val from t; insert t values (5, 50);");
            AreEqual(entriesBefore, sim.PlanCacheCount);
        }
    }

    [TestMethod]
    public void MixedBatch_SetThenSelect_NotCached()
    {
        // The EF modification-batch prefix. SET carries a session effect, not
        // a plan, so a batch containing one is declined however cacheable the
        // SELECT after it would be alone.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var entriesBefore = sim.PlanCacheCount;
            const string sql = "set nocount on; select val from t;";
            _ = RunBatch(connection, sql);
            _ = RunBatch(connection, sql);
            AreEqual(entriesBefore, sim.PlanCacheCount);
        }
    }

    [TestMethod]
    public void MultiSelectBatch_ReplayRefreshesPerStatementState()
    {
        // The replay loop stamps each statement's own frame rather than the
        // batch's: two statements reading the same statement-scoped built-in
        // must see two draws, exactly as the dispatch loop's top-of-iteration
        // clear gives them on the uncached run.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            const string sql = "select rand() from t; select rand() from t;";
            var uncached = RunBatch(connection, sql);
            var missesBefore = sim.PlanCacheMisses;
            var cached = RunBatch(connection, sql);
            AreEqual(missesBefore, sim.PlanCacheMisses, "second run should have hit the cache");

            // Within one statement the draw is frozen; across the two it is
            // not — on the cached path just as on the uncached one.
            foreach (var run in new[] { uncached, cached })
            {
                HasCount(2, run);
                AreEqual(1, run[0].Distinct().Count(), "draw is per statement, not per row");
                AreEqual(1, run[1].Distinct().Count(), "draw is per statement, not per row");
                AreNotEqual(run[0][0], run[1][0], "each statement draws its own value");
            }
        }
    }

    [TestMethod]
    public void MultiSelectBatch_ReplayKeepsRowCountPerStatement()
    {
        // @@ROWCOUNT is maintained statement by statement through the replay
        // loop, so a following batch reads the last statement's count.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            const string sql = "select val from t where id > 1; select id from t;";
            _ = RunBatch(connection, sql);
            var hitsBefore = sim.PlanCacheHits;
            _ = RunBatch(connection, sql);
            AreEqual(hitsBefore + 1, sim.PlanCacheHits);
            AreEqual(3, RunBatch(connection, "select @@rowcount")[0][0]);
        }
    }

    [TestMethod]
    public void DifferentParameterTypes_DistinctEntries()
    {
        // Same SQL text with different declared parameter DbTypes parses to
        // different inferred result types (and possibly different overloads),
        // so each must get its own cache entry — the parameter signature is
        // part of the key.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var entriesBefore = sim.PlanCacheCount;

            using (var asInt = connection.CreateCommand())
            {
                asInt.CommandText = "select val from t where id = @p";
                var p = asInt.CreateParameter();
                p.ParameterName = "@p";
                p.DbType = System.Data.DbType.Int32;
                p.Value = 1;
                _ = asInt.Parameters.Add(p);
                _ = asInt.ExecuteScalar();
            }
            using (var asBigInt = connection.CreateCommand())
            {
                asBigInt.CommandText = "select val from t where id = @p";
                var p = asBigInt.CreateParameter();
                p.ParameterName = "@p";
                p.DbType = System.Data.DbType.Int64;
                p.Value = 1L;
                _ = asBigInt.Parameters.Add(p);
                _ = asBigInt.ExecuteScalar();
            }

            // Two cache entries: same text, different param-type signatures.
            AreEqual(entriesBefore + 2, sim.PlanCacheCount);
        }
    }

    [TestMethod]
    public void IdenticalParameterValues_DifferentValues_StillHits()
    {
        // The cache key includes parameter NAME and TYPE, not VALUE — calls
        // with the same SQL text and same param shape but different values
        // share a plan. This is the dominant EF path: prepared SQL with
        // parameter placeholders fired with many different runtime values.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            int Run(int idValue)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "select val from t where id = @p";
                var p = command.CreateParameter();
                p.ParameterName = "@p";
                p.DbType = System.Data.DbType.Int32;
                p.Value = idValue;
                _ = command.Parameters.Add(p);
                return (int)command.ExecuteScalar()!;
            }
            AreEqual(10, Run(1));
            var hitsAfterFirst = sim.PlanCacheHits;
            AreEqual(20, Run(2));
            AreEqual(30, Run(3));
            // Two hits, one per replay.
            AreEqual(hitsAfterFirst + 2, sim.PlanCacheHits);
        }
    }

    [TestMethod]
    public void ResultsMatchAcrossHitAndMiss()
    {
        // Correctness sanity: the cached replay must produce the same row set
        // a fresh parse would. Run twice, compare results — both pre- and
        // post-cache the answer is the same set.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var firstPass = ReadRows(connection, "select id, val from t order by id desc");
            var secondPass = ReadRows(connection, "select id, val from t order by id desc");
            HasCount(firstPass.Count, secondPass);
            for (var i = 0; i < firstPass.Count; i++)
            {
                AreEqual(firstPass[i].Id, secondPass[i].Id);
                AreEqual(firstPass[i].Val, secondPass[i].Val);
            }
            IsGreaterThanOrEqualTo(1L, sim.PlanCacheHits);
        }

        static List<(int Id, int Val)> ReadRows(SimulatedDbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<(int, int)>();
            while (reader.Read())
                rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
            return rows;
        }
    }

    [TestMethod]
    public void NonSelectBatch_NotCached()
    {
        // An INSERT / UPDATE / DELETE batch (no top-level SELECT) doesn't
        // populate a cache candidate — the dispatch SELECT arm is the only
        // arm that sets PlanCacheCandidate. Verifies the cache stays empty
        // even for a quite-common DML shape.
        var sim = new Simulation();
        var connection = sim.CreateDbConnection();
        connection.Open();
        using (connection)
        {
            using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = "create table t (id int not null)";
                _ = ddl.ExecuteNonQuery();
            }
            var entriesBefore = sim.PlanCacheCount;
            using var dml = connection.CreateCommand();
            dml.CommandText = "insert t values (1)";
            _ = dml.ExecuteNonQuery();
            AreEqual(entriesBefore, sim.PlanCacheCount);
        }
    }

    // ---- per-execution state on a shared cached plan. A cached Selection is
    // one object executed by many commands (possibly concurrently), so every
    // value that varies per execution — TOP / OFFSET / FETCH parameter counts,
    // RAND draws, the statement clock, aggregate / window bind results — must
    // live in per-execution scope, never on the plan or its expression tree. ----

    private static int RunCountWithParams(SimulatedDbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            _ = command.Parameters.Add(p);
        }

        using var reader = command.ExecuteReader();
        var rows = 0;
        while (reader.Read())
            rows++;
        return rows;
    }

    private static object RunScalar(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    [TestMethod]
    public void ParameterizedTop_ReplayResolvesNewValue()
    {
        // EF's Take(n) shape: same text, different @p per execution. The TOP
        // count must resolve against the EXECUTING batch — a parse-time-baked
        // count would return the first execution's row count forever.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            AreEqual(2, RunCountWithParams(connection, "select top (@p) id from t", ("@p", 2)));
            var hitsBefore = sim.PlanCacheHits;
            AreEqual(3, RunCountWithParams(connection, "select top (@p) id from t", ("@p", 3)));
            AreEqual(hitsBefore + 1, sim.PlanCacheHits);
            AreEqual(1, RunCountWithParams(connection, "select top (@p) id from t", ("@p", 1)));
        }
    }

    [TestMethod]
    public void ParameterizedOffsetFetch_ReplayResolvesNewValues()
    {
        // EF's Skip/Take pagination shape. A parse-time-baked OFFSET/FETCH
        // pair froze pagination on the first page.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            const string page = "select id from t order by id offset @o rows fetch next @f rows only";
            AreEqual(1, RunCountWithParams(connection, page, ("@o", 0), ("@f", 1)));
            var hitsBefore = sim.PlanCacheHits;
            AreEqual(2, RunCountWithParams(connection, page, ("@o", 1), ("@f", 2)));
            AreEqual(hitsBefore + 1, sim.PlanCacheHits);
        }
    }

    [TestMethod]
    public void Rand_ReplayDrawsFreshValue()
    {
        // RAND() freezes per statement EXECUTION (each row of one execution
        // sees the call site's single draw), but successive executions of the
        // cached plan must each draw fresh — instance-cached, the same
        // "random" value replayed forever.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var first = (double)RunScalar(connection, "select top 1 rand() from t");
            var hitsBefore = sim.PlanCacheHits;
            var second = (double)RunScalar(connection, "select top 1 rand() from t");
            AreEqual(hitsBefore + 1, sim.PlanCacheHits);
            AreNotEqual(first, second);
        }
    }

    [TestMethod]
    public void GetDate_ReplayReadsCurrentClock()
    {
        // The replay path bypasses the dispatch loop, so it must stamp the
        // per-statement frame itself — without that, a replayed GETDATE()
        // reads default(DateTime).
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            _ = RunScalar(connection, "select getdate() from t where id = 1");
            var hitsBefore = sim.PlanCacheHits;
            var replayed = (DateTime)RunScalar(connection, "select getdate() from t where id = 1");
            AreEqual(hitsBefore + 1, sim.PlanCacheHits);
            IsGreaterThan(DateTime.UtcNow.AddMinutes(-5), replayed);
        }
    }

    [TestMethod]
    public void ConcurrentReplays_AggregateResults_StayIsolated()
    {
        // One cached plan, many concurrent executions: per-group SUM / COUNT
        // results bind into the EXECUTING batch, never onto the shared
        // expression instances — instance-bound results cross-contaminated
        // concurrent readers (measured ~1% of reads returning another
        // execution's group value before the fix).
        var sim = new Simulation();
        using var setup = sim.CreateDbConnection();
        {
            setup.Open();
            using var ddl = setup.CreateCommand();
            ddl.CommandText = """
                create table s (g int not null, v int not null);
                insert s select value % 10, value from generate_series(1, 500)
                """;
            _ = ddl.ExecuteNonQuery();
            var expected = RunScalar(setup, "select sum(v) from s where g = 3").ToString();

            var wrong = 0;
            _ = Parallel.For(0, 8, _ =>
            {
                using var c = sim.CreateDbConnection();
                c.Open();
                for (var i = 0; i < 200; i++)
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "select g, sum(v) from s group by g order by sum(v) desc";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        if (r.GetInt32(0) == 3 && Convert.ToInt64(r.GetValue(1)).ToString() != expected)
                            _ = Interlocked.Increment(ref wrong);
                    }
                }
            });
            AreEqual(0, wrong);
        }
    }

    [TestMethod]
    public void ConcurrentReplays_WindowResults_StayIsolated()
    {
        // Same isolation contract for window functions (SUM OVER running
        // totals), whose per-row bind is even more interleaving-prone than
        // the per-group aggregate bind.
        var sim = new Simulation();
        using var setup = sim.CreateDbConnection();
        {
            setup.Open();
            using var ddl = setup.CreateCommand();
            ddl.CommandText = """
                create table s (g int not null, v int not null);
                insert s select value % 10, value from generate_series(1, 500)
                """;
            _ = ddl.ExecuteNonQuery();
            const string query = "select max(rq) from (select sum(v) over (order by g, v) as rq from s) x";
            var expected = RunScalar(setup, query).ToString();

            var wrong = 0;
            _ = Parallel.For(0, 8, _ =>
            {
                using var c = sim.CreateDbConnection();
                c.Open();
                for (var i = 0; i < 150; i++)
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = query;
                    if (cmd.ExecuteScalar()!.ToString() != expected)
                        _ = Interlocked.Increment(ref wrong);
                }
            });
            AreEqual(0, wrong);
        }
    }

    [TestMethod]
    public void RecursiveCte_NotCached_EitherAnchorShape()
    {
        // A recursive-CTE plan rebinds CteBinding.CurrentIterationRows at
        // execution time, so a cached copy replayed concurrently would
        // cross-feed iteration rowsets between commands. Both anchor shapes
        // must decline promotion: the FROM-less anchor via
        // BuildSynthesizedSqlRow's disqualifier, the FROM-ful anchor via the
        // recursive-CTE builder's own. Depth still resolves per execution.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            const string fromless = "with r as (select 1 as n union all select n + 1 from r where n < @d) select count(*) from r";
            const string fromful = "with r as (select id as n from t where id = 1 union all select n + 1 from r where n < @d) select count(*) from r";
            var hitsBefore = sim.PlanCacheHits;
            var entriesBefore = sim.PlanCacheCount;
            foreach (var query in new[] { fromless, fromful })
            {
                foreach (var depth in new[] { 7, 12 })
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = query;
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@d";
                    p.Value = depth;
                    _ = cmd.Parameters.Add(p);
                    AreEqual(depth, Convert.ToInt32(cmd.ExecuteScalar()!));
                }
            }

            AreEqual(hitsBefore, sim.PlanCacheHits);
            AreEqual(entriesBefore, sim.PlanCacheCount);
        }
    }

    [TestMethod]
    public void NonDefaultIsolationLevel_NeitherHitsNorPromotes()
    {
        // A cached plan carries the lock acquisitions its parsing session
        // made, so replaying one under a different isolation level would
        // settle the wrong protection — most visibly a SERIALIZABLE reader's
        // key-range fence. Both the lookup and the promotion skip.
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            AreEqual(3, RunCount(connection, "select val from t"));
            var hitsBefore = sim.PlanCacheHits;
            var entriesBefore = sim.PlanCacheCount;

            using (var setIso = connection.CreateCommand())
            {
                setIso.CommandText = "set transaction isolation level serializable";
                _ = setIso.ExecuteNonQuery();
            }

            AreEqual(3, RunCount(connection, "select val from t"));
            AreEqual(3, RunCount(connection, "select id from t"));
            AreEqual(hitsBefore, sim.PlanCacheHits);
            AreEqual(entriesBefore, sim.PlanCacheCount);
        }
    }
}
