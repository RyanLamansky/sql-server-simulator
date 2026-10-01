namespace SqlServerSimulator;

// The refusals of a write through a partitioned view — a stored UNION ALL view
// over member tables. Every number, class and state below was observed raised
// by SQL Server 2025 (probed 2026-10-01). A view is named three-part as
// db.schema.view, a member table as [db].[schema].[table], whatever the
// statement wrote; the states that vary by statement are the caller's.
partial class SimulatedSqlException
{
    /// <summary>
    /// What real answers a positioned <c>UPDATE</c> / <c>DELETE</c> naming a
    /// partitioned view while its cursor reads that view, by any path (directly,
    /// through a view, a derived table or a join), opened or not: the session
    /// ends — Msg 596 at severity 21, then the severity-20 Msg 0 SqlClient
    /// reports a severed command with — its transaction rolled back and no
    /// <c>CATCH</c> able to intercept it (probed 2026-10-01 against SQL Server
    /// 2025). The caller marks the connection so the command closes it once
    /// this has been delivered.
    /// </summary>
    internal static SimulatedSqlException PositionedWriteThroughPartitionedViewEndsSession() => KillState(attention: false);

    /// <summary>Mimics SQL Server's Msg 417 — <c>TOP</c> on an <c>UPDATE</c> or <c>DELETE</c> through a partitioned view.</summary>
    internal static SimulatedSqlException TopOnPartitionedView() =>
        new("TOP is not allowed in an UPDATE or DELETE statement against a partitioned view.", 417, 16, 1);

    /// <summary>Mimics SQL Server's Msg 489 — an <c>OUTPUT</c> clause on a write through a partitioned view, named as written.</summary>
    internal static SimulatedSqlException OutputOnPartitionedView(string writtenName) =>
        new($"The OUTPUT clause cannot be specified because the target view \"{writtenName}\" is a partitioned view.", 489, 16, 1);

    /// <summary>Mimics SQL Server's Msg 5317 — a partitioned view as a <c>MERGE</c> target.</summary>
    internal static SimulatedSqlException MergeTargetIsPartitionedView() =>
        new("The target of a MERGE statement cannot be a partitioned view.", 5317, 16, 1);

    /// <summary>Mimics SQL Server's Msg 4431 — a member carries a <c>rowversion</c> column.</summary>
    internal static SimulatedSqlException PartitionedViewTimestampColumn(string view, string table, byte state) =>
        new($"Partitioned view '{view}' is not updatable because table '{table}' has a timestamp column.", 4431, 16, state);

    /// <summary>Mimics SQL Server's Msg 4433 — an <c>INSERT</c> through a partitioned view one of whose members has an identity column.</summary>
    internal static SimulatedSqlException PartitionedViewInsertIdentity(string view, string table) =>
        new($"Cannot INSERT into partitioned view '{view}' because table '{table}' has an IDENTITY constraint.", 4433, 16, 4);

    /// <summary>Mimics SQL Server's Msg 4434 — a member carries an <c>INSTEAD OF</c> trigger.</summary>
    internal static SimulatedSqlException PartitionedViewInsteadOfTrigger(string view, string table, byte state) =>
        new($"Partitioned view '{view}' is not updatable because table '{table}' has an INSTEAD OF trigger.", 4434, 16, state);

    /// <summary>Mimics SQL Server's Msg 4436 — no column the members' CHECK constraints partition (state 12), or one outside their primary keys (state 13).</summary>
    internal static SimulatedSqlException UnionAllViewNoPartitioningColumn(string view, byte state) =>
        new($"UNION ALL view '{view}' is not updatable because a partitioning column was not found.", 4436, 16, state);

    /// <summary>Mimics SQL Server's Msg 4437 — a <c>BULK INSERT</c> into a partitioned view, or a view over one, naming the partitioned view.</summary>
    internal static SimulatedSqlException PartitionedViewBulkTarget(string view) =>
        new($"Partitioned view '{view}' is not updatable as the target of a bulk operation.", 4437, 16, 4);

    /// <summary>Mimics SQL Server's Msg 4438 — a member has a column the view doesn't deliver.</summary>
    internal static SimulatedSqlException PartitionedViewMissingColumns(string view, byte state) =>
        new($"Partitioned view '{view}' is not updatable because it does not deliver all columns from its member tables.", 4438, 16, state);

    /// <summary>Mimics SQL Server's Msg 4439 — the statement reads a member table besides writing through the view.</summary>
    internal static SimulatedSqlException PartitionedViewSourceReadsMember(string view, string table) =>
        new($"Partitioned view '{view}' is not updatable because the source query contains references to partition table '{table}'.", 4439, 16, 6);

