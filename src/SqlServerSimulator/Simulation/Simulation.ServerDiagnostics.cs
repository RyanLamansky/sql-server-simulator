using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_server_diagnostics: the health report the failover machinery reads,
// probed 2026-10-16 against SQL Server 2025.
partial class Simulation
{
    private static readonly SqlType[] ServerDiagnosticsSchema =
        [SqlType.DateTime, SqlType.SystemName, SqlType.SystemName, SqlType.Int32, SqlType.SystemName, SqlType.NVarcharMax];

    private static readonly string[] ServerDiagnosticsColumnNames =
        ["create_time", "component_type", "component_name", "state", "state_desc", "data"];

    /// <summary>
    /// <c>sp_server_diagnostics [@repeat_interval]</c>: collects for five
    /// seconds, then sends one row per component — <c>system</c>,
    /// <c>resource</c>, <c>query_processing</c>, <c>io_subsystem</c> and
    /// <c>events</c>, each with its state and an XML report — followed by an
    /// informational message of one space, and returns 1. A
    /// <c>@repeat_interval</c> of 0 (the default) reports once; one of five or
    /// more reports again every that many seconds until the request is
    /// cancelled. A negative interval or one under five is Msg 3217, an
    /// argument that isn't an <c>int</c> Msg 214, and a second argument Msg
    /// 8144, the first and last ending the batch, as does Msg 297 for a
    /// principal without <c>VIEW SERVER PERFORMANCE STATE</c>.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpServerDiagnostics(BatchContext batch, string? returnCodeVar)
    {
        const string procedure = "sp_server_diagnostics";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var returnCode = returnCodeVar is null ? null : batch.GetVariableSlot(returnCodeVar);
        var connection = batch.Connection;
        if (!connection.Security.EffectiveIsDbo && !this.HoldsServerPermission(connection.Security.Effective.LoginName, Permission.ViewServerPerformanceState))
            throw AtProcedureLine(SimulatedSqlException.UserLacksPermissionToPerformAction(state: 100), procedure, 1).EndingBatch();
        if (arguments.Count > 1)
            throw AtProcedureLine(SimulatedSqlException.TooManyArgumentsToFunction(procedure, state: 20), procedure, 1).EndingBatch();

        var interval = 0L;
        if (arguments is [{ IsDefault: false } argument])
        {
            // Any name stands for the one parameter, so a misspelt one
            // still reads its value (probed 2026-10-16 against SQL Server 2025).
            if (argument.Value.IsNull || argument.Value.Type is not (Int32SqlType or SmallIntSqlType or TinyIntSqlType))
            {
                _ = returnCode?.Value = SqlValue.FromInt32(1).CoerceTo(returnCode.DeclaredType);
                throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameterOfType("@repeat_interval", "int", state: 1), procedure, 1);
            }
            interval = argument.Value.CoerceTo(SqlType.Int32).AsInt32;
            if (argument.Name is { } name && !BuiltInToken.Equals(name, "repeat_interval"))
                interval = -1;
            if (interval is < 0 or (> 0 and < 5))
                throw AtProcedureLine(SimulatedSqlException.RepeatIntervalInvalid(), procedure, 1).EndingBatch();
        }

