using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The sp_query_store_* procedures, which act on a database's captured Query
// Store data (QueryStoreData) — behavior probed 2026-09-29 against SQL
// Server 2025, see docs/claude/database-options.md#query-store.
partial class Simulation
{
    /// <summary>
    /// Binds an <c>sp_query_store_*</c> call's arguments to
    /// <paramref name="parameters"/> by position or by name, returning each
    /// supplied value (null where omitted). Too many positional arguments is
    /// Msg 8144, a missing one of the first <paramref name="required"/> Msg
    /// 313 and a NULL one Msg 214 naming its <c>bigint</c> type — all state
    /// 51, as real's procedures raise them.
    /// </summary>
    private static SqlValue?[] BindQueryStoreArguments(BatchContext batch, string procedureName, string[] parameters, int required)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        var values = new SqlValue?[parameters.Length];
        var positional = 0;
        foreach (var argument in arguments)
        {
            int index;
            if (argument.Name is { } name)
            {
                index = Array.FindIndex(parameters, parameter => BuiltInToken.Equals(parameter, name.TrimStart('@')));
                if (index < 0)
                    throw SimulatedSqlException.NotAParameterForProcedure(name.TrimStart('@'), procedureName);
            }
            else
            {
                index = positional++;
                if (index >= parameters.Length)
                    throw SimulatedSqlException.TooManyArgumentsToFunction(procedureName, 51);
            }
            values[index] = argument.IsDefault ? null : argument.Value;
        }
        if (batch.IsSkipping)
            return values;
        for (var i = 0; i < required; i++)
        {
            if (values[i] is not { } value)
                throw SimulatedSqlException.InsufficientArgumentsToFunction(procedureName, 51);
            if (value.IsNull)
                throw SimulatedSqlException.SystemProcedureParameterType(parameters[i], i == 1 && parameters[1] == "query_hints" ? "nvarchar(max)" : "bigint", 51);
        }
        return values;
    }

    /// <summary>An argument as the <c>bigint</c> id it names.</summary>
    private static long QueryStoreId(SqlValue? value) => value!.Value.CoerceTo(SqlType.BigInt).AsInt64;

    /// <summary>
    /// <c>sp_query_store_flush_db</c>: writes the in-memory portion of the
    /// store to disk on real. The simulator's store is visible as soon as a
    /// statement completes, so it only checks its (absent) arguments.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpQueryStoreFlushDb(BatchContext batch)
    {
        _ = BindQueryStoreArguments(batch, "sp_query_store_flush_db", [], 0);
        yield break;
    }

    /// <summary>
    /// <c>sp_query_store_force_plan @query_id, @plan_id
    /// [, @disable_optimized_plan_forcing] [, @force_plan_scope]</c>: marks the
    /// plan forced and records its forcing location. The simulator compiles
    /// one plan per query, so forcing never fails. Refused, forcing and
    /// unforcing alike, while the store is OFF (Msg 12405 state 4, ahead of
    /// the id checks), for a query it doesn't hold
    /// (Msg 12402) and a plan that isn't the query's (Msg 12406). Forcing a
    /// forced plan again is quiet.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpQueryStoreForcePlan(BatchContext batch, bool force)
    {
        var name = force ? "sp_query_store_force_plan" : "sp_query_store_unforce_plan";
        var arguments = BindQueryStoreArguments(batch, name,
            force ? ["query_id", "plan_id", "disable_optimized_plan_forcing", "force_plan_scope"] : ["query_id", "plan_id", "force_plan_scope"], 2);
        if (batch.IsSkipping)
            yield break;
        var database = batch.CurrentDatabase;
        if (database.QueryStore.DesiredState == QueryStoreState.Off)
            throw SimulatedSqlException.QueryStoreNotEnabled(database.Id, 4);
        var queryId = QueryStoreId(arguments[0]);
        var planId = QueryStoreId(arguments[1]);
        var data = database.QueryStoreData;
        lock (data.Gate)
        {
            var query = data.FindQuery(queryId) ?? throw SimulatedSqlException.QueryStoreQueryNotFound(queryId, database.Id, 2);
            var plan = data.FindPlan(planId);
            if (plan is null || plan.Query != query)
                throw SimulatedSqlException.QueryStorePlanNotFoundForQuery(planId, queryId);
            plan.IsForced = force;
            _ = data.ForcingLocations.RemoveAll(location => location.PlanId == planId);
            if (force)
            {
                plan.OptimizedPlanForcingDisabled = arguments[2] is { IsNull: false } disable && disable.CoerceTo(SqlType.Int32).AsInt32 != 0;
                data.ForcingLocations.Add(new QueryStoreForcingLocation(data.NextForcingLocationId++, queryId, planId, DateTime.UtcNow));
            }
        }
    }

    /// <summary>
    /// <c>sp_query_store_remove_query @query_id</c>: removes the query, its
    /// plans and their statistics, its hints, and its text once nothing else
    /// shares it. Works on a store in any state; Msg 12402 state 1 for a query
    /// the store doesn't hold.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpQueryStoreRemoveQuery(BatchContext batch)
    {
        var arguments = BindQueryStoreArguments(batch, "sp_query_store_remove_query", ["query_id"], 1);
        if (batch.IsSkipping)
            yield break;
        var database = batch.CurrentDatabase;
        var queryId = QueryStoreId(arguments[0]);
        var data = database.QueryStoreData;
        lock (data.Gate)
            data.RemoveQuery(data.FindQuery(queryId) ?? throw SimulatedSqlException.QueryStoreQueryNotFound(queryId, database.Id, 1));
    }

    /// <summary>
    /// <c>sp_query_store_remove_plan @plan_id</c> (Msg 12403 state 1 for a
    /// plan the store doesn't hold) and <c>sp_query_store_reset_exec_stats
    /// @plan_id</c> (state 2): the first removes the plan with its statistics,
    /// leaving the query, the second only the statistics.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpQueryStorePlanAction(BatchContext batch, bool remove)
    {
        var arguments = BindQueryStoreArguments(batch, remove ? "sp_query_store_remove_plan" : "sp_query_store_reset_exec_stats", ["plan_id"], 1);
        if (batch.IsSkipping)
            yield break;
        var database = batch.CurrentDatabase;
        var planId = QueryStoreId(arguments[0]);
        var data = database.QueryStoreData;
        lock (data.Gate)
        {
            var plan = data.FindPlan(planId) ?? throw SimulatedSqlException.QueryStorePlanNotFound(planId, database.Id, remove ? (byte)1 : (byte)2);
            if (remove)
                data.RemovePlan(plan);
            else
                data.ResetRuntimeStats(plan);
        }
    }

    /// <summary>
    /// <c>sp_query_store_set_hints @query_id, @query_hints</c>: replaces the
    /// query's hints with the given <c>OPTION ( … )</c> clause under a new
    /// hint id; <c>sp_query_store_clear_hints @query_id</c> removes them,
    /// quietly when there are none. A clause not opening with <c>OPTION (</c>
    /// and a hint name no query hint begins with are Msg 102 at the word,
    /// and a query the store doesn't hold Msg 12402 (state 5, and 6 for
    /// clear); while the store is OFF both are Msg 12405 (states 6 and 7). The
    /// hints are recorded, not applied.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpQueryStoreHints(BatchContext batch, bool set)
    {
        var arguments = set
            ? BindQueryStoreArguments(batch, "sp_query_store_set_hints", ["query_id", "query_hints", "query_hint_scope", "replica_group_id"], 2)
            : BindQueryStoreArguments(batch, "sp_query_store_clear_hints", ["query_id", "replica_group_id"], 1);
        if (batch.IsSkipping)
            yield break;
        var database = batch.CurrentDatabase;
        if (database.QueryStore.DesiredState == QueryStoreState.Off)
            throw SimulatedSqlException.QueryStoreNotEnabled(database.Id, set ? (byte)6 : (byte)7);
        var queryId = QueryStoreId(arguments[0]);
        var data = database.QueryStoreData;
        lock (data.Gate)
        {
            if (data.FindQuery(queryId) is null)
                throw SimulatedSqlException.QueryStoreQueryNotFound(queryId, database.Id, set ? (byte)5 : (byte)6);
        }
        string? hints = null;
        if (set)
        {
            hints = arguments[1]!.Value.CoerceTo(SqlType.NVarcharMax).AsString;
            CheckQueryHintClause(hints, database);
        }
        lock (data.Gate)
        {
            _ = data.Hints.RemoveAll(hint => hint.QueryId == queryId);
            if (hints is not null)
                data.Hints.Add(new QueryStoreHint(data.NextHintId++, queryId, hints));
        }
    }

    /// <summary>
    /// The syntax check <c>sp_query_store_set_hints</c> runs over its clause:
    /// <c>OPTION</c>, an opening parenthesis, then hints each beginning with a
    /// word a query hint begins with.
    /// </summary>
    private static void CheckQueryHintClause(string clause, Database database)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (Tokenizer.NextToken(clause, ref index, database.Collation, quotedIdentifiers: true, database.CompatibilityLevel) is Token token)
        {
            if (token is not (Whitespace or Comment))
                tokens.Add(token);
        }
        if (tokens.Count == 0 || tokens[0] is not ReservedKeyword { Keyword: Keyword.Option })
            throw SimulatedSqlException.SyntaxErrorNear(tokens.Count == 0 ? null : tokens[0]);
        if (tokens.Count < 2 || tokens[1] is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(tokens.Count < 2 ? tokens[0] : tokens[1]);
        var depth = 0;
        var expectHint = true;
        for (var i = 2; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (expectHint && depth == 0)
            {
                if (!IsQueryHintWord(token))
                    throw SimulatedSqlException.SyntaxErrorNear(token);
                expectHint = false;
                continue;
            }
            switch (token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' } when depth == 0:
                    return;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: ',' } when depth == 0:
                    expectHint = true;
                    break;
            }
        }
        throw SimulatedSqlException.SyntaxErrorNear(tokens[^1]);
    }

    /// <summary>Whether <paramref name="token"/> is a word a query hint in an <c>OPTION</c> clause begins with.</summary>
    private static bool IsQueryHintWord(Token token) => token switch
    {
        ReservedKeyword { Keyword: Keyword.Merge or Keyword.Use or Keyword.Table or Keyword.Option } => true,
        UnquotedString word => BuiltInToken.EqualsAny(word.Value,
            "CONCAT", "DISABLE_OPTIMIZED_PLAN_FORCING", "EXPAND", "FAST", "FORCE", "FORCESCAN", "FORCESEEK", "HASH", "IGNORE_NONCLUSTERED_COLUMNSTORE_INDEX",
            "KEEP", "KEEPFIXED", "LABEL", "LOOP", "MAX_GRANT_PERCENT", "MAXDOP", "MAXRECURSION", "MIN_GRANT_PERCENT", "NO_PERFORMANCE_SPOOL",
            "OPTIMIZE", "ORDER", "PARAMETERIZATION", "QUERYTRACEON", "RECOMPILE", "ROBUST"),
        _ => false,
    };

    /// <summary>
    /// <c>sp_query_store_consistency_check</c>: checks the store's internal
    /// tables, which real only does while the store is OFF (Msg 12427
    /// otherwise). The simulator's store has nothing to repair.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpQueryStoreConsistencyCheck(BatchContext batch)
    {
        _ = BindQueryStoreArguments(batch, "sp_query_store_consistency_check", ["database_id"], 0);
        if (batch.IsSkipping)
            yield break;
        if (batch.CurrentDatabase.QueryStore.DesiredState != QueryStoreState.Off)
            throw SimulatedSqlException.QueryStoreConsistencyCheckWhileEnabled();
    }

    /// <summary>
    /// <c>sp_query_store_clear_message_queues</c>: empties the store's pending
    /// message queues, which the simulator's store doesn't have — it takes no
    /// arguments, Msg 8144 for any.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpQueryStoreClearMessageQueues(BatchContext batch)
    {
        _ = BindQueryStoreArguments(batch, "sp_query_store_clear_message_queues", [], 0);
        yield break;
    }

    /// <summary>
    /// <c>sp_query_store_remove_plan_feedback @feature_id [, @plan_id]</c>:
    /// removes a feedback feature's plan feedback, of which the simulator
    /// records none. Real refuses a plan (Msg 12469, checked first), features
    /// 1 to 3 (Msg 12468) and anything but 1 to 4 (Msg 12467), and wants the
    /// feature (Msg 201 state 62, a NULL Msg 214 state 56, a third argument
    /// Msg 8144 state 120); feature 4 succeeds, whatever the store's state.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpQueryStoreRemovePlanFeedback(BatchContext batch)
    {
        const string name = "sp_query_store_remove_plan_feedback";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (arguments.Count > 2)
            throw SimulatedSqlException.TooManyArgumentsToFunction(name, 120);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count == 0 || arguments[0].IsDefault)
            throw SimulatedSqlException.ProcedureExpectsParameter(name, "feature_id", 62);
        if (arguments.Count == 2)
            throw SimulatedSqlException.QueryStoreFeedbackForPlanUnsupported();
        if (arguments[0].Value.IsNull)
            throw SimulatedSqlException.SystemProcedureParameterType("feature_id", "tinyint", 56);
        var featureId = arguments[0].Value.CoerceTo(SqlType.BigInt).AsInt64;
        if (featureId is < 1 or > 4)
            throw SimulatedSqlException.QueryStoreFeedbackFeatureInvalid(featureId);
        if (featureId < 4)
            throw SimulatedSqlException.QueryStoreFeedbackRemovalUnsupported(featureId);
    }
}
