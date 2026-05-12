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
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Output })
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

        var outputTarget = TryParseOutputIntoTarget(context, expressions.Count);

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

        return new MutationOutputProjection(table, [.. expressions], [.. names], schema, context.Batch, outputTarget);
    }

    /// <summary>
    /// Parses an optional <c>INTO @t [(col_list)]</c> suffix on an OUTPUT
    /// clause. Returns <see langword="null"/> when no INTO keyword is
    /// present. The target must be a table variable (v1 scope —
    /// <c>OUTPUT INTO &lt;regular_table&gt;</c> isn't modeled; real SQL Server
    /// accepts both but the bundle's coverage is just table variables).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Column mapping resolves at parse time: with no column list, projection
    /// column i maps to target column i (count must match — probe-confirmed
    /// Msg 213 on mismatch). With a column list, projection column i maps to
    /// the target column whose name matches list[i].
    /// </para>
    /// <para>
    /// Probe-confirmed against SQL Server 2025 (2026-05-12): a table variable
    /// must already be declared (Msg 1087 otherwise); the column-list count
    /// must match the projection count (Msg 213); columns named in the list
    /// must exist in the target (Msg 207).
    /// </para>
    /// </remarks>
    private static OutputTarget? TryParseOutputIntoTarget(ParserContext context, int projectionColumnCount)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Into })
            return null;
        context.MoveNextRequired();

        // v1 scope: only table-variable targets. Regular-table INTO targets
        // are deferred (NotSupportedException at the @t-only check below).
        if (context.Token is not AtPrefixedString)
            throw new NotSupportedException("OUTPUT INTO with a regular-table target isn't modeled in v1; use a table variable (@t) instead.");

        var targetName = BatchContext.ParseObjectName(context, acceptTableVariable: true);
        if (!context.Batch.TryResolveTable(targetName, out var targetTable))
            throw SimulatedSqlException.MustDeclareTableVariable(targetName.Leaf);
        context.MoveNextOptional();

        int[] columnOrdinals;
        if (context.Token is Operator { Character: '(' })
        {
            var ordinals = new List<int>();
            do
            {
                if (context.GetNextRequired() is not StringToken columnNameTok)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var matched = -1;
                for (var i = 0; i < targetTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(targetTable.Columns[i].Name, columnNameTok.Value))
                    {
                        matched = i;
                        break;
                    }
                }
                if (matched < 0)
                    throw SimulatedSqlException.InvalidColumnName(new MultiPartName(columnNameTok.Value));
                ordinals.Add(matched);
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            if (ordinals.Count != projectionColumnCount)
                throw SimulatedSqlException.OutputIntoColumnCountMismatch();
            columnOrdinals = [.. ordinals];
        }
        else
        {
            // Positional mapping. Count must match the target's full column
            // count (probe-confirmed Msg 213 on mismatch).
            if (targetTable.Columns.Length != projectionColumnCount)
                throw SimulatedSqlException.OutputIntoColumnCountMismatch();
            columnOrdinals = new int[projectionColumnCount];
            for (var i = 0; i < projectionColumnCount; i++)
                columnOrdinals[i] = i;
        }

        return new OutputTarget(targetTable, columnOrdinals);
    }

    /// <summary>
    /// Resolved <c>OUTPUT … INTO &lt;target&gt;</c> binding. <see cref="Target"/>
    /// is the table variable the projection appends rows to;
    /// <see cref="ProjectionToTargetOrdinal"/> maps projection column index
    /// to target table column ordinal (positional fill if INTO had no
    /// explicit column list).
    /// </summary>
    private sealed class OutputTarget(HeapTable target, int[] projectionToTargetOrdinal)
    {
        public readonly HeapTable Target = target;
        public readonly int[] ProjectionToTargetOrdinal = projectionToTargetOrdinal;

        /// <summary>
        /// Appends one row to <see cref="Target"/>. Columns named in the
        /// projection map by ordinal via <see cref="ProjectionToTargetOrdinal"/>;
        /// any target column not covered receives a NULL (real SQL Server
        /// would also apply a DEFAULT for unfilled columns — that path isn't
        /// modeled in v1 since EF / app patterns always project every
        /// non-default target column).
        /// </summary>
        public void Append(SqlValue[] projectedValues)
        {
            var targetValues = new SqlValue[this.Target.Columns.Length];
            for (var i = 0; i < targetValues.Length; i++)
                targetValues[i] = SqlValue.Null(this.Target.Columns[i].Type);
            for (var i = 0; i < projectedValues.Length; i++)
                targetValues[this.ProjectionToTargetOrdinal[i]] = projectedValues[i];
            this.Target.Heap.Insert(
                RowEncoder.EncodeRow(this.Target.StoredColumns, targetValues, this.Target.Heap),
                undoLog: null);
        }
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
        SqlType[] schema,
        BatchContext batch,
        OutputTarget? outputTarget)
    {
        private readonly BatchContext batch = batch;

        public readonly SqlType[] Schema = schema;

        public readonly string[] ColumnNames = columnNames;

        /// <summary>
        /// True when this OUTPUT clause includes an <c>INTO @t</c> target.
        /// The dispatching caller suppresses the per-row result-set yield in
        /// this case and surfaces the statement as a non-query (matches real
        /// SQL Server: <c>OUTPUT … INTO target</c> directs rows to the
        /// target only, without returning them to the client).
        /// </summary>
        public bool HasTarget => outputTarget is not null;

        /// <summary>
        /// Encodes one OUTPUT row by running each parsed expression against
        /// a per-row resolver that dispatches on <c>INSERTED.&lt;col&gt;</c>
        /// (post-update / post-insert values) and <c>DELETED.&lt;col&gt;</c>
        /// (pre-update / pre-delete values). Pass <see langword="null"/>
        /// for whichever side doesn't apply (DELETE has no INSERTED row);
        /// referencing the absent side is a parse-time error, so this
        /// runtime path doesn't need to defend against it. Returns the
        /// encoded projection-shape bytes when there's no INTO target;
        /// returns <see langword="null"/> when an INTO target consumed the
        /// row (caller skips the per-row result-set append in that case).
        /// </summary>
        public byte[]? ProjectRow(SqlValue[]? insertedValues, SqlValue[]? deletedValues)
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
                projected[i] = expressions[i].Run(new RuntimeContext(Resolve, this.batch));
            if (outputTarget is not null)
            {
                outputTarget.Append(projected);
                return null;
            }
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
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Output })
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

        var outputTarget = TryParseOutputIntoTarget(context, expressions.Count);

        var schema = new SqlType[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
            schema[i] = expressions[i].GetSqlType(ResolveOutputType);

        return new OutputProjection(expressions, [.. columnNames], schema, destinationTable, sourceColumnNames, context.Batch, outputTarget);
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
        (string SourceAlias, string[] SourceColumns, SqlType[] SourceTypes)? source,
        BatchContext batch,
        OutputTarget? outputTarget)
    {
        public readonly SqlType[] Schema = schema;
        public readonly string[] ColumnNames = columnNames;
        private readonly BatchContext batch = batch;

        /// <summary>See <see cref="MutationOutputProjection.HasTarget"/>.</summary>
        public bool HasTarget => outputTarget is not null;

        /// <summary>
        /// Evaluates each projection expression against the just-inserted row
        /// (the <c>INSERTED</c> virtual table) and, for MERGE, the matching
        /// source-row values addressed via the source alias. Returns the
        /// encoded projection-shape bytes when there's no INTO target;
        /// returns <see langword="null"/> when an INTO target consumed the
        /// row (caller skips the per-row result-set append).
        /// </summary>
        public byte[]? ProjectRow(SqlValue[] insertedRow, SqlValue[]? sourceRowValues)
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
                projected[i] = expressions[i].Run(new RuntimeContext(Resolve, this.batch));
            if (outputTarget is not null)
            {
                outputTarget.Append(projected);
                return null;
            }
            return RowEncoder.EncodeRow(this.Schema, projected);
        }
    }
}
