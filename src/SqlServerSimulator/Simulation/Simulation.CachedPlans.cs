using System.Collections.Concurrent;
using System.Diagnostics;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

// The plan cache as sys.dm_exec_cached_plans and its siblings report it: which
// batches and modules real keeps a plan for, and what their statements and
// calls measured — see docs/claude/plan-cache.md#the-plan-cache-dmvs.
partial class Simulation
{
    /// <summary>The plans real would hold, which the plan-cache DMVs read.</summary>
    internal readonly CachedPlanRegistry CachedPlans = new();

    /// <summary>
    /// Per batch text the compile walked, where each of its statements ends
    /// (see <see cref="StatementEnds"/>), as real's <c>statement_end_offset</c>
    /// and statement events place it.
    /// </summary>
    private readonly ConcurrentDictionary<string, StatementEnds> statementEnds = new(StringComparer.Ordinal);

    /// <summary>How many texts <see cref="statementEnds"/> holds, counted as the other compile memos are.</summary>
    private int statementEndsCount;

    /// <summary>
    /// Publishes where the statements of a batch the compile walked end,
    /// answering the ends a run of the text fills in — <paramref name="ends"/>,
    /// or an earlier walk's still missing some — or null when nothing needs
    /// filling in.
    /// </summary>
    internal StatementEnds? NoteStatementEnds(string batchText, StatementEnds ends)
    {
        ends.Publish();
        if (ends.IsEmpty && !ends.Incomplete)
            return null;
        if (this.statementEnds.TryGetValue(batchText, out var earlier))
        {
            // A complete walk replaces one that left ends to its run.
            if (earlier.Incomplete && !ends.Incomplete && this.statementEnds.TryUpdate(batchText, ends, earlier))
                return null;
            return earlier.Incomplete ? earlier : null;
        }
        if (Volatile.Read(ref this.statementEndsCount) >= PlanCacheCapacity)
            return null;
        if (this.statementEnds.TryAdd(batchText, ends))
            _ = Interlocked.Increment(ref this.statementEndsCount);
        return ends.Incomplete ? ends : null;
    }

    /// <summary>
    /// The two ends of the statement of <paramref name="batchText"/> starting
    /// at <paramref name="startIndex"/>, as <see cref="statementEnds"/> holds
    /// them; null when no walk or run recorded it.
    /// </summary>
    internal (int Tight, int Loose)? StatementEndIndex(string batchText, int startIndex) =>
        this.statementEnds.TryGetValue(batchText, out var ends) && ends.TryGet(startIndex, out var end) ? end : null;

    /// <summary>
    /// <paramref name="batchText"/> with the statements carrying a secret
    /// masked, as a batch event and a cached plan report it, or null for a
    /// text carrying none (see <see cref="SecretStatements"/>) — one search
    /// for the marker words, which nearly every text stops at.
    /// </summary>
    internal string? MaskedBatchText(string? batchText) =>
        SecretStatements.MayCarry(batchText) && this.statementEnds.TryGetValue(batchText!, out var ends) ? ends.MaskedText(batchText!) : null;

    /// <summary>
    /// Whether <paramref name="batchText"/> carries a secret, which keeps it
    /// from every session surface: no <c>sys.dm_exec_sql_text</c> row for its
    /// handle, a NULL input buffer, an empty deadlock input buffer.
    /// </summary>
    internal bool HidesBatchText(string? batchText) =>
        SecretStatements.MayCarry(batchText) && this.statementEnds.TryGetValue(batchText!, out var ends) && ends.HoldsSecret;

    /// <summary>Whether every secret <paramref name="batchText"/> carries is a query's — <c>SELECT WITHOUT QUERY</c>.</summary>
    internal bool SecretsAreQueries(string batchText) =>
        this.statementEnds.TryGetValue(batchText, out var ends) && ends.SecretsAreQueries;

    /// <summary>
    /// Whether the secrets <paramref name="batchText"/> may carry are known —
    /// its statements walked — so a batch event can mask them as it fires.
    /// </summary>
    internal bool SecretsKnown(string batchText) =>
        !SecretStatements.MayCarry(batchText) || this.statementEnds.ContainsKey(batchText);

