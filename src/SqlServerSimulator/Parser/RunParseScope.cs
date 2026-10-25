using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// What the batch compile walk sets aside to parse a statement as the run
/// would (<see cref="BatchContext.ParsesForRun"/>) — its skip mode, its
/// create-time binding, the binder errors it gathers and its session-scoped
/// mark — which <see cref="Leave"/> puts back: as a query's parse ends, or at
/// a write's split point (<see cref="BatchContext.WriteParseForRun"/>), so the
/// write's execution half runs skipped, as after the walk's own parse.
/// </summary>
internal sealed class RunParseScope
{
    private readonly bool skips, createTime, compiling, scoped;
    private readonly List<SimulatedSqlException>? gathered;

    /// <summary>Whether <see cref="Leave"/> has put the walk's state back.</summary>
    public bool Left;

    /// <summary>The statistics the parse deferred (<see cref="BatchContext.DeferredStatistics"/>), taken as it left.</summary>
    public List<(HeapTable Table, int Ordinal)>? Statistics;

    public RunParseScope(BatchContext walk)
    {
        (this.skips, this.createTime, this.compiling, this.scoped, this.gathered) =
            (walk.SkipModeFlag, walk.CreateTimeBinding, walk.CompilingForRun, walk.HasSessionScopedReference, walk.CreateTimeBindErrors);
        walk.SkipModeFlag = false;
        walk.CreateTimeBinding = false;
        walk.CompilingForRun = false;
        walk.CreateTimeBindErrors = null;
        walk.HasSessionScopedReference = false;
        walk.DeferredStatistics = null;
        walk.ParsesForRun = true;
        walk.Connection.PendingMessages.RefusesMessages = true;
    }

    /// <summary>
    /// Puts the walk's state back, once; a session-scoped reference the parse
    /// met counts for nothing, since a parse that met one is never taken.
    /// </summary>
    public void Leave(BatchContext walk)
    {
        if (this.Left)
            return;
        this.Left = true;
        this.Statistics = walk.DeferredStatistics;
        walk.Connection.PendingMessages.RefusesMessages = false;
        walk.ParsesForRun = false;
        walk.ReplayLockLog = null;
        walk.DeferredStatistics = null;
        walk.SkipModeFlag = this.skips;
        walk.CreateTimeBinding = this.createTime;
        walk.CompilingForRun = this.compiling;
        walk.CreateTimeBindErrors = this.gathered;
        walk.HasSessionScopedReference = this.scoped;
    }
}