        _ = returnCode?.Value = SqlValue.FromInt32(1).CoerceTo(returnCode.DeclaredType);
        var wait = TimeSpan.FromSeconds(5);
        while (true)
        {
            WaitInterruptibly(batch, wait, "SP_SERVER_DIAGNOSTICS_SLEEP");
            batch.PollCancellation();
            yield return new SimulatedSqlResultSet(ServerDiagnosticsSchema, ServerDiagnosticsColumnNames, this.ServerDiagnosticsRows(connection));
            yield return Printed(SimulatedSqlException.ServerDiagnosticsReportEndMessage(batch));
            // The message leaves @@ROWCOUNT at the report's rows.
            connection.LastStatementRowCount = 5;
            if (interval == 0)
                yield break;
            wait = TimeSpan.FromSeconds(interval);
        }
    }

    /// <summary>
    /// The five component rows of one report, collected now. The figures a
    /// simulation has — its sessions' waits, the running requests, the
    /// process's memory — are its own; the rest read as a healthy server's
    /// zeros, so every component is <c>clean</c> but <c>events</c>, whose
    /// state real reports <c>unknown</c>.
    /// </summary>
    private List<SqlValue[]> ServerDiagnosticsRows(SimulatedDbConnection connection)
    {
        var created = SqlValue.FromDateTime(DateTime.UtcNow);
        var instance = SqlValue.FromSystemName("instance");
        SqlValue[] Row(string component, int state, string stateDesc, string data) =>
            [created, instance, SqlValue.FromSystemName(component), SqlValue.FromInt32(state), SqlValue.FromSystemName(stateDesc), SqlValue.FromNVarchar(data)];

        var memory = GC.GetGCMemoryInfo();
        var available = Math.Max(0, memory.TotalAvailableMemoryBytes - memory.MemoryLoadBytes);
        var workingSet = Environment.WorkingSet;
        var cpus = Environment.ProcessorCount;
        var resource = new StringBuilder("<resource lastNotification=\"RESOURCE_MEMPHYSICAL_HIGH\" outOfMemoryExceptions=\"0\" isAnyPoolOutOfMemory=\"0\" processOutOfMemoryPeriod=\"0\">")
            .Append("<memoryReport name=\"Process/System Counts\" unit=\"Value\">");
        foreach (var (description, value) in (ReadOnlySpan<(string, long)>)[
            ("Available Physical Memory", available), ("Available Virtual Memory", 70364449144832), ("Available Paging File", available),
            ("Working Set", workingSet), ("Percent of Committed Memory in WS", 100), ("Page Faults", 0), ("System physical memory high", 1),
            ("System physical memory low", 0), ("Process physical memory low", 0), ("Process virtual memory low", 0)])
        {
            AppendDiagnosticsEntry(resource, description, value);
        }
        _ = resource.Append("</memoryReport><memoryReport name=\"Memory Manager\" unit=\"KB\">");
        var committedKb = workingSet / 1024;
        foreach (var (description, value) in (ReadOnlySpan<(string, long)>)[
            ("VM Reserved", committedKb), ("VM Committed", committedKb), ("Locked Pages Allocated", 0), ("Large Pages Allocated", 0),
            ("Emergency Memory", 1024), ("Emergency Memory In Use", 0), ("Target Committed", committedKb), ("Current Committed", committedKb),
            ("Pages Allocated", committedKb), ("Pages Reserved", 0), ("Pages Free", 0), ("Pages In Use", committedKb),
            ("Page Alloc Potential", committedKb), ("NUMA Growth Phase", 2), ("Last OOM Factor", 0), ("Last OS Error", 0)])
        {
            AppendDiagnosticsEntry(resource, description, value);
        }
        _ = resource.Append("</memoryReport></resource>");

        var requests = this.SnapshotConnections();
        Array.Sort(requests, static (a, b) => a.Spid.CompareTo(b.Spid));
        var maxWorkers = cpus <= 4 ? 512 : 512 + ((cpus - 4) * 16);
        var queryProcessing = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"<queryProcessing maxWorkers=\"{maxWorkers}\" workersCreated=\"{requests.Length}\" workersIdle=\"0\" tasksCompletedWithinInterval=\"0\" pendingTasks=\"0\" oldestPendingTaskWaitingTime=\"0\" hasUnresolvableDeadlockOccurred=\"0\" hasDeadlockedSchedulersOccurred=\"0\" trackingNonYieldingScheduler=\"0x0\"><topWaits>"));
        foreach (var preemptive in (ReadOnlySpan<bool>)[false, true])
        {
            _ = queryProcessing.Append(preemptive ? "<preemptive>" : "<nonPreemptive>");
            var waits = new List<(string Type, WaitStatistics.Totals Totals)>();
            foreach (var waitType in WaitStatistics.WaitTypes)
            {
                if (waitType.StartsWith("PREEMPTIVE_", StringComparison.Ordinal) == preemptive && this.WaitStats.Of(waitType) is { WaitingTasks: > 0 } totals)
                    waits.Add((waitType, totals));
            }
            foreach (var byDuration in (ReadOnlySpan<bool>)[false, true])
            {
                waits.Sort(byDuration
                    ? static (a, b) => b.Totals.WaitMilliseconds.CompareTo(a.Totals.WaitMilliseconds)
                    : static (a, b) => b.Totals.WaitingTasks.CompareTo(a.Totals.WaitingTasks));
                _ = queryProcessing.Append(byDuration ? "<byDuration>" : "<byCount>");
                foreach (var (waitType, totals) in waits.Take(10))
                {
                    _ = queryProcessing.Append(string.Create(CultureInfo.InvariantCulture,
                        $"<wait waitType=\"{waitType}\" waits=\"{totals.WaitingTasks}\" averageWaitTime=\"{totals.WaitMilliseconds / totals.WaitingTasks}\" maxWaitTime=\"{totals.MaxWaitMilliseconds}\"/>"));
                }
                _ = queryProcessing.Append(byDuration ? "</byDuration>" : "</byCount>");
            }
            _ = queryProcessing.Append(preemptive ? "</preemptive>" : "</nonPreemptive>");
        }
        _ = queryProcessing.Append("</topWaits><cpuIntensiveRequests>")
            .Append(string.Create(CultureInfo.InvariantCulture, $"<request sessionId=\"{connection.Spid}\" requestId=\"0\" command=\"EXECUTE\" taskAddress=\"0x0\" cpuUtilization=\"0\" cpuTimeMs=\"0\"/>"))
            .Append("</cpuIntensiveRequests><pendingTasks></pendingTasks><blockingTasks></blockingTasks></queryProcessing>");

        return
        [
            Row("system", 1, "clean", "<system spinlockBackoffs=\"0\" sickSpinlockType=\"none\" sickSpinlockTypeAfterAv=\"none\" latchWarnings=\"0\" isAccessViolationOccurred=\"0\" writeAccessViolationCount=\"0\" totalDumpRequests=\"0\" intervalDumpRequests=\"0\" nonYieldingTasksReported=\"0\" pageFaults=\"0\" systemCpuUtilization=\"0\" sqlCpuUtilization=\"0\" BadPagesDetected=\"0\" BadPagesFixed=\"0\" LastBadPageAddress=\"0x0\"/>"),
            Row("resource", 1, "clean", resource.ToString()),
            Row("query_processing", 1, "clean", queryProcessing.ToString()),
            Row("io_subsystem", 1, "clean", "<ioSubsystem ioLatchTimeouts=\"0\" intervalLongIos=\"0\" totalLongIos=\"0\"><longestPendingRequests></longestPendingRequests></ioSubsystem>"),
            Row("events", 0, "unknown", string.Create(CultureInfo.InvariantCulture,
                $"<events><session startTime=\"{this.SeedDate:yyyy-MM-ddTHH:mm:ss.fff}\" droppedEvents=\"0\" largestDroppedEvent=\"0\"><RingBufferTarget truncated=\"0\" processingTime=\"0\" totalEventsProcessed=\"0\" eventCount=\"0\" droppedCount=\"0\" memoryUsed=\"0\"></RingBufferTarget></session></events>")),
        ];
    }

    private static void AppendDiagnosticsEntry(StringBuilder builder, string description, long value) =>
        builder.Append("<entry description=\"").Append(description).Append("\" value=\"").Append(value.ToString(CultureInfo.InvariantCulture)).Append("\"/>");
}
