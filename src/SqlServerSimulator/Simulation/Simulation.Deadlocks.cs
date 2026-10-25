using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The reports a deadlock makes — the Extended Events and the trace-flag error
// log lines; see docs/claude/extended-events.md#deadlock-reports.
partial class Simulation
{
    /// <summary>How many deadlocks the server has found, which numbers each one's <c>deadlock_id</c>.</summary>
    internal int DeadlockCount;

    /// <summary>
    /// Whether anything reports a deadlock: trace flag 1204 or 1222 on
    /// server-wide, or a running session taking one of the three deadlock
    /// events — <c>system_health</c> takes <c>xml_deadlock_report</c>.
    /// </summary>
    internal bool ReportsDeadlocks() =>
        XeEvents.Takes(this, XeEventKind.XmlDeadlockReport) || XeEvents.Takes(this, XeEventKind.LockDeadlock)
        || XeEvents.Takes(this, XeEventKind.LockDeadlockChain) || this.IsGlobalTraceFlagOn(1204) || this.IsGlobalTraceFlagOn(1222);

    /// <summary>Reports <paramref name="cycle"/>, from its victim's thread before its error rolls anything back.</summary>
    internal void ReportDeadlock(DeadlockCycle cycle) => DeadlockReport.Report(this, cycle);

    /// <summary>How many blocked-process reports the server has made, which numbers each one's <c>monitorLoop</c>.</summary>
    internal int BlockedProcessReportCount;

    /// <summary>
    /// Whether <paramref name="waiter"/>'s lock wait had passed one more
    /// multiple of the blocked process threshold by <paramref name="asOfTicks"/>,
    /// counting it when so; asked between the wait's slices and as it times
    /// out, so a session taking <c>blocked_process_report</c> and a nonzero
    /// threshold must both be in place before the configuration is read.
    /// </summary>
    internal bool DueBlockedProcessReport(SessionToken waiter, long asOfTicks)
    {
        if (!XeEvents.Takes(this, XeEventKind.BlockedProcessReport))
            return false;
        var threshold = this.BlockedProcessThresholdSeconds;
        if (threshold <= 0)
            return false;
        if (asOfTicks - waiter.WaitStartedTicks < (waiter.BlockedProcessReports + 1) * 1000L * threshold)
            return false;
        waiter.BlockedProcessReports++;
        return true;
    }

    /// <summary>Fires a <c>blocked_process_report</c> about <paramref name="waiter"/>'s wait, from its own thread.</summary>
    internal void ReportBlockedProcess(SessionToken waiter, LockResource resource, LockMode mode) =>
        BlockedProcessReport.Report(this, waiter, resource, mode);
}