    /// <summary>
    /// The cached plan the statements of <paramref name="batch"/>, a client's
    /// or a dynamic batch, are recorded under — looked up or added at the
    /// batch's first statement real keeps a plan for, which counts the batch's
    /// use of it. Null for a batch real doesn't cache: a module body whose
    /// call kept no plan, and a client or dynamic batch declaring a table
    /// variable (probed 2026-10-15 against SQL Server 2025: neither
    /// <c>DECLARE @t TABLE …; SELECT * FROM @t</c> nor <c>…; INSERT @t SELECT
    /// … FROM t</c> leaves a plan).
    /// </summary>
    internal static CachedPlan? CachedPlanOfBatch(BatchContext batch, int userId)
    {
        if (batch.CachedPlan is { } plan)
            return plan;
        if (batch.CachedPlanDeclined || batch.CachedPlanOrigin is CachedPlanOrigin.None or CachedPlanOrigin.Module)
            return null;
        if (batch.TableVariables is { Count: > 0 } || batch.CachedPlanDatabase is not { } database)
        {
            batch.CachedPlanDeclined = true;
            return null;
        }

        if (batch.CachedPlanMemo?.Value is { Removed: false } memo && memo.Key.DatabaseId == database.Id && SettingsMatch(batch, memo.Key))
        {
            _ = Interlocked.Increment(ref memo.UseCount);
            batch.CachedPlan = memo;
            NoteRunningPlan(batch, memo);
            return memo;
        }

        var command = batch.Parser.Command;
        string? declaration;
        try
        {
            declaration = batch.CachedPlanOrigin switch
            {
                CachedPlanOrigin.ClientBatch when command.Parameters.Count != 0 || command.ParameterDeclaration is not null =>
                    command.ParameterDeclaration ?? SqlClientParameterDeclaration(command.Parameters),
                CachedPlanOrigin.ExecuteSql when batch.CachedPlanDeclaration is { Length: > 0 } declared => declared,
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            // A parameter whose value maps to no DbType — the simulator's own
            // commands binding SqlValues, a TVP bound to a reader — has no
            // declaration SqlClient would send, so its batch keeps no plan.
            batch.CachedPlanDeclined = true;
            return null;
        }
        var kind = declaration is null ? CachedPlanKind.Adhoc : CachedPlanKind.Prepared;
        // A batch carrying a secret is cached under its masked text — one
        // whose secret is a SELECT's, as real keeps none for a login's DDL
        // (probed 2026-10-16 against SQL Server 2025).
        var masked = batch.Connection.Simulation.MaskedBatchText(command.CommandText);
        if (masked is not null && !batch.Connection.Simulation.SecretsAreQueries(command.CommandText))
        {
            batch.CachedPlanDeclined = true;
            return null;
        }
        var cachedText = masked ?? command.CommandText;
        plan = GetOrAddBatchPlan(batch, kind, cachedText, declaration, database, userId, out var created);
        _ = Interlocked.Increment(ref plan.UseCount);
        batch.CachedPlan = plan;
        batch.CachedPlanCompiled = created;
        if (batch.CachedPlanMemo is { } memoSlot)
            memoSlot.Value = plan;
        NoteRunningPlan(batch, plan);
        return plan;
    }

    /// <summary>
    /// What <c>sys.dm_exec_requests</c> reports of a session's running
    /// statement: the text its <c>sql_handle</c> names — the batch's, or, for
    /// a statement simple parameterization rewrites, the prepared text real
    /// runs it under — the statement's offsets in that text, the end one -1
    /// for a batch's last statement, and the handle of the plan it runs under,
    /// the one real would cache when none is cached yet (probed 2026-10-16
    /// against SQL Server 2025: a blocked <c>select * from t where id = 2</c>
    /// reports <c>(@1 tinyint)SELECT * FROM [t] WHERE [id]=@1</c> from offset
    /// 24 to its last character, and every running request a plan handle).
    /// </summary>
    internal RunningStatement ReportedStatement(SimulatedDbConnection connection, string batchText, int statementStart, Database database)
    {
        var start = Math.Clamp(statementStart, 0, batchText.Length);
        var end = ExtendedEvents.LockReports.StatementEnd(this, batchText, start);
        var through = end < 0 ? batchText.Length : Math.Min(end + 1, batchText.Length);
        var statement = batchText.AsSpan(start, through - start).TrimEnd().TrimEnd(';').TrimEnd();
        if (connection.NestingLevel == 0 && statement.Length != 0
            && this.QueryStoreShapeOf(statement, database, parser: null, tokensFrom: null) is { PreparedText: { } prepared } shape)
        {
            return new RunningStatement(prepared, 2 * shape.ParameterizedPrefixLength, 2 * (prepared.Length - 1),
                BuiltInResources.SqlHandleOf(prepared), PlanHandleFor(connection, CachedPlanKind.Prepared, prepared, database));
        }
        return new RunningStatement(batchText, 2 * start, end < 0 ? -1 : 2 * end, BuiltInResources.SqlHandleOf(batchText),
            connection.Session.RunningPlanHandle ?? PlanHandleFor(connection, CachedPlanKind.Adhoc, batchText, database));
    }

    /// <summary>The handle of the batch plan of <paramref name="text"/> the session's settings compile, cached or not.</summary>
    private static byte[] PlanHandleFor(SimulatedDbConnection connection, CachedPlanKind kind, string text, Database database)
    {
        var key = new CachedPlanKey(kind, database.Id, 0, text, null, SetOptionsOf(connection, connection.QuotedIdentifiers),
            connection.Language.LangId, DateFormatCode(connection.DateFormat), connection.DateFirst);
        return CachedPlan.PlanHandleOf(key, BuiltInResources.AdHocObjectIdOf(text));
    }

    /// <summary>A client's batch runs under <paramref name="plan"/>, the plan its request reports.</summary>
    private static void NoteRunningPlan(BatchContext batch, CachedPlan plan)
    {
        if (batch.CachedPlanOrigin == CachedPlanOrigin.ClientBatch)
            batch.Connection.Session.RunningPlanHandle = plan.PlanHandle;
    }

    /// <summary>Whether the session's plan-affecting settings are the ones <paramref name="key"/> was compiled under.</summary>
    private static bool SettingsMatch(BatchContext batch, in CachedPlanKey key)
    {
        var connection = batch.Connection;
        return key.SetOptions == SetOptionsOf(connection, batch.Parser.QuotedIdentifiers)
            && key.LanguageId == connection.Language.LangId
            && key.DateFormat == DateFormatCode(connection.DateFormat)
            && key.DateFirst == connection.DateFirst;
    }

    /// <summary>
    /// The batch plan of <paramref name="text"/>, its parameters declared by
    /// <paramref name="declaration"/>, compiled under the session's settings,
    /// added when there is none; <paramref name="created"/> says which. The
    /// plan's text is a prepared batch's declaration in parentheses followed
    /// by the batch, as real shows it (probed 2026-10-15 against SQL Server
    /// 2025).
    /// </summary>
    private static CachedPlan GetOrAddBatchPlan(BatchContext batch, CachedPlanKind kind, string text, string? declaration, Database database, int userId, out bool created)
    {
        var connection = batch.Connection;
        var key = new CachedPlanKey(kind, database.Id, 0, text, declaration, SetOptionsOf(connection, batch.Parser.QuotedIdentifiers),
            connection.Language.LangId, DateFormatCode(connection.DateFormat), connection.DateFirst);
        var registry = connection.Simulation.CachedPlans;
        if (registry.Find(key) is { } found)
        {
            created = false;
            return found;
        }
        var planText = declaration is null ? text : "(" + declaration + ")" + text;
        return registry.Add(new CachedPlan(key, database, planText, module: null, userId, batch.CurrentStatement.UtcNow), out created);
    }

    /// <summary>
    /// Notes what a top-level statement of a client's or dynamic batch is, for
    /// the rule that such a batch of nothing but <c>SET</c> options and
    /// <c>BEGIN</c> / <c>COMMIT</c> / <c>ROLLBACK</c> / <c>SAVE</c>
    /// <c>TRANSACTION</c> statements is cached though none of them is a query,
    /// where one adding anything real doesn't compile a plan for — a constant
    /// <c>SELECT</c>, a <c>DECLARE</c>, a variable's <c>SET</c>, <c>PRINT</c>,
    /// <c>EXEC</c>, DDL — isn't (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    internal static void NoteCachedPlanStatement(BatchContext batch)
    {
        if (batch.CachedPlanStatements == CachedPlanStatements.Other)
            return;
        var parser = batch.Parser;
        var settings = parser.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Set } set => FollowingCharacter(parser.Command.CommandText, set.EndIndex) != '@',
            ReservedKeyword { Keyword: Keyword.Commit or Keyword.Rollback or Keyword.Save } => true,
            ReservedKeyword { Keyword: Keyword.Begin } begin => FollowingWordIs(parser.Command.CommandText, begin.EndIndex, "TRAN"),
            _ => false,
        };
        batch.CachedPlanStatements = settings ? CachedPlanStatements.SettingsOnly : CachedPlanStatements.Other;

