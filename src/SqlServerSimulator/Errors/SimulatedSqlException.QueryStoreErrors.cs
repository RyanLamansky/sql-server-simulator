namespace SqlServerSimulator;

// ALTER DATABASE … SET QUERY_STORE's value checks and the sp_query_store_*
// procedures' refusals — wording, class and state probed 2026-09-29 against
// SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 153 for a QUERY_STORE value out of range:
    /// <c>DATA_FLUSH_INTERVAL_SECONDS</c> below 60 (class 15 state 5) or an
    /// <c>INTERVAL_LENGTH_MINUTES</c> outside 1, 5, 10, 15, 30, 60 and 1440
    /// (class 16 state 6), each named by its catalog column. Raised while
    /// the batch compiles, so none of it runs.
    /// </summary>
    internal static SimulatedSqlException QueryStoreOptionInvalid(string option, byte @class, byte state) =>
        new($"Invalid usage of the option {option} in the ALTER DATABASE statement.", 153, @class, state);

    /// <summary>Mimics SQL Server error 12401: a QUERY_STORE sub-option written twice in one block. Class 15 state 2.</summary>
    internal static SimulatedSqlException QueryStoreOptionRepeated(string option) =>
        new($"The QUERY_STORE option '{option}' was specified more than once. Each option can be specified only once.", 12401, 15, 2);

    /// <summary>Mimics SQL Server error 12417: two QUERY_STORE clauses in one <c>SET</c> list. Class 16 state 1.</summary>
    internal static SimulatedSqlException QueryStoreOptionGivenTwice() =>
        new("Only one Query Store option can be given in ALTER DATABASE statement.", 12417, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 12452: a <c>QUERY_CAPTURE_POLICY</c> count or
    /// CPU budget of 0 — class 15, state 2 for
    /// <c>total_execution_cpu_time_ms</c> and 1 for the other two.
    /// </summary>
    internal static SimulatedSqlException QueryStoreCapturePolicyValueInvalid(long value, string option, byte state) =>
        new($"The value {value} is not valid for query_capture_policy option {option}. The value must be between 1 and 2147483647.", 12452, 15, state);

    /// <summary>Mimics SQL Server error 12453: a <c>STALE_CAPTURE_POLICY_THRESHOLD</c> outside an hour to seven days. Class 16 state 1.</summary>
    internal static SimulatedSqlException QueryStoreStaleThresholdInvalid() =>
        new("Invalid value provided for query_capture_policy option stale_capture_policy_threshold. The value must be between 1 hour and 7 days.", 12453, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 12402: an <c>sp_query_store_*</c> procedure
    /// named a query the store doesn't hold. Class 11; the state is the
    /// procedure's own — 1 for <c>remove_query</c>, 2 for <c>force_plan</c>
    /// and <c>unforce_plan</c>, 5 for <c>set_hints</c>, 6 for
    /// <c>clear_hints</c>.
    /// </summary>
    internal static SimulatedSqlException QueryStoreQueryNotFound(long queryId, int databaseId, byte state) =>
        new($"Query with provided query_id ({queryId}) is not found in the Query Store for database ({databaseId}). Check the query_id value and rerun the command.", 12402, 11, state);

    /// <summary>
    /// Mimics SQL Server error 12403: <c>sp_query_store_remove_plan</c>
    /// (state 1) or <c>sp_query_store_reset_exec_stats</c> (state 2) named a
    /// plan the store doesn't hold. Class 11. Real's
    /// <c>reset_exec_stats</c> fills both numbers from uninitialized memory;
    /// the simulator names the plan and database asked about.
    /// </summary>
    internal static SimulatedSqlException QueryStorePlanNotFound(long planId, int databaseId, byte state) =>
        new($"Query plan with provided plan_id ({planId}) is not found in the Query Store for database ({databaseId}). Check the plan_id value and rerun the command.", 12403, 11, state);

    /// <summary>Mimics SQL Server error 12406: <c>sp_query_store_force_plan</c> named a plan that isn't the query's. Class 11 state 1.</summary>
    internal static SimulatedSqlException QueryStorePlanNotFoundForQuery(long planId, long queryId) =>
        new($"Query plan with provided plan_id ({planId}) is not found in the Query Store for query ({queryId}). Check the plan_id value and rerun the command.", 12406, 11, 1);

    /// <summary>
    /// Mimics SQL Server error 12405: a plan-forcing or hint procedure while
    /// the store is OFF. Class 16; state 4 for <c>force_plan</c> and
    /// <c>unforce_plan</c>, 6 for <c>set_hints</c>, 7 for <c>clear_hints</c>.
    /// </summary>
    internal static SimulatedSqlException QueryStoreNotEnabled(int databaseId, byte state) =>
        new($"The command failed because the query store is not enabled for database ({databaseId}). Make sure that the query store is enabled for the database and rerun the command.", 12405, 16, state);

    /// <summary>Mimics SQL Server error 12427: <c>sp_query_store_consistency_check</c> while the store is on. Class 16 state 3.</summary>
    internal static SimulatedSqlException QueryStoreConsistencyCheckWhileEnabled() =>
        new("Cannot perform operation on Query Store while it is enabled. Please turn off Query Store for the database and try again.", 12427, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 214 from an <c>sp_query_store_*</c> procedure:
    /// a NULL where it needs an id (<c>bigint</c>) or a hint clause. Class 16,
    /// state 51 as those procedures raise it.
    /// </summary>
    internal static SimulatedSqlException SystemProcedureParameterType(string parameterName, string typeName, byte state) =>
        new($"Procedure expects parameter '@{parameterName}' of type '{typeName}'.", 214, 16, state);

    /// <summary>Mimics SQL Server error 12467: <c>sp_query_store_remove_plan_feedback</c> named no feedback feature (0, or past 4). Class 16 state 1.</summary>
    internal static SimulatedSqlException QueryStoreFeedbackFeatureInvalid(long featureId) =>
        new($"The feature_id {featureId} is invalid.", 12467, 16, 1);

    /// <summary>Mimics SQL Server error 12468: <c>sp_query_store_remove_plan_feedback</c> for features 1 to 3, whose feedback it can't remove. Class 16 state 1.</summary>
    internal static SimulatedSqlException QueryStoreFeedbackRemovalUnsupported(long featureId) =>
        new($"Removing feedback for feature_id {featureId} is not supported.", 12468, 16, 1);

    /// <summary>Mimics SQL Server error 12469: <c>sp_query_store_remove_plan_feedback</c> given a plan as well. Class 16 state 1.</summary>
    internal static SimulatedSqlException QueryStoreFeedbackForPlanUnsupported() =>
        new("Removing feedback for a specific plan is not supported.", 12469, 16, 1);
}
