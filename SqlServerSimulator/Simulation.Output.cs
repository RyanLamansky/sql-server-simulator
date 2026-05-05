using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Detects the contextual <c>OUTPUT</c> keyword on the current token and,
    /// if present, parses the comma-separated projection list following the
    /// rules documented on <see cref="OutputProjection"/>. Returns
    /// <see langword="null"/> when <c>OUTPUT</c> is absent (the surrounding
    /// statement just continues with VALUES).
    /// </summary>
    /// <param name="context">Parser state, positioned on the token after the column-list closer.</param>
    /// <param name="destinationTable">The INSERT target — supplies the columns reachable through <c>INSERTED</c>.</param>
    /// <param name="sourceColumnNames">For MERGE only: the source alias's column names. <see langword="null"/> for plain INSERT.</param>
    private static OutputProjection? TryParseOutputClause(ParserContext context, HeapTable destinationTable, (string SourceAlias, string[] SourceColumns, SqlType[] SourceTypes)? sourceColumnNames)
    {
        if (!context.MatchContextual(ContextualKeyword.Output))
            return null;

        var expressions = new List<Expression>();
        var columnNames = new List<string>();

        SqlType ResolveOutputType(List<string> name)
        {
            if (name.Count >= 2 && Collation.Default.Equals(name[0], "INSERTED"))
            {
                var lastPart = name[^1];
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, lastPart))
                        return destinationTable.Columns[i].Type;
                }
            }
            else if (sourceColumnNames is var (sourceAlias, sourceCols, sourceTypes) && Collation.Default.Equals(name[0], sourceAlias))
            {
                var lastPart = name[^1];
                for (var i = 0; i < sourceCols.Length; i++)
                {
                    if (Collation.Default.Equals(sourceCols[i], lastPart))
                        return sourceTypes[i];
                }
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(string.Join('.', name));
        }

        do
        {
            context.MoveNextRequired();
            var expr = Expression.Parse(context);
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.As }:
                    expr = Expression.AssignName(expr, context.GetNextRequired<Name>());
                    context.MoveNextOptional();
                    break;
                case Name aliasName:
                    expr = Expression.AssignName(expr, aliasName);
                    context.MoveNextOptional();
                    break;
            }
            expressions.Add(expr);
            columnNames.Add(expr.Name);
        }
        while (context.Token is Operator { Character: ',' });

        var schema = new SqlType[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
            schema[i] = expressions[i].GetSqlType(ResolveOutputType);

        return new OutputProjection(expressions, [.. columnNames], schema, destinationTable, sourceColumnNames);
    }

    /// <summary>
    /// Holds the parsed <c>OUTPUT</c> projection together with its statically
    /// resolved schema and the column-name resolvers it needs at row time.
    /// Backs both <c>INSERT ... OUTPUT</c> and <c>MERGE ... OUTPUT</c>; the
    /// MERGE source-alias plumbing is opt-in via the constructor.
    /// </summary>
    private sealed class OutputProjection(
        List<Expression> expressions,
        string[] columnNames,
        SqlType[] schema,
        HeapTable destinationTable,
        (string SourceAlias, string[] SourceColumns, SqlType[] SourceTypes)? source)
    {
        public readonly SqlType[] Schema = schema;
        public readonly string[] ColumnNames = columnNames;

        /// <summary>
        /// Evaluates each projection expression against the just-inserted row
        /// (the <c>INSERTED</c> virtual table) and, for MERGE, the matching
        /// source-row values addressed via the source alias. Returns the
        /// encoded output row in <see cref="Schema"/> shape.
        /// </summary>
        public byte[] ProjectRow(SqlValue[] insertedRow, SqlValue[]? sourceRowValues)
        {
            SqlValue Resolve(List<string> name)
            {
                if (name.Count >= 2 && Collation.Default.Equals(name[0], "INSERTED"))
                {
                    var lastPart = name[^1];
                    for (var i = 0; i < destinationTable.Columns.Length; i++)
                    {
                        if (Collation.Default.Equals(destinationTable.Columns[i].Name, lastPart))
                            return insertedRow[i];
                    }
                }
                else if (source is var (sourceAlias, sourceCols, _) && sourceRowValues is not null && Collation.Default.Equals(name[0], sourceAlias))
                {
                    var lastPart = name[^1];
                    for (var i = 0; i < sourceCols.Length; i++)
                    {
                        if (Collation.Default.Equals(sourceCols[i], lastPart))
                            return sourceRowValues[i];
                    }
                }
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(string.Join('.', name));
            }

            var projected = new SqlValue[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
                projected[i] = expressions[i].Run(Resolve);
            return RowEncoder.EncodeRow(this.Schema, projected);
        }
    }
}
