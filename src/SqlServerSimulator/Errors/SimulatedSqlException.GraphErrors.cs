namespace SqlServerSimulator;

// Node and edge tables and MATCH; wordings probed 2026-09-27 against SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>Msg 13900: a MATCH pattern names something no FROM source is called.</summary>
    internal static SimulatedSqlException MatchIdentifierNotBound(string identifier) =>
        new($"Identifier '{identifier}' in a MATCH clause could not be bound.", 13900, 16, 1);

    /// <summary>Msg 13901: a MATCH pattern's node position names a source that isn't a node table.</summary>
    internal static SimulatedSqlException MatchIdentifierNotNode(string identifier) =>
        new($"Identifier '{identifier}' in a MATCH clause is not a node table or an alias for a node table.", 13901, 16, 1);

    /// <summary>Msg 13902: a MATCH pattern's edge position names a source that isn't an edge table.</summary>
    internal static SimulatedSqlException MatchIdentifierNotEdge(string identifier) =>
        new($"Identifier '{identifier}' in a MATCH clause is not an edge table or an alias for an edge table.", 13902, 16, 1);

    /// <summary>Appends a further MATCH binding error, which real reports alongside the earlier ones.</summary>
    internal static SimulatedSqlException AndMatchError(SimulatedSqlException earlier, SimulatedSqlException next) =>
        FollowedBy(earlier, next);

    /// <summary>Msg 13903: one edge source appears in two MATCH patterns.</summary>
    internal static SimulatedSqlException MatchEdgeUsedTwice(string identifier) =>
        new($"Edge table '{identifier}' used in more than one MATCH pattern.", 13903, 16, 1);

    /// <summary>Msg 13905: a MATCH predicate under OR or NOT.</summary>
    internal static SimulatedSqlException MatchCombinedWithOrOrNot() =>
        new("A MATCH clause may not be directly combined with other expressions using OR or NOT.", 13905, 16, 1);

    /// <summary>
    /// Msg 13908: an INSERT column list (state 1) or UPDATE SET (state 3) names
    /// one of a graph table's hidden internal columns.
    /// </summary>
    internal static SimulatedSqlException InternalGraphColumnAccess(string columnName, byte state) =>
        new($"Cannot access internal graph column '{columnName}'.", 13908, 16, state);

    /// <summary>
    /// Msg 13913: <c>ALTER TABLE</c> drops or alters one of a node or edge
    /// table's internal columns — state 5 for a drop, 2 for an alter.
    /// </summary>
    internal static SimulatedSqlException InternalGraphColumnCannotBeAltered(byte state) =>
        new("Internal graph columns cannot be altered.", 13913, 16, state);

    /// <summary>Msg 13914: <c>CREATE TABLE #t … AS NODE | AS EDGE</c>.</summary>
    internal static SimulatedSqlException GraphTableCannotBeTemporary() =>
        new("Cannot create a node or edge table as a temporary table.", 13914, 16, 1);

    /// <summary>Msg 13920: a MATCH pattern names a source joined with JOIN or APPLY rather than a comma.</summary>
    internal static SimulatedSqlException MatchIdentifierInJoin(string identifier, byte state = 1) =>
        new($"Identifier '{identifier}' in a MATCH clause is used with a JOIN clause or APPLY operator. JOIN and APPLY are not supported with MATCH clauses.", 13920, 16, state);

    /// <summary>
    /// Msg 13921: an INSERT writes a <c>$node_id</c> / <c>$edge_id</c> that
    /// doesn't read as an identifier of the target table — NULL included.
    /// </summary>
    internal static SimulatedSqlException GraphPseudoColumnJsonMalformed(string pseudoColumn) =>
        new($"JSON data for INSERT/UPDATE of graph pseudocolumn '{pseudoColumn}' is malformed.", 13921, 16, 1);

    /// <summary>Msg 13930: a <c>CONNECTION</c> constraint on a table that isn't an edge table.</summary>
    internal static SimulatedSqlException EdgeConstraintOnNonEdgeTable(string tableName) =>
        new($"Edge constraint cannot be created on table '{tableName}'.  The table is not an edge table.", 13930, 16, 1);

    /// <summary>Msg 13931: a <c>CONNECTION</c> clause names a table that doesn't exist.</summary>
    internal static SimulatedSqlException EdgeConstraintTableNotFound(string constraintName, string tableName) =>
        new($"Edge constraint '{constraintName}' references invalid table '{tableName}'.  Table could not be found.", 13931, 16, 1);

    /// <summary>Msg 13933: a <c>CONNECTION</c> clause names a table that isn't a node table.</summary>
    internal static SimulatedSqlException EdgeConstraintMustReferenceNodes() =>
        new("Edge constraint must reference node tables.", 13933, 16, 1);

    /// <summary>Msg 13934: <c>DROP TABLE</c> of a node table an edge constraint references.</summary>
    internal static SimulatedSqlException NodeTableReferencedByEdgeConstraint(string tableName) =>
        new($"Could not drop node table '{tableName}' because it is referenced by an edge constraint.", 13934, 16, 1);

    /// <summary>Msg 13944: <c>TRUNCATE TABLE</c> of a node table an edge constraint references, named as written.</summary>
    internal static SimulatedSqlException CannotTruncateNodeTableReferencedByEdgeConstraint(string tableName) =>
        new($"Cannot truncate table '{tableName}' because it is being referenced by an EDGE constraint.", 13944, 16, 1);

    /// <summary>Msg 13942: a <c>SHORTEST_PATH</c> quantifier <c>{n, m}</c> whose <c>n</c> isn't 1.</summary>
    internal static SimulatedSqlException ShortestPathInitialQuantifier() =>
        new("The initial recursive quantifier must be 1: {1, ... }.", 13942, 15, 2);

    /// <summary>Msg 13943: a <c>SHORTEST_PATH</c> quantifier <c>{1, m}</c> whose <c>m</c> isn't past 1.</summary>
    internal static SimulatedSqlException ShortestPathFinalQuantifier() =>
        new("The final recursive quantifier must be greater than the initial recursive quantifier.", 13943, 15, 1);

    /// <summary>Msg 13948: a <c>SHORTEST_PATH</c>'s recursive edge or node isn't marked <c>FOR PATH</c>.</summary>
    internal static SimulatedSqlException ShortestPathNeedsForPath(string alias) =>
        new($"The table name or alias '{alias}' must be marked as FOR PATH to be used in the recursive section of a SHORTEST_PATH clause.", 13948, 16, 1);

    /// <summary>
    /// Msg 13949: a <c>FOR PATH</c> source no <c>SHORTEST_PATH</c> recurses
    /// over — named database-qualified when it has no alias (state 1), by its
    /// alias otherwise (state 2).
    /// </summary>
    internal static SimulatedSqlException ForPathSourceUnused(string name, byte state) =>
        new($"The table name or alias '{name}' was marked as FOR PATH but was not used in the recursive section of a SHORTEST_PATH clause.", 13949, 16, state);

    /// <summary>Msg 13952: a <c>WITHIN GROUP (GRAPH PATH)</c> aggregate reading no recursive path.</summary>
    internal static SimulatedSqlException GraphPathAggregateWithoutPath(string aggregate) =>
        new($"No columns in the aggregate '{aggregate}' WITHIN GROUP(GRAPH PATH) reference a recursive path.", 13952, 16, 2);

    /// <summary>Msg 13954: a <c>WITHIN GROUP (GRAPH PATH)</c> aggregate reading a column off the path.</summary>
    internal static SimulatedSqlException GraphPathAggregateReadsOffPath(string identifier, string aggregate) =>
        new($"Identifier '{identifier}' in aggregate '{aggregate}' WITHIN GROUP(GRAPH PATH) is not referencing a recursive path and cannot be used.", 13954, 16, 1);

    /// <summary>Msg 13957: a <c>SHORTEST_PATH</c> inside a subquery.</summary>
    internal static SimulatedSqlException RecursiveMatchInSubquery() =>
        new("Recursive MATCH queries cannot be used in subqueries.", 13957, 16, 2);

    /// <summary>Msg 13961: a <c>FOR PATH</c> source's column read outside a graph path aggregate.</summary>
    internal static SimulatedSqlException ForPathColumnOutsideAggregate(string identifier) =>
        new($"The alias or identifier '{identifier}' cannot be used in the select list, order by, group by, or having context.", 13961, 16, 1);

    /// <summary>
    /// Msg 547: an edge row joins nodes no clause of the edge constraint
    /// admits, or a node that doesn't exist.
    /// </summary>
    internal static SimulatedSqlException EdgeConstraintConflict(string verb, string constraintName, string databaseName, string qualifiedEdgeTable) =>
        new($"The {verb} statement conflicted with the EDGE constraint \"{constraintName}\". The conflict occurred in database \"{databaseName}\", table \"{qualifiedEdgeTable}\".", 547, 16, 0);

    /// <summary>Msg 547: a node delete would strand an edge an edge constraint without <c>ON DELETE CASCADE</c> protects.</summary>
    internal static SimulatedSqlException EdgeReferenceConflict(string verb, string constraintName, string databaseName, string qualifiedEdgeTable) =>
        new($"The {verb} statement conflicted with the EDGE REFERENCE constraint \"{constraintName}\". The conflict occurred in database \"{databaseName}\", table \"{qualifiedEdgeTable}\".", 547, 16, 0);
}
