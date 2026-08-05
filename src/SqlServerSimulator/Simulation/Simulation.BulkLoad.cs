using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the <c>INSERT BULK [schema].[table] (col Type [COLLATE c], …)
    /// [WITH (opt, …)]</c> preamble SqlClient's <c>SqlBulkCopy</c> sends as a
    /// SQL batch immediately before the <c>BulkLoadBCP</c> data packet, into a
    /// <see cref="BulkInsertPlan"/> the network layer feeds back to
    /// <see cref="ExecuteBulkInsert"/> once the row stream arrives. Only the
    /// target table, the ordered target-column names, and the semantic
    /// <c>WITH</c> options (<c>KEEP_NULLS</c> / <c>CHECK_CONSTRAINTS</c> /
    /// <c>FIRE_TRIGGERS</c>) are extracted — per-column wire types are
    /// redundant with the table's own column definitions and the remaining
    /// options (<c>TABLOCK</c>, <c>ORDER</c>, <c>ROWS_PER_BATCH</c>, …) carry
    /// no modeled effect. <c>KEEP_IDENTITY</c> is expressed by the identity
    /// column's presence in the column list, not a WITH option (probe-confirmed
    /// against SQL Server 2025, 2026-07-18).
    /// </summary>
    internal static BulkInsertPlan PrepareBulkInsert(SimulatedDbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = commandText;
#pragma warning restore CA2100
        var batch = new BatchContext(command);
        var context = batch.Parser;
        var collation = batch.CurrentDatabase.Collation;

        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Insert })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Bulk })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var name = BatchContext.ParseObjectName(context);
        if (!batch.TryResolveTable(name, out var table))
            throw SimulatedSqlException.InvalidObjectName(name);

        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var targetColumns = new List<HeapColumn>();
        while (true)
        {
            if (context.GetNextRequired() is not Name columnToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            targetColumns.Add(table.Columns.FirstOrDefault(c => collation.Equals(c.Name, columnToken.Value))
                ?? throw SimulatedSqlException.InvalidColumnName(columnToken.Value));

            // Skip the column's type (and any COLLATE clause / parenthesized
            // length args) up to the next top-level ',' or the closing ')'.
            var depth = 0;
            var atDelimiter = false;
            while (!atDelimiter)
            {
                context.MoveNextRequired();
                switch (context.Token)
                {
                    case Operator { Character: '(' }:
                        depth++;
                        break;
                    case Operator { Character: ')' } when depth == 0:
                    case Operator { Character: ',' } when depth == 0:
                        atDelimiter = true;
                        break;
                    case Operator { Character: ')' }:
                        depth--;
                        break;
                }
            }

            if (context.Token is Operator { Character: ')' })
                break;
        }

        var keepNulls = false;
        var checkConstraints = false;
        var fireTriggers = false;
        context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var depth = 0;
            var atEnd = false;
            while (!atEnd)
            {
                context.MoveNextRequired();
                switch (context.Token)
                {
                    case Operator { Character: '(' }:
                        depth++;
                        break;
                    case Operator { Character: ')' } when depth == 0:
                        atEnd = true;
                        break;
                    case Operator { Character: ')' }:
                        depth--;
                        break;
                    case Name option when depth == 0:
                        if (collation.Equals(option.Value, "KEEP_NULLS"))
                            keepNulls = true;
                        else if (collation.Equals(option.Value, "CHECK_CONSTRAINTS"))
                            checkConstraints = true;
                        else if (collation.Equals(option.Value, "FIRE_TRIGGERS"))
                            fireTriggers = true;
                        break;
                }
            }
        }

        return new BulkInsertPlan(table, [.. targetColumns], keepNulls, checkConstraints, fireTriggers);
    }

    /// <summary>
    /// Writes one <c>SqlBulkCopy</c> batch's decoded rows to the destination
    /// heap with bulk-load semantics, statement-atomic under the connection's
    /// active transaction (external-transaction rollback undoes them; an
    /// auto-commit call commits on success). Row order follows
    /// <see cref="BulkInsertPlan.TargetColumns"/>. Returns the rows written.
    /// </summary>
    /// <remarks>
    /// Bulk-load divergences from a normal INSERT, all probed against SQL
    /// Server 2025 (2026-07-18):
    /// <list type="bullet">
    /// <item>PRIMARY KEY / UNIQUE always enforce; NOT NULL always enforces.</item>
    /// <item>CHECK and FOREIGN KEY enforce only under the
    /// <c>CHECK_CONSTRAINTS</c> option; without it the constraints are left
    /// unchecked and marked <c>is_not_trusted = 1</c> on success.</item>
    /// <item>AFTER triggers fire only under <c>FIRE_TRIGGERS</c>.</item>
    /// <item>The identity column keeps its supplied values when it appears in
    /// the column list (KeepIdentity), otherwise the engine generates them.</item>
    /// <item>Without <c>KEEP_NULLS</c>, a NULL supplied for a column that has a
    /// DEFAULT gets the default; with it, the NULL is stored.</item>
    /// <item>Computed / rowversion / period columns are never sent by the
    /// client and are populated server-side, exactly as for INSERT.</item>
    /// </list>
    /// </remarks>
    internal int ExecuteBulkInsert(BulkInsertPlan plan, List<SqlValue[]> rows, SimulatedDbConnection connection)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = " ";
