using System.Collections.Concurrent;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;

namespace SqlServerSimulator;

// The plan cache over repeated SELECT batches and the token memo: the key and entry, promotion, replay and clearing.
public sealed partial class Simulation
{
    /// <summary>
    /// Per-instance parse-result cache for single-SELECT command batches
    /// (the EF-query shape). Keyed by (<see cref="DbCommand.CommandText"/>,
    /// current database name, parameter type signature); the entry records
    /// the parsed <see cref="Selection"/> and the <see cref="SchemaVersion"/>
    /// it was parsed under, so a DDL bump invalidates without per-entry walk.
    /// Capped at <see cref="PlanCacheCapacity"/>: once full, new entries are
    /// silently dropped (the working set for a stable EF app is dozens of
    /// queries, so the cap is mostly defensive). Bypassed for batches that
    /// reference session-scoped tables (<c>#temp</c>, <c>##gtemp</c>,
    /// <c>@t</c>), contain DDL, or aren't a single top-level SELECT — the
    /// candidate is captured by the dispatch loop and dropped on any
    /// disqualifying condition.
    /// </summary>
    private readonly ConcurrentDictionary<PlanCacheKey, PlanCacheEntry> planCache = new();

    /// <summary>
    /// How many keys <see cref="planCache"/> holds, counted on add rather than
    /// read from the dictionary, whose <c>Count</c> takes all its locks. Only
    /// <see cref="ClearPlanCache"/> removes keys, decrementing as it does.
    /// </summary>
    private int planCacheCount;

    private const int PlanCacheCapacity = 1024;

    /// <summary>
    /// Per-instance memo of tokenized command texts, shared by every parse
    /// this simulation runs — batches, module bodies, stored expressions.
    /// Where <see cref="planCache"/> skips parsing for the one statement kind
    /// that produces a re-executable plan, this skips <em>tokenizing</em> for
    /// every statement kind, including the DML that parses and executes in one
    /// pass. See <see cref="TokenMemo"/> for why it needs no invalidation.
    /// </summary>
    internal readonly TokenMemo TokenMemo = new();

    /// <summary>Test-observable: total hits on the plan cache since
    /// construction. Incremented after a key match against a non-stale
    /// entry, just before <c>ReplayCachedSelection</c> is called.</summary>
    internal long PlanCacheHits;

    /// <summary>Test-observable: total misses on the plan cache where an
    /// eligible command text fell through to the full parse path (either no
    /// key, no entry, or a stale-version entry). Incremented exactly once
    /// per eligible call that doesn't hit.</summary>
    internal long PlanCacheMisses;

    /// <summary>Test-observable: live count of entries in the plan cache.</summary>
    internal int PlanCacheCount => Volatile.Read(ref this.planCacheCount);

