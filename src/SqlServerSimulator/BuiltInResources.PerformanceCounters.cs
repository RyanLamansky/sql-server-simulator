using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sys.dm_os_performance_counters and its sysperfinfo compatibility view: the
// SQLServer: objects monitoring tools read, each counter named, typed and
// ordered as SQL Server 2025 lists it (probed 2026-10-16), its value read off
// the simulator's state where the simulator keeps that state; see
// docs/claude/catalog-views.md#performance-counters.
internal static partial class BuiltInResources
{
    private static void RegisterPerformanceCounters(Dictionary<string, CatalogView> views)
    {
        var name = NCharSqlType.Get(128, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);
        HeapColumn[] columns =
        [
            new("object_name", name, 128, false),
            new("counter_name", name, 128, false),
            new("instance_name", name, 128, true),
            new("cntr_value", SqlType.BigInt, null, false),
            new("cntr_type", SqlType.Int32, null, false),
        ];
        views["sys.dm_os_performance_counters"] = new CatalogView("dm_os_performance_counters", columns, (batch, _) => PerformanceCounterRows(batch, name));
        var sysperfinfo = new CatalogView("sysperfinfo", columns, (batch, _) => PerformanceCounterRows(batch, name));
        views["sysperfinfo"] = sysperfinfo;
        views["sys.sysperfinfo"] = sysperfinfo;
    }

    /// <summary>
    /// The counters, as <c>object</c>, <c>counter</c>, <c>instance</c> and
    /// <c>cntr_type</c>, in real's order; an instance of <c>*</c> stands for
    /// each database and then <c>_Total</c>.
    /// </summary>
    private static readonly (string Object, string Counter, string Instance, int Type)[] PerformanceCounterDefinitions = ReadPerformanceCounters();

