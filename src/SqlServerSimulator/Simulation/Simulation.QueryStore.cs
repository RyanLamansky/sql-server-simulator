using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// Query Store capture: which statements a database's store records, the text
// and identity it records them under, and the runtime statistics fed from
// each execution — see docs/claude/database-options.md#query-store.
partial class Simulation
{
    /// <summary>
    /// Each distinct statement text's <see cref="QueryStoreShape"/>, read off
    /// its tokens once. Keyed by the text alone, which is all a shape
    /// depends on, and looked up by span so a repeat execution allocates
    /// nothing; bounded like the plan cache, past which a new text is
    /// analyzed on every execution rather than stored.
    /// </summary>
    private readonly ConcurrentDictionary<string, QueryStoreShape> queryStoreShapes = new(StringComparer.Ordinal);

    private int queryStoreShapeCount;

    /// <summary>
    /// Where a statement's capture started: its clock, the session's
    /// <c>WAITFOR</c> time by then (which is no CPU), and the logical reads
    /// its statistics had already counted.
    /// </summary>
    private readonly struct QueryStoreCapture(Database database, long startedAt, long waitedBefore, long lockWaitedBefore, long readsBefore)
    {
        public readonly Database Database = database;
        public readonly long StartedAt = startedAt;
        public readonly long WaitedBefore = waitedBefore;
        public readonly long LockWaitedBefore = lockWaitedBefore;
        public readonly long ReadsBefore = readsBefore;
    }

    /// <summary>
    /// Whether a statement opening with <paramref name="token"/> is a kind
    /// Query Store can capture — a query, a write, an assignment or
    /// declaration whose value reads a table, a <c>RETURN</c> of one — so
    /// its execution is timed. <c>IF</c> and <c>WHILE</c> capture their
    /// conditions themselves.
    /// </summary>
    private static bool IsQueryStoreCandidate(Token? token) => token switch
    {
        ReservedKeyword { Keyword: Keyword.Select or Keyword.With or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge or Keyword.Set or Keyword.Declare or Keyword.Return } => true,
        Operator { Character: '(' } => true,
        _ => false,
    };

    /// <summary>
    /// Starts timing a statement for Query Store, or null when the store
    /// won't record it: a batch walking in skip mode, a function or view
    /// body (inlined into its caller), a module's CREATE-time bind, and a
    /// database whose store isn't READ_WRITE — or is capturing nothing new
    /// and holds nothing to update. The check reads two fields, so a
    /// database with its store off pays nothing more.
    /// </summary>
    private static QueryStoreCapture? BeginQueryStoreCapture(BatchContext batch, IoStatistics? io)
    {
        if (batch.IsSkipping || batch.SuppressDiagnosticsResolution || batch.CreateTimeBinding)
            return null;
        var database = batch.CurrentDatabase;
        var options = database.QueryStore;
        if (options.DesiredState != QueryStoreState.ReadWrite
            || (options.CaptureMode == QueryStoreCaptureMode.None && database.QueryStoreData.IsEmpty))
        {
            return null;
        }
        var connection = batch.Connection;
        return new QueryStoreCapture(database, Stopwatch.GetTimestamp(), connection.WaitedTicks, connection.Session.LockWaitedMilliseconds, io?.TotalLogicalReads() ?? 0);
    }