#pragma warning restore CA2100
        var batch = new BatchContext(command);
        batch.CurrentStatement.UtcNow = DateTime.UtcNow;
        var outcome = RunMutation(batch.Parser, context => this.BulkInsertRows(plan, rows, context));
        return outcome.RecordsAffected;
    }

    private SimulatedNonQuery BulkInsertRows(BulkInsertPlan plan, List<SqlValue[]> rows, ParserContext context)
    {
        var batch = context.Batch;
        var table = plan.Table;
        var identityOrdinal = table.IdentityOrdinal;
        var identityColumn = identityOrdinal >= 0 ? table.Columns[identityOrdinal] : null;
        var keepIdentity = identityColumn is not null && Array.Exists(plan.TargetColumns, c => ReferenceEquals(c, identityColumn));

        var targetOrdinals = new int[plan.TargetColumns.Length];
        for (var i = 0; i < plan.TargetColumns.Length; i++)
            targetOrdinals[i] = Array.IndexOf(table.Columns, plan.TargetColumns[i]);

        var hasTriggers = plan.FireTriggers && HasAfterTrigger(batch, table, TriggerActions.Insert);
        var triggerRows = hasTriggers ? new List<SqlValue[]>(rows.Count) : null;
        var nullResolver = new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), batch);

        foreach (var sourceRow in rows)
        {
            batch.BumpRowStamp();
            var rowValues = new SqlValue[table.Columns.Length];
            for (var i = 0; i < rowValues.Length; i++)
                rowValues[i] = SqlValue.Null(table.Columns[i].Type);

            // Columns absent from the bulk column list take their DEFAULT.
            for (var i = 0; i < table.Columns.Length; i++)
            {
                var column = table.Columns[i];
                if (column.Default is null || Array.IndexOf(targetOrdinals, i) >= 0)
                    continue;
                rowValues[i] = CoerceForInsert(column.Default.Run(nullResolver), column.Type);
            }

            for (var i = 0; i < plan.TargetColumns.Length; i++)
            {
                var targetColumn = plan.TargetColumns[i];
                var ordinal = targetOrdinals[i];
                var source = sourceRow[i];

                // KEEP_NULLS off: a NULL supplied for a column with a DEFAULT
                // gets the default rather than being stored as NULL.
                if (source.IsNull && !plan.KeepNulls && targetColumn.Default is not null)
                {
                    rowValues[ordinal] = CoerceForInsert(targetColumn.Default.Run(nullResolver), targetColumn.Type);
                    continue;
                }

                EnforceMaxLength(source, targetColumn, table.Name, batch.Connection);
                rowValues[ordinal] = CoerceForInsert(source, targetColumn.Type);
                if (ReferenceEquals(targetColumn, identityColumn))
                    identityColumn!.Identity!.ObserveExplicit(rowValues[ordinal].CoerceTo(SqlType.BigInt).AsInt64);
            }

            if (identityColumn is not null && !keepIdentity)
            {
                long generated;
                try
                {
                    generated = identityColumn.Identity!.GenerateNext();
                }
                catch (OverflowException)
                {
                    throw SimulatedSqlException.IdentityOverflow(identityColumn.Type.ToString()!);
                }

                rowValues[identityOrdinal] = CoerceForIdentity(generated, identityColumn);
            }

            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (table.Columns[i].Type == SqlType.RowVersion)
                    rowValues[i] = SqlValue.FromRowVersion(context.Batch.DatabaseFor(table).AllocateRowVersion());
            }

            if (table.PeriodColumns is { } pc
                && table.Columns[pc.StartOrdinal].GeneratedAs != GeneratedAlwaysAsRow.None)
            {
                rowValues[pc.StartOrdinal] = SqlValue.FromDateTime2(table.Columns[pc.StartOrdinal].Type, batch.CurrentStatement.UtcNow);
                rowValues[pc.EndOrdinal] = SqlValue.FromDateTime2(table.Columns[pc.EndOrdinal].Type, DateTime.MaxValue);
            }

            EvaluateComputedColumns(table, rowValues, batch);
            EnforceNotNull(table, rowValues);
            if (plan.CheckConstraints)
                EnforceCheckConstraints(table, rowValues, batch);

            var storedValues = ProjectStoredValues(table, rowValues);
            if (EnforceKeyConstraints(table, storedValues, batch) == RowKeyVerdict.SkipDuplicate
                || EnforceUniqueIndexes(table, rowValues, storedValues, batch) == RowKeyVerdict.SkipDuplicate)
            {
                continue;
            }

            if (plan.CheckConstraints)
                EnforceOutgoingForeignKeys(table, [rowValues], context, "INSERT");

            table.OwningDatabase?.RejectWriteWhenReadOnly();
            var (pageIndex, slotIndex) = table.Heap.Insert(RowEncoder.EncodeRow(table.StoredColumns, storedValues, table.Heap), batch.CurrentUndoLog);
            if (IsLockableTable(table))
            {
                batch.AcquireRowLockTxScoped(table, pageIndex, slotIndex, LockMode.Exclusive);
                VersionStore.CaptureWrite(batch, table, (pageIndex, slotIndex), oldRid: null, oldPayload: null, VersionWriteKind.Insert);
            }

            triggerRows?.Add((SqlValue[])rowValues.Clone());
        }

        this.EnforceIndexedViews(table, batch);

        // Default bulk (no CHECK_CONSTRAINTS) leaves the table's CHECK and
        // outgoing FK constraints unvalidated against the streamed rows, so
        // real SQL Server marks them untrusted. Runs only on a successful
        // batch — a mid-batch failure rolls back via RunMutation's undo log.
        if (!plan.CheckConstraints)
        {
            foreach (var check in table.CheckConstraints)
                check.IsNotTrusted = true;
            foreach (var fk in table.OutgoingForeignKeys)
                fk.IsNotTrusted = true;
        }

        if (triggerRows is { Count: > 0 })
        {
            batch.Connection.LastStatementRowCount = triggerRows.Count;
            this.FireTriggers(batch, table, TriggerActions.Insert, insertedRows: triggerRows, deletedRows: null, affectedRowCount: triggerRows.Count);
        }

        return new SimulatedNonQuery(rows.Count);
    }
}

/// <summary>
/// The parsed <c>INSERT BULK</c> preamble: the destination table, the ordered
/// target columns from its column list, and the semantic <c>WITH</c> options.
/// KeepIdentity is derived from the identity column's presence in
/// <see cref="TargetColumns"/> rather than carried as a flag.
/// </summary>
internal sealed class BulkInsertPlan(HeapTable table, HeapColumn[] targetColumns, bool keepNulls, bool checkConstraints, bool fireTriggers)
{
    public readonly HeapTable Table = table;

    public readonly HeapColumn[] TargetColumns = targetColumns;

    public readonly bool KeepNulls = keepNulls;

    public readonly bool CheckConstraints = checkConstraints;

    public readonly bool FireTriggers = fireTriggers;
}