    private static (string, string, string, int)[] ReadPerformanceCounters()
    {
        using var stream = typeof(BuiltInResources).Assembly.GetManifestResourceStream("SqlServerSimulator.PerformanceCounters.tsv")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.PerformanceCounters.tsv is missing.");
        using var reader = new StreamReader(stream);
        var counters = new List<(string, string, string, int)>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            if (fields.Length == 4)
                counters.Add((fields[0], fields[1], fields[2], int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture)));
        }
        return [.. counters];
    }

    /// <summary>The log a database reports in use, in KB, as <c>DBCC SQLPERF(LOGSPACE)</c> reports it.</summary>
    private const long LogUsedKb = Simulation.ReportedLogBytesUsed / 1024;

    /// <summary>The real counter instance names of the <see cref="LockCounters.Kind"/> slots, the rest of real's instances reading zero.</summary>
    private static int LockCounterSlot(string instance) => instance switch
    {
        "Application" => (int)LockCounters.Kind.Application,
        "Key" => (int)LockCounters.Kind.Key,
        "Metadata" => (int)LockCounters.Kind.Metadata,
        "Object" => (int)LockCounters.Kind.Object,
        "Page" => (int)LockCounters.Kind.Page,
        "RID" => (int)LockCounters.Kind.Rid,
        _ => -1,
    };

    /// <summary>What one read of the counters measures, gathered once for every row.</summary>
    private sealed class CounterReading
    {
        public long UserConnections;
        public long Blocked;
        public long ActiveTempTables;
        public long SessionsInTransactions;
        public long Transactions;
        public long SnapshotTransactions;
        public long LongestTransactionSeconds;
        public long DatabasePages;
        public long PageLifeSeconds;
        public long TotalServerKb;
        public long TargetServerKb;
        public long VersionStoreKb;
        public long TempdbFreeKb;
        public readonly long[] PlanCount = new long[2];
        public readonly long[] PlanBytes = new long[2];
        public readonly long[] PlanUses = new long[2];

        /// <summary>Per database, in id order: its name, data and log file sizes in KB, and its active transactions.</summary>
        public readonly List<(string Name, long DataKb, long LogKb, long Active)> Databases = [];
    }

    private static CounterReading ReadCounters(Parser.BatchContext batch)
    {
        var simulation = batch.Connection.Simulation;
        var reading = new CounterReading();
        var connections = simulation.SnapshotConnections();
        var transactions = new Dictionary<long, (SimulatedDbConnection Connection, SimulatedDbTransaction Transaction)>();
        var now = DateTime.UtcNow;
        reading.UserConnections = connections.Length;
        reading.ActiveTempTables = simulation.GlobalTempTables.Count;
        foreach (var connection in connections)
        {
            if (connection.WaitingOnResource is not null)
                reading.Blocked++;
            reading.ActiveTempTables += connection.TempTables.Count;
            if (connection.CurrentTransaction is not { } transaction)
                continue;
            reading.SessionsInTransactions++;
            var id = transaction.TransactionId;
            if (!transactions.TryAdd(id, (connection, transaction)))
                continue;
            if (transaction.SnapshotXid is not null)
                reading.SnapshotTransactions++;
            reading.LongestTransactionSeconds = Math.Max(reading.LongestTransactionSeconds, (long)(now - transaction.BeginTimeUtc).TotalSeconds);
        }
        reading.Transactions = transactions.Count;

        foreach (var (database, _) in DbId.DatabasesWithIds(simulation))
        {
            var pages = SumDataFilePages(database);
            reading.DatabasePages += pages;
            long dataKb = 0;
            long freeKb = 0;
            foreach (var file in database.FilesInOrder())
            {
                if (file.IsLog)
                    continue;
                long size = FileSizePages(database, file);
                dataKb += size * 8;
                var used = file.FileId == 1 ? pages : EmptyDataFileUsedPages;
                freeKb += (size - Math.Min(size, (used + 7) / 8 * 8)) * 8;
            }
            if (database.Name == Simulation.TempdbDatabaseName)
                reading.TempdbFreeKb = freeKb;
            foreach (var row in VersionStoreDmvs.EnumerateDmTranVersionStoreSpaceUsage(batch, database))
                reading.VersionStoreKb += row[2].AsInt64;
            long active = 0;
            if (transactions.Count != 0)
            {
                var tables = new Simulation.DatabaseTables(simulation, database, connections);
                foreach (var (_, (connection, transaction)) in transactions)
                {
                    if (tables.WrittenBy(connection, transaction) != 0)
                        active++;
                }
            }
            reading.Databases.Add((database.Name, dataKb, Simulation.LogFileBytes(database) / 1024, active));
        }

        foreach (var plan in simulation.LiveCachedPlans())
        {
            var group = plan.IsBatch ? 0 : 1;
            int uses;
            lock (plan)
                uses = plan.UseCount;
            reading.PlanCount[group]++;
            reading.PlanBytes[group] += CachedPlanSizeBytes(plan);
            reading.PlanUses[group] += uses;
        }

        reading.PageLifeSeconds = (Environment.TickCount64 - simulation.StartTicks) / 1000;
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
            reading.TotalServerKb = process.WorkingSet64 / 1024;
        reading.TargetServerKb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024;
        return reading;
    }

    /// <summary>
    /// The rows of <c>sys.dm_os_performance_counters</c>. A database's
    /// counters list for each database in id order, then for
    /// <c>_Total</c>, which sums them.
    /// </summary>
    private static IEnumerable<SqlValue[]> PerformanceCounterRows(Parser.BatchContext batch, NCharSqlType name)
    {
        var simulation = batch.Connection.Simulation;
        var reading = ReadCounters(batch);
        var locks = simulation.LockManager.Counters;
        var waits = simulation.WaitStats;
        var definitions = PerformanceCounterDefinitions;
        var start = 0;
        while (start < definitions.Length)
        {
            var end = start;
            while (end < definitions.Length && definitions[end].Object == definitions[start].Object)
                end++;
            if (definitions[start].Instance != "*")
            {
                for (var i = start; i < end; i++)
                {
                    var (objectName, counter, instance, type) = definitions[i];
                    yield return Row(objectName, counter, instance, type, Value(objectName, counter, instance));
                }
            }
            else
            {
                long totalData = 0, totalLog = 0, totalActive = 0;
                foreach (var (databaseName, dataKb, logKb, active) in reading.Databases)
                {
                    for (var i = start; i < end; i++)
                        yield return Row(definitions[i].Object, definitions[i].Counter, databaseName, definitions[i].Type, DatabaseValue(definitions[i].Counter, dataKb, logKb, LogUsedKb, active));
                    totalData += dataKb;
                    totalLog += logKb;
                    totalActive += active;
                }
                for (var i = start; i < end; i++)
                    yield return Row(definitions[i].Object, definitions[i].Counter, "_Total", definitions[i].Type, DatabaseValue(definitions[i].Counter, totalData, totalLog, LogUsedKb * reading.Databases.Count, totalActive));
            }
            start = end;
        }

        SqlValue[] Row(string objectName, string counter, string instance, int type, long value) =>
        [
            SqlValue.FromNChar(name, objectName),
            SqlValue.FromNChar(name, counter),
            SqlValue.FromNChar(name, instance),
            SqlValue.FromInt64(value),
            SqlValue.FromInt32(type),
        ];

        static long DatabaseValue(string counter, long dataKb, long logKb, long usedKb, long active) => counter switch
        {
            "Active Transactions" => active,
            "Data File(s) Size (KB)" => dataKb,
            "Log File(s) Size (KB)" => logKb,
            "Log File(s) Used Size (KB)" => usedKb,
            "Percent Log Used" => logKb == 0 ? 0 : usedKb * 100 / logKb,
            _ => 0,
        };

        long Value(string objectName, string counter, string instance)
        {
            switch (objectName)
            {
                case "SQLServer:Buffer Manager":
                case "SQLServer:Buffer Node":
                    return counter switch
                    {
                        "Buffer cache hit ratio" or "Buffer cache hit ratio base" => Math.Max(1, reading.DatabasePages),
                        "Database pages" => reading.DatabasePages,
                        "Page life expectancy" => reading.PageLifeSeconds,
                        "Target pages" => reading.TargetServerKb / 8,
                        _ => 0,
                    };
                case "SQLServer:General Statistics":
                    return counter switch
                    {
                        "Active Temp Tables" => reading.ActiveTempTables,
                        "Connection Reset/sec" => Volatile.Read(ref simulation.ConnectionResetCount),
                        "Logical Connections" or "User Connections" => reading.UserConnections,
                        "Logins/sec" => Volatile.Read(ref simulation.LoginCount),
                        "Logouts/sec" => Volatile.Read(ref simulation.LogoutCount),
                        "Processes blocked" => reading.Blocked,
                        "Transactions" => reading.SessionsInTransactions,
                        _ => 0,
                    };
                case "SQLServer:Locks":
                    return LockValue(counter, instance);
                case "SQLServer:Memory Manager":
                case "SQLServer:Memory Node":
                    {
                        var databaseKb = reading.DatabasePages * 8;
                        return counter switch
                        {
                            "Database Cache Memory (KB)" or "Database Node Memory (KB)" => databaseKb,
                            "SQL Cache Memory (KB)" => (reading.PlanBytes[0] + reading.PlanBytes[1]) / 1024,
                            "Stolen Node Memory (KB)" or "Stolen Server Memory (KB)" => Math.Max(0, reading.TotalServerKb - databaseKb),
                            "Target Node Memory (KB)" or "Target Server Memory (KB)" => reading.TargetServerKb,
                            "Total Node Memory (KB)" or "Total Server Memory (KB)" => reading.TotalServerKb,
                            _ => 0,
                        };
                    }
                case "SQLServer:Plan Cache":
                    return PlanCacheValue(counter, instance);
                case "SQLServer:SQL Statistics":
                    return counter switch
                    {
                        "Batch Requests/sec" => Volatile.Read(ref simulation.BatchRequestCount),
                        "SQL Compilations/sec" => Volatile.Read(ref simulation.CachedPlans.Compilations),
                        _ => 0,
                    };
                case "SQLServer:Transactions":
                    return counter switch
                    {
                        "Free Space in tempdb (KB)" => reading.TempdbFreeKb,
                        "Longest Transaction Running Time" => reading.LongestTransactionSeconds,
                        "Snapshot Transactions" => reading.SnapshotTransactions,
                        "Transactions" => reading.Transactions,
                        "Version Store Size (KB)" => reading.VersionStoreKb,
                        _ => 0,
                    };
                // A wait kind's counter has its measures for instances.
                case "SQLServer:Wait Statistics":
                    {
                        var totals = counter switch
                        {
                            "Lock waits" => LockWaitTotals(waits),
                            "Network IO waits" => waits.Of("ASYNC_NETWORK_IO"),
                            _ => default,
                        };
                        return instance switch
                        {
                            "Average wait time (ms)" => totals.WaitingTasks == 0 ? 0 : totals.WaitMilliseconds / totals.WaitingTasks,
                            "Cumulative wait time (ms) per second" => totals.WaitMilliseconds,
                            "Waits in progress" => counter == "Lock waits" ? reading.Blocked : 0,
                            "Waits started per second" => totals.WaitingTasks,
                            _ => 0,
                        };
                    }
                default:
                    return 0;
            }
        }

        long LockValue(string counter, string instance)
        {
            long Sum(long[] values)
            {
                if (instance == "_Total")
                    return values.Sum();
                var slot = LockCounterSlot(instance);
                return slot < 0 ? 0 : values[slot];
            }
            return counter switch
            {
                "Average Wait Time (ms)" or "Lock Wait Time (ms)" => Sum(locks.WaitMilliseconds),
                "Average Wait Time Base" or "Lock Waits/sec" => Sum(locks.Waits),
                "Lock Requests/sec" => Sum(locks.Requests),
                "Lock Timeouts (timeout > 0)/sec" => Sum(locks.TimeoutsAfterWaiting),
                "Lock Timeouts/sec" => Sum(locks.Timeouts),
                "Number of Deadlocks/sec" => Sum(locks.Deadlocks),
                _ => 0,
            };
        }

        long PlanCacheValue(string counter, string instance)
        {
            long Pick(long[] values) => instance switch
            {
                "Object Plans" => values[1],
                "SQL Plans" => values[0],
                "_Total" => values[0] + values[1],
                _ => 0,
            };
            return counter switch
            {
                "Cache Hit Ratio" => Pick(reading.PlanUses) - Pick(reading.PlanCount),
                "Cache Hit Ratio Base" => Pick(reading.PlanUses),
                "Cache Object Counts" => Pick(reading.PlanCount),
                "Cache Pages" => (Pick(reading.PlanBytes) + 8191) / 8192,
                _ => 0,
            };
        }
    }

    /// <summary>The server's lock waits, every <c>LCK_M_</c> wait type's totals summed.</summary>
    private static WaitStatistics.Totals LockWaitTotals(WaitStatistics waits)
    {
        var sum = default(WaitStatistics.Totals);
        foreach (var (waitType, totals) in waits.Snapshot())
        {
            if (!waitType.StartsWith("LCK_M_", StringComparison.Ordinal))
                continue;
            sum.WaitingTasks += totals.WaitingTasks;
            sum.WaitMilliseconds += totals.WaitMilliseconds;
            sum.MaxWaitMilliseconds = Math.Max(sum.MaxWaitMilliseconds, totals.MaxWaitMilliseconds);
        }
        return sum;
    }
}