    /// <summary>
    /// Records a timed statement's execution — the command text from
    /// <paramref name="start"/> to <paramref name="end"/> — into its
    /// database's store, if the statement is one real captures:
    /// <c>SELECT</c> reading a table (or calling a user function), any
    /// <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> / <c>MERGE</c>, and a
    /// <c>SET</c>, <c>DECLARE</c>, <c>RETURN</c> or condition whose
    /// expression reads a table (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    /// <remarks>The execution type is 0 regular, 3 aborted (attention), 4 an exception.</remarks>
    private static void EndQueryStoreCapture(
        BatchContext batch, QueryStoreCapture capture, IoStatistics? io, int start, int end, byte executionType, long rowCount, ParserContext.Checkpoint? tokensFrom = null)
    {
        var commandText = batch.Parser.Command.CommandText;
        if (start < 0 || end <= start || end > commandText.Length)
            return;
        var connection = batch.Connection;
        var elapsed = Stopwatch.GetElapsedTime(capture.StartedAt).Ticks;
        var lockWaitMilliseconds = connection.Session.LockWaitedMilliseconds - capture.LockWaitedBefore;
        var cpu = Math.Max(0, elapsed - (connection.WaitedTicks - capture.WaitedBefore) - (lockWaitMilliseconds * TimeSpan.TicksPerMillisecond));
        var reads = Math.Max(0, (io?.TotalLogicalReads() ?? 0) - capture.ReadsBefore);

        var shape = connection.Simulation.QueryStoreShapeOf(commandText.AsSpan(start, end - start), capture.Database, batch.Parser, tokensFrom);
        if (!shape.Captured && !(shape.CapturedWhenCallingFunction && batch.CurrentStatement.CallsUserFunction))
            return;

        var isModule = batch.ModuleObjectId != 0 || batch.TriggerFrame is not null || batch.ProcFrame is { IsDynamicSql: false };
        string text;
        byte parameterizationType;
        long offsetStart, offsetEnd;
        string? tableVariableScope = null;
        if (shape.Parameterized is { } parameterized && !isModule)
        {
            text = parameterized;
            parameterizationType = 2;
            offsetStart = 2L * shape.ParameterizedPrefixLength;
            offsetEnd = 2L * (parameterized.Length - 1);
        }
        else
        {
            string? prefix = null;
            if (shape.DeclaresVariables)
            {
                if (ReadsTableVariable(batch, shape))
                    tableVariableScope = commandText;
                prefix = VariablePrefix(batch, shape);
            }
            text = prefix is null ? shape.Text : prefix + shape.Text;
            parameterizationType = prefix is not null && (batch.ProcFrame is { IsDynamicSql: true } || (connection.NestingLevel == 0 && batch.Parser.Command.Parameters.Count > 0))
                ? (byte)1
                : (byte)0;
            offsetStart = 2L * start;
            offsetEnd = 2L * (end - 1);
        }

        var context = new QueryStoreContextKey(
            SetOptionsOf(connection, batch.Parser.QuotedIdentifiers),
            connection.Language.LangId,
            DateFormatCode(connection.DateFormat),
            connection.DateFirst,
            isModule || !shape.UsesDefaultSchema ? -2 : Database.DboSchemaId);
        var execution = new QueryStoreExecution(
            new QueryStoreQueryKey(text, context, batch.ModuleObjectId, parameterizationType, tableVariableScope),
            shape,
            commandText,
            offsetStart,
            offsetEnd,
            DateTime.UtcNow,
            elapsed / 10,
            cpu / 10,
            reads,
            rowCount,
            executionType,
            lockWaitMilliseconds);
        RecordQueryStoreExecution(capture.Database, execution);
    }

    /// <summary>
    /// Starts timing an <c>IF</c> or <c>WHILE</c> condition, which real
    /// captures as a query of its own — <c>if exists (select …)</c> — when
    /// it reads a table. The condition counts its reads into the enclosing
    /// statement's statistics when <c>STATISTICS IO</c> gathers them, and
    /// into <paramref name="conditionIo"/> otherwise.
    /// </summary>
    private static QueryStoreCapture? BeginConditionQueryStoreCapture(BatchContext batch, out IoStatistics? conditionIo)
    {
        conditionIo = null;
        var connection = batch.Connection;
        if (BeginQueryStoreCapture(batch, connection.StatementIo) is not { } capture)
            return null;
        connection.StatementIo ??= conditionIo = new IoStatistics();
        return capture;
    }

    /// <summary>Records a condition <see cref="BeginConditionQueryStoreCapture"/> timed, the text from its keyword to its end.</summary>
    private static void EndConditionQueryStoreCapture(BatchContext batch, QueryStoreCapture capture, IoStatistics? conditionIo, int start, int end, SimulatedSqlException? error)
    {
        var connection = batch.Connection;
        var io = conditionIo ?? connection.StatementIo;
        if (conditionIo is not null)
            connection.StatementIo = null;
        if (error is not null && IsQueryStoreCompileError(error))
            return;
        EndQueryStoreCapture(batch, capture, io, start, end, error is null ? (byte)0 : error.IsAttention ? (byte)3 : (byte)4, 0);
    }