    /// <summary>Mimics SQL Server's Msg 4440 — a member has no primary key.</summary>
    internal static SimulatedSqlException UnionAllViewMemberWithoutPrimaryKey(string view, string table) =>
        new($"UNION ALL view '{view}' is not updatable because a primary key was not found on table '{table}'.", 4440, 16, 9);

    /// <summary>Mimics SQL Server's Msg 4442 — one table read by two members.</summary>
    internal static SimulatedSqlException UnionAllViewTableUsedTwice(string view, string table) =>
        new($"UNION ALL view '{view}' is not updatable because base table '{table}' is used multiple times.", 4442, 16, 7);

    /// <summary>Mimics SQL Server's Msg 4443 — a member projecting one column twice.</summary>
    internal static SimulatedSqlException UnionAllViewColumnUsedTwice(string view, string column, string table) =>
        new($"UNION ALL view '{view}' is not updatable because column '{column}' of base table '{table}' is used multiple times.", 4443, 16, 8);

    /// <summary>Mimics SQL Server's Msg 4444 — a member's primary key column isn't one of the view's columns as it stands.</summary>
    internal static SimulatedSqlException UnionAllViewPrimaryKeyNotProjected(string view, string table) =>
        new($"UNION ALL view '{view}' is not updatable because the primary key of table '{table}' is not included in the union result.", 4444, 16, 10);

    /// <summary>Mimics SQL Server's Msg 4445 — a member's primary key sits in other view columns than the first member's.</summary>
    internal static SimulatedSqlException UnionAllViewPrimaryKeyMisaligned(string view, string table) =>
        new($"UNION ALL view '{view}' is not updatable because the primary key of table '{table}' is not unioned with primary keys of preceding tables.", 4445, 16, 11);

    /// <summary>Mimics SQL Server's Msg 4448 — an <c>INSERT</c> through a partitioned view leaving a column out.</summary>
    internal static SimulatedSqlException PartitionedViewInsertMissingValues(string view) =>
        new($"Cannot INSERT into partitioned view '{view}' because values were not supplied for all columns.", 4448, 16, 17);

    /// <summary>Mimics SQL Server's Msg 4449 — a <c>DEFAULT</c> written through a view over a set operator.</summary>
    internal static SimulatedSqlException DefaultsThroughSetOperatorView() =>
        new("Using defaults is not allowed in views that contain a set operator.", 4449, 16, 1);

    /// <summary>Mimics SQL Server's Msg 4450 — an <c>UPDATE</c> of the partitioning column when a member has an identity column.</summary>
    internal static SimulatedSqlException PartitionedViewUpdateIdentity(string view, string column, string table) =>
        new($"Cannot update partitioned view '{view}' because the definition of the view column '{column}' in table '{table}' has an IDENTITY constraint.", 4450, 16, 1);

    /// <summary>Mimics SQL Server's Msg 4452 — an <c>UPDATE</c> of the partitioning column when a cascading foreign key references a member.</summary>
    internal static SimulatedSqlException PartitionedViewUpdateCascade(string column, string view, string table) =>
        new($"Cannot UPDATE partitioning column '{column}' of view '{view}' because the table '{table}' has a CASCADE DELETE or CASCADE UPDATE constraint.", 4452, 16, 21);

    /// <summary>Mimics SQL Server's Msg 4453 — an <c>UPDATE</c> of the partitioning column when a member carries a DML trigger.</summary>
    internal static SimulatedSqlException PartitionedViewUpdateTrigger(string column, string view, string table) =>
        new($"Cannot UPDATE partitioning column '{column}' of view '{view}' because the table '{table}' has a INSERT, UPDATE or DELETE trigger.", 4453, 16, 22);

    /// <summary>Mimics SQL Server's Msg 4454 — members whose partitioning columns differ in type.</summary>
    internal static SimulatedSqlException PartitionedViewPartitionTypesDiffer(string view, byte state) =>
        new($"Cannot update the partitioned view \"{view}\" because the partitioning columns of its member tables have mismatched types.", 4454, 16, state);

    /// <summary>Mimics SQL Server's Msg 4456 — an <c>UPDATE</c> of the partitioning column when members' other columns differ in type.</summary>
    internal static SimulatedSqlException PartitionedViewColumnTypesDiffer(string view) =>
        new($"The partitioned view \"{view}\" is not updatable because one or more of the non-partitioning columns of its member tables have mismatched types.", 4456, 16, 18);

    /// <summary>
    /// Mimics SQL Server's Msg 4457 — a written row whose partitioning value no
    /// member's constraints admit. It ends the batch, leaving an open
    /// transaction standing, and a <c>TRY</c> frame catches it.
    /// </summary>
    internal static SimulatedSqlException PartitionedViewValueFitsNoMember() =>
        new("The attempted insert or update of the partitioned view failed because the value of the partitioning column does not belong to any of the partitions.", 4457, 16, 1) { TerminatesBatch = true };
}
