using System.Collections.Concurrent;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Command texts that compiled without error, each with the
    /// <see cref="SchemaVersion"/> it compiled under, so a repeated batch skips
    /// <see cref="CompileBatch"/>. Keyed and capped like the plan cache: a batch
    /// that binds against the same schema under the same key compiles the same
    /// way, except one that resolved a session's <c>#temp</c> table, which isn't
    /// recorded. A hit only ever skips the pass, so an input the key leaves out
    /// can cost a batch its up-front error, never raise one it shouldn't.
    /// </summary>
    private readonly ConcurrentDictionary<PlanCacheKey, long> compiledBatches = new();

    /// <summary>
    /// How many keys <see cref="compiledBatches"/> holds, counted as
    /// <see cref="planCacheCount"/> is.
    /// </summary>
    private int compiledBatchCount;

    /// <summary>
    /// Compiles a batch before any of it runs, as real does, so an error that
    /// real raises while compiling stops the batch before its first statement
    /// rather than where the error sits. The batch's text walks the dispatch
    /// loop on <paramref name="compileBatch"/>, a throwaway context, the way a
    /// module body binds at <c>CREATE</c>: every statement parses and binds,
    /// nothing runs, and a statement naming an object that doesn't exist yet
    /// defers to when it runs. Real reports every binder error the batch holds,
    /// unless its parse phase fails first (a syntax error, an undeclared
    /// variable), which it reports alone (probed 2026-09-24 against SQL Server
    /// 2025).
    /// </summary>
    /// <param name="compileBatch">The throwaway context the walk reads the batch on.</param>
    /// <param name="key">The batch's plan-cache key, under which a compile is remembered; null when it has none.</param>
    /// <param name="inliningFailures">
    /// The non-aborting errors the compile sends ahead of everything the batch
    /// runs when it compiles: one per scalar function call it couldn't inline
    /// (see <see cref="InlinedScalarCalls"/>), in binding order; null when there
    /// are none, or when a compile of the same text sent them before. A batch
    /// that doesn't compile carries them in its report instead, among its
    /// binder errors.
    /// </param>
    /// <param name="sendsOnce">
    /// Whether the failures go out only the first time the text compiles under
    /// the current schema, as a batch's and dynamic SQL's do; a module body's
    /// caller keeps its own compiled plan and decides instead.
    /// </param>
    /// <param name="dynamicKey">
    /// A dynamic batch's key (<see cref="DynamicBatchKey"/>), under which its
    /// compile is remembered as a top-level batch's is under <paramref name="key"/>;
    /// what a compile sends is still settled as for a batch without one.
    /// </param>
    /// <returns>The error or errors that stop the batch, or <see langword="null"/> when it compiled.</returns>
    /// <remarks>
    /// Real keeps compiling the statements after one it defers, and so does the
    /// walk past a write to a missing table, which it reads to its end; a
    /// deferral raised mid-statement stops the walk, because its recovery scan
    /// can't tell where that statement ended, so an error past it surfaces when
    /// its statement runs.
    /// </remarks>
    private SimulatedSqlException? CompileBatch(BatchContext compileBatch, PlanCacheKey? key, out List<SimulatedSqlException>? inliningFailures, bool sendsOnce = true, PlanCacheKey? dynamicKey = null)
    {
        inliningFailures = null;
        var schemaVersion = Volatile.Read(ref this.SchemaVersion);
        var remembered = key ?? dynamicKey;
        if (remembered is { } cached && this.compiledBatches.TryGetValue(cached, out var compiledUnder) && compiledUnder == schemaVersion)
            return null;

        var errors = new List<SimulatedSqlException>();
        var inlined = new InlinedScalarCalls(errors, body: false);
        compileBatch.InlinedCalls = inlined;
        compileBatch.CurrentStatement.UtcNow = DateTime.UtcNow;
        compileBatch.CompilingForRun = true;
        // A USE the walk meets switches the database it binds in.
        var connection = compileBatch.Connection;
        var enteredDatabase = connection.CurrentDatabase;
        try
        {
            if (!this.BindWithoutRunning(compileBatch, errors) || compileBatch.BatchAborted)
                inlined.CompileOnRunFrom(compileBatch.Parser.Token?.StartIndex ?? int.MaxValue);
        }
        catch (SimulatedSqlException parsePhase)
        {
            connection.CurrentDatabase = enteredDatabase;
            return IsRecoverableSyntaxError(parsePhase) && compileBatch.Parser.Token is { } errorToken
                ? this.WithRecoveredSyntaxErrors(compileBatch, parsePhase, errorToken, command =>
                {
                    var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
                    var recovery = compileBatch.ProcFrame is { } frame
                        ? new BatchContext(command, variables, new ProcFrame(frame.ProcedureName, frame.IsDynamicSql))
                        : new BatchContext(command, variables);
                    recovery.CompilingForRun = true;
                    return recovery;
                })
                : parsePhase;
        }
        finally
        {
            connection.CurrentDatabase = enteredDatabase;
        }

        if (SettleSilentCompileEnd(compileBatch, errors) is { } silent)
            return silent;
        var failures = this.InliningFailuresOf(compileBatch, inlined);
        if (errors.Exists(static error => error.PreemptsBinderErrors))
        {
            SimulatedSqlException.DropBinderErrorsBehindDeclarations(errors);
            failures = null;
        }
        if (errors.Count > 0)
        {
            // Each failure goes where its call bound among the binder errors.
            if (failures is not null)
            {
                for (var i = failures.Count - 1; i >= 0; i--)
                    errors.Insert(failures[i].Position, failures[i].Failure);
            }
            var report = SimulatedSqlException.Aggregate(errors);
            report.CatchReadsFirstEntry = true;
            return report;
        }
        if (compileBatch.DeferredOptimizerError is { } optimizerError && !compileBatch.WalkMetDdl)
            return optimizerError;

        compileBatch.StatementsCompiledOnRun = inlined.CompiledOnRun;
        compileBatch.JoinOrderWarnedStatements ??= [];
        // A text compiled before under the current schema — dynamic SQL,
        // whose compile no key remembers — runs on real's cached plan, so
        // what its compile sends goes out the first time only.
        bool? firstUnderSchema = null;
        if (failures is not null && (!sendsOnce || inlined.RecompilesEveryRun || (firstUnderSchema ??= this.SendsInliningFailures(enteredDatabase, compileBatch.Parser.Command.CommandText))))
        {
            inliningFailures = new List<SimulatedSqlException>(failures.Count);
            foreach (var (_, failure) in failures)
                inliningFailures.Add(failure);
        }

        foreach (var message in compileBatch.CompileMessages ?? [])
        {
            if (message.Number != SimulatedSqlException.JoinOrderEnforcedMessageNumber || key is not null || !sendsOnce
                || (firstUnderSchema ??= this.SendsInliningFailures(enteredDatabase, compileBatch.Parser.Command.CommandText)))
            {
                connection.PendingMessages.Enqueue(message);
            }
        }
        if (remembered is { } compiled && !compileBatch.ResolvedTempTable && !inlined.RecompilesEveryRun)
        {
            if (this.compiledBatches.ContainsKey(compiled))
                this.compiledBatches[compiled] = schemaVersion;
            else if (Volatile.Read(ref this.compiledBatchCount) < PlanCacheCapacity && this.compiledBatches.TryAdd(compiled, schemaVersion))
                _ = Interlocked.Increment(ref this.compiledBatchCount);
        }
        return null;
    }

    /// <summary>
    /// What a procedure's or dynamic batch's compile ending silently
    /// (<see cref="SimulatedSqlException.EndsCompileSilently"/>) does to its
    /// caller: null when the call just ends, its scope closed with no status
    /// and <c>@@ERROR</c> as it was, and the caller goes on; otherwise, with a
    /// <c>TRY</c> open anywhere around it or <c>XACT_ABORT</c> on, the error
    /// that ends the caller's whole batch (probed 2026-10-08 against SQL Server
    /// 2025).
    /// </summary>
    private static SimulatedSqlException? CallerEndedBySilentCompile(SimulatedDbConnection connection) =>
        connection.XactAbort || connection.OpenTryFrames > 0
            ? SimulatedSqlException.CalledCompileEndedSilently(connection.XactAbort)
            : null;

    /// <summary>
    /// Whether the scope whose compile ended silently closes ahead of
    /// <paramref name="callerEnded"/>, the error ending its caller's batch: it
    /// does, its DONE clear, under a <c>TRY</c> further out than its caller;
    /// with the <c>TRY</c> in its caller it is the scope the error closes, and
    /// under <c>XACT_ABORT</c> nothing closes (probed 2026-10-08 against
    /// SQL Server 2025). Notes the <c>TRY</c> on the error either way.
    /// </summary>
    private static bool ClosesSilentScope(SimulatedSqlException callerEnded, BatchContext caller)
    {
        if (!callerEnded.ClosesAbandonedScopes)
            return false;
        callerEnded.ReachedTryScope = caller.TryFrameDepth > 0;
        return !callerEnded.ReachedTryScope;
    }

    /// <summary>
    /// Settles a walk that met <see cref="BatchContext.SilentCompileEnd"/>: the
    /// binder errors gathered ahead of it are the report, those after it are
    /// dropped, and with none ahead of it the failure itself is the walk's
    /// outcome (probed 2026-10-08 against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException? SettleSilentCompileEnd(BatchContext walked, List<SimulatedSqlException> errors)
    {
        if (walked.SilentCompileEnd is not var (failure, before))
            return null;
        errors.RemoveRange(before, errors.Count - before);
        return errors.Count == 0 ? failure : null;
    }

    /// <summary>
    /// A top-level batch whose compile ended silently: under <c>XACT_ABORT</c>
    /// real rolls the transaction back and closes the batch with a DONE
    /// carrying the error bit alone, SqlClient's class-11 Msg 0; otherwise
    /// nothing at all, a null here (probed 2026-10-08 against SQL Server 2025).
    /// </summary>
    private static SimulatedErrorOutcome? EndedSilently(SimulatedDbConnection connection)
    {
        if (!connection.XactAbort)
            return null;
        connection.CurrentTransaction?.EndRollback();
        return new SimulatedErrorOutcome(SimulatedSqlException.CalledCompileEndedSilently(xactAbort: true));
    }

    /// <summary>
    /// The throwaway context <see cref="CompileBatch"/> walks
    /// <paramref name="executing"/>'s text on: the same command, a copy of the
    /// variables, table variables and cursor variables its parameters seeded (so the walk's own
    /// <c>DECLARE</c>s don't collide with the run's), and the same frame —
    /// a trigger body's firing frame included — and error attribution.
    /// </summary>
    private static BatchContext CompileContextFor(BatchContext executing, SimulatedDbCommand command)
    {
        var variables = new Dictionary<string, VariableSlot>(executing.Variables, BatchContext.VariableNameComparer);
        var compile = executing.ProcFrame is { } frame
            ? new BatchContext(command, variables, new ProcFrame(frame.ProcedureName, frame.IsDynamicSql))
            : new BatchContext(command, variables);
        foreach (var (name, table) in executing.TableVariables ?? [])
            compile.SetTableVariable(name, table);
        foreach (var (name, cursor) in executing.CursorVariables ?? [])
            compile.SetCursorVariable(name, cursor);
        compile.LineOffset = executing.LineOffset;
        compile.ErrorProcedureName = executing.ErrorProcedureName;
        compile.ForceTempTableScope = executing.ForceTempTableScope;
        // A trigger body binds inserted / deleted against the firing frame.
        compile.TriggerFrame = executing.TriggerFrame;
        compile.ModuleSchema = executing.ModuleSchema;
        return compile;
    }

    /// <summary>The parse-phase errors real's parser recovers from and parses on past.</summary>
    private static bool IsRecoverableSyntaxError(SimulatedSqlException error) => error.Number is 102 or 111 or 137 or 156 or 178 or 181 or 319 or 1054 or 4145;

    /// <summary>
    /// Whether <paramref name="error"/> is one the grammar's own actions raise
    /// rather than a token the parser can't take — a module <c>CREATE</c> not
    /// first in its batch (Msg 111), a valued <c>RETURN</c> outside a module
    /// (Msg 178), a <c>WITH</c> after an unterminated statement (Msg 319), a
    /// schema-bound body's select-list star or <c>GROUP BY ALL</c> (Msg 1054),
    /// a non-boolean expression where a condition belongs (Msg 4145) — which
    /// recovery reports however few tokens have parsed since the last.
    /// </summary>
    private static bool IsGrammarActionError(SimulatedSqlException error) => error.Number is 111 or 178 or 319 or 1054 or 4145;

    /// <summary>
    /// <paramref name="first"/> followed by the syntax errors real's parser
    /// reports past it. Real recovers the way a yacc parser does: it restarts
    /// at the offending token, discarding tokens that can't begin anything,
    /// and reports a further syntax error only once three tokens have parsed
    /// since the last one — so <c>select 1 +; select 2 +;</c> reports both,
    /// <c>(select 1 a) d NATURAL JOIN (select 1 a) e</c> adds Msg 102 near
    /// <c>e</c>, and a table hint real refuses adds the Msg 319 its
    /// <c>WITH</c> raises when read as a common table expression, which is
    /// never held back (probed 2026-09-28 against SQL Server 2025). Each
    /// restart parses the text from there on with everything before it
    /// blanked out, so positions and lines stay as written.
    /// </summary>
    /// <param name="compileBatch">The batch or module body whose walk raised <paramref name="first"/>.</param>
    /// <param name="first">The error the walk stopped on.</param>
    /// <param name="errorToken">The token the walk's parser stood on when it raised.</param>
    /// <param name="buildRecovery">
    /// Builds the context each restart walks the blanked text on, with the
    /// frame the text runs in — a module body's own, so a restart reads its
    /// <c>RETURN</c> and its parameters as the body does. The variables and
    /// table variables the failed walk declared carry over, as real's parser
    /// keeps them.
    /// </param>
    /// <param name="closingEnd">
    /// The <c>END</c> that closes the text as written, for a function body
    /// captured without it — the token an error at the end of the text names.
    /// </param>
    private SimulatedSqlException WithRecoveredSyntaxErrors(BatchContext compileBatch, SimulatedSqlException first, Parser.Token errorToken, Func<SimulatedDbCommand, BatchContext> buildRecovery, string? closingEnd = null)
    {
        var text = compileBatch.Parser.Command.CommandText;
        var connection = compileBatch.Connection;
        var enteredDatabase = connection.CurrentDatabase;
        var errors = new List<SimulatedSqlException> { first };
        // A grammar action's error names a construct the parser read whole;
        // the parser resumes past the token it stopped at.
        // A Msg 4145 or Msg 137 sends the parser on in place rather than into
        // recovery, so the tokens after it count toward the next report — the
        // 4145's own token among them, since the parser raised it on reading
        // that token, not past it (probed 2026-09-30 against SQL Server 2025).
        var restart = first.Number == 4145 ? errorToken.StartIndex
            : IsGrammarActionError(first) || first.Number == 137 ? errorToken.EndIndex
            : errorToken.StartIndex;
        // Where each error stopped the parser, and where the count of tokens
        // parsed since the last error starts — which a block's END, read as
        // one rather than as an error, doesn't reset.
        List<int> errorOffsets = [errorToken.StartIndex];
        int? countFrom = first.Number is 4145 or 137 ? restart : null;
        // A Msg 4145 raised on a `(` ended the condition before it, so the
        // parser reads on from that `(` as the start of a statement, a query
        // or not: `WHERE [abs](1) IS NULL` reports nothing past the 4145
        // (probed 2026-10-01 against SQL Server 2025).
        var readOnInPlace = first.Number == 4145 && errorToken is Parser.Tokens.Operator { Character: '(' };
        for (var attempts = 0; attempts < 64; attempts++)
        {
            if (!readOnInPlace)
                restart = NextStatementStart(text, restart);
            readOnInPlace = false;
            if (restart >= text.Length)
                break;
            countFrom ??= restart;
            var masked = string.Create(text.Length, (text, restart), static (span, state) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = i >= state.restart || state.text[i] is '\n' or '\r' ? state.text[i] : ' ';
            });
            using var command = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // the batch's own text, blanked in part
            command.CommandText = masked;
#pragma warning restore CA2100
            var recovery = buildRecovery(command);
            if (!ReferenceEquals(recovery.Variables, compileBatch.Variables))
            {
                foreach (var (name, slot) in compileBatch.Variables)
                    _ = recovery.Variables.TryAdd(name, slot);
            }
            foreach (var (name, table) in compileBatch.TableVariables ?? [])
            {
                if (!recovery.HasTableVariable(name))
                    recovery.SetTableVariable(name, table);
            }
            recovery.LineOffset = compileBatch.LineOffset;
            recovery.ErrorProcedureName = compileBatch.ErrorProcedureName;
            // Past a schema-bound body's header, the rest of the batch is still
            // that body, which recovery reads as statements.
            if (compileBatch.Parser.SchemaBoundBody != SchemaBoundBody.None)
                recovery.Parser.SchemaBoundBody = SchemaBoundBody.Statements;
            try
            {
                _ = this.BindWithoutRunning(recovery, []);
                break;
            }
            // An error at the end of the text names the last token.
            catch (SimulatedSqlException next) when (IsRecoverableSyntaxError(next) && (recovery.Parser.Token ?? recovery.Parser.LastToken) is { } at && at.StartIndex >= restart)
            {
                // An END at a statement's start closes a block opened before
                // the restart, which the blanked text no longer shows; real's
                // parser, still inside the block, reads it as the block's end.
                // An END TRY's CATCH block opens there too, which a restart at
                // its BEGIN would read as a CATCH without a TRY.
                if (next.Number == 102 && at is Parser.Tokens.ReservedKeyword { Keyword: Parser.Keyword.End } && OpenBlocksBefore(text, at.StartIndex, errorOffsets) > 0)
                {
                    restart = PastCatchOpening(text, at.EndIndex);
                    continue;
                }
                errorOffsets.Add(at.StartIndex);
                // At the end of the text every token has parsed; a Msg 137's
                // variable has too.
                var atEnd = recovery.Parser.Token is null;
                var parsed = TokensBetween(text, countFrom.Value, atEnd || next.Number == 137 ? at.EndIndex : at.StartIndex);
                if (atEnd && next.Number == 102 && closingEnd is not null)
                {
                    next = SimulatedSqlException.SyntaxErrorNearKeyword(closingEnd);
                    next.ResolveDiagnostics(Parser.Token.LineAt(text, text.Length), compileBatch.LineOffset, compileBatch.ErrorProcedureName);
                }
                countFrom = next.Number switch
                {
                    137 => at.EndIndex,
                    4145 => at.StartIndex,
                    _ => null,
                };
                // A WITH the parser restarts at reads as a common table
                // expression after an unterminated statement, and fails as one.
                var startsWithWith = masked.AsSpan(restart).StartsWith("with", StringComparison.OrdinalIgnoreCase)
                    && (restart + 4 >= masked.Length || (!char.IsLetterOrDigit(masked[restart + 4]) && masked[restart + 4] != '_'));
                var readAsCte = startsWithWith && parsed < 3;
                if (readAsCte)
                {
                    next = SimulatedSqlException.CteRequiresPrecedingSemicolon();
                    next.ResolveDiagnostics(Parser.Token.LineAt(masked, restart), compileBatch.LineOffset, compileBatch.ErrorProcedureName);
                }
                var reported = parsed >= 3 || IsGrammarActionError(next);
                if (reported)
                    errors.Add(next);
                var resume = reported && !readAsCte && next.Number != 137 ? at.StartIndex : at.EndIndex;
                restart = resume > restart ? resume : at.EndIndex;
            }
            catch (SimulatedSqlException next) when (next.Number == 1028)
            {
                // GROUP BY ALL's grouping-set refusal is reported after the
                // syntax errors before it, and ends the parse there.
                errors.Add(next);
                break;
            }
            catch (SimulatedSqlException)
            {
                break;
            }
            catch (NotSupportedException)
            {
                break;
            }
            finally
            {
                connection.CurrentDatabase = enteredDatabase;
            }
        }
        return errors.Count == 1 ? first : SimulatedSqlException.Aggregate(errors);
    }

    /// <summary>
    /// The offset of the first token from <paramref name="from"/> on that can
    /// begin a statement — a keyword, <c>(</c>, <c>;</c> or <c>THROW</c> —
    /// the recovering parser discarding names and operators on the way.
    /// </summary>
    private static int NextStatementStart(string text, int from)
    {
        var index = from;
        try
        {
            while (Parser.Tokenizer.NextToken(text, ref index, Collation.Baseline) is { } token)
            {
                switch (token)
                {
                    case Parser.Tokens.Whitespace or Parser.Tokens.Comment:
                        continue;
                    case Parser.Tokens.ReservedKeyword or Parser.Tokens.Operator { Character: ';' }:
                    case Parser.Tokens.UnquotedString { Value: var word } when word.Equals("THROW", StringComparison.OrdinalIgnoreCase):
                    case Parser.Tokens.Operator { Character: '(' } when OpensQuery(text, index):
                        return token.StartIndex;
                }
            }
        }
        catch (SimulatedSqlException)
        {
        }
        return text.Length;
    }

    /// <summary>
    /// Whether the <c>(</c> just read opens a query — a statement can begin
    /// with one only as <c>(SELECT …)</c>, however deeply nested.
    /// </summary>
    private static bool OpensQuery(string text, int index)
    {
        while (Parser.Tokenizer.NextToken(text, ref index, Collation.Baseline) is { } token)
        {
            switch (token)
            {
                case Parser.Tokens.Whitespace or Parser.Tokens.Comment or Parser.Tokens.Operator { Character: '(' }:
                    continue;
                default:
                    return token is Parser.Tokens.ReservedKeyword { Keyword: Parser.Keyword.Select };
            }
        }
        return false;
    }

    /// <summary>
    /// How many <c>BEGIN … END</c> blocks — <c>BEGIN TRY</c> and
    /// <c>BEGIN CATCH</c> among them, a <c>BEGIN TRAN</c> not — are open at
    /// <paramref name="offset"/> of <paramref name="text"/>. A <c>CASE</c>'s
    /// <c>END</c> closes the <c>CASE</c>, except one still open where a syntax
    /// error stopped the parser, which recovery abandons (probed 2026-09-30
    /// against SQL Server 2025).
    /// </summary>
    /// <param name="text">The text being recovered.</param>
    /// <param name="offset">Where the count is taken.</param>
    /// <param name="errorOffsets">Where each error so far stopped the parser, in order.</param>
    private static int OpenBlocksBefore(string text, int offset, List<int> errorOffsets)
    {
        var open = new Stack<bool>();
        var (index, nextError) = (0, 0);
        var afterBegin = false;
        try
        {
            while (index < offset && Parser.Tokenizer.NextToken(text, ref index, Collation.Baseline) is { } token && token.StartIndex < offset)
            {
                if (token is Parser.Tokens.Whitespace or Parser.Tokens.Comment)
                    continue;
                for (; nextError < errorOffsets.Count && errorOffsets[nextError] <= token.StartIndex; nextError++)
                {
                    while (open.TryPeek(out var isBlock) && !isBlock)
                        _ = open.Pop();
                }
                switch (token)
                {
                    case Parser.Tokens.ReservedKeyword { Keyword: Parser.Keyword.Tran or Parser.Keyword.Transaction or Parser.Keyword.Distributed } when afterBegin:
                        _ = open.Pop();
                        break;
                    case Parser.Tokens.ReservedKeyword { Keyword: Parser.Keyword.Begin }:
                        open.Push(true);
                        afterBegin = true;
                        continue;
                    case Parser.Tokens.ReservedKeyword { Keyword: Parser.Keyword.Case }:
                        open.Push(false);
                        break;
                    case Parser.Tokens.ReservedKeyword { Keyword: Parser.Keyword.End }:
                        _ = open.TryPop(out _);
                        break;
                }
                afterBegin = false;
            }
        }
        catch (SimulatedSqlException)
        {
        }
        var blocks = 0;
        foreach (var isBlock in open)
        {
            if (isBlock)
                blocks++;
        }
        return blocks;
    }

    /// <summary>
    /// The offset past <c>TRY BEGIN CATCH</c> when those three words follow
    /// <paramref name="from"/> — an <c>END</c>'s <c>TRY</c> and the
    /// <c>CATCH</c> block it leads into — otherwise <paramref name="from"/>.
    /// </summary>
    private static int PastCatchOpening(string text, int from)
    {
        var index = from;
        var matched = 0;
        try
        {
            while (Parser.Tokenizer.NextToken(text, ref index, Collation.Baseline) is { } token)
            {
                if (token is Parser.Tokens.Whitespace or Parser.Tokens.Comment)
                    continue;
                var expected = matched switch
                {
                    0 => token is Parser.Tokens.UnquotedString { Value: var tryWord } && tryWord.Equals("TRY", StringComparison.OrdinalIgnoreCase),
                    1 => token is Parser.Tokens.ReservedKeyword { Keyword: Parser.Keyword.Begin },
                    _ => token is Parser.Tokens.UnquotedString { Value: var catchWord } && catchWord.Equals("CATCH", StringComparison.OrdinalIgnoreCase),
                };
                if (!expected)
                    return from;
                if (++matched == 3)
                    return token.EndIndex;
            }
        }
        catch (SimulatedSqlException)
        {
        }
        return from;
    }

    /// <summary>The tokens, whitespace and comments aside, between two offsets of <paramref name="text"/>.</summary>
    private static int TokensBetween(string text, int from, int to)
    {
        var count = 0;
        var index = from;
        try
        {
            while (index < to && Parser.Tokenizer.NextToken(text, ref index, Collation.Baseline) is { } token)
            {
                if (token is not (Parser.Tokens.Whitespace or Parser.Tokens.Comment) && token.StartIndex < to)
                    count++;
            }
        }
        catch (SimulatedSqlException)
        {
        }
        return count;
    }
}