    /// <summary>
    /// Records a statement the dispatch loop ran: regular when it completed,
    /// an exception when a run-time error ended it, aborted when the client's
    /// attention did. An error met while compiling — a missing object or
    /// column, a syntax error — leaves nothing to record, as on real. A
    /// failing statement's text runs to the next statement boundary, found
    /// without moving the cursor.
    /// </summary>
    private static void EndFramedQueryStoreCapture(BatchContext batch, QueryStoreCapture capture, IoStatistics? io, SimulatedSqlException? error, ParserContext.Checkpoint statementStart)
    {
        if (error is not null && IsQueryStoreCompileError(error))
            return;
        var parser = batch.Parser;
        var end = parser.PreviousTokenEnd;
        if (error is not null && parser.Token is { } token && !IsStatementBoundary(token))
        {
            var resume = parser.SaveCheckpoint();
            try
            {
                while (parser.Token is not null && !IsStatementBoundary(parser.Token))
                    parser.MoveNextOptional();
                end = parser.PreviousTokenEnd;
            }
            catch (SimulatedSqlException)
            {
                // Text the tokenizer refuses ends the statement where it stands.
            }
            finally
            {
                parser.RestoreCheckpoint(resume);
            }
        }
        // A MERGE's terminator is part of its text (probed 2026-09-29).
        if (parser.Token is Operator { Character: ';' } separator && separator.StartIndex == end
            && FirstTokenIs(parser.Command.CommandText, batch.CurrentStatement.StartIndex, "MERGE"))
        {
            end = separator.EndIndex;
        }
        var executionType = error switch
        {
            null => (byte)0,
            { IsAttention: true } => (byte)3,
            _ => (byte)4,
        };
        EndQueryStoreCapture(batch, capture, io, batch.CurrentStatement.StartIndex, end, executionType, error is null ? batch.Connection.LastStatementRowCount : 0,
            error is null ? statementStart : null);
    }

