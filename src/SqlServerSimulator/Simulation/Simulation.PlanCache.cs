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

    /// <summary>Cache key for <see cref="planCache"/> and <see cref="dmlPlanSets"/>. The schema-version
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
    private sealed class PlanCacheEntry(Selection[] plans, ReplayedLock[][] locks, (int Start, int End)[] spans, long schemaVersionAtParse)
    {
        public readonly Selection[] Plans = plans;

        /// <summary>Where each of <see cref="Plans"/> is written in the command, for its Query Store capture.</summary>
        public readonly (int Start, int End)[] Spans = spans;

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
        foreach (var (key, _) in this.dmlPlanSets)
        {
            if (Matches(key) && this.dmlPlanSets.TryRemove(key, out _))
                _ = Interlocked.Decrement(ref this.dmlPlanSetCount);
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
        => string.IsNullOrEmpty(command.CommandText)
            ? null
            : command.Connection is { CurrentDatabase: { } currentDb } connection
                && connection.SessionIsolationLevel == System.Data.IsolationLevel.ReadCommitted
                && !connection.NoBrowseTable
                // A replayed plan opens no implicit transaction and runs under
                // NOEXEC / PARSEONLY / FMTONLY, each of which a parse settles.
                && !connection.ImplicitTransactions && !connection.NoExec && !connection.ParseOnly && !connection.FmtOnly
                // A replay reports no STATISTICS IO / TIME, and a compile that
                // reports its time has to run.
                && !connection.StatisticsIo && !connection.StatisticsTime
                && BuildPlanCacheParameterSignature(command) is { } sig
                    ? new PlanCacheKey(command.CommandText, currentDb.Name, connection.Security.EffectiveDefaultSchemaName(currentDb), sig, connection.QuotedIdentifiers, connection.DateFormat, connection.AnsiNulls, connection.ConcatNullYieldsNull)
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
                var selection = entry.Plans[statement];
                // Replay bypasses the dispatch loop, so each statement stamps
                // the per-statement frame the loop's top-of-iteration would.
                // Without the clock a replayed GETDATE() reads
                // default(DateTime) rather than now; without the per-statement
                // clears a second statement would read the first's frozen
                // RAND() draw and cached subquery results. StartLine mirrors
                // the single-statement dispatch value for ERROR_LINE parity.
                batch.CurrentStatement.UtcNow = DateTime.UtcNow;
                batch.CurrentStatement.StartLine = 1;
                batch.CurrentStatement.AutocommitTransactionId = 0;
                batch.CurrentStatement.StatementScopedValues = null;
                batch.CurrentStatement.SubqueryResults = null;
                batch.CurrentStatement.RowAddresses = null;
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
                try
                {
                    executed = DataMasking.ForClient(selection.Execute(batch), selection.ColumnMasks, batch).WithRowCountLimit(connection.RowCountLimit);
                    rowCount = executed.MaterializeRows();
                    if (selection.CountsForClauseSourceRows)
                        rowCount = executed.ReportedRowCount = batch.CurrentStatement.ForClauseSourceRows;
                    if (queryStore is { } capture)
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
                    if (queryStoreIo is not null)
                        connection.StatementIo = null;
                    if (announcedReader)
                    {
                        LobReclamation.Leave(connection.Session);
                        Volatile.Write(ref connection.Session.StatementSnapshotXid, long.MaxValue);
                    }
                }
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
                yield return replayed;
                if (cutShort is not null)
                    ExceptionDispatchInfo.Throw(cutShort);
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
