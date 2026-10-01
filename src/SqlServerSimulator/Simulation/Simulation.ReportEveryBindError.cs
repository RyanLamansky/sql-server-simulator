using System.Runtime.ExceptionServices;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Real's binder reports every error a statement carries — each unbindable
    /// reference once per occurrence, in its own clause order, at its own line —
    /// where the parse here stops at the first. When <paramref name="first"/>
    /// is one of those, the statement is read again from
    /// <paramref name="start"/> with a <see cref="BindErrorReport"/> installed
    /// (see <see cref="ReadForBindErrors"/>), and the whole report replaces the
    /// lone error. Answers the error to handle and whether the re-read parsed
    /// to the statement's end, where the parser is left.
    /// </summary>
    /// <remarks>
    /// Only a failing statement pays for this: the parse that succeeds never
    /// installs a report, so its recording sites cost a null check. A re-read
    /// that gathers nothing — the miss surfaced while the statement ran,
    /// through a path skip mode doesn't reach — hands <paramref name="first"/>
    /// back unchanged, with the parser where it failed.
    /// </remarks>
    private (SimulatedSqlException Error, bool ResumedAtStatementEnd) ReportEveryBindError(
        BatchContext batch, SimulatedSqlException first, ParserContext.Checkpoint start, bool requireSemicolonBeforeCte, bool atBatchStart)
    {
        first.BindReportSettled = true;
        if (batch.BindErrors is not null
            || !BindErrorReport.StartsReport(first)
            || !ReportsEveryBindError(batch.Parser, start)
            || (batch.IsSkipping && (IsDeferrableNameResolutionError(first) || (IsBinderError(first) && batch.CurrentStatement.BindsDeferredSource))))
        {
            return (first, false);
        }

        var parser = batch.Parser;
        var failedAt = parser.SaveCheckpoint();
        var inlinedCallsBefore = batch.InlinedCalls?.Calls.Count ?? 0;
        var (report, stopper, completed) = ReadForBindErrors(batch, start, () =>
        {
            foreach (var _ in this.DispatchOneStatementCore(batch, requireSemicolonBeforeCte, atBatchStart))
            {
                // Skip mode yields nothing; the enumeration drives the parse.
            }
        });
        batch.InlinedCalls?.DropBehindErrors(report, inlinedCallsBefore);

        // The re-read gets at least as far as the first read did, so even an
        // unfinished one leaves the recovery scan less of the statement.
        var end = parser.SaveCheckpoint();
        var parameterized = batch.ErrorProcedureName.Length == 0
            && batch.ProcFrame is not { IsDynamicSql: false }
            && BindErrorReport.IsSimplyParameterizable(StatementTokens(parser, start, completed ? end.Token?.StartIndex : null));
        parser.RestoreCheckpoint(end);

        if (report.Build(stopper, start.Token!.LineNumber, parameterized) is not { } whole)
        {
            parser.RestoreCheckpoint(failedAt);
            return (first, false);
        }
        return (whole, completed);
    }

    /// <summary>
    /// Runs <paramref name="parse"/> — a module body's own query, read outside
    /// the dispatch loop — and, when it fails with an error that starts a
    /// report, reads it again for the whole report, as
    /// <see cref="ReportEveryBindError"/> does for a statement. Lines report
    /// relative to the body's text, shifted by <paramref name="lineOffset"/>.
    /// </summary>
    internal static T ReportingEveryBindError<T>(BatchContext batch, int lineOffset, Func<T> parse)
    {
        var start = batch.Parser.SaveCheckpoint();
        try
        {
            return parse();
        }
        catch (SimulatedSqlException first) when (!first.BindReportSettled && batch.BindErrors is null && BindErrorReport.StartsReport(first) && start.Token is not null)
        {
            first.BindReportSettled = true;
            var (report, stopper, _) = ReadForBindErrors(batch, start, () => _ = parse());
            if (report.Build(stopper, start.Token.LineNumber + lineOffset, parameterized: false, lineOffset) is { } whole)
                throw whole;
            ExceptionDispatchInfo.Throw(first);
            throw;
        }
    }

    /// <summary>
    /// Reads the statement at <paramref name="start"/> again through
    /// <paramref name="read"/> in skip mode with a fresh
    /// <see cref="BindErrorReport"/> installed, whose recording sites carry on
    /// past a miss. Answers the report, the error that ended the read early if
    /// one did, and whether it reached the statement's end.
    /// </summary>
    private static (BindErrorReport Report, SimulatedSqlException? Stopper, bool Completed) ReadForBindErrors(BatchContext batch, ParserContext.Checkpoint start, Action read)
    {
        var report = new BindErrorReport(start.Token!.command);
        var savedSkip = batch.SkipModeFlag;
        SimulatedSqlException? stopper = null;
        var completed = false;
        batch.Parser.RestoreCheckpoint(start);
        batch.BindErrors = report;
        batch.SkipModeFlag = true;
        try
        {
            read();
            completed = true;
        }
        catch (SimulatedSqlException error)
        {
            stopper = error;
        }
        catch (NotSupportedException)
        {
            // A gap the first read didn't reach ends the re-read; what it
            // gathered stands.
        }
        finally
        {
            batch.BindErrors = null;
            batch.SkipModeFlag = savedSkip;
        }
        return (report, stopper, completed);
    }

    /// <summary>
    /// Whether the statement at <paramref name="start"/> is one whose re-read
    /// stays within itself: a query or DML statement, a <c>SET</c> or
    /// <c>RETURN</c> reading one, an <c>IF</c> / <c>WHILE</c> condition, or a
    /// <c>CREATE</c> / <c>ALTER</c> of a view or function, whose body binds on
    /// the statement's own batch. Peeks without moving the cursor.
    /// </summary>
    private static bool ReportsEveryBindError(ParserContext parser, ParserContext.Checkpoint start)
    {
        switch (start.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge or Keyword.With or Keyword.Set or Keyword.Return or Keyword.If or Keyword.While }:
            case Operator { Character: '(' }:
                return true;
            case ReservedKeyword { Keyword: Keyword.Create or Keyword.Alter }:
                var resume = parser.SaveCheckpoint();
                parser.RestoreCheckpoint(start);
                try
                {
                    var next = parser.GetNextOptional();
                    if (next is ReservedKeyword { Keyword: Keyword.Or })
                        next = parser.GetNextOptional() is null ? null : parser.GetNextOptional();
                    return next is ReservedKeyword { Keyword: Keyword.View or Keyword.Function };
                }
                finally
                {
                    parser.RestoreCheckpoint(resume);
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// The tokens from <paramref name="start"/> up to <paramref name="endIndex"/>,
    /// or, where the statement's end is unknown, to the next statement
    /// boundary outside parentheses. Leaves the cursor wherever it stopped.
    /// </summary>
    private static List<Token> StatementTokens(ParserContext parser, ParserContext.Checkpoint start, int? endIndex)
    {
        parser.RestoreCheckpoint(start);
        var tokens = new List<Token>();
        var depth = 0;
        while (parser.Token is { } token && (endIndex is not { } end || token.StartIndex < end))
        {
            if (endIndex is null && tokens.Count > 0 && depth == 0
                && IsStatementBoundary(token) && token is not ReservedKeyword { Keyword: Keyword.Select or Keyword.With or Keyword.Set })
            {
                break;
            }
            depth += token switch
            {
                Operator { Character: '(' } => 1,
                Operator { Character: ')' } => -1,
                _ => 0,
            };
            tokens.Add(token);
            if (!parser.MoveNext())
                break;
        }
        return tokens;
    }
}