    private static bool FirstTokenIs(string text, int start, string word) =>
        start + word.Length <= text.Length && text.AsSpan(start, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An error met compiling the statement — a syntax error, a missing object
    /// or column — which leaves real nothing to record, where a run-time error
    /// records an exception execution.
    /// </summary>
    private static bool IsQueryStoreCompileError(SimulatedSqlException error) => error.Class == 15 || IsBatchAbortingNameResolution(error);

    /// <summary>
    /// Whether the statement reads a table variable, which scopes its query to
    /// the batch: real then gives the query a <c>batch_sql_handle</c>
    /// (probed 2026-09-29).
    /// </summary>
    private static bool ReadsTableVariable(BatchContext batch, QueryStoreShape shape)
    {
        foreach (var name in shape.Variables)
        {
            if (batch.TableVariables.ContainsKey(name[1..]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The declaration real prefixes a statement's text with for the
    /// variables and parameters it reads — <c>(@x int,@s varchar(10))</c>,
    /// in the order the statement first names them, each spelled as it was
    /// declared (probed 2026-09-29). Table variables aren't declared there.
    /// Null for a condition or <c>RETURN</c>, which real stores bare, and
    /// when nothing the statement names is a scalar variable.
    /// </summary>
    private static string? VariablePrefix(BatchContext batch, QueryStoreShape shape)
    {
        if (shape.IsConditionOrReturn)
            return null;
        StringBuilder? prefix = null;
        foreach (var name in shape.Variables)
        {
            var bare = name[1..];
            if (!batch.Variables.TryGetValue(bare, out var slot))
                continue;
            var declared = bare;
            foreach (var key in batch.Variables.Keys)
            {
                if (BatchContext.VariableNameComparer.Equals(key, bare))
                {
                    declared = key;
                    break;
                }
            }
            prefix = prefix is null ? new StringBuilder("(") : prefix.Append(',');
            _ = prefix.Append('@').Append(declared).Append(' ').Append(DescribeTypeName(slot.DeclaredType, slot.SpelledNumeric));
        }
        return prefix?.Append(')').ToString();
    }

    /// <summary>
    /// <c>sys.query_context_settings.set_options</c>: real's plan-attribute
    /// bits for the session's plan-affecting options, the parallel-plan bit
    /// (2) always set — 0xFB for a SqlClient session (probed 2026-09-29).
    /// </summary>
    private static int SetOptionsOf(SimulatedDbConnection connection, bool quotedIdentifier) =>
        (connection.AnsiPadding ? 1 : 0)
        | 2
        | (connection.ConcatNullYieldsNull ? 8 : 0)
        | (connection.AnsiWarnings ? 16 : 0)
        | (connection.AnsiNulls ? 32 : 0)
        | (quotedIdentifier ? 64 : 0)
        | (connection.AnsiNullDefaultOn ? 128 : 0)
        | (connection.AnsiNullDefaultOff ? 256 : 0)
        | (connection.Arithabort ? 4096 : 0)
        | (connection.NumericRoundabort ? 8192 : 0);

    /// <summary><c>sys.query_context_settings.date_format</c>: 1 for mdy, 2 for dmy, and on through the six orders.</summary>
    private static short DateFormatCode(DateOrder order) =>
        order == DateOrder.Mdy ? (short)1
        : order == DateOrder.Dmy ? (short)2
        : order == DateOrder.Ymd ? (short)3
        : order == DateOrder.Ydm ? (short)4
        : order == DateOrder.Myd ? (short)5
        : (short)6;

    /// <summary>One statement execution, as a store records it.</summary>
    private readonly struct QueryStoreExecution(
        QueryStoreQueryKey key, QueryStoreShape shape, string batchText, long offsetStart, long offsetEnd, DateTime now,
        long durationMicroseconds, long cpuMicroseconds, long logicalReads, long rowCount, byte executionType, long lockWaitMilliseconds)
    {
        public readonly QueryStoreQueryKey Key = key;
        public readonly QueryStoreShape Shape = shape;
        public readonly string BatchText = batchText;
        public readonly long OffsetStart = offsetStart;
        public readonly long OffsetEnd = offsetEnd;
        public readonly DateTime Now = now;
        public readonly long DurationMicroseconds = durationMicroseconds;
        public readonly long CpuMicroseconds = cpuMicroseconds;
        public readonly long LogicalReads = logicalReads;
        public readonly long RowCount = rowCount;
        public readonly byte ExecutionType = executionType;
        public readonly long LockWaitMilliseconds = lockWaitMilliseconds;
    }

    /// <summary>
    /// Adds <paramref name="execution"/> to <paramref name="database"/>'s
    /// store: to a captured query's runtime statistics, or — for a query not
    /// captured yet — capturing it when the capture mode says so. ALL
    /// captures on the first execution; AUTO on the 30th within a day or once
    /// its executions have spent 100 ms of CPU, which is what real's AUTO
    /// policy does (probed 2026-09-29: a query run 31 times reports 2
    /// executions, one run 42 times 13); CUSTOM by its own policy; NONE only
    /// updates what is already captured.
    /// </summary>
    private static void RecordQueryStoreExecution(Database database, in QueryStoreExecution execution)
    {
        var data = database.QueryStoreData;
        var options = database.QueryStore;
        lock (data.Gate)
        {
            var now = execution.Now;
            if (!data.QueriesByKey.TryGetValue(execution.Key, out var query))
            {
                if (options.CaptureMode == QueryStoreCaptureMode.None
                    || (options.CaptureMode != QueryStoreCaptureMode.All && !PassesCapturePolicy(data, options, execution)))
                {
                    return;
                }
                query = CaptureQuery(data, execution);
            }
            query.LastExecutionTime = now;
            var plan = query.Plan ??= CompilePlan(data, query, execution, database.CompatibilityLevel);
            plan.LastExecutionTime = now;

            var interval = CurrentInterval(data, options, now);
            var statsKey = (plan.PlanId, interval.Id, execution.ExecutionType);
            if (!data.RuntimeStatsByKey.TryGetValue(statsKey, out var stats))
            {
                stats = new QueryStoreRuntimeStats(data.NextRuntimeStatsId++, plan.PlanId, interval.Id, execution.ExecutionType, now);
                data.RuntimeStats.Add(stats);
                data.RuntimeStatsByKey.Add(statsKey, stats);
            }
            var first = stats.Count == 0;
            stats.Count++;
            stats.LastExecutionTime = now;
            stats.Duration.Add(execution.DurationMicroseconds, first);
            stats.CpuTime.Add(execution.CpuMicroseconds, first);
            stats.LogicalIoReads.Add(execution.LogicalReads, first);
            stats.LogicalIoWrites.Add(0, first);
            stats.RowCount.Add(execution.RowCount, first);

            // A statement blocked on a lock records a Lock wait (wait
            // category 3), which WAIT_STATS_CAPTURE_MODE = OFF suppresses.
            if (execution.LockWaitMilliseconds > 0 && options.WaitStatsCaptureOn)
            {
                if (!data.WaitStatsByKey.TryGetValue(statsKey, out var waits))
                {
                    waits = new QueryStoreWaitStats(data.NextWaitStatsId++, plan.PlanId, interval.Id, execution.ExecutionType);
                    data.WaitStats.Add(waits);
                    data.WaitStatsByKey.Add(statsKey, waits);
                }
                waits.Count++;
                waits.WaitTime.Add(execution.LockWaitMilliseconds, waits.Count == 1);
            }
        }
    }

    /// <summary>
    /// Whether a threshold capture mode captures the query now: its tally of
    /// executions and CPU time since the stale threshold last reset it
    /// reaches the policy's execution count or CPU budget. AUTO's policy is
    /// fixed — 30 executions, 100 ms, a day — and CUSTOM's is the
    /// configured one. Caller holds the store's gate.
    /// </summary>
    private static bool PassesCapturePolicy(QueryStoreData data, QueryStoreOptions options, in QueryStoreExecution execution)
    {
        var custom = options.CaptureMode == QueryStoreCaptureMode.Custom;
        var executionCount = custom ? options.CapturePolicyExecutionCount : 30;
        var cpuBudgetMs = custom ? options.CapturePolicyTotalExecutionCpuTimeMs : 100;
        var staleHours = custom ? options.CapturePolicyStaleThresholdHours : 24;
        if (!data.Pending.TryGetValue(execution.Key, out var pending))
        {
            if (data.Pending.Count >= QueryStoreData.PendingCapacity)
                data.Pending.Clear();
            pending = new QueryStorePending(execution.Now);
            data.Pending.Add(execution.Key, pending);
        }
        else if (execution.Now - pending.Since > TimeSpan.FromHours(staleHours))
        {
            pending.Since = execution.Now;
            pending.Executions = 0;
            pending.CpuMicroseconds = 0;
        }
        pending.Executions++;
        pending.CpuMicroseconds += execution.CpuMicroseconds;
        if (pending.Executions < executionCount && pending.CpuMicroseconds < cpuBudgetMs * 1000)
            return false;
        _ = data.Pending.Remove(execution.Key);
        return true;
    }

    /// <summary>Adds the text, context settings and query rows for a newly captured query. Caller holds the gate.</summary>
    private static QueryStoreQuery CaptureQuery(QueryStoreData data, in QueryStoreExecution execution)
    {
        var key = execution.Key;
        if (!data.TextsByText.TryGetValue(key.Text, out var text))
        {
            text = new QueryStoreText(data.NextTextId++, key.Text, StatementSqlHandleOf(key.Text));
            data.Texts.Add(text);
            data.TextsByText.Add(key.Text, text);
        }
        var context = data.ContextSettings.Find(existing => existing.Key.Equals(key.Context));
        if (context is null)
        {
            context = new QueryStoreContextSettings(data.NextContextSettingsId++, key.Context);
            data.ContextSettings.Add(context);
        }
        var query = new QueryStoreQuery(
            data.NextQueryId++, key, text, context, execution.Shape.QueryHash,
            key.BatchText is { } scope ? BuiltInResources.SqlHandleOf(scope) : null,
            execution.Now)
        {
            LastCompileBatchSqlHandle = BuiltInResources.SqlHandleOf(execution.BatchText),
            LastCompileBatchOffsetStart = execution.OffsetStart,
            LastCompileBatchOffsetEnd = execution.OffsetEnd,
        };
        data.Queries.Add(query);
        data.QueriesByKey.Add(key, query);
        return query;
    }

    /// <summary>The query's plan, compiled at its first captured execution (or its first after a plan was removed). Caller holds the gate.</summary>
    private static QueryStorePlan CompilePlan(QueryStoreData data, QueryStoreQuery query, in QueryStoreExecution execution, CompatibilityLevel compatibilityLevel)
    {
        var planId = data.NextPlanId++;
        var level = (short)compatibilityLevel;
        var plan = new QueryStorePlan(planId, query, level, execution.Shape.QueryPlanHash, ShowPlanXml(query, execution.Shape, level), execution.Shape.IsTrivial, execution.Now);
        data.Plans.Add(plan);
        return plan;
    }

    /// <summary>
    /// The interval <paramref name="now"/> falls in: the latest one while it
    /// lasts, else one opened now — <c>INTERVAL_LENGTH_MINUTES</c> long and
    /// aligned to that length from midnight, as real's are (probed
    /// 2026-09-29: a 60-minute interval runs 01:00 to 02:00, a 1-minute one
    /// from second 0). A changed length takes effect at the next interval.
    /// Caller holds the gate.
    /// </summary>
    private static QueryStoreInterval CurrentInterval(QueryStoreData data, QueryStoreOptions options, DateTime now)
    {
        if (data.Intervals.Count > 0 && data.Intervals[^1] is { } latest && latest.Start <= now && now < latest.End)
            return latest;
        var length = TimeSpan.FromMinutes(options.IntervalLengthMinutes is > 0 and <= 1440 ? options.IntervalLengthMinutes : 60).Ticks;
        var start = new DateTime(now.Ticks - (now.Ticks % length), DateTimeKind.Utc);
        var interval = new QueryStoreInterval(data.NextIntervalId++, start, start.AddTicks(length));
        data.Intervals.Add(interval);
        return interval;
    }

    /// <summary>
    /// <c>statement_sql_handle</c>: the type byte 9, a zero byte, the MD5 of
    /// the stored text's UTF-16 bytes and zeros to 44 bytes — real's own
    /// derivation, so a handle matches the one real computes for the same
    /// text (probed 2026-09-29).
    /// </summary>
    internal static byte[] StatementSqlHandleOf(string text)
    {
        var handle = new byte[BuiltInResources.SqlHandleLength];
        handle[0] = 9;
        // Real's own derivation, reproduced for the matching bytes rather than for security.
#pragma warning disable CA5351
        _ = MD5.HashData(MemoryMarshal.AsBytes(text.AsSpan()), handle.AsSpan(2, 16));
#pragma warning restore CA5351
        return handle;
    }

    /// <summary>
    /// A ShowPlan XML document with real's outer shape — the statement
    /// element and its attributes, the SET options, a <c>QueryPlan</c>
    /// element — and no operator tree, the simulator having no optimizer
    /// whose plan it could describe.
    /// </summary>
    private static string ShowPlanXml(QueryStoreQuery query, QueryStoreShape shape, short compatibilityLevel)
    {
        var statementText = query.Key.ParameterizationType == 2 && shape.Parameterized is { } parameterized
            ? parameterized[shape.ParameterizedPrefixLength..]
            : shape.Text;
        var options = query.Context.Key.SetOptions;
        static string Flag(int options, int bit) => (options & bit) != 0 ? "true" : "false";
        var invariant = CultureInfo.InvariantCulture;
        return new StringBuilder()
            .Append(invariant, $"<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\" Version=\"1.599\" Build=\"{ReferenceBuild.ProductVersion}\">")
            .Append("<BatchSequence><Batch><Statements>")
            .Append(invariant, $"<StmtSimple StatementText=\"{SecurityElement.Escape(statementText)}\" StatementId=\"1\" StatementCompId=\"1\" StatementType=\"{shape.StatementType}\"")
            .Append(invariant, $" StatementSqlHandle=\"0x{Convert.ToHexString(query.Text.StatementSqlHandle)}\" DatabaseContextSettingsId=\"{query.Context.Id}\"")
            .Append(invariant, $" ParentObjectId=\"{query.Key.ObjectId}\" StatementParameterizationType=\"{query.Key.ParameterizationType}\" RetrievedFromCache=\"false\"")
            .Append(invariant, $" StatementOptmLevel=\"{(shape.IsTrivial ? "TRIVIAL" : "FULL")}\" QueryHash=\"0x{Convert.ToHexString(shape.QueryHash)}\"")
            .Append(invariant, $" QueryPlanHash=\"0x{Convert.ToHexString(shape.QueryPlanHash)}\" CardinalityEstimationModelVersion=\"{compatibilityLevel}\">")
            .Append(invariant, $"<StatementSetOptions QUOTED_IDENTIFIER=\"{Flag(options, 64)}\" ARITHABORT=\"{Flag(options, 4096)}\" CONCAT_NULL_YIELDS_NULL=\"{Flag(options, 8)}\"")
            .Append(invariant, $" ANSI_NULLS=\"{Flag(options, 32)}\" ANSI_PADDING=\"{Flag(options, 1)}\" ANSI_WARNINGS=\"{Flag(options, 16)}\" NUMERIC_ROUNDABORT=\"{Flag(options, 8192)}\"></StatementSetOptions>")
            .Append("<QueryPlan CachedPlanSize=\"16\" CompileTime=\"0\" CompileCPU=\"0\" CompileMemory=\"0\"></QueryPlan>")
            .Append("</StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>")
            .ToString();
    }

    /// <summary>
    /// The shape of the statement <paramref name="text"/> spells, from the
    /// cache or read now. The database supplies the tokenizer's collation and
    /// compatibility level.
    /// </summary>
    private QueryStoreShape QueryStoreShapeOf(ReadOnlySpan<char> text, Database database, ParserContext parser, ParserContext.Checkpoint? tokensFrom)
    {
        var lookup = this.queryStoreShapes.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(text, out var cached))
            return cached;
        var shape = QueryStoreShape.Read(new string(text), tokensFrom is { } start ? parser.StatementTokens(start) : null, database);
        if (Volatile.Read(ref this.queryStoreShapeCount) < PlanCacheCapacity * 4 && this.queryStoreShapes.TryAdd(shape.Text, shape))
            _ = Interlocked.Increment(ref this.queryStoreShapeCount);
        return shape;
    }

    /// <summary>Forgets every statement shape, with the plan cache: <c>DBCC FREEPROCCACHE</c>.</summary>
    private void ClearQueryStoreShapes()
    {
        this.queryStoreShapes.Clear();
        Volatile.Write(ref this.queryStoreShapeCount, 0);
    }
}

/// <summary>
/// What Query Store needs to know about one statement text, read off its
/// tokens: whether real captures it, the text real stores for it, the
/// variables it names (whose declaration prefixes the stored text), whether
/// it resolves a one-part name through the default schema, and the hashes
/// and statement type its plan reports.
/// </summary>
internal sealed class QueryStoreShape
{
    public readonly string Text;

    /// <summary>Whether real captures the statement whatever it calls.</summary>
    public readonly bool Captured;

    /// <summary>A FROM-less query, which real captures only when it calls a user function.</summary>
    public readonly bool CapturedWhenCallingFunction;

    /// <summary>The simple-parameterized text, or null; see <see cref="SimpleParameterization"/>.</summary>
    public readonly string? Parameterized;

    /// <summary>The length of <see cref="Parameterized"/>'s declaration, which the batch offsets skip.</summary>
    public readonly int ParameterizedPrefixLength;

    /// <summary>Every <c>@name</c> the statement reads, <c>@</c> included, in first-appearance order.</summary>
    public readonly string[] Variables;

    public bool DeclaresVariables => this.Variables.Length > 0;

    /// <summary>An <c>IF</c> / <c>WHILE</c> condition or a <c>RETURN</c>, whose text real stores without a declaration.</summary>
    public readonly bool IsConditionOrReturn;

    /// <summary>A one-part object name the default schema resolves, which gives the context settings a default schema.</summary>
    public readonly bool UsesDefaultSchema;

    public readonly bool IsTrivial;

    /// <summary>The ShowPlan <c>StatementType</c>.</summary>
    public readonly string StatementType;

    public readonly byte[] QueryHash;
    public readonly byte[] QueryPlanHash;

    private QueryStoreShape(string text, bool captured, bool capturedWhenCallingFunction, string? parameterized, string[] variables,
        bool isConditionOrReturn, bool usesDefaultSchema, bool isTrivial, string statementType, byte[] hash)
    {
        this.Text = text;
        this.Captured = captured;
        this.CapturedWhenCallingFunction = capturedWhenCallingFunction;
        this.Parameterized = parameterized;
        this.ParameterizedPrefixLength = parameterized is null ? 0 : parameterized.IndexOf(')', StringComparison.Ordinal) + 1;
        this.Variables = variables;
        this.IsConditionOrReturn = isConditionOrReturn;
        this.UsesDefaultSchema = usesDefaultSchema;
        this.IsTrivial = isTrivial;
        this.StatementType = statementType;
        this.QueryHash = hash[..8];
        this.QueryPlanHash = hash[8..16];
    }

    /// <summary>
    /// Reads <paramref name="text"/>'s shape from its tokens — the parser's
    /// own when it still holds them, else the text tokenized again.
    /// </summary>
    public static QueryStoreShape Read(string text, List<Token>? parsed, Database database)
    {
        var tokens = parsed ?? [];
        var index = 0;
        try
        {
            while (parsed is null && Tokenizer.NextToken(text, ref index, database.Collation, quotedIdentifiers: true, database.CompatibilityLevel) is Token token)
            {
                if (token is not (Whitespace or Comment))
                    tokens.Add(token);
            }
        }
        catch (SimulatedSqlException)
        {
            tokens.Clear();
        }
        while (tokens.Count > 0 && tokens[^1] is Operator { Character: ';' } && tokens[0] is not ReservedKeyword { Keyword: Keyword.Merge })
            tokens.RemoveAt(tokens.Count - 1);

        var lead = tokens.Count > 0 ? tokens[0] : null;
        var hasFrom = false;
        var writes = false;
        var joinsOrGroups = false;
        var selects = 0;
        var intoSeen = false;
        var usesDefaultSchema = false;
        var variables = new List<string>();
        var hash = new QueryHashBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            switch (token)
            {
                case ReservedKeyword { Keyword: Keyword.From }:
                    hasFrom = true;
                    break;
                case ReservedKeyword { Keyword: Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge }:
                    writes = true;
                    break;
                case ReservedKeyword { Keyword: Keyword.Join or Keyword.Group or Keyword.Order or Keyword.Union or Keyword.Except or Keyword.Intersect or Keyword.Distinct or Keyword.Top or Keyword.Over }:
                    joinsOrGroups = true;
                    break;
                case ReservedKeyword { Keyword: Keyword.Select }:
                    selects++;
                    break;
                case ReservedKeyword { Keyword: Keyword.Into }:
                    intoSeen = true;
                    break;
                case AtPrefixedString variable:
                    var name = "@" + variable.Span.ToString();
                    if (!variables.Exists(existing => BatchContext.VariableNameComparer.Equals(existing, name)))
                        variables.Add(name);
                    break;
            }
            if (!usesDefaultSchema
                && (token is ReservedKeyword { Keyword: Keyword.From or Keyword.Join or Keyword.Into or Keyword.Update or Keyword.Merge or Keyword.Delete or Keyword.Insert }
                    || (token is UnquotedString word && word.Span.Equals("USING", StringComparison.OrdinalIgnoreCase)))
                && i + 1 < tokens.Count && tokens[i + 1] is Name target && !target.Value.StartsWith('#')
                && (i + 2 >= tokens.Count || tokens[i + 2] is not Operator { Character: '.' }))
            {
                usesDefaultSchema = true;
            }
            switch (token)
            {
                case ReservedKeyword keyword:
                    hash.Add((int)keyword.Keyword);
                    break;
                case Name name:
                    hash.AddFolded(name.Value);
                    break;
                case Literal or Numeric:
                    hash.Add('?');
                    break;
                default:
                    hash.AddFolded(token.Source);
                    break;
            }
            hash.Add(' ');
        }

        var isCondition = lead is ReservedKeyword { Keyword: Keyword.If or Keyword.While or Keyword.Return };
        var captured = lead switch
        {
            ReservedKeyword { Keyword: Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge } => true,
            ReservedKeyword { Keyword: Keyword.With } => hasFrom || writes,
            ReservedKeyword { Keyword: Keyword.Select } or Operator { Character: '(' } => hasFrom,
            ReservedKeyword { Keyword: Keyword.Set or Keyword.Declare or Keyword.Return or Keyword.If or Keyword.While } => hasFrom,
            _ => false,
        };
        var statementType = lead switch
        {
            ReservedKeyword { Keyword: Keyword.Insert } => "INSERT",
            ReservedKeyword { Keyword: Keyword.Update } => "UPDATE",
            ReservedKeyword { Keyword: Keyword.Delete } => "DELETE",
            ReservedKeyword { Keyword: Keyword.Merge } => "MERGE",
            ReservedKeyword { Keyword: Keyword.If or Keyword.While } => "COND",
            ReservedKeyword { Keyword: Keyword.Return } => "RETURN",
            ReservedKeyword { Keyword: Keyword.Declare } when tokens.Exists(t => t is ReservedKeyword { Keyword: Keyword.Cursor }) => "DECLARE CURSOR",
            ReservedKeyword { Keyword: Keyword.Set or Keyword.Declare } => "ASSIGN",
            _ when intoSeen && !writes => "SELECT INTO",
            _ => "SELECT",
        };
        var parameterized = lead is ReservedKeyword { Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete }
            ? SimpleParameterization.Parameterize(tokens)
            : null;
        return new QueryStoreShape(
            text,
            captured,
            capturedWhenCallingFunction: !captured && lead is ReservedKeyword { Keyword: Keyword.Select },
            parameterized,
            [.. variables],
            isCondition,
            usesDefaultSchema,
            isTrivial: (captured && !joinsOrGroups && selects <= 1 && !writes) || parameterized is not null,
            statementType,
            hash.ToBytes());
    }

    /// <summary>
    /// Two FNV-1a accumulators over a statement's normalized tokens —
    /// keywords by identity, names case-folded, every literal alike — whose
    /// states give the query hash and the plan hash. Real's own hashes come
    /// from its trees and can't be reproduced; these share their key
    /// property, that texts differing only in literals, case or spacing hash
    /// alike.
    /// </summary>
    private struct QueryHashBuilder()
    {
        private ulong query = 14695981039346656037;
        private ulong plan = 1099511628211 * 31;

        public void Add(int value)
        {
            this.query = (this.query ^ (uint)value) * 1099511628211;
            this.plan = (this.plan ^ (uint)value) * 1099511628211;
        }

        public void AddFolded(ReadOnlySpan<char> text)
        {
            foreach (var c in text)
                this.Add(char.ToUpperInvariant(c));
        }

        public readonly byte[] ToBytes()
        {
            var bytes = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, this.query);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), this.plan);
            return bytes;
        }
    }
}
