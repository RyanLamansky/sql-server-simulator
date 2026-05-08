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
        if (context.GetNextRequired() is not StringToken leadingIdentToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // The leading identifier is either the target table (single-table form
        // `UPDATE t SET ...`) or an alias for the FROM clause that follows
        // (multi-table form `UPDATE alias SET alias.col = ... FROM table AS alias ...` —
        // what EF Core's ExecuteUpdate emits). Try table-resolution now; defer
        // the InvalidObjectName error until after we've seen whether a FROM
        // clause provides the binding.
        _ = context.Simulation.HeapTables.TryGetValue(leadingIdentToken.Value, out var leadingTable);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Phase 1 of two-pass parsing: collect raw (columnName, expr) pairs
        // without resolving column ordinals — we may not know the binding
        // table yet (alias form). LHS supports both bare `col = expr` and
        // alias-qualified `[a].[col] = expr` forms; the alias is accepted
        // verbatim without cross-checking against the FROM-clause's alias
        // (the simulator is single-source so the validation is moot).
        var rawAssignments = new List<(string ColumnName, Expression Expr)>();
        while (true)
        {
            if (context.GetNextRequired() is not StringToken first)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            string columnName;
            switch (context.GetNextRequired())
            {
                case Operator { Character: '.' }:
                    if (context.GetNextRequired() is not StringToken col)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    columnName = col.Value;
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Operator { Character: '=' }:
                    columnName = first.Value;
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            context.MoveNextRequired();
            var expr = Expression.Parse(context);
            rawAssignments.Add((columnName, expr));

            if (context.Token is Operator { Character: ',' })
                continue;
            break;
        }

        // OUTPUT requires a known table. EF Core never emits OUTPUT alongside
        // ExecuteUpdate's multi-table FROM, so the simulator only supports it
        // when leading-identifier resolution gave us the target up-front.
        MutationOutputProjection? output = null;
        if (leadingTable is not null)
            output = TryParseOutputClauseForMutation(context, leadingTable, allowInserted: true, allowDeleted: true);

        // Optional FROM clause for the multi-table form. Single-source-only:
        // multiple sources or a JOIN raise NotSupportedException so the gap
        // is visible. When both leading-identifier and FROM-clause forms
        // are present (e.g. `UPDATE t SET ... FROM t AS alias`), the FROM
        // table wins — matching SQL Server's binding precedence.
        var table = context.Token is ReservedKeyword { Keyword: Keyword.From }
            ? ParseSingleSourceFromClause(context)
            : leadingTable;

        table = table ?? throw SimulatedSqlException.InvalidObjectName(leadingIdentToken);

        // Phase 2: resolve column ordinals against the (now known) target table.
        var assignments = new List<(int ColumnOrdinal, Expression Expr)>();
        foreach (var (colName, expr) in rawAssignments)
        {
            var columnOrdinal = -1;
            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (Collation.Default.Equals(table.Columns[i].Name, colName))
                {
                    columnOrdinal = i;
                    break;
                }
            }
            if (columnOrdinal < 0)
                throw SimulatedSqlException.InvalidColumnName(colName);

            var column = table.Columns[columnOrdinal];
            if (column.Identity is not null)
                throw SimulatedSqlException.CannotUpdateIdentityColumn(column.Name);
            if (column.Computed is not null)
                throw SimulatedSqlException.ColumnCannotBeModified(column.Name);
            if (column.Type == SqlType.RowVersion)
                throw SimulatedSqlException.CannotUpdateTimestampColumn();

            assignments.Add((columnOrdinal, expr));
        }

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

        // Phase 3: tombstone old, insert new. Validation has already passed —
        // no statement-level rollback gets triggered for these writes, but
        // pass the undo log anyway so an explicit transaction (Bundle 2) can
        // unwind them later. Each pair (delete-old, insert-new) records two
        // log entries; LIFO walk pops the insert first, then the delete,
        // restoring the original row.
        foreach (var (pageIndex, slotIndex, _, _) in affected)
            table.Heap.DeleteAt(pageIndex, slotIndex, context.CurrentUndoLog);
        foreach (var (_, _, fullNew, _) in affected)
            table.Heap.Insert(RowEncoder.EncodeRow(table.StoredColumns, ProjectStoredValues(table, fullNew), table.Heap), context.CurrentUndoLog);

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
    /// Parses the <c>FROM &lt;table&gt; [AS] &lt;alias&gt;</c> tail of a
    /// multi-table UPDATE / DELETE statement (the EF7+ ExecuteUpdate /
    /// ExecuteDelete shape). Enters with <see cref="ParserContext.Token"/>
    /// on the <c>FROM</c> keyword; returns the resolved target table and
    /// leaves the cursor on the first token after the alias (typically
    /// <c>WHERE</c> or end-of-statement). Multiple sources or a JOIN raise
    /// <see cref="NotSupportedException"/> — the simulator's UPDATE / DELETE
    /// pipeline is single-source only. The alias itself is parsed but
    /// discarded; runtime column-resolvers use <c>name.Leaf</c>, so any
    /// alias prefix in SET / WHERE / OUTPUT references resolves to the
    /// target column without per-alias validation.
    /// </summary>
    private static HeapTable ParseSingleSourceFromClause(ParserContext context)
    {
        if (context.GetNextRequired() is not StringToken fromTableToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (!context.Simulation.HeapTables.TryGetValue(fromTableToken.Value, out var fromTable))
            throw SimulatedSqlException.InvalidObjectName(fromTableToken);

        var afterTable = context.GetNextOptional();
        if (afterTable is ReservedKeyword { Keyword: Keyword.As })
        {
            context.MoveNextRequired<StringToken>();
            context.MoveNextOptional();
        }
        else if (afterTable is StringToken)
        {
            context.MoveNextOptional();
        }

        return context.Token is Operator { Character: ',' } or ReservedKeyword { Keyword: Keyword.Inner or Keyword.Left or Keyword.Right or Keyword.Full or Keyword.Cross or Keyword.Join }
            ? throw new NotSupportedException("Multi-source FROM clauses (joins, comma-separated sources) aren't supported in UPDATE / DELETE.")
            : fromTable;
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
