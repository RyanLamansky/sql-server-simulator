using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

// The batch compile walk's parse of a statement as the run would parse it,
// which the run replays rather than parsing the statement a second time — see
// docs/claude/plan-cache.md#the-compile-walk-against-the-run.
partial class Simulation
{
    /// <summary>
    /// Parses the <c>SELECT</c> statement at the compile walk's cursor as the
    /// run would — the walk's skip mode and create-time binding set aside, the
    /// locks, permission checks and statistics the parse would take only
    /// logged (<see cref="BatchContext.ParsesForRun"/>) — and files the parse
    /// as the statement's plan on the running batch (<see cref="BatchContext.WalkPlans"/>),
    /// which the run replays where it would have parsed the statement again.
    /// Answers the parsed query, or null, the cursor back at
    /// <paramref name="start"/>, for the walk to parse the statement its own
    /// way: a statement whose parse could send what a compile sends — a hint,
    /// a user function, a temporary table, a sequence — or act on the server,
    /// one under settings a replay wouldn't run under, and one the parse
    /// refuses, which the walk's own parse reports as it does.
    /// </summary>
    private Selection? TryParseQueryForRun(BatchContext walk, ParserContext.Checkpoint start)
    {
        var context = walk.Parser;
        if (walk.CompiledFor is not { PlanCacheKey: { } key, CachedPlanOrigin: CachedPlanOrigin.ClientBatch } running
            || !ParsesForRunUnder(walk, key)
            || !ReadsAsPlainQuery(context.MemoizedTokensAhead()))
        {
            return null;
        }

        var connection = walk.Connection;
        var skips = walk.SkipModeFlag;
        var createTime = walk.CreateTimeBinding;
        var compiling = walk.CompilingForRun;
        var gathered = walk.CreateTimeBindErrors;
        var scoped = walk.HasSessionScopedReference;
        var schemaVersion = Volatile.Read(ref this.SchemaVersion);
        var database = context.CurrentDatabase;
#if DEBUG
        var before = RunParseAudit.Snapshot(walk);
#endif
        var parserState = context.SaveStatementState();
        var heldLocks = walk.StatementSchemaLocks.Count;
        walk.SkipModeFlag = false;
        walk.CreateTimeBinding = false;
        walk.CompilingForRun = false;
        walk.CreateTimeBindErrors = null;
        walk.HasSessionScopedReference = false;
        walk.DeferredStatistics = null;
        walk.ParsesForRun = true;
        connection.PendingMessages.RefusesMessages = true;
        var recording = BeginStatementRecording(walk);
        recording.Statement = start;
        var taken = false;
        try
        {
            var selection = ParseSelectStatement(context, browse: false);
            // A view's body binds in a batch of its own, which hands a join
            // hint's warning on for the compile to send.
            if (context.ForBrowseSeen || context.JoinOrderEnforced || selection.IntoTarget is not null
                || !recording.TakeParse(context, walk.ReplayLockLog, walk.CurrentStatement)
                || Volatile.Read(ref this.SchemaVersion) != schemaVersion)
            {
                Abandon(walk, start, parserState, heldLocks);
                return null;
            }
            recording.Query = selection;
            (running.WalkPlans ??= [])[start.MemoPosition] = new StatementPlanEntry(recording, schemaVersion, database);
            if (walk.DeferredStatistics is { } statistics)
                (running.RunParseStatistics ??= [])[start.MemoPosition] = statistics;
            taken = true;
            return selection;
        }
        catch (Exception refused) when (refused is SimulatedSqlException or RunParseDeclinedException or NotSupportedException)
        {
            Abandon(walk, start, parserState, heldLocks);
            return null;
        }
        finally
        {
            connection.PendingMessages.RefusesMessages = false;
            walk.ParsesForRun = false;
            walk.ReplayLockLog = null;
            walk.DeferredStatistics = null;
            walk.SkipModeFlag = skips;
            walk.CreateTimeBinding = createTime;
            walk.CompilingForRun = compiling;
            walk.CreateTimeBindErrors = gathered;
            walk.HasSessionScopedReference = scoped || (taken && walk.HasSessionScopedReference);
#if DEBUG
            EndPrincipalWatch(recording);
            if (taken)
                PlanCacheCaptureAudit.VerifyPrincipalIndependent(recording.PrincipalRead, key.CommandText);
            else
                RunParseAudit.Verify(walk, before);
#endif
        }
    }