    /// <summary>Cache key for <see cref="planCache"/> and <see cref="statementPlanSets"/>. The schema-version
    /// is intentionally NOT part of the key — it sits on the entry so a stale
    /// lookup overwrites in place rather than orphaning entries on every DDL.
    /// The session's QUOTED_IDENTIFIER setting IS part of the key: it changes
    /// how <c>"…"</c> tokenizes, so the same text parses to different plans
    /// under each setting (mirroring real SQL Server, whose plan-cache keys
    /// fold in the parse-time SET options).
    /// <para>
    /// The session's isolation level isn't a key component but a cacheability
    /// gate: a plan's FROM sources carry the lock acquisitions their parsing
    /// session made, so replaying one under a different level would settle the
    /// wrong session's protection, or none — a SERIALIZABLE reader's phantom
    /// fence most visibly. Anything but the default READ COMMITTED therefore
    /// skips both the lookup and the promotion and re-parses per execution.
    /// </para></summary>
    internal readonly struct PlanCacheKey(string commandText, string databaseName, string defaultSchemaName, string parameterSignature, bool quotedIdentifiers, DateOrder dateFormat, bool ansiNulls, bool concatNullYieldsNull)
        : IEquatable<PlanCacheKey>
    {
        public readonly string CommandText = commandText;
        public readonly string DatabaseName = databaseName;

        /// <summary>
        /// The effective principal's default schema
        /// (<see cref="BatchContext.DefaultSchemaName"/>), which an unqualified
        /// name searches before <c>dbo</c>: a plan answers only principals
        /// whose default schema is the one it was parsed under, so principals
        /// sharing one — <c>dbo</c> the common case — share plans. Real keys
        /// its plan cache the same way, as the <c>user_id</c> plan attribute,
        /// which holds the default schema's id rather than the user's (probed
        /// 2026-10-04 against SQL Server 2025: two users defaulting to one
        /// schema reuse one plan); real shares a text naming nothing
        /// unqualified across every schema (<c>user_id</c> -2), which the
        /// simulator doesn't distinguish.
        /// </summary>
        public readonly string DefaultSchemaName = defaultSchemaName;
        public readonly string ParameterSignature = parameterSignature;
        public readonly bool QuotedIdentifiers = quotedIdentifiers;

        /// <summary>
        /// The <c>SET DATEFORMAT</c> order, which real counts among the
        /// options a plan is cached under: a date string read while parsing
        /// reads differently under another.
        /// </summary>
        public readonly DateOrder DateFormat = dateFormat;

        /// <summary>
        /// <c>ANSI_NULLS</c> and <c>CONCAT_NULL_YIELDS_NULL</c>, which a
        /// comparison and a string <c>+</c> capture while parsing.
        /// </summary>
        public readonly bool AnsiNulls = ansiNulls, ConcatNullYieldsNull = concatNullYieldsNull;

        // Implemented rather than inherited: this is a dictionary key, and
        // ValueType.Equals would box both sides and compare them by reflection.
        // Ordinal string comparison is what EqualityComparer<string>.Default
        // does, so the key keeps the exact identity it had before.
        public bool Equals(PlanCacheKey other) =>
            this.QuotedIdentifiers == other.QuotedIdentifiers
            && this.DateFormat == other.DateFormat
            && this.AnsiNulls == other.AnsiNulls
            && this.ConcatNullYieldsNull == other.ConcatNullYieldsNull
            && string.Equals(this.CommandText, other.CommandText, StringComparison.Ordinal)
            && string.Equals(this.DatabaseName, other.DatabaseName, StringComparison.Ordinal)
            && string.Equals(this.DefaultSchemaName, other.DefaultSchemaName, StringComparison.Ordinal)
            && string.Equals(this.ParameterSignature, other.ParameterSignature, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is PlanCacheKey other && this.Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(this.CommandText, this.DatabaseName, this.DefaultSchemaName, this.ParameterSignature, this.QuotedIdentifiers, this.DateFormat, this.AnsiNulls, this.ConcatNullYieldsNull);
    }

    /// <summary>Cache entry: the batch's parsed <see cref="Selection"/>s in
    /// dispatch order, plus the <see cref="SchemaVersion"/> active when they
    /// were parsed. Usually one; a batch of several top-level SELECTs caches
    /// as the sequence it is.</summary>
    private sealed class PlanCacheEntry(Selection[] plans, ReplayedLock[][] locks, (int Start, int End, int Line)[] spans, long schemaVersionAtParse)
    {
        public readonly Selection[] Plans = plans;

        /// <summary>Where each of <see cref="Plans"/> is written in the command, for its Query Store capture, and the line it starts on, for its errors.</summary>
        public readonly (int Start, int End, int Line)[] Spans = spans;

        /// <summary>The locks each of <see cref="Plans"/> took as it parsed, which its replay retakes.</summary>
        public readonly ReplayedLock[][] Locks = locks;
        public readonly long SchemaVersionAtParse = schemaVersionAtParse;
    }

    /// <summary>
    /// Promotes a freshly-parsed top-level <see cref="Selection"/> into the
    /// per-instance plan cache when the batch context's stashed key
    /// components are set and the live <see cref="SchemaVersion"/> still
    /// matches the version captured at batch start. Called from
    /// <see cref="RunSelectStatement"/> AFTER row materialization but BEFORE
    /// the dispatch yields the outcome — so the entry is in the
    /// cache by the time the consumer sees the first row, even if the
    /// consumer disposes the reader without draining the rest of the
    /// iterator. The caller is responsible for the upstream gates (block
    /// depth, first statement, no session-scoped references, parser at EOB).
    /// </summary>
    internal void TryPromoteSelectionsToPlanCache(BatchContext batch)
    {
        if (batch.PlanCacheSequence is not { Count: > 0 } plans) return;
        if (batch.PlanCacheCommandText is not { } text) return;
        if (batch.PlanCacheDatabaseName is not { } dbName) return;
        if (batch.PlanCacheParameterSignature is not { } paramSig) return;
        if (Volatile.Read(ref this.SchemaVersion) != batch.PlanCacheSchemaVersion) return;
        // A cacheable batch is SELECTs only (no SET can be among them), so the
        // connection's live setting still equals the value at parse.
        var key = new PlanCacheKey(text, dbName, batch.PlanCacheKey!.Value.DefaultSchemaName, paramSig, batch.Connection.QuotedIdentifiers, batch.Connection.DateFormat, batch.Connection.AnsiNulls, batch.Connection.ConcatNullYieldsNull);
        // Refresh-in-place semantics: when a DDL has invalidated the prior
        // entry under this key, the indexer overwrites without growing the
        // dictionary. The capacity cap therefore only gates fresh keys, not
        // re-cached versions of an already-tracked one.
#if DEBUG
        PlanCacheCaptureAudit.Verify(plans, text);
        PlanCacheCaptureAudit.VerifyPrincipalIndependent(batch.PrincipalReadWhileParsing, text);
#endif
        var entry = new PlanCacheEntry([.. plans], [.. batch.PlanCacheSequenceLocks!], [.. batch.PlanCacheSequenceSpans!], batch.PlanCacheSchemaVersion);
        if (this.planCache.ContainsKey(key))
            this.planCache[key] = entry;
        else if (this.PlanCacheCount < PlanCacheCapacity && this.planCache.TryAdd(key, entry))
            _ = Interlocked.Increment(ref this.planCacheCount);
    }

    /// <summary>
    /// Completes an error a replayed <c>SELECT</c> raised as the dispatch loop
    /// completes a parsed statement's (<c>StatementLifecycle.SettleError</c>):
    /// redacted when a security predicate applied while it ran, attributed to
    /// the statement's line, and with the rollbacks real performs before the
    /// error reaches anyone — a deadlock victim's, the transaction-aborting
    /// class's and <c>SET XACT_ABORT ON</c>'s.
    /// </summary>
    private static SimulatedSqlException SettleReplayedError(BatchContext batch, SimulatedSqlException thrown, int rowSecurityMarks)
    {
        var connection = batch.Connection;
        var ex = connection.RowSecurityMarks != rowSecurityMarks && thrown.RedactedForRowSecurity() is { } redacted ? redacted : thrown;
        ex.ResolveDiagnostics(batch.CurrentStatement.StartLine, batch.LineOffset, batch.ErrorProcedureName);
        if (!ex.RaisingScopeRecorded)
        {
            ex.RaisingScopeRecorded = true;
            ex.RaisedByClientSelect = true;
        }
        if (ex.IsAttention && !ex.AttentionSettled)
        {
            ex.AttentionSettled = true;
            connection.AttentionEndedWrite = false;
        }
        if (ex.Class == 13 && connection.CurrentTransaction is { } victim)
        {
            if (connection.OpenTryFrames > 0 && !victim.Doomed)
                victim.UndoAsDeadlockVictim();
            else
                victim.EndRollback();
        }
        if (ex.AbortsTransaction)
            connection.CurrentTransaction?.EndRollback();
        ApplyXactAbortPromotion(connection, ex);
        connection.LastStatementRowCount = 0;
        return ex;
    }

    /// <summary>
    /// Empties the plan cache and the compiled-batch memo beside it — every
    /// entry, or with <paramref name="sqlHandle"/> those whose command text it
    /// hashes (<c>sys.dm_exec_requests.sql_handle</c>'s value) — and, for a
    /// whole-cache clear, the token memo too, so the next execution of any text
    /// tokenizes and parses afresh: <c>DBCC FREEPROCCACHE</c> and
    /// <c>DBCC FREESYSTEMCACHE('ALL')</c>. With <paramref name="database"/>,
    /// only the entries compiled in it, the token memo kept: <c>ALTER DATABASE
    /// SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE</c>.
    /// </summary>
    internal void ClearPlanCache(byte[]? sqlHandle = null, Database? database = null)
    {
        bool Matches(PlanCacheKey key) =>
            (sqlHandle is null || BuiltInResources.SqlHandleOf(key.CommandText).AsSpan().SequenceEqual(sqlHandle.AsSpan(0, BuiltInResources.SqlHandleLength)))
            && (database is null || BuiltInToken.Equals(key.DatabaseName, database.Name));
        foreach (var (key, _) in this.planCache)
        {
            if (Matches(key) && this.planCache.TryRemove(key, out _))
                _ = Interlocked.Decrement(ref this.planCacheCount);
        }
        foreach (var (key, _) in this.compiledBatches)
        {
            if (Matches(key) && this.compiledBatches.TryRemove(key, out _))
                _ = Interlocked.Decrement(ref this.compiledBatchCount);
        }
        foreach (var (key, _) in this.statementPlanSets)
        {
            if (Matches(key) && this.statementPlanSets.TryRemove(key, out _))
                _ = Interlocked.Decrement(ref this.statementPlanSetCount);
        }
        if (sqlHandle is null)
        {
            this.ForgetSentInliningFailures(database);
            _ = Interlocked.Increment(ref this.modulePlanGeneration);
        }
        if (sqlHandle is null && database is null)
        {
            this.TokenMemo.Clear();
            this.ClearQueryStoreShapes();
        }
    }

    /// <summary>
    /// Whether the statement just parsed is the batch's last: nothing remains
    /// but statement separators. Probes forward from the parser's lookahead
    /// position and restores it, so the caller's cursor is untouched.
    /// <para>
    /// The separator tolerance is what admits the <c>SELECT … ;</c> form every
    /// client that terminates its statements emits. A bare <c>Token is null</c>
    /// test would decline those, and declining them is the difference between
    /// a cache that serves an ORM and one that serves only text with no
    /// trailing punctuation.
    /// </para>
    /// </summary>
    private static bool IsAtEndOfBatch(ParserContext context)
    {
        if (context.Token is null)
            return true;
        if (context.Token is not Operator { Character: ';' })
            return false;
        var checkpoint = context.SaveCheckpoint();
        try
        {
            while (context.Token is Operator { Character: ';' })
                context.MoveNextOptional();
            return context.Token is null;
        }
        catch (SimulatedSqlException)
        {
            // Text the tokenizer refuses lies past the separators. That is not
            // the end of the batch, and raising from here would report the
            // error ahead of the result set this statement has already
            // produced — the dispatch loop reaches the same text a moment
            // later and reports it in its own place.
            return false;
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// Builds the plan-cache key for a command, or returns <see langword="null"/>
    /// when caching can't apply (no text, no connection, no current database).
    /// The parameter signature folds in each parameter's name, declared
    /// <see cref="DbParameter.DbType"/>, declared
    /// <see cref="DbParameter.Size"/> / <see cref="DbParameter.Precision"/> /
    /// <see cref="DbParameter.Scale"/> in <see cref="DbCommand.Parameters"/>
    /// declaration order — variations in any of those alter parse-time type
    /// inference (e.g. result column types when the SELECT projects a
    /// parameter) and so demand a separate cached plan.
    /// </summary>
    private static PlanCacheKey? TryBuildPlanCacheKey(SimulatedDbCommand command)
        => string.IsNullOrEmpty(command.CommandText) || command.Connection is not { } connection
            ? null
            : BuildPlanCacheParameterSignature(command) is { } sig
                ? PlanCacheKeyFor(connection, command.CommandText, sig)
                : null;

    /// <summary>
    /// The key a dynamic batch's compile is remembered under (see
    /// <see cref="CompileBatch"/>): its text, and in place of a command's
    /// parameter signature <paramref name="declarations"/> — how the batch was
    /// called and, for <c>sp_executesql</c>, its parameter definitions, which
    /// settle every type its variables start with — so no top-level command's
    /// key can match it. Null where the plan cache's own gates say no.
    /// </summary>
    private static PlanCacheKey? DynamicBatchKey(SimulatedDbConnection connection, string text, string declarations) =>
        connection.InsertExecTargetTypes is null ? PlanCacheKeyFor(connection, text, "\0" + declarations) : null;

    /// <summary>
    /// The key a module body's statement plans are filed under, taken as the
    /// first of its statements that may have one asks
    /// (<see cref="BatchContext.StatementPlanModule"/>): its text, and in place of a
    /// command's parameter signature the module's object id — the module's
    /// parameters, its schema and its creation-time settings settle what its
    /// body binds to, and altering it moves the schema version every entry is
    /// stamped with — so no command's or dynamic batch's key can match it.
    /// Null where the plan cache's own gates say no.
    /// </summary>
    private static PlanCacheKey? ModuleBodyKey(SimulatedDbConnection connection, Schemas.SchemaObject module, string bodyText) =>
        PlanCacheKeyFor(connection, bodyText, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\u0001{module.ObjectId}"));

    /// <summary>
    /// Files <paramref name="body"/>'s statement plans under <paramref name="key"/>,
    /// so a statement that ran there before replays its plan: a procedure's,
    /// trigger's, function's or dynamic batch's, keyed by
    /// <see cref="ModuleBodyKey"/> or <see cref="DynamicBatchKey"/> and taken
    /// once the body's settings and database are in place.
    /// </summary>
    private void AttachStatementPlans(BatchContext body, PlanCacheKey? key)
    {
        if (key is not { } attached)
            return;
        body.PlanCacheKey = attached;
        body.StatementPlans = this.statementPlanSets.TryGetValue(attached, out var plans) ? plans : null;
    }

    /// <summary>
    /// <paramref name="text"/>'s key under the session's current settings, or
    /// null where caching can't apply: no current database, a session outside
    /// the default READ COMMITTED, or one of the options a parse settles.
    /// </summary>
    private static PlanCacheKey? PlanCacheKeyFor(SimulatedDbConnection connection, string text, string signature) =>
        connection is { CurrentDatabase: { } currentDb }
            && connection.SessionIsolationLevel == System.Data.IsolationLevel.ReadCommitted
            && !connection.NoBrowseTable
            // A replayed plan opens no implicit transaction and runs under
            // NOEXEC / PARSEONLY / FMTONLY, each of which a parse settles.
            && !connection.ImplicitTransactions && !connection.NoExec && !connection.ParseOnly && !connection.FmtOnly
            // A replay reports no STATISTICS IO / TIME, and a compile that
            // reports its time has to run.
            && !connection.StatisticsIo && !connection.StatisticsTime
                ? new PlanCacheKey(text, currentDb.Name, connection.Security.EffectiveDefaultSchemaName(currentDb), signature, connection.QuotedIdentifiers, connection.DateFormat, connection.AnsiNulls, connection.ConcatNullYieldsNull)
                : null;

    private static string? BuildPlanCacheParameterSignature(SimulatedDbCommand command)
    {
        var parameters = command.Parameters;
        if (parameters.Count == 0)
            return "";
        var sb = new System.Text.StringBuilder();
        foreach (SimulatedDbParameter p in parameters)
        {
            if (sb.Length > 0)
                _ = sb.Append('|');
            // DbType is a stable shorthand that already covers the type-
            // inference dimension parser code reads (string vs numeric vs
            // temporal); precision / scale / size catch the
            // decimal(p,s) / varchar(n) variants that bind through the same
            // DbType. ParameterName is included because the parser resolves
            // VariableReference by name. The getter raises ArgumentException
            // for an unmapped CLR Value (the TVP IDataReader binding path is
            // the live case) — returning null bypasses the cache for this
            // command, which is the right behavior anyway since structured
            // parameters carry session-scoped data the cache doesn't model.
            DbType dbType;
            try
            {
                dbType = p.DbType;
            }
            catch (ArgumentException)
            {
                return null;
            }
            _ = sb.Append(p.ParameterName).Append(':').Append((int)dbType).Append(':')
                .Append(p.Size).Append(':').Append((int)p.Precision).Append(':').Append((int)p.Scale);
        }
        return sb.ToString();
    }

    /// <summary>
    /// What a replayed statement gives back as it ends — the reads it counted,
    /// the LOB epoch and statement snapshot it announced — which a statement
    /// whose rows went out as its client read them gives back after its last.
    /// </summary>
    private static void EndReplayedStatement(SimulatedDbConnection connection, IoStatistics? queryStoreIo, bool announcedReader)
    {
        if (queryStoreIo is not null)
            connection.StatementIo = null;
        if (announcedReader)
        {
            LobReclamation.Leave(connection.Session);
            Volatile.Write(ref connection.Session.StatementSnapshotXid, long.MaxValue);
        }
    }

    /// <summary>
    /// Replays a cached batch's <see cref="Selection"/> sequence against a fresh
    /// <see cref="BatchContext"/> for the incoming command, mirroring the
    /// <see cref="RunSelectStatement"/> for outcome
    /// shape (result-set vs assignment-only NonQuery) and for
    /// <see cref="SimulatedDbConnection.LastStatementRowCount"/>
    /// maintenance. Bypasses tokenization and parsing entirely.
    /// </summary>
    /// <remarks>
    /// The first statement's schema locks can wait out a definition change
    /// that leaves the plans stale; the replay then stops having run nothing
    /// and says so through <paramref name="stale"/>, for the caller to parse
    /// the batch instead.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> ReplayCachedSelections(SimulatedDbCommand command, PlanCacheEntry entry, System.Runtime.CompilerServices.StrongBox<bool> stale)
    {
        var batch = new BatchContext(command);
        try
        {
            var connection = batch.Connection;
            for (var statement = 0; statement < entry.Plans.Length; statement++)
            {
                if (statement > 0 && command.YieldsBetweenStatements)
                    yield return new SimulatedStatementBoundary();
                var selection = entry.Plans[statement];
                // Replay bypasses the dispatch loop, so each statement stamps
                // the per-statement frame the loop's top-of-iteration would.
                // Without the clock a replayed GETDATE() reads
                // default(DateTime) rather than now; without the per-statement
                // clears a second statement would read the first's frozen
                // RAND() draw and cached subquery results. StartLine mirrors
                // the single-statement dispatch value for ERROR_LINE parity.
                batch.CurrentStatement.UtcNow = DateTime.UtcNow;
                batch.CurrentStatement.StartLine = entry.Spans[statement].Line;
                batch.CurrentStatement.AutocommitTransactionId = 0;
                batch.CurrentStatement.StatementScopedValues = null;
                batch.CurrentStatement.SubqueryResults = null;
                batch.CurrentStatement.RowAddresses = null;
                batch.CurrentStatement.ReadsSnapshot = false;
                batch.CurrentStatement.CatalogViewRows = null;
#if DEBUG
                batch.CurrentStatement.AuditedCatalogRowSets = null;
#endif
                batch.CurrentStatement.ComputedUniqueKeys = null;
                batch.CurrentStatement.LockTallies = null;
                batch.CurrentStatement.EscalatedTables = null;
                batch.CurrentStatement.NullEliminated = false;
                batch.CurrentStatement.OwesOverflowNotice = batch.CurrentStatement.OwesDivideByZeroNotice = false;
                batch.RcsiStatementSnapshotXid = null;
                batch.BumpRowStamp();
                // The parse this replay skips took these locks; the
                // replaying session takes them now, and gives back the
                // statement-scoped ones when the statement ends — when the
                // consumer moves past it, as in the dispatch loop, or in the
                // finally below.
                var current = batch.TakeReplayedLocks(entry.Locks[statement], statement == 0 ? entry.SchemaVersionAtParse : null);
                if (statement == 0 && (!current || Volatile.Read(ref connection.Simulation.SchemaVersion) != entry.SchemaVersionAtParse))
                {
                    batch.ReleaseStatementSchemaLocks();
                    stale.Value = true;
                    yield break;
                }
                // The cached plan is shared across principals; re-run the
                // SELECT permission check against the replaying session's
                // current principal.
                PermissionEnforcement.CheckReadSources(batch, selection.ReferencedSecurables, selection.ReadColumnsByObject);
                batch.CurrentStatement.SendsRows = selection.IntoTarget is null && !selection.IsAssignmentOnly;
                // As in the dispatch loop, rows before a failing one go out
                // ahead of its error (see EndedByError).
                SimulatedSqlResultSet? executed = null;
                SimulatedSqlException? cutShort = null;
                int rowCount;
                // Query Store times a replayed statement as the dispatch loop
                // does a parsed one.
                var queryStore = BeginQueryStoreCapture(batch, io: null);
                var queryStoreIo = queryStore is not null ? connection.StatementIo = new IoStatistics() : null;
                // As the dispatch loop's statements do (see LobReclamation).
                var announcedReader = connection.Simulation.LobReclamation.Enter(connection.Session);
                var rowSecurityMarks = connection.RowSecurityMarks;
                // A result larger than real gets ahead of its client goes out
                // as the client reads it, as the dispatch loop sends one.
                ResultStream? stream = null;
                try
                {
                    executed = DataMasking.ForClient(selection.Execute(batch), selection.ColumnMasks, batch).WithRowCountLimit(connection.RowCountLimit);
                    if (batch.StreamsResultRows && !selection.IsAssignmentOnly)
                        stream = executed.BeginStreaming(batch.CurrentStatement, batch.Connection);
                    rowCount = stream is null ? executed.MaterializeRows() : 0;
                    if (selection.CountsForClauseSourceRows)
                    {
                        if (stream is null)
                            rowCount = executed.ReportedRowCount = batch.CurrentStatement.ForClauseSourceRows;
                        else
                            stream.CountsForClauseSourceRows = true;
                    }
                    if (stream is null && queryStore is { } capture)
                        EndQueryStoreCapture(batch, capture, queryStoreIo, entry.Spans[statement].Start, entry.Spans[statement].End, 0, rowCount);
                }
                catch (SimulatedSqlException error) when (!selection.IsAssignmentOnly)
                {
                    if (queryStore is { } capture)
                        EndQueryStoreCapture(batch, capture, queryStoreIo, entry.Spans[statement].Start, entry.Spans[statement].End, error.IsAttention ? (byte)3 : (byte)4, 0);
                    cutShort = error;
                    rowCount = 0;
                    executed ??= new SimulatedSqlResultSet(selection.Schema, selection.ColumnNames, new List<byte[]>())
                    {
                        ColumnNullability = selection.ColumnNullability,
                        ColumnReportsNumeric = selection.ColumnReportsNumeric,
                        ColumnAliasTypes = selection.ColumnAliasTypes,
                        ColumnIdentitySources = selection.ColumnIdentitySources,
                        ColumnWireFlags = selection.ColumnWireFlags,
                        HiddenColumnCount = selection.HiddenColumnCount,
                        Browse = selection.Browse,
                    };
                    executed.EndedByError = true;
                    executed.ErrorCaught = CaughtByTryFrame(batch, error);
                }
                finally
                {
                    if (stream is null)
                        EndReplayedStatement(connection, queryStoreIo, announcedReader);
                }
                if (stream is null)
                    connection.LastStatementRowCount = rowCount;
                var replayed = selection.IsAssignmentOnly
                    ? new SimulatedNonQuery(rowCount, countsRowsReturned: true)
                    : (SimulatedStatementOutcome)executed;
                // Replay bypasses the dispatch loop, so it stamps the NOCOUNT
                // suppression and the TEXTSIZE the loop's post-statement
                // walk would have.
                replayed.CountSuppressed = connection.NoCount;
                if (connection.TextSize >= 0 && replayed is SimulatedQueryResult query)
                    query.ClientTextSize = connection.TextSize;
                foreach (var message in DrainPendingMessages(connection))
                    yield return message;
                if (stream is null)
                {
                    yield return replayed;
                }
                else
                {
                    var finished = false;
                    try
                    {
                        stream.Suspend(batch);
                        yield return replayed;
                        while (true)
                        {
                            stream.Resume(batch);
                            stream.Produce();
                            if (stream.Complete)
                                break;
                            stream.Suspend(batch);
                            yield return ResultStream.Marker;
                        }
                        finished = true;
                    }
                    finally
                    {
                        if (!finished)
                        {
                            stream.Resume(batch);
                            stream.Abandon();
                        }
                        EndReplayedStatement(connection, queryStoreIo, announcedReader);
                    }
                    rowCount = stream.Error is null ? stream.StatementRowCount(batch) : 0;
                    connection.LastStatementRowCount = rowCount;
                    if (stream.Error is { } streamed)
                    {
                        cutShort = streamed;
                        executed.EndedByError = true;
                        executed.ErrorCaught = CaughtByTryFrame(batch, streamed);
                    }
                    if (queryStore is { } streamedCapture)
                        EndQueryStoreCapture(batch, streamedCapture, queryStoreIo, entry.Spans[statement].Start, entry.Spans[statement].End, cutShort is null ? (byte)0 : cutShort.IsAttention ? (byte)3 : (byte)4, rowCount);
                    // The advance that ran the statement to its end returns
                    // here, before anything its ending sends.
                    yield return ResultStream.Marker;
                }
                if (cutShort is not null)
                {
                    // Settled and sent as the dispatch loop settles and sends
                    // a parsed statement's error: the batch carries on past
                    // one that ends only its statement, stops at one that ends
                    // the batch, and propagates any other.
                    var settled = SettleReplayedError(batch, cutShort, rowSecurityMarks);
                    batch.BatchAborted = EndsBatch(settled);
                    if (!batch.BatchAborted && !IsStatementTerminating(settled))
                        ExceptionDispatchInfo.Throw(settled);
                    connection.LastErrorNumber = settled.AtAtErrorNumber;
                    var errorOutcome = new SimulatedErrorOutcome(settled);
                    if (connection.FramesEveryStatement)
                    {
                        errorOutcome.DoneKind = batch.BatchAborted ? StatementDoneKind.Batch : StatementDoneKind.Select;
                        errorOutcome.TransactionEventMark = connection.TransactionEventsRecorded;
                    }
                    yield return errorOutcome;
                    if (settled.FollowingMessage is { } following)
                        yield return new SimulatedInfoOutcome(following, followsRows: true);
                    if (IsStatementTerminationNoticed(batch, settled))
                        yield return new SimulatedInfoOutcome(SimulatedSqlException.StatementTerminatedMessage(batch, settled), followsRows: true);
                    batch.ReleaseStatementSchemaLocks();
                    if (batch.BatchAborted)
                        break;
                    continue;
                }
                // A statement that succeeded clears @@ERROR, as the dispatch
                // loop's does.
                connection.LastErrorNumber = 0;
                if (batch.CurrentStatement.NullEliminated && connection.AnsiWarnings)
                    yield return NullEliminatedWarning(batch);
                foreach (var notice in ArithmeticNotices(batch))
                    yield return notice;
                batch.ReleaseStatementSchemaLocks();
            }

            WriteBackOutputParameters(batch);
        }
        finally
        {
            batch.ReleaseStatementSchemaLocks();
            // Whatever a consumer that stopped early left unread goes with it.
            batch.Connection.PendingMessages.Clear();
        }
    }
}