        static char FollowingCharacter(string text, int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;
            return index < text.Length ? text[index] : '\0';
        }

        static bool FollowingWordIs(string text, int index, string prefix)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;
            return text.AsSpan(index).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Ends a client's or dynamic batch for the plan cache: one that ran only
    /// settings statements is cached as it ends, having recorded no statement
    /// that would have added its plan.
    /// </summary>
    internal static void EndCachedPlanBatch(BatchContext batch)
    {
        if (batch.CachedPlanStatements == CachedPlanStatements.SettingsOnly && batch.CachedPlan is null)
            _ = CachedPlanOfBatch(batch, userId: -2);
    }

    /// <summary>
    /// Records a statement execution Query Store's capture timed under the
    /// cached plan it belongs to: its batch's or module's, or — for a
    /// statement simple parameterization rewrote — the prepared plan of the
    /// rewritten text, which counts a use each time a batch holding the
    /// statement compiles (probed 2026-10-15 against SQL Server 2025: three ad
    /// hoc texts compiling four such statements leave the prepared plan at 4
    /// uses and its statement at every execution).
    /// </summary>
    private static void RecordCachedPlanStatement(BatchContext batch, Database database, QueryStoreShape shape, QueryStoreQueryKey? storeKey, int start, int end,
        ParserContext.Checkpoint? tokensFrom, DateTime now, long elapsedMicroseconds, long cpuMicroseconds, long logicalReads, long rowCount)
    {
        var userId = shape.UsesDefaultSchema ? Database.DboSchemaId : -2;
        if (CachedPlanOfBatch(batch, userId) is not { } plan)
            return;
        CachedPlan target;
        int startOffset, endOffset;
        if (shape.PreparedText is { } parameterized && !IsModuleBody(batch))
        {
            target = GetOrAddBatchPlan(batch, CachedPlanKind.Prepared, parameterized, declaration: null, batch.CachedPlanDatabase!, userId, out var created);
            if (created || batch.CachedPlanCompiled)
                _ = Interlocked.Increment(ref target.UseCount);
            startOffset = 2 * shape.ParameterizedPrefixLength;
            endOffset = 2 * (parameterized.Length - 1);
        }
        else
        {
            target = plan;
            // A module's statements sit where its CREATE text has them, and a
            // prepared batch's past its declaration.
            var shift = target.Module is not null ? batch.ModuleBodyOffset()
                : target.Key.Kind == CachedPlanKind.Prepared ? target.Text!.Length - batch.Parser.Command.CommandText.Length
                : 0;
            startOffset = 2 * (start + shift);
            endOffset = 2 * (end - 1 + shift);
        }

        bool recorded;
        lock (target)
            recorded = target.FindStatement(startOffset, endOffset) is { } statement && Record(statement);
        if (!recorded)
        {
            // The text Query Store stores for the statement, which its
            // statement_sql_handle hashes, is read only as the statement
            // first runs under the plan.
            storeKey ??= QueryStoreKeyOf(batch, database, start, end, tokensFrom, out _, out _, out _);
            if (storeKey is not { } key)
                return;
            var contextSettingsId = QueryStoreContextSettingsId(database, key.Context);
            lock (target)
                _ = Record(target.FindStatement(startOffset, endOffset) ?? target.AddStatement(startOffset, endOffset, now, key.Text, shape, key.ParameterizationType, contextSettingsId));
        }
        if (batch.CachedPlanOrigin == CachedPlanOrigin.Module)
            batch.CachedPlanReads += logicalReads;

        // Caller holds the plan's lock.
        bool Record(CachedStatementStats statement)
        {
            var first = statement.ExecutionCount == 0;
            statement.ExecutionCount++;
            statement.LastExecutionTime = now;
            statement.WorkerTime.Add(cpuMicroseconds, first);
            statement.ElapsedTime.Add(elapsedMicroseconds, first);
            statement.LogicalReads.Add(logicalReads, first);
            statement.Rows.Add(rowCount, first);
            target.LastExecutionTime = now;
            return true;
        }
    }