    /// <summary>
    /// Puts the compile walk back where it stood at <paramref name="start"/>
    /// for its own parse of the statement, which the abandoned parse for the
    /// run may have left partway: the parser's state, the statement's frame,
    /// and the Sch-S locks taken past the <paramref name="heldLocks"/> it held,
    /// which the walk's own parse takes again.
    /// </summary>
    private static void Abandon(BatchContext walk, ParserContext.Checkpoint start, in ParserContext.StatementState state, int heldLocks)
    {
        walk.Parser.RestoreStatementState(state, start);
        var frame = walk.CurrentStatement;
        var serial = frame.DispatchSerial;
        frame.BeginDispatch();
        frame.DispatchSerial = serial;
        walk.ReleaseStatementSchemaStabilitySince(heldLocks);
    }

    /// <summary>
    /// Whether the compile walk, where it stands, parses as the run whose plan
    /// cache it fills would: the settings the cache key took and the ones the
    /// cache stays out of as <see cref="MayCacheStatementPlan"/> judges them,
    /// with nothing the walk's own parse settles beside binding — no binder
    /// error gathered yet, which fails the batch anyway, no <c>FIPS_FLAGGER</c>
    /// and no security policy, whose predicates the walk binds with the batch.
    /// </summary>
    private static bool ParsesForRunUnder(BatchContext walk, PlanCacheKey key)
    {
        var connection = walk.Connection;
        var context = walk.Parser;
        return walk.CreateTimeBindErrors is not { Count: > 0 }
            && !walk.BatchAborted
            && walk.BindErrors is null
            && walk.FunctionBodyShape is null
            && walk.DependencySink is null
            && walk.ProcFrame is null
            && walk.TriggerFrame is null
            && context.CteBindings is null
            && connection.FipsFlagger == FipsLevel.Off
            && !connection.Simulation.DeclaresSecurityPolicies
            && connection.SessionIsolationLevel == System.Data.IsolationLevel.ReadCommitted
            && !connection.ImplicitTransactions && !connection.NoExec && !connection.ParseOnly && !connection.FmtOnly
            && !connection.StatisticsIo && !connection.StatisticsTime && !connection.NoBrowseTable
            && key.QuotedIdentifiers == context.QuotedIdentifiers
            && key.DateFormat == connection.DateFormat
            && key.AnsiNulls == connection.AnsiNulls
            && key.ConcatNullYieldsNull == connection.ConcatNullYieldsNull
            && connection.CurrentDatabase is { } database
            && string.Equals(key.DatabaseName, database.Name, StringComparison.Ordinal)
            && string.Equals(key.DefaultSchemaName, walk.DefaultSchemaName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the statement <paramref name="tokens"/> open with — a
    /// <c>SELECT</c>, ending at a separator, a keyword starting another
    /// statement or the text's end — reads only tables and views by one- or
    /// two-part names in its <c>FROM</c> clause through the clauses of a plain
    /// query, calling no function but a built-in one: nothing whose parse
    /// sends what a compile sends or touches what belongs to the session.
    /// </summary>
    private static bool ReadsAsPlainQuery(ReadOnlySpan<Token> tokens)
    {
        if (tokens is not [ReservedKeyword { Keyword: Keyword.Select }, ..])
            return false;
        var depth = 0;
        var cases = 0;
        var readsFrom = false;
        Token? previous = null;
        for (var i = 0; i < tokens.Length; previous = tokens[i++])
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    // A name called in a FROM clause is a rowset function, and
                    // a dotted name called anywhere a user function or a method.
                    if (previous is Name && (IsFromSourceLead(tokens, i - 1) || (i >= 2 && tokens[i - 2] is Operator { Character: '.' })))
                        return false;
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    if (--depth < 0)
                        return false;
                    break;
                case Operator { Character: ';' } when depth == 0:
                    return readsFrom;
                case Operator { Character: '.' } when previous is Operator { Character: '.' } || (i >= 2 && tokens[i - 2] is Operator { Character: '.' }):
                    // A third name part: another database's, or a server's.
                    return false;
                case ReservedKeyword { Keyword: var keyword }:
                    // A join hint — HASH, LOOP, MERGE, REMOTE between the join's
                    // type and JOIN — has a compile send its warning.
                    if (keyword switch
                    {
                        Keyword.Join => previous is UnquotedString && i >= 2 && IsJoinType(tokens[i - 2]),
                        Keyword.Merge => IsJoinType(previous),
                        _ => false,
                    })
                    {
                        return false;
                    }
                    if (depth == 0 && cases == 0 && i > 0 && EndsQuery(keyword, previous))
                        return readsFrom;
                    if (!IsPlainQueryKeyword(keyword))
                        return false;
                    if (keyword == Keyword.Case)
                        cases++;
                    else if (keyword == Keyword.End)
                        cases--;
                    else if (keyword == Keyword.From && depth == 0)
                        readsFrom = true;
                    break;
                case AtPrefixedString when IsFromSourceLead(tokens, i):
                    return false;
                case Name name when name.Value.StartsWith('#') || name.Value.StartsWith('$'):
                    return false;
                case UnterminatedToken:
                    return false;
            }
        }
        return readsFrom;
    }

