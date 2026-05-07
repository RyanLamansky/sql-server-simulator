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
        var output = TryParseLiteralOutputClauseForMutation(context);

        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            where = BooleanExpression.Parse(context);
        }

        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        var addressesToDelete = new List<(int PageIndex, int SlotIndex)>();
        foreach (var (pageIndex, slotIndex, rowBytes) in table.Heap.EnumerateRowsWithAddress())
        {
            if (where is null)
            {
                addressesToDelete.Add((pageIndex, slotIndex));
                continue;
            }

            var fullValues = new SqlValue[table.Columns.Length];
            for (var i = 0; i < table.Columns.Length; i++)
            {
                var ord = table.StorageOrdinals[i];
                fullValues[i] = ord < 0
                    ? SqlValue.Null(table.Columns[i].Type)
                    : RowDecoder.DecodeColumn(storedColumns, rowBytes, ord, lobStore);
            }
            EvaluateComputedColumns(table, fullValues);

            SqlValue Resolve(List<string> name)
            {
                var leaf = name[^1];
                for (var k = 0; k < table.Columns.Length; k++)
                {
                    if (Collation.Default.Equals(table.Columns[k].Name, leaf))
                        return fullValues[k];
                }
                throw SimulatedSqlException.InvalidColumnName(name);
            }

            if (where.Run(Resolve) == true)
                addressesToDelete.Add((pageIndex, slotIndex));
        }

        foreach (var (pageIndex, slotIndex) in addressesToDelete)
            table.Heap.DeleteAt(pageIndex, slotIndex);

        if (output is var (outputSchema, outputNames, outputRow))
        {
            var rows = new List<byte[]>(addressesToDelete.Count);
            for (var i = 0; i < addressesToDelete.Count; i++)
                rows.Add(outputRow);
            return new SimulatedSqlResultSet(outputSchema, outputNames, rows);
        }
        return new SimulatedNonQuery(addressesToDelete.Count);
    }
}
