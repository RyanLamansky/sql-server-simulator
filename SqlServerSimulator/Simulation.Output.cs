using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// OUTPUT-clause parser for UPDATE / DELETE. Supports literal /
    /// parameter expressions and <c>INSERTED.&lt;col&gt;</c> /
    /// <c>DELETED.&lt;col&gt;</c> column references; star expansion
    /// (<c>INSERTED.*</c> / <c>DELETED.*</c>), bare column refs, and
    /// table-alias qualifiers aren't modeled (CLAUDE.md flags those).
    /// <paramref name="allowInserted"/> and <paramref name="allowDeleted"/>
    /// gate which qualifier the call site permits — UPDATE allows both;
    /// DELETE allows only DELETED (INSERTED.col on DELETE is rejected at
    /// parse time with Msg 4104, matching the probed real-server
    /// behavior). The returned <see cref="MutationOutputProjection"/> is
    /// re-runnable once per affected row.
    /// </summary>
    /// <remarks>
    /// On entry the cursor sits at the first lookahead token after the
    /// preceding clause (SET assignments for UPDATE; the table-name slot
    /// for DELETE). The schema resolves at parse time with a type-only
    /// resolver that mirrors the run-time resolver — bare column refs
    /// (<c>name.Count == 1</c>) raise Msg 207
    /// (<c>"Invalid column name 'X'."</c>); qualifiers other than
    /// INSERTED / DELETED raise Msg 4104. INSERTED columns on DELETE and
    /// any star-form raise the same Msg 4104 (Msg 207 stays for the
    /// bare-name case to match SQL Server's probe-confirmed shape).
    /// </remarks>
    private static MutationOutputProjection? TryParseOutputClauseForMutation(
        ParserContext context,
        HeapTable table,
        bool allowInserted,
        bool allowDeleted)
    {
        if (!context.MatchContextual(ContextualKeyword.Output))
            return null;

        var expressions = new List<Expression>();
        var names = new List<string>();
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
            names.Add(expr.Name);
        } while (context.Token is Operator { Character: ',' });

        SqlType ResolveOutputType(MultiPartName reference)
        {
            if (reference.Count == 1)
                throw SimulatedSqlException.InvalidColumnName(reference);

            var insertedRef = Collation.Default.Equals(reference.ImmediateQualifier, "INSERTED");
            var deletedRef = Collation.Default.Equals(reference.ImmediateQualifier, "DELETED");

            if (insertedRef && !allowInserted)
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(reference.ToString());
            if (deletedRef && !allowDeleted)
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(reference.ToString());
            if (!insertedRef && !deletedRef)
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(reference.ToString());

            for (var i = 0; i < table.Columns.Length; i++)
            {
                if (Collation.Default.Equals(table.Columns[i].Name, reference.Leaf))
                    return table.Columns[i].Type;
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(reference.ToString());
        }

        var schema = new SqlType[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
            schema[i] = expressions[i].GetSqlType(ResolveOutputType);

        return new MutationOutputProjection(table, [.. expressions], [.. names], schema);
    }

    /// <summary>
    /// Holds the parsed UPDATE / DELETE OUTPUT projection together with
    /// its statically resolved schema. Re-runnable once per affected row
    /// via <see cref="ProjectRow"/>; the row's <c>INSERTED</c> /
    /// <c>DELETED</c> values are passed in per call (the inner resolver
    /// dispatches on the qualifier).
    /// </summary>
    private sealed class MutationOutputProjection(
        HeapTable table,
        Expression[] expressions,
        string[] columnNames,
        SqlType[] schema)
    {
        public readonly SqlType[] Schema = schema;

        public readonly string[] ColumnNames = columnNames;

        /// <summary>
        /// Encodes one OUTPUT row by running each parsed expression against
        /// a per-row resolver that dispatches on <c>INSERTED.&lt;col&gt;</c>
        /// (post-update / post-insert values) and <c>DELETED.&lt;col&gt;</c>
        /// (pre-update / pre-delete values). Pass <see langword="null"/>
        /// for whichever side doesn't apply (DELETE has no INSERTED row);
        /// referencing the absent side is a parse-time error, so this
        /// runtime path doesn't need to defend against it.
        /// </summary>
        public byte[] ProjectRow(SqlValue[]? insertedValues, SqlValue[]? deletedValues)
        {
            SqlValue Resolve(MultiPartName reference)
            {
                var source = (Collation.Default.Equals(reference.ImmediateQualifier, "INSERTED") ? insertedValues
                    : Collation.Default.Equals(reference.ImmediateQualifier, "DELETED") ? deletedValues
                    : null)
                    ?? throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(reference.ToString());
                for (var i = 0; i < table.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(table.Columns[i].Name, reference.Leaf))
                        return source[i];
                }
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(reference.ToString());
            }

            var projected = new SqlValue[expressions.Length];
            for (var i = 0; i < expressions.Length; i++)
                projected[i] = expressions[i].Run(Resolve);
            return RowEncoder.EncodeRow(this.Schema, projected);
        }
    }

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

        SqlType ResolveOutputType(MultiPartName name)
        {
            if (Collation.Default.Equals(name.ImmediateQualifier, "INSERTED"))
            {
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                        return destinationTable.Columns[i].Type;
                }
            }
            else if (sourceColumnNames is var (sourceAlias, sourceCols, sourceTypes) && Collation.Default.Equals(name.ImmediateQualifier, sourceAlias))
            {
                for (var i = 0; i < sourceCols.Length; i++)
                {
                    if (Collation.Default.Equals(sourceCols[i], name.Leaf))
                        return sourceTypes[i];
                }
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
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
            SqlValue Resolve(MultiPartName name)
            {
                if (Collation.Default.Equals(name.ImmediateQualifier, "INSERTED"))
                {
                    for (var i = 0; i < destinationTable.Columns.Length; i++)
                    {
                        if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                            return insertedRow[i];
                    }
                }
                else if (source is var (sourceAlias, sourceCols, _) && sourceRowValues is not null && Collation.Default.Equals(name.ImmediateQualifier, sourceAlias))
                {
                    for (var i = 0; i < sourceCols.Length; i++)
                    {
                        if (Collation.Default.Equals(sourceCols[i], name.Leaf))
                            return sourceRowValues[i];
                    }
                }
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
            }

            var projected = new SqlValue[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
                projected[i] = expressions[i].Run(Resolve);
            return RowEncoder.EncodeRow(this.Schema, projected);
        }
    }
}
