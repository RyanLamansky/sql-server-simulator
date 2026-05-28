using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards the per-<see cref="Simulation"/> plan cache for single-SELECT
/// command batches (the EF-query shape). Every parse-and-replay path here
/// is observable through the internal <see cref="Simulation.PlanCacheHits"/>
/// / <see cref="Simulation.PlanCacheMisses"/> / <see cref="Simulation.PlanCacheCount"/>
/// counters, which gate on cache-key matching plus schema-version validation;
/// the cache is bypassed entirely for batches that aren't a single top-level
/// SELECT (multi-statement, mixed DDL+DML, reference a #temp / ##gtemp /
/// table-variable, or whose parameters' DbType can't be inferred — TVP
/// structured binding).
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

    [TestMethod]
    public void MultiStatementBatch_NotCached()
    {
        // Two top-level SELECT statements in one CommandText: the second
        // statement's dispatch clears the candidate (the cache only models
        // single-statement batches).
        var (sim, connection) = OpenWithTable();
        using (connection)
        {
            var entriesBefore = sim.PlanCacheCount;
            using var command = connection.CreateCommand();
            command.CommandText = "select val from t; select id from t;";
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    // drain first result set
                }
                _ = reader.NextResult();
                while (reader.Read())
                {
                    // drain second
                }
            }
            AreEqual(entriesBefore, sim.PlanCacheCount);
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
}
