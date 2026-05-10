using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the INSERT preamble (<c>INTO</c> keyword, table name, optional
    /// column list, and VALUES tuples) and writes the resulting rows to the
    /// destination table's heap.
    /// </summary>
    private static SimulatedStatementOutcome ParseInsert(ParserContext context)
    {
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Into })
            context.MoveNextRequired();

        var destinationTableToken = context.Token as StringToken
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        return context.Simulation.HeapTables.TryGetValue(destinationTableToken.Value, out var destinationTable)
            ? ProcessHeapInsert(destinationTable, context)
            : throw SimulatedSqlException.InvalidObjectName(destinationTableToken);
    }

    /// <summary>
    /// INSERT processor. Parses the column subset, optional <c>OUTPUT</c>
    /// clause, and VALUES tuples; converts each value token to a
    /// <see cref="SqlValue"/> typed to its target column; encodes each row
    /// via <see cref="RowEncoder"/> and appends the bytes to
    /// <paramref name="destinationTable"/>'s heap. When <c>OUTPUT</c> is
    /// present, the projected per-row results stream out as a
    /// <see cref="SimulatedSqlResultSet"/> (consumed by
    /// <c>ExecuteReader</c>); otherwise a plain <see cref="SimulatedNonQuery"/>
    /// is returned.
    /// </summary>
    private static SimulatedStatementOutcome ProcessHeapInsert(HeapTable destinationTable, ParserContext context)
    {
        var identityOrdinal = destinationTable.IdentityOrdinal;
        var identityColumn = identityOrdinal >= 0 ? destinationTable.Columns[identityOrdinal] : null;
        var identityInsertOn = identityColumn is not null
            && context.Connection.IdentityInsertTable is string activeTable
            && Collation.Default.Equals(activeTable, destinationTable.Name);

        HeapColumn[] destinationColumns;
        if (context.GetNextRequired() is Operator { Character: '(' })
        {
            var usedColumns = new List<HeapColumn>();
            while (true)
            {
                if (context.GetNextRequired() is not StringToken column)
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                var columnName = column.Value;
                var tableColumn = destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, columnName))
                    ?? throw SimulatedSqlException.InvalidColumnName(columnName);
                if (tableColumn.Computed is not null)
                    throw SimulatedSqlException.ColumnCannotBeModified(tableColumn.Name);
                if (tableColumn.Type == SqlType.RowVersion)
                    throw SimulatedSqlException.CannotInsertExplicitTimestamp();
                usedColumns.Add(tableColumn);

                var separator = context.GetNextRequired();
                if (separator is Operator { Character: ')' })
                    break;
                if (separator is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            destinationColumns = [.. usedColumns];

            context.MoveNextRequired();
        }
        else
        {
            // No column list: target every regular column (skip identity when
            // IDENTITY_INSERT is OFF and skip every computed column — neither
            // is a writable destination from the VALUES side; rowversion is
            // also auto-generated and never accepts explicit values).
            destinationColumns = (identityColumn is not null && !identityInsertOn)
                ? [.. destinationTable.Columns.Where(c => c.Identity is null && c.Computed is null && c.Type != SqlType.RowVersion)]
                : [.. destinationTable.Columns.Where(c => c.Computed is null && c.Type != SqlType.RowVersion)];
        }

        if (identityColumn is not null)
        {
            var identityListed = destinationColumns.Any(c => ReferenceEquals(c, identityColumn));
            if (identityListed && !identityInsertOn)
                throw SimulatedSqlException.CannotInsertExplicitIdentity(destinationTable.Name);
            if (!identityListed && identityInsertOn)
                throw SimulatedSqlException.ExplicitIdentityRequired(destinationTable.Name);
        }

        var output = TryParseOutputClause(context, destinationTable, sourceColumnNames: null);

        var sourceRows = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Values } => EvaluateValuesTuples(context),
            ReservedKeyword { Keyword: Keyword.Select } => ExecuteSelectSource(context, destinationColumns.Length),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

        decimal? lastIdentityValue = null;
        var outputRows = output is null ? null : new List<byte[]>(sourceRows.Count);
        foreach (var sourceRow in sourceRows)
        {
            var rowValues = new SqlValue[destinationTable.Columns.Length];
            for (var i = 0; i < rowValues.Length; i++)
                rowValues[i] = SqlValue.Null(destinationTable.Columns[i].Type);

            // Defaults run only for columns not in the destination column list:
            // when an INSERT supplies an explicit value (including explicit
            // NULL), the column's DEFAULT must not fire. This also keeps
            // sequence-advancing defaults (NEWSEQUENTIALID) from being burned
            // on rows that already provide the value.
            for (var i = 0; i < destinationTable.Columns.Length; i++)
            {
                var column = destinationTable.Columns[i];
                if (column.Default is null) continue;
                var listed = false;
                for (var j = 0; j < destinationColumns.Length; j++)
                {
                    if (ReferenceEquals(destinationColumns[j], column))
                    {
                        listed = true;
                        break;
                    }
                }
                if (listed) continue;
                var defaultValue = column.Default.Run(name => throw SimulatedSqlException.InvalidColumnName(name));
                rowValues[i] = CoerceForInsert(defaultValue, column.Type);
            }

            for (var i = 0; i < destinationColumns.Length; i++)
            {
                var targetColumn = destinationColumns[i];
                var ordinal = -1;
                for (var j = 0; j < destinationTable.Columns.Length; j++)
                {
                    if (ReferenceEquals(destinationTable.Columns[j], targetColumn))
                    {
                        ordinal = j;
                        break;
                    }
                }

                var source = sourceRow[i];
                EnforceMaxLength(source, targetColumn, destinationTable.Name, context.Connection);
                var coerced = CoerceForInsert(source, targetColumn.Type);
                rowValues[ordinal] = coerced;

                if (ReferenceEquals(targetColumn, identityColumn))
                {
                    var explicitValue = coerced.CoerceTo(SqlType.BigInt).AsInt64;
                    identityColumn.Identity!.ObserveExplicit(explicitValue);
                    lastIdentityValue = explicitValue;
                }
            }

            if (identityColumn is not null && !destinationColumns.Any(c => ReferenceEquals(c, identityColumn)))
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
                lastIdentityValue = generated;
            }

            // Auto-generate rowversion for every row in a table that has one.
            for (var i = 0; i < destinationTable.Columns.Length; i++)
            {
                if (destinationTable.Columns[i].Type == SqlType.RowVersion)
                    rowValues[i] = SqlValue.FromRowVersion(context.Simulation.AllocateRowVersion());
            }

            // Evaluate computed columns now — both persisted (whose result
            // gets stored) and non-persisted (whose result OUTPUT may reference
            // and which the row's full SqlValue array needs filled). Refs in
            // the expression bind only to stored columns thanks to Msg 1759
            // at CREATE TABLE.
            EvaluateComputedColumns(destinationTable, rowValues);
            EnforceNotNull(destinationTable, rowValues);
            EnforceCheckConstraints(destinationTable, rowValues);

            var storedValues = ProjectStoredValues(destinationTable, rowValues);
            EnforceKeyConstraints(destinationTable, storedValues);
            destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.StoredColumns, storedValues, destinationTable.Heap), context.CurrentUndoLog);

            if (output is { } o)
                outputRows!.Add(o.ProjectRow(rowValues, sourceRowValues: null));
        }

        // Per SQL Server: any INSERT updates SCOPE_IDENTITY/@@IDENTITY —
        // to the generated/explicit identity if the table has one, or to
        // NULL otherwise (resetting state from a prior identity insert).
        context.Connection.LastIdentity = lastIdentityValue;

        return output is { } o2
            ? new SimulatedSqlResultSet(o2.Schema, o2.ColumnNames, outputRows!)
            : new SimulatedNonQuery(sourceRows.Count);
    }

    /// <summary>
    /// Parses INSERT's <c>VALUES (…), (…)</c> source via the shared
    /// <c>ParseValuesTuples</c> helper, then eagerly evaluates each cell
    /// expression to a <see cref="SqlValue"/>. VALUES expressions can't
    /// reference columns; the column-resolver hook always raises
    /// <see cref="SimulatedSqlException.InvalidColumnName(string)"/>.
    /// </summary>
    private static List<SqlValue[]> EvaluateValuesTuples(ParserContext context)
    {
        var tuples = ParseValuesTuples(context);
        var rows = new List<SqlValue[]>(tuples.Count);
        foreach (var tuple in tuples)
        {
            var values = new SqlValue[tuple.Length];
            for (var i = 0; i < tuple.Length; i++)
                values[i] = tuple[i].Run(name => throw SimulatedSqlException.InvalidColumnName(name));
            rows.Add(values);
        }
        return rows;
    }

    /// <summary>
    /// Parses and executes the <c>SELECT</c>-source side of <c>INSERT … SELECT</c>.
    /// Validates the projection-count vs insert-list count at parse time
    /// (Msg 120 / Msg 121, matching SQL Server's pre-execution diagnostic),
    /// then buffers the result into a list of rows so the existing per-row
    /// encode loop can run unchanged. Buffering also makes self-insert
    /// (<c>INSERT t SELECT … FROM t</c>) safe — the source materializes
    /// before any destination write.
    /// </summary>
    private static List<SqlValue[]> ExecuteSelectSource(ParserContext context, int expectedColumnCount)
    {
        var selection = Selection.Parse(context, depth: 0);

        if (selection.Schema.Length < expectedColumnCount)
            throw SimulatedSqlException.InsertSelectListFewerThanInsertList();
        if (selection.Schema.Length > expectedColumnCount)
            throw SimulatedSqlException.InsertSelectListMoreThanInsertList();

        var resultSet = selection.Execute();
        var rows = new List<SqlValue[]>();
        foreach (var rowBytes in resultSet.RowBytes)
            rows.Add(RowDecoder.DecodeRow(resultSet.Schema, rowBytes));
        return rows;
    }
}
