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
            && context.Simulation.IdentityInsertTable is string activeTable
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
            // is a writable destination from the VALUES side).
            destinationColumns = (identityColumn is not null && !identityInsertOn)
                ? [.. destinationTable.Columns.Where(c => c.Identity is null && c.Computed is null)]
                : [.. destinationTable.Columns.Where(c => c.Computed is null)];
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

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Values })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var sourceRows = new List<Expression[]>();

        do
        {
            if (context.GetNextRequired<Operator>() is not { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var sourceValues = new List<Expression>();
            while (true)
            {
                // Position context.Token at the start of the value expression
                // (just past the '(' or the previous ','), then let
                // Expression.Parse consume it. Parse leaves context.Token at
                // the first un-consumed token, which must be ',' or ')' here.
                context.MoveNextRequired();
                if (context.Token is Operator { Character: ',' or ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                sourceValues.Add(Expression.Parse(context));

                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            sourceRows.Add([.. sourceValues]);

        } while (context.GetNextOptional() is Operator { Character: ',' });

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

                var source = sourceRow[i].Run(name => throw SimulatedSqlException.InvalidColumnName(name));
                EnforceMaxLength(source, targetColumn, destinationTable.Name, context.Simulation);
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
            destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.StoredColumns, storedValues, destinationTable.Heap));

            if (output is { } o)
                outputRows!.Add(o.ProjectRow(rowValues, sourceRowValues: null));
        }

        // Per SQL Server: any INSERT updates SCOPE_IDENTITY/@@IDENTITY —
        // to the generated/explicit identity if the table has one, or to
        // NULL otherwise (resetting state from a prior identity insert).
        context.Simulation.LastIdentity = lastIdentityValue;

        return output is { } o2
            ? new SimulatedSqlResultSet(o2.Schema, o2.ColumnNames, outputRows!)
            : new SimulatedNonQuery(sourceRows.Count);
    }
}
