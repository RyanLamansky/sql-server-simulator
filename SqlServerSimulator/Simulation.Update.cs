using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and executes an UPDATE statement of the form
    /// <c>UPDATE &lt;table&gt; SET col = expr [, col = expr]* [WHERE pred]</c>.
    /// Single-table only — multi-table forms (<c>UPDATE alias SET ... FROM ...</c>),
    /// <c>OUTPUT</c> clauses on UPDATE, and <c>WITH</c> table hints aren't
    /// supported here; see CLAUDE.md for the EF Core deferrals tied to these.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two-phase execution: phase 1 walks the heap, picks rows matching
    /// the WHERE predicate, and computes their new full-column values
    /// (every SET RHS evaluated against the same <em>pre-update</em> snapshot
    /// of the row, matching SQL Server's documented behavior — verified by
    /// probe). Per-row constraints (NOT NULL via Msg 515 with the
    /// <c>"UPDATE fails."</c> verb; CHECK via Msg 547 with
    /// <c>"UPDATE statement"</c>) fire here. Phase 2 validates PK / UNIQUE
    /// against the <em>post-update</em> virtual state — every affected
    /// row's new key is checked against the other affected rows' new
    /// keys plus the non-affected heap rows' existing keys; rows being
    /// updated don't self-collide. Phase 3 mutates: each affected row's
    /// old slot is tombstoned, then the new bytes are appended.
    /// </para>
    /// <para>
    /// Per-row pattern matches the INSERT path — same <see cref="EvaluateComputedColumns"/>,
    /// <see cref="EnforceNotNull"/>, <see cref="EnforceCheckConstraints"/>,
    /// <see cref="ProjectStoredValues"/>, and <see cref="RowEncoder"/>
    /// pipeline — so storage encoding (oversize var-column off-row push
    /// included) is shared.
    /// </para>
    /// <para>
    /// LOB chains held by replaced row payloads are orphaned in
    /// <see cref="Heap.LobPages"/>; full LOB lifecycle is a follow-up bundle.
    /// </para>
    /// </remarks>
    private static SimulatedStatementOutcome ParseUpdate(ParserContext context)
    {
        if (context.GetNextRequired() is not StringToken tableToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (!context.Simulation.HeapTables.TryGetValue(tableToken.Value, out var table))
            throw SimulatedSqlException.InvalidObjectName(tableToken);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var assignments = new List<(int ColumnOrdinal, Expression Expr)>();
        while (true)
        {
            if (context.GetNextRequired() is not StringToken columnNameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var columnOrdinal = -1;
            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (Collation.Default.Equals(table.Columns[i].Name, columnNameToken.Value))
                {
                    columnOrdinal = i;
                    break;
                }
            }
            if (columnOrdinal < 0)
                throw SimulatedSqlException.InvalidColumnName(columnNameToken.Value);

            var column = table.Columns[columnOrdinal];
            if (column.Identity is not null)
                throw SimulatedSqlException.CannotUpdateIdentityColumn(column.Name);
            if (column.Computed is not null)
                throw SimulatedSqlException.ColumnCannotBeModified(column.Name);
            if (column.Type == SqlType.RowVersion)
                throw SimulatedSqlException.CannotUpdateTimestampColumn();

            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            var expr = Expression.Parse(context);
            assignments.Add((columnOrdinal, expr));

            if (context.Token is Operator { Character: ',' })
                continue;
            break;
        }

        var output = TryParseOutputClauseForMutation(context, table, allowInserted: true, allowDeleted: true);

        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            where = BooleanExpression.Parse(context);
        }

        // Phase 1: pick affected rows and compute their new values. When OUTPUT
        // is present we also retain the pre-update full-column snapshot so the
        // projection can resolve DELETED.<col> / INSERTED.<col> in phase 3.
        var affected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();
        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        foreach (var (pageIndex, slotIndex, rowBytes) in table.Heap.EnumerateRowsWithAddress())
        {
            var fullValues = new SqlValue[table.Columns.Length];
            for (var i = 0; i < table.Columns.Length; i++)
            {
                var ord = table.StorageOrdinals[i];
                fullValues[i] = ord < 0
                    ? SqlValue.Null(table.Columns[i].Type)
                    : RowDecoder.DecodeColumn(storedColumns, rowBytes, ord, lobStore);
            }
            // Fill non-persisted computed slots from the pre-update stored values.
            EvaluateComputedColumns(table, fullValues);

            SqlValue ResolveOriginal(MultiPartName name)
            {
                for (var k = 0; k < table.Columns.Length; k++)
                {
                    if (Collation.Default.Equals(table.Columns[k].Name, name.Leaf))
                        return fullValues[k];
                }
                throw SimulatedSqlException.InvalidColumnName(name);
            }

            if (where is not null && where.Run(ResolveOriginal) != true)
                continue;

            var newValues = new SqlValue[table.Columns.Length];
            Array.Copy(fullValues, newValues, fullValues.Length);

            foreach (var (ordinal, expr) in assignments)
            {
                var raw = expr.Run(ResolveOriginal);
                EnforceMaxLength(raw, table.Columns[ordinal], table.Name, context.Simulation);
                newValues[ordinal] = CoerceForInsert(raw, table.Columns[ordinal].Type);
            }

            // Auto-bump rowversion on every affected row, regardless of SET list.
            for (var ci = 0; ci < table.Columns.Length; ci++)
            {
                if (table.Columns[ci].Type == SqlType.RowVersion)
                    newValues[ci] = SqlValue.FromRowVersion(context.Simulation.AllocateRowVersion());
            }

            // Recompute computed columns: their inputs may have changed.
            EvaluateComputedColumns(table, newValues);
            EnforceNotNull(table, newValues, "UPDATE");
            EnforceCheckConstraints(table, newValues, "UPDATE");

            // Snapshot the pre-update full-column row only when OUTPUT may
            // need it. fullValues carries the row's pre-update view at this
            // point — Array.Copy created a fresh newValues array, so
            // fullValues is still an exclusive reference.
            var oldSnapshot = output is null ? null : fullValues;
            affected.Add((pageIndex, slotIndex, newValues, oldSnapshot));
        }

        if (affected.Count == 0)
            return output is null ? new SimulatedNonQuery(0) : new SimulatedSqlResultSet(output.Schema, output.ColumnNames, []);

        // Phase 2: PK / UNIQUE validation against the post-update virtual state.
        EnforceKeyConstraintsForUpdate(table, affected);

        // Phase 3: tombstone old, insert new. Validation has already passed,
        // so no rollback is required.
        foreach (var (pageIndex, slotIndex, _, _) in affected)
            table.Heap.DeleteAt(pageIndex, slotIndex);
        foreach (var (_, _, fullNew, _) in affected)
            table.Heap.Insert(RowEncoder.EncodeRow(table.StoredColumns, ProjectStoredValues(table, fullNew), table.Heap));

        if (output is not null)
        {
            var rows = new List<byte[]>(affected.Count);
            foreach (var (_, _, fullNew, fullOld) in affected)
                rows.Add(output.ProjectRow(insertedValues: fullNew, deletedValues: fullOld));
            return new SimulatedSqlResultSet(output.Schema, output.ColumnNames, rows);
        }
        return new SimulatedNonQuery(affected.Count);
    }

    /// <summary>
    /// PK / UNIQUE validation for UPDATE: each affected row's new stored-key
    /// tuple is checked against (a) every other affected row's new key and
    /// (b) every non-affected heap row's existing key. Self-collision (a row
    /// matching its own pre-update self) is impossible because affected
    /// addresses are excluded from the heap-side scan. Edge case not
    /// modeled: SQL Server allows mass "shift" updates like
    /// <c>UPDATE t SET k = k + 1</c> over a unique-key column — the simulator's
    /// per-row check fails when the shifted value matches another affected
    /// row's pre-shift value pattern (CLAUDE.md flags this as a quirk).
    /// </summary>
    private static void EnforceKeyConstraintsForUpdate(HeapTable table, List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected)
    {
        if (table.KeyConstraints.Length == 0)
            return;

        var affectedAddrs = new HashSet<(int, int)>();
        var storedSnapshots = new SqlValue[affected.Count][];
        for (var i = 0; i < affected.Count; i++)
        {
            _ = affectedAddrs.Add((affected[i].PageIndex, affected[i].SlotIndex));
            storedSnapshots[i] = ProjectStoredValues(table, affected[i].FullNew);
        }

        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        for (var i = 0; i < affected.Count; i++)
        {
            var myStored = storedSnapshots[i];

            foreach (var constraint in table.KeyConstraints)
            {
                for (var j = 0; j < affected.Count; j++)
                {
                    if (i == j)
                        continue;
                    if (KeyTuplesEqualStored(myStored, storedSnapshots[j], constraint))
                        throw KeyViolationForUpdate(table, constraint, myStored);
                }

                foreach (var (p, s, bytes) in table.Heap.EnumerateRowsWithAddress())
                {
                    if (affectedAddrs.Contains((p, s)))
                        continue;
                    var allEqual = true;
                    for (var k = 0; k < constraint.StorageOrdinals.Length; k++)
                    {
                        var ord = constraint.StorageOrdinals[k];
                        var existing = RowDecoder.DecodeColumn(storedColumns, bytes, ord, lobStore);
                        if (!existing.Equals(myStored[ord]))
                        {
                            allEqual = false;
                            break;
                        }
                    }
                    if (allEqual)
                        throw KeyViolationForUpdate(table, constraint, myStored);
                }
            }
        }
    }

    private static bool KeyTuplesEqualStored(SqlValue[] a, SqlValue[] b, KeyConstraint constraint)
    {
        for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
        {
            var ord = constraint.StorageOrdinals[i];
            if (!a[ord].Equals(b[ord]))
                return false;
        }
        return true;
    }

    private static SimulatedSqlException KeyViolationForUpdate(HeapTable table, KeyConstraint constraint, SqlValue[] storedValues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
        {
            if (i > 0)
                _ = sb.Append(", ");
            _ = sb.Append(FormatKeyValue(storedValues[constraint.StorageOrdinals[i]]));
        }
        return SimulatedSqlException.ViolationOfKeyConstraint(constraint.ViolationKindWord, constraint.Name, table.Name, sb.ToString());
    }
}
