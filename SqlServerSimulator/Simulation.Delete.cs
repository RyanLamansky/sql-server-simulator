using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and executes <c>DELETE FROM &lt;table&gt; [WHERE pred]</c>.
    /// Rows matching the predicate are tombstoned at the page level; their
    /// payload bytes and any LOB chains are not reclaimed (CLAUDE.md flags
    /// this as a leak quirk pending the LOB-lifecycle bundle). Multi-table
    /// forms (<c>DELETE alias FROM ...</c>), <c>OUTPUT DELETED.*</c>, and
    /// other DELETE variants aren't supported here.
    /// </summary>
    private static SimulatedStatementOutcome ParseDelete(ParserContext context)
    {
        // FROM is optional in T-SQL DELETE; consume if present.
        var next = context.GetNextRequired();
        if (next is ReservedKeyword { Keyword: Keyword.From })
            next = context.GetNextRequired();

        if (next is not StringToken tableToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (!context.Simulation.HeapTables.TryGetValue(tableToken.Value, out var table))
            throw SimulatedSqlException.InvalidObjectName(tableToken);

        _ = context.GetNextOptional();
        // INSERTED isn't a valid qualifier in DELETE OUTPUT (probe-confirmed Msg 4104).
        var output = TryParseOutputClauseForMutation(context, table, allowInserted: false, allowDeleted: true);

        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            where = BooleanExpression.Parse(context);
        }

        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        var deleted = new List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)>();
        foreach (var (pageIndex, slotIndex, rowBytes) in table.Heap.EnumerateRowsWithAddress())
        {
            // Decode full row values when we need them — for WHERE evaluation,
            // for OUTPUT projection, or both. Skip the work entirely when
            // neither is in play (no-WHERE / no-OUTPUT).
            SqlValue[]? fullValues = null;
            if (where is not null || output is not null)
            {
                fullValues = new SqlValue[table.Columns.Length];
                for (var i = 0; i < table.Columns.Length; i++)
                {
                    var ord = table.StorageOrdinals[i];
                    fullValues[i] = ord < 0
                        ? SqlValue.Null(table.Columns[i].Type)
                        : RowDecoder.DecodeColumn(storedColumns, rowBytes, ord, lobStore);
                }
                EvaluateComputedColumns(table, fullValues);
            }

            if (where is not null)
            {
                var localValues = fullValues!;
                SqlValue Resolve(List<string> name)
                {
                    var leaf = name[^1];
                    for (var k = 0; k < table.Columns.Length; k++)
                    {
                        if (Collation.Default.Equals(table.Columns[k].Name, leaf))
                            return localValues[k];
                    }
                    throw SimulatedSqlException.InvalidColumnName(name);
                }

                if (where.Run(Resolve) != true)
                    continue;
            }

            deleted.Add((pageIndex, slotIndex, output is null ? null : fullValues));
        }

        foreach (var (pageIndex, slotIndex, _) in deleted)
            table.Heap.DeleteAt(pageIndex, slotIndex);

        if (output is not null)
        {
            var rows = new List<byte[]>(deleted.Count);
            foreach (var (_, _, fullOld) in deleted)
                rows.Add(output.ProjectRow(insertedValues: null, deletedValues: fullOld));
            return new SimulatedSqlResultSet(output.Schema, output.ColumnNames, rows);
        }
        return new SimulatedNonQuery(deleted.Count);
    }
}