    /// <summary>Whether <paramref name="token"/> is a join's type, which a join hint follows.</summary>
    private static bool IsJoinType(Token? token) =>
        token is ReservedKeyword { Keyword: Keyword.Inner or Keyword.Left or Keyword.Right or Keyword.Full or Keyword.Outer };

    /// <summary>Whether the token at <paramref name="index"/> names a FROM source: it follows <c>FROM</c>, <c>JOIN</c> or <c>APPLY</c>.</summary>
    private static bool IsFromSourceLead(ReadOnlySpan<Token> tokens, int index) =>
        index > 0 && tokens[index - 1] switch
        {
            ReservedKeyword { Keyword: Keyword.From or Keyword.Join } => true,
            UnquotedString word => word.Span.Equals("APPLY", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>
    /// Whether <paramref name="keyword"/>, met outside parentheses and any
    /// <c>CASE</c> after <paramref name="previous"/>, ends the query: a
    /// <c>SELECT</c> no set operator leads, an <c>ELSE</c> or <c>END</c> of the
    /// statement holding it, a <c>FETCH</c> no <c>ROWS</c> leads, and a keyword
    /// only a statement begins with. Any other keyword continues it.
    /// </summary>
    private static bool EndsQuery(Keyword keyword, Token? previous) => keyword switch
    {
        Keyword.Select => previous is not ReservedKeyword { Keyword: Keyword.Union or Keyword.All or Keyword.Except or Keyword.Intersect },
        Keyword.Fetch => previous is not UnquotedString,
        Keyword.Alter or Keyword.Backup or Keyword.Begin or Keyword.Break or Keyword.Bulk or Keyword.Checkpoint or Keyword.Close
            or Keyword.Commit or Keyword.Continue or Keyword.Create or Keyword.Dbcc or Keyword.Deallocate or Keyword.Declare
            or Keyword.Delete or Keyword.Deny or Keyword.Drop or Keyword.Dump or Keyword.Else or Keyword.End or Keyword.Exec
            or Keyword.Execute or Keyword.Goto or Keyword.Grant or Keyword.If or Keyword.Insert or Keyword.Kill or Keyword.Load
            or Keyword.Merge or Keyword.Open or Keyword.Print or Keyword.RaisError or Keyword.ReadText or Keyword.Reconfigure
            or Keyword.Restore or Keyword.Return or Keyword.Revert or Keyword.Revoke or Keyword.Rollback or Keyword.Save
            or Keyword.Set or Keyword.SetUser or Keyword.Shutdown or Keyword.Truncate or Keyword.Update or Keyword.UpdateText
            or Keyword.Use or Keyword.WaitFor or Keyword.While or Keyword.WriteText => true,
        _ => false,
    };

    /// <summary>The reserved keywords a plain query's clauses are made of.</summary>
    private static bool IsPlainQueryKeyword(Keyword keyword) => keyword is
        Keyword.All or Keyword.And or Keyword.Any or Keyword.As or Keyword.Asc or Keyword.Between or Keyword.By
        or Keyword.Case or Keyword.Coalesce or Keyword.Collate or Keyword.Convert or Keyword.Cross
        or Keyword.Current_Date or Keyword.Current_Time or Keyword.Current_Timestamp or Keyword.Current_User
        or Keyword.Desc or Keyword.Distinct or Keyword.Else or Keyword.End or Keyword.Escape or Keyword.Except
        or Keyword.Exists or Keyword.Fetch or Keyword.From or Keyword.Full or Keyword.Group or Keyword.Having
        or Keyword.In or Keyword.Inner or Keyword.Intersect or Keyword.Is or Keyword.Join or Keyword.Left
        or Keyword.Like or Keyword.Not or Keyword.Null or Keyword.NullIf or Keyword.On or Keyword.Or
        or Keyword.Order or Keyword.Outer or Keyword.Over or Keyword.Percent or Keyword.Right or Keyword.Select
        or Keyword.Session_User or Keyword.Some or Keyword.System_user or Keyword.Then or Keyword.Top
        or Keyword.Try_Convert or Keyword.Union or Keyword.User or Keyword.When or Keyword.Where;
}