    /// <summary>
    /// A module call's use of its cached plan, from <see cref="BeginCachedModuleCall"/>
    /// to <see cref="EndCachedModuleCall"/>.
    /// </summary>
    internal readonly struct CachedModuleCall(CachedPlan plan, long startedAt, long waitedBefore, long lockWaitedBefore)
    {
        public readonly CachedPlan Plan = plan;
        public readonly long StartedAt = startedAt;
        public readonly long WaitedBefore = waitedBefore;
        public readonly long LockWaitedBefore = lockWaitedBefore;
    }

    /// <summary>
    /// Begins a call of <paramref name="module"/> — a procedure, a DML trigger,
    /// or a function the optimizer doesn't inline — running in
    /// <paramref name="body"/>: looks its plan up or adds it, counts the use
    /// (a function's once per calling statement, however many rows call it),
    /// and starts the call's clock. A body that compiled afresh
    /// (<paramref name="compiledBody"/> another than the plan was measured
    /// under) starts its statements' measures over under a new plan
    /// generation, as real's recompiled statements do (probed 2026-10-15
    /// against SQL Server 2025). Null for a natively compiled module, whose
    /// calls real doesn't measure.
    /// </summary>
    internal static CachedModuleCall? BeginCachedModuleCall(BatchContext caller, BatchContext body, SchemaObject module, CachedPlanKind kind, object? compiledBody)
    {
        if (caller.IsSkipping || module.IsNativelyCompiled)
            return null;
        var connection = body.Connection;
        // A temporary procedure's plan is tempdb's, though its body runs bound
        // in another database (probed 2026-10-16 against SQL Server 2025).
        var database = module is Procedure { TemporaryBindingDatabase: not null } temporary ? temporary.Schema.Database : connection.CurrentDatabase;
        var key = new CachedPlanKey(kind, database.Id, module.ObjectId, text: null, declaration: null, SetOptionsOf(connection, connection.QuotedIdentifiers),
            connection.Language.LangId, DateFormatCode(connection.DateFormat), connection.DateFirst);
        var registry = connection.Simulation.CachedPlans;
        var now = caller.CurrentStatement.UtcNow;
        var plan = registry.Find(key);
        if (plan is not null && (!ReferenceEquals(plan.Module, module) || !ReferenceEquals(plan.Text, module.DefinitionText)))
        {
            // The module was altered, or dropped and made again, since.
            var stale = plan;
            registry.Remove(held => ReferenceEquals(held, stale));
            plan = null;
        }
        plan ??= registry.Add(new CachedPlan(key, database, module.DefinitionText, module, module.SchemaId, now), out _);

        lock (plan)
        {
            if (compiledBody is not null)
            {
                if (plan.CompiledBody is not null && !ReferenceEquals(plan.CompiledBody, compiledBody))
                {
                    plan.PlanGeneration++;
                    plan.Statements.Clear();
                }
                plan.CompiledBody = compiledBody;
            }
            if (module is UserDefinedFunction)
            {
                var statement = ((long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(caller.CurrentStatement) << 32) | (uint)caller.CurrentStatement.DispatchSerial;
                if (plan.LastUseStatement != statement)
                {
                    plan.LastUseStatement = statement;
                    _ = Interlocked.Increment(ref plan.UseCount);
                }
            }
            else
            {
                _ = Interlocked.Increment(ref plan.UseCount);
            }
        }
        body.CachedPlanOrigin = CachedPlanOrigin.Module;
        body.CachedPlan = plan;
        return new CachedModuleCall(plan, Stopwatch.GetTimestamp(), connection.WaitedTicks, connection.Session.LockWaitedMilliseconds);
    }

    /// <summary>Ends a module call <see cref="BeginCachedModuleCall"/> began, adding what it measured to its plan's call statistics.</summary>
    internal static void EndCachedModuleCall(BatchContext body, CachedModuleCall? call)
    {
        if (call is not { } begun)
            return;
        var connection = body.Connection;
        var elapsed = Stopwatch.GetElapsedTime(begun.StartedAt).Ticks;
        var lockWaitMilliseconds = connection.Session.LockWaitedMilliseconds - begun.LockWaitedBefore;
        var cpu = Math.Max(0, elapsed - (connection.WaitedTicks - begun.WaitedBefore) - (lockWaitMilliseconds * TimeSpan.TicksPerMillisecond));
        var plan = begun.Plan;
        lock (plan)
        {
            var first = plan.ExecutionCount == 0;
            plan.ExecutionCount++;
            plan.LastExecutionTime = body.CurrentStatement.UtcNow == default ? DateTime.UtcNow : body.CurrentStatement.UtcNow;
            plan.WorkerTime.Add(cpu / 10, first);
            plan.ElapsedTime.Add(elapsed / 10, first);
            plan.LogicalReads.Add(body.CachedPlanReads, first);
        }
    }

    /// <summary>
    /// The plans held now that still describe what they compiled: a plan of a
    /// database since dropped, or of a module since dropped or altered, is
    /// gone from real's cache with it, and is removed here as it is read.
    /// </summary>
    internal CachedPlan[] LiveCachedPlans()
    {
        var plans = this.CachedPlans.Snapshot();
        List<CachedPlan>? stale = null;
        foreach (var plan in plans)
        {
            if (!this.Databases.TryGetValue(plan.Database.Name, out var database) || !ReferenceEquals(database, plan.Database)
                || (plan.Module is { } module && (!ModuleStillDefined(plan.Database, module) || !ReferenceEquals(module.DefinitionText, plan.Text))))
            {
                (stale ??= []).Add(plan);
            }
        }
        if (stale is null)
            return plans;
        this.CachedPlans.Remove(stale.Contains);
        return [.. plans.Except(stale)];
    }

    private static bool ModuleStillDefined(Database database, SchemaObject module)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            if (schema.SchemaId == module.SchemaId)
                return schema.Holds(module);
        }
        return false;
    }

    /// <summary>
    /// Removes cached plans — every one, those a handle names (a plan handle's
    /// plan, or the plans a SQL handle's text or module compiled), or those of
    /// one database — for <c>DBCC FREEPROCCACHE</c>, <c>DBCC
    /// FREESYSTEMCACHE</c> and <c>ALTER DATABASE SCOPED CONFIGURATION CLEAR
    /// PROCEDURE_CACHE</c> (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    internal void RemoveCachedPlans(byte[]? handle, Database? database)
    {
        if (handle is null && database is null)
        {
            this.CachedPlans.Remove(null);
            return;
        }
        this.CachedPlans.Remove(plan =>
            (database is null || ReferenceEquals(plan.Database, database))
            && (handle is null
                || handle.AsSpan(0, BuiltInResources.SqlHandleLength).SequenceEqual(plan.PlanHandle)
                || handle.AsSpan(0, BuiltInResources.SqlHandleLength).SequenceEqual(plan.SqlHandle)));
    }

    /// <summary>Removes the cached plan of a module <c>sp_recompile</c> marked, which real drops at once (probed 2026-10-15 against SQL Server 2025).</summary>
    internal void RemoveCachedPlansOf(SchemaObject module) =>
        this.CachedPlans.Remove(plan => ReferenceEquals(plan.Module, module));
}
