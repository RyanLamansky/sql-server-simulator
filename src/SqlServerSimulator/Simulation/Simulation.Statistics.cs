using System.Diagnostics;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

// SET STATISTICS IO / TIME: the Msg 3615 table lines, the Msg 3612 execution
// times and the Msg 3613 compile times, and where each lands among a batch's
// results — see docs/claude/session-options.md.
partial class Simulation
{
    /// <summary>
    /// Where a statement's <c>STATISTICS TIME</c> clock started: the
    /// <see cref="Stopwatch"/> timestamp and the session's time spent waiting
    /// by then (<see cref="SimulatedDbConnection.WaitedTicks"/>).
    /// </summary>
    private readonly struct StatementClock(long startedAt, long waitedBefore)
    {
        public readonly long StartedAt = startedAt;
        public readonly long WaitedBefore = waitedBefore;

        public static StatementClock Start(SimulatedDbConnection connection) => new(Stopwatch.GetTimestamp(), connection.WaitedTicks);

        /// <summary>
        /// The CPU and elapsed milliseconds since the start, each truncated as
        /// real's are. The simulator runs a statement on one thread, so its
        /// CPU time is its elapsed time less what it spent in <c>WAITFOR</c>.
        /// </summary>
        public (long Cpu, long Elapsed) Milliseconds(SimulatedDbConnection connection)
        {
            var elapsed = Stopwatch.GetElapsedTime(this.StartedAt).Ticks;
            var cpu = Math.Max(0, elapsed - (connection.WaitedTicks - this.WaitedBefore));
            return (cpu / TimeSpan.TicksPerMillisecond, elapsed / TimeSpan.TicksPerMillisecond);
        }
    }

    /// <summary>
    /// Whether <paramref name="batch"/>'s statements report their own
    /// statistics: not while it walks in skip mode, and not in a function or
    /// view body, which inlines into the statement that called it.
    /// </summary>
    private static bool ReportsStatistics(BatchContext batch) => !batch.IsSkipping && !batch.SuppressDiagnosticsResolution;

    /// <summary>
    /// A statement's Msg 3612, after its time since <paramref name="clock"/>,
    /// attributed to <paramref name="moduleName"/> for a module's <c>CREATE</c>
    /// or <c>ALTER</c>.
    /// </summary>
    private static SimulatedError ExecutionTimes(BatchContext batch, StatementClock clock, string? moduleName = null)
    {
        var (cpu, elapsed) = clock.Milliseconds(batch.Connection);
        var message = SimulatedSqlException.ExecutionTimesMessage(batch, cpu, elapsed);
        if (moduleName is not null)
            message.Procedure = moduleName;
        return message;
    }

