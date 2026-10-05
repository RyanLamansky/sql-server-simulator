namespace SqlServerSimulator;

// Row-level security: CREATE / ALTER / DROP SECURITY POLICY and what a
// predicate refuses at run time. Every wording probed 2026-10-04 against SQL
// Server 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 33504: a write met a block predicate that
    /// refuses the row — the new row of an <c>AFTER INSERT</c> / <c>AFTER
    /// UPDATE</c> predicate, the row as it stood for a <c>BEFORE UPDATE</c> /
    /// <c>BEFORE DELETE</c> one. <paramref name="qualifiedTable"/> is
    /// <c>database.schema.table</c>, the base table even when the write named a
    /// view.
    /// </summary>
    internal static SimulatedSqlException BlockPredicateConflict(string qualifiedTable) =>
        new($"The attempted operation failed because the target object '{qualifiedTable}' has a block predicate that conflicts with this operation. If the operation is performed on a view, the block predicate might be enforced on the underlying table. Modify the operation to target only the rows that are allowed by the block predicate.", 33504, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 33512, the trailer of a non-schema-bound
    /// predicate's binding error: its function was dropped (state 0) or
    /// changed shape, or a column it reads is gone (state 2).
    /// <paramref name="tableName"/> is the target's own name.
    /// </summary>
    internal static SimulatedSqlException NonSchemaBoundPredicateBindingFailed(string tableName, byte state) =>
        new($"Binding for the non-schema bound security predicate on object '{tableName}' failed with one or more errors, indicating the schema of the predicate function has changed. Update or drop the affected security predicates.", 33512, 16, state);

    /// <summary>
    /// <paramref name="error"/> — Msg 208 for a dropped predicate function,
    /// 313 / 8144 for one whose parameters changed, 207 for a column gone —
    /// then the Msg 33512 that names the predicate's target.
    /// </summary>
    internal static SimulatedSqlException FollowedByPredicateBindingFailure(SimulatedSqlException error, string tableName, byte state) =>
        FollowedBy(error, NonSchemaBoundPredicateBindingFailed(tableName, state)).EndingBatch();

    /// <summary>
    /// Mimics the Msg 262 then Msg 15247 (state 7) a principal without
    /// <c>ALTER ANY SECURITY POLICY</c> earns from <c>CREATE SECURITY POLICY</c>.
    /// </summary>
    internal static SimulatedSqlException CreateSecurityPolicyDenied(string databaseName) =>
        FollowedBy(DatabasePermissionDenied("ALTER ANY SECURITY POLICY", databaseName), UserDoesNotHavePermission(7));

    /// <summary>
    /// Mimics the pair of Msg 229 (states 5 and 9) a principal without
    /// <c>SELECT</c> on a predicate's function earns from the statement adding
    /// the predicate.
    /// </summary>
    internal static SimulatedSqlException PredicateFunctionSelectDenied(string functionName, string databaseName, string schemaName) =>
        FollowedBy(
            new($"The SELECT permission was denied on the object '{functionName}', database '{databaseName}', schema '{schemaName}'.", 229, 14, 5),
            new($"The SELECT permission was denied on the object '{functionName}', database '{databaseName}', schema '{schemaName}'.", 229, 14, 9));

    /// <summary>
    /// Mimics SQL Server error 33268: a predicate's target that doesn't exist
    /// or isn't a user object (state 1), an <c>ALTER SECURITY POLICY</c> naming
    /// no policy (state 2), or one its principal may not alter (state 7). The
    /// name is spelled as written.
    /// </summary>
    internal static SimulatedSqlException SecurityPolicyObjectNotFound(string writtenName, byte state) =>
        new($"Cannot find the object \"{writtenName}\" because it does not exist or you do not have permissions.", 33268, 16, state);

    /// <summary>
    /// Mimics SQL Server error 33270: a predicate names a function that doesn't
    /// exist or isn't an inline table-valued function — a scalar or
    /// multi-statement function, a procedure, a table, a synonym, a built-in.
    /// The name is spelled as written.
    /// </summary>
    internal static SimulatedSqlException PredicateFunctionNotInlineTableValued(string writtenName) =>
        new($"Cannot find the object \"{writtenName}\" or this object is not an inline table-valued function.", 33270, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 33262: a second predicate of one kind on one
    /// table and operation in a policy — a block predicate guarding every
    /// operation overlaps any other. State 1 for a clash within the statement,
    /// 2 for one with a predicate the policy already holds.
    /// </summary>
    internal static SimulatedSqlException SecurityPredicateAlreadyDefined(string kind, string table, string policy, byte state) =>
        new($"A {kind} predicate for the same operation has already been defined on table '{table}' in the security policy '{policy}'.", 33262, 16, state);

    /// <summary>
    /// Mimics SQL Server error 33261: an <c>ALTER</c> or <c>DROP</c> of a
    /// predicate the policy doesn't hold — no predicate of that kind on the
    /// table, or none for that exact operation.
    /// </summary>
    internal static SimulatedSqlException SecurityPolicyHasNoPredicateOn(string policy, string table) =>
        new($"The security policy '{policy}' does not contain a predicate on table '{table}'.", 33261, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 33264: a table takes predicates from one enabled
    /// policy at a time, so enabling a second one over it — by creating it
    /// enabled, turning it on, or adding the table to it — is refused.
    /// </summary>
    internal static SimulatedSqlException SecurityPolicyTableAlreadyReferenced(string policy, string table, string enabledPolicy) =>
        new($"The security policy '{policy}' cannot be enabled with a predicate on table '{table}'. Table '{table}' is already referenced by the enabled security policy '{enabledPolicy}'.", 33264, 16, 1) { AbortsAsUnderXactAbort = true };

    /// <summary>Mimics SQL Server error 33265: a predicate on a table an indexed view reads.</summary>
    internal static SimulatedSqlException SecurityPolicyTableInIndexedView(string policy, string table, string view) =>
        new($"The security policy '{policy}' cannot have a predicate on table '{table}' because this table is referenced by the indexed view '{view}'.", 33265, 16, 1);

    /// <summary>Mimics SQL Server error 33266: an index on a view that reads a table some security policy names.</summary>
    internal static SimulatedSqlException IndexedViewOverSecurityPolicyTable(string view, string table) =>
        new($"The index on the view '{view}' cannot be created because the view is referencing table '{table}' that is referenced by a security policy.", 33266, 16, 1);

    /// <summary>Mimics SQL Server error 33263: a predicate's target is neither a user table nor a schema-bound view — a synonym, say.</summary>
    internal static SimulatedSqlException SecurityPredicateTargetKind(string name) =>
        new($"Security predicates can only be added to user tables and schema bound views. '{name}' is not a user table or a schema bound view.", 33263, 16, 1);

    /// <summary>Mimics SQL Server error 33503: a block predicate on a view.</summary>
    internal static SimulatedSqlException BlockPredicateTargetKind(string name) =>
        new($"BLOCK predicates can only be added to user tables. '{name}' is not a user table.", 33503, 16, 1);

    /// <summary>Mimics SQL Server error 33269: a predicate on a <c>#</c> table.</summary>
    internal static SimulatedSqlException SecurityPredicateOnTemporaryObject() =>
        new("Security predicates are not allowed on temporary objects. Object names that begin with '#' denote temporary objects.", 33269, 15, 1);

    /// <summary>Mimics SQL Server error 33510: a block predicate on a temporal table's history table.</summary>
    internal static SimulatedSqlException BlockPredicateOnHistoryTable(string table) =>
        new($"BLOCK security predicates cannot reference history tables. Table '{table}' is a temporal or ledger history table.", 33510, 16, 1);

    /// <summary>Mimics SQL Server error 112: a variable among a predicate's arguments; <paramref name="verb"/> is <c>CREATE</c> or <c>ALTER</c>.</summary>
    internal static SimulatedSqlException VariablesNotAllowedInSecurityPolicy(string verb) =>
        new($"Variables are not allowed in the {verb} SECURITY POLICY statement.", 112, 15, 4);

    /// <summary>
    /// Mimics SQL Server error 166: a database-qualified policy name, or a
    /// predicate's database-qualified target. <paramref name="clause"/> is the
    /// statement real names: <c>CREATE SECURITY POLICY</c>, <c>ADD/ALTER FILTER
    /// PREDICATE</c>, <c>ADD/ALTER BLOCK PREDICATE</c> — and <c>DROP INDEX</c>
    /// for its deprecated <c>table.index</c> form with a database prefix.
    /// </summary>
    internal static SimulatedSqlException SecurityPolicyNameDatabaseQualified(string clause) =>
        new($"'{clause}' does not allow specifying the database name as a prefix to the object name.", 166, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 3701 for <c>DROP SECURITY POLICY</c>: a missing
    /// policy is class 11 state 5 named as written; one the principal may not
    /// drop is class 14 state 20 named by its leaf.
    /// </summary>
    internal static SimulatedSqlException CannotDropSecurityPolicy(string name, bool permission) =>
        permission
            ? new($"Cannot drop the security policy '{name}', because it does not exist or you do not have permission.", 3701, 14, 20)
            : new($"Cannot drop the security policy '{name}', because it does not exist or you do not have permission.", 3701, 11, 5);

    /// <summary>
    /// Mimics SQL Server error 2760 state 5: <c>CREATE SECURITY POLICY</c> in a
    /// schema that doesn't exist.
    /// </summary>
    internal static SimulatedSqlException SecurityPolicySchemaMissing(string schemaName) =>
        new($"The specified schema name \"{schemaName}\" either does not exist or you do not have permission to use it.", 2760, 16, 5) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 343: a <c>CREATE</c>, <c>DROP</c> or
    /// <c>ALTER</c> naming a word that is no object type — the
    /// <c>DROP FILTER PREDICATE</c> a <c>CREATE SECURITY POLICY</c> can't hold
    /// reads as a statement of its own, and fails so.
    /// </summary>
    internal static SimulatedSqlException UnknownObjectType(string word) =>
        new($"Unknown object type '{word}' used in a CREATE, DROP, or ALTER statement.", 343, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 102 at the place a filter predicate was followed
    /// by an operation, which real words as <c>'BEFORE/AFTER'</c> whichever
    /// was written.
    /// </summary>
    internal static SimulatedSqlException FilterPredicateTakesNoOperation() =>
        new("Incorrect syntax near 'BEFORE/AFTER'.", 102, 15, 1);

    /// <summary>
    /// This error's text as real sends it from a statement that applied a
    /// row-level security predicate: a conversion or truncation error never
    /// quotes a value, type or name there, whichever row raised it.
    /// </summary>
    internal SimulatedSqlException? RedactedForRowSecurity() => this.Message.Contains("******", StringComparison.Ordinal) ? null : this.Number switch
    {
        2628 => new("String or binary data would be truncated in table '******', column '******'. Truncated value: '******'.", 2628, 16, this.State),
        _ => this.RedactedForMask(),
    };
}
