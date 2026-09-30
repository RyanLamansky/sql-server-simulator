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
    /// <returns>The error or errors that stop the batch, or <see langword="null"/> when it compiled.</returns>
    /// <remarks>
    /// Real keeps compiling the statements after one it defers; the walk stops at
    /// a deferred DML target, because its recovery scan can't tell where that
    /// statement ended, so an error past it surfaces when its statement runs.
    /// </remarks>
    private SimulatedSqlException? CompileBatch(BatchContext compileBatch, PlanCacheKey? key)
    {
        var schemaVersion = Volatile.Read(ref this.SchemaVersion);
        if (key is { } cached && this.compiledBatches.TryGetValue(cached, out var compiledUnder) && compiledUnder == schemaVersion)
            return null;

        var errors = new List<SimulatedSqlException>();
        compileBatch.CurrentStatement.UtcNow = DateTime.UtcNow;
        compileBatch.CompilingForRun = true;
        // A USE the walk meets switches the database it binds in.
        var connection = compileBatch.Connection;
        var enteredDatabase = connection.CurrentDatabase;
        try
        {
            _ = this.BindWithoutRunning(compileBatch, errors);
        }
        catch (SimulatedSqlException parsePhase)
        {
            connection.CurrentDatabase = enteredDatabase;
            return IsRecoverableSyntaxError(parsePhase) && compileBatch.Parser.Token is { } errorToken
                ? this.WithRecoveredSyntaxErrors(compileBatch, parsePhase, errorToken)
                : parsePhase;
        }
        finally
        {
            connection.CurrentDatabase = enteredDatabase;
        }

        if (errors.Count > 0)
        {
            var report = SimulatedSqlException.Aggregate(errors);
            report.CatchReadsFirstEntry = true;
            return report;
        }
        if (compileBatch.DeferredOptimizerError is { } optimizerError && !compileBatch.WalkMetDdl)
            return optimizerError;

        if (key is { } compiled && !compileBatch.ResolvedTempTable)
        {
            if (this.compiledBatches.ContainsKey(compiled))
                this.compiledBatches[compiled] = schemaVersion;
            else if (Volatile.Read(ref this.compiledBatchCount) < PlanCacheCapacity && this.compiledBatches.TryAdd(compiled, schemaVersion))
                _ = Interlocked.Increment(ref this.compiledBatchCount);
        }
        return null;
    }

    /// <summary>
    /// The throwaway context <see cref="CompileBatch"/> walks
    /// <paramref name="executing"/>'s text on: the same command, a copy of the
    /// variables and table variables its parameters seeded (so the walk's own
    /// <c>DECLARE</c>s don't collide with the run's), and the same frame and
    /// error attribution.
    /// </summary>
    private static BatchContext CompileContextFor(BatchContext executing, SimulatedDbCommand command)
    {
        var variables = new Dictionary<string, VariableSlot>(executing.Variables, BatchContext.VariableNameComparer);
        var compile = executing.ProcFrame is { } frame
            ? new BatchContext(command, variables, new ProcFrame(frame.ProcedureName, frame.IsDynamicSql))
            : new BatchContext(command, variables);
        foreach (var (name, table) in executing.TableVariables)
            compile.TableVariables[name] = table;
        compile.LineOffset = executing.LineOffset;
        compile.ErrorProcedureName = executing.ErrorProcedureName;
        compile.ForceTempTableScope = executing.ForceTempTableScope;
        return compile;
    }

    /// <summary>The syntax errors real's parser recovers from and parses on past.</summary>
    private static bool IsRecoverableSyntaxError(SimulatedSqlException error) => error.Number is 102 or 111 or 156 or 178 or 319;

    /// <summary>
    /// Whether <paramref name="error"/> is one the grammar's own actions raise
    /// rather than a token the parser can't take — a module <c>CREATE</c> not
    /// first in its batch (Msg 111), a valued <c>RETURN</c> outside a module
    /// (Msg 178), a <c>WITH</c> after an unterminated statement (Msg 319) —
    /// which recovery reports however few tokens have parsed since the last.
    /// </summary>
    private static bool IsGrammarActionError(SimulatedSqlException error) => error.Number is 111 or 178 or 319;

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
    private SimulatedSqlException WithRecoveredSyntaxErrors(BatchContext compileBatch, SimulatedSqlException first, Parser.Token errorToken)
    {
        var text = compileBatch.Parser.Command.CommandText;
        var connection = compileBatch.Connection;
        var enteredDatabase = connection.CurrentDatabase;
        var errors = new List<SimulatedSqlException> { first };
        // A grammar action's error names a construct the parser read whole;
        // the parser resumes past the token it stopped at.
        var restart = IsGrammarActionError(first) ? errorToken.EndIndex : errorToken.StartIndex;
        for (var attempts = 0; attempts < 64; attempts++)
        {
            restart = NextStatementStart(text, restart);
            if (restart >= text.Length)
                break;
            var masked = string.Create(text.Length, (text, restart), static (span, state) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = i >= state.restart || state.text[i] is '\n' or '\r' ? state.text[i] : ' ';
            });
            using var command = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // the batch's own text, blanked in part
            command.CommandText = masked;
#pragma warning restore CA2100
            var variables = new Dictionary<string, VariableSlot>(compileBatch.Variables, BatchContext.VariableNameComparer);
            var recovery = compileBatch.ProcFrame is { } frame
                ? new BatchContext(command, variables, new ProcFrame(frame.ProcedureName, frame.IsDynamicSql))
                : new BatchContext(command, variables);
            recovery.LineOffset = compileBatch.LineOffset;
            recovery.ErrorProcedureName = compileBatch.ErrorProcedureName;
            recovery.CompilingForRun = true;
            try
            {
                _ = this.BindWithoutRunning(recovery, []);
                break;
            }
            catch (SimulatedSqlException next) when (IsRecoverableSyntaxError(next) && recovery.Parser.Token is { } at && at.StartIndex >= restart)
            {
                var parsed = TokensBetween(masked, restart, at.StartIndex);
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
                var resume = reported && !readAsCte ? at.StartIndex : at.EndIndex;
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