    /// <summary>
    /// What a statement that completed sends for <c>SET STATISTICS IO</c> /
    /// <c>TIME</c>: a Msg 3615 per table it read, then its Msg 3612. A query's
    /// go after its DONE, anything else's before it, as real sends them
    /// (probed 2026-09-28 against SQL Server 2025); a statement that sent no
    /// outcome of its own has no DONE to precede. A write whose <c>OUTPUT</c>
    /// returned rows sends them after its rows' DONE and closes with a second
    /// DONE counting the rows it wrote, as real does only while reporting.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> StatisticsReport(
        BatchContext batch, IoStatistics? io, List<SimulatedStatementOutcome> outcomes, bool timed, StatementClock clock, bool isCall, string? moduleName)
    {
        if ((io is null || io.IsEmpty) && !timed)
            yield break;
        var ownOutcome = false;
        var query = false;
        foreach (var outcome in outcomes)
        {
            ownOutcome |= outcome is SimulatedQueryResult or SimulatedNonQuery;
            query |= outcome is SimulatedQueryResult;
        }
        var followsRows = ownOutcome && !isCall && !query;
        if (io is not null)
        {
            foreach (var line in io.Lines())
                yield return new SimulatedInfoOutcome(SimulatedSqlException.TableIoMessage(batch, line), followsRows);
        }
        if (timed)
            yield return new SimulatedInfoOutcome(ExecutionTimes(batch, clock, moduleName), followsRows);
        if (query && batch.CurrentStatement.WritesRows)
        {
            yield return new SimulatedNonQuery(batch.Connection.LastStatementRowCount)
            {
                DoneKind = batch.CurrentStatement.DoneKind,
                InModule = batch.ProcFrame is not null || batch.TriggerFrame is not null,
                CountSuppressed = batch.Connection.NoCount,
                TransactionEventMark = batch.Connection.TransactionEventsRecorded,
            };
        }
    }

    /// <summary>
    /// Queues what the statement running has read so far, and forgets it: an
    /// <c>IF</c> or <c>WHILE</c> condition reports before its body, and a DML
    /// statement before the triggers it fires (probed 2026-09-28 against SQL
    /// Server 2025). <paramref name="line"/> overrides the statement's line —
    /// a <c>WHILE</c>'s, whose body's statements have since overwritten it.
    /// </summary>
    internal static void QueueIoReport(BatchContext batch, int? line = null)
    {
        if (!batch.Connection.StatisticsIo || batch.Connection.StatementIo is not { IsEmpty: false } io)
            return;
        foreach (var text in io.Lines())
        {
            var message = SimulatedSqlException.TableIoMessage(batch, text);
            if (line is { } overridden)
                message.LineNumber = overridden + batch.LineOffset;
            batch.Connection.PendingMessages.Enqueue(message);
        }
        io.Clear();
    }

    /// <summary>
    /// Queues an <c>IF</c> / <c>WHILE</c> condition's statistics, which it
    /// sends as a statement of its own ahead of the branch it chose.
    /// </summary>
    private static void QueueConditionStatistics(BatchContext batch, StatementClock? clock, int line)
    {
        if (!ReportsStatistics(batch))
            return;
        QueueIoReport(batch, line);
        if (clock is { } started && batch.Connection.StatisticsTime)
        {
            var message = ExecutionTimes(batch, started);
            message.LineNumber = line + batch.LineOffset;
            batch.Connection.PendingMessages.Enqueue(message);
        }
    }

    /// <summary>
    /// The Msg 3612 a structural statement sends — <c>BEGIN TRY</c>, a
    /// <c>CATCH</c> entered, <c>END CATCH</c> — which close with a DONE of
    /// their own and so report as statements; null when not timing.
    /// </summary>
    private static SimulatedInfoOutcome? StructuralExecutionTimes(BatchContext batch, int? line = null)
    {
        if (!batch.Connection.StatisticsTime || !ReportsStatistics(batch))
            return null;
        var message = ExecutionTimes(batch, StatementClock.Start(batch.Connection));
        if (line is { } own)
            message.LineNumber = own + batch.LineOffset;
        return new SimulatedInfoOutcome(message);
    }

    /// <summary>
    /// A compile's Msg 3613 at <paramref name="line"/> — the line of the last
    /// statement the compile read, which is where real reports it — in
    /// <paramref name="procedure"/> when it compiled a module.
    /// </summary>
    private static SimulatedError CompileTime(BatchContext batch, StatementClock? clock, int line, string procedure)
    {
        var (cpu, elapsed) = clock is { } started ? started.Milliseconds(batch.Connection) : (0, 0);
        var message = SimulatedSqlException.ParseAndCompileTimeMessage(batch, cpu, elapsed);
        message.LineNumber = line;
        message.Procedure = procedure;
        return message;
    }

    /// <summary>
    /// Whether real compiles the statement at the cursor through simple
    /// parameterization, which <c>STATISTICS TIME</c> reports as a compile of
    /// its own ahead of it (probed 2026-09-28 against SQL Server 2025). Read
    /// once the statement has run, from <paramref name="start"/> to the
    /// cursor, which it leaves where it was.
    /// </summary>
    private static bool CompilesParameterized(BatchContext batch, ParserContext.Checkpoint start)
    {
        if (start.Token is not ReservedKeyword { Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete }
            || batch.ErrorProcedureName.Length != 0
            || batch.ProcFrame is { IsDynamicSql: false })
        {
            return false;
        }
        var parser = batch.Parser;
        var end = parser.SaveCheckpoint();
        try
        {
            return BindErrorReport.IsSimplyParameterizable(StatementTokens(parser, start, end.Token?.StartIndex ?? int.MaxValue));
        }
        finally
        {
            parser.RestoreCheckpoint(end);
        }
    }

    /// <summary>
    /// The module a batch's leading <c>CREATE</c> / <c>ALTER</c> makes, to
    /// which real attributes the batch's compile; null for any other batch.
    /// </summary>
    private static string? BatchCreatedModuleName(SimulatedDbCommand command)
    {
        var parser = new BatchContext(command, new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer)).Parser;
        try
        {
            return parser.MoveNext() ? CreatedModuleName(parser) : null;
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The name of the procedure, function, trigger or view a statement
    /// creates or alters, to which real attributes the statement's own
    /// statistics; null for any other statement. Leaves the cursor where it
    /// was.
    /// </summary>
    private static string? CreatedModuleName(ParserContext parser)
    {
        if (parser.Token is not ReservedKeyword { Keyword: Keyword.Create or Keyword.Alter })
            return null;
        var start = parser.SaveCheckpoint();
        try
        {
            var kind = parser.GetNextOptional();
            if (kind is ReservedKeyword { Keyword: Keyword.Or })
            {
                _ = parser.GetNextOptional();
                kind = parser.GetNextOptional();
            }
            if (kind is not ReservedKeyword { Keyword: Keyword.Proc or Keyword.Procedure or Keyword.Function or Keyword.Trigger or Keyword.View })
                return null;
            string? leaf = null;
            while (parser.GetNextOptional() is Name name)
            {
                leaf = name.Value;
                if (parser.GetNextOptional() is not Operator { Character: '.' })
                    break;
            }
            return leaf;
        }
        finally
        {
            parser.RestoreCheckpoint(start);
        }
    }
}
