using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Manages the higher-level logic to convert a sequence of command tokens into tabular results.
/// </summary>
internal sealed class Selection
{
    internal readonly SimulatedQueryResult Results;

    private Selection(SimulatedQueryResult results) => this.Results = results;

    /// <summary>
    /// Creates a <see cref="Selection"/> from a series of tokens. Follows the
    /// lookahead contract documented on <see cref="ParserContext"/>: on
    /// return, <see cref="ParserContext.Token"/> is the first token not
    /// consumed by the SELECT (typically <c>;</c>, <c>)</c> for a derived
    /// table, or null at end of command).
    /// </summary>
    /// <param name="context">Manages the overall parsing state.</param>
    /// <param name="depth">The current depth of recursed selection, such as with derived tables. 0 for the top-level SELECT.</param>
    /// <returns>The prepared command.</returns>
    /// <exception cref="SimulatedSqlException">A variety of messages are possible for various problems with the command.</exception>
    /// <exception cref="NotSupportedException">A condition was encountered that may be valid but can't currently be parsed.</exception>
    public static Selection Parse(ParserContext context, uint depth)
    {
        var distinct = false;
        int? topCount = null;

        var firstToken = context.GetNextRequired();

        // DISTINCT/ALL appear before TOP. SQL Server rejects `TOP n DISTINCT`
        // at parse time (Msg 156), and the only other quantifier is ALL which
        // is the implicit default — accept it but treat as no-op. Switch (vs
        // chained ifs) lets the compiler emit a single ReservedKeyword type
        // check for both arms.
        switch (firstToken)
        {
            case ReservedKeyword { Keyword: Keyword.Distinct }:
                distinct = true;
                firstToken = context.GetNextRequired();
                break;
            case ReservedKeyword { Keyword: Keyword.All }:
                firstToken = context.GetNextRequired();
                break;
        }

        if (firstToken is ReservedKeyword { Keyword: Keyword.Top })
        {
            var resolved = Expression
                .Parse(context.MoveNextRequiredReturnSelf())
                .Run(name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name));
            topCount = !resolved.IsNull && resolved.Type == SqlType.Int32
                ? resolved.AsInt32
                : throw SimulatedSqlException.TopFetchRequiresInteger();
        }

        List<Expression> expressions = [];
        List<BooleanExpression> excluders = [];
        List<OrderBySpec> orderBy = [];

        do
        {
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.From }:
                    break;

                case ReservedKeyword { Keyword: Keyword.Left or Keyword.Right or Keyword.Convert or Keyword.Try_Convert }:
                    // LEFT, RIGHT, CONVERT, and TRY_CONVERT are reserved
                    // keywords but valid as function-call heads inside a
                    // SELECT projection.
                    expressions.Add(Expression.Parse(context));
                    break;

                case ReservedKeyword { Keyword: not Keyword.Null } keyword:
                    throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword);

                case Operator { Character: ',' }:
                    if (expressions.Count == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    continue;
                case Operator { Character: ')' }:
                    if (depth == 0)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    goto ExitWhileTokenLoop;

                default:
                    expressions.Add(Expression.Parse(context));
                    break;
            }

            switch (context.Token)
            {
                case null:
                    goto ExitWhileTokenLoop;

                // End of statement in a multi-statement batch. Leave the ';'
                // as the current token so the outer dispatch loop sees it on
                // its next iteration (where it's a no-op separator) and
                // continues with whatever statement follows.
                case Operator { Character: ';' }:
                    goto ExitWhileTokenLoop;

                case Operator { Character: ',' }:
                    continue;

                case Name name:
                    expressions[^1] = Expression.AssignName(expressions[^1], name);
                    continue;

                case ReservedKeyword { Keyword: Keyword.As }:
                    expressions[^1] = Expression.AssignName(expressions[^1], context.GetNextRequired<Name>());
                    continue;

                case ReservedKeyword { Keyword: Keyword.From }:
                    switch (context.GetNextRequired())
                    {
                        case Name tableName:
                            if (!context.Simulation.HeapTables.TryGetValue(tableName.Value, out var heapTable)
                                && !Simulation.SystemHeapTables.TryGetValue(tableName.Value, out heapTable))
                            {
                                throw SimulatedSqlException.InvalidObjectName(tableName);
                            }

                            ConsumeOptionalAliasWhereOrderBy(context, excluders, orderBy);

                            var heapColumnNames = new string[heapTable.Columns.Length];
                            for (var ci = 0; ci < heapColumnNames.Length; ci++)
                                heapColumnNames[ci] = heapTable.Columns[ci].Name;

                            return new(BuildSqlProjection(heapColumnNames, heapTable.Columns, heapTable.StoredColumns, heapTable.StorageOrdinals, heapTable.Heap, heapTable.Rows, expressions, excluders, orderBy, distinct, topCount));

                        case Operator { Character: '(' }:
                            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
                                throw SimulatedSqlException.SyntaxErrorNear(context);

                            if (Selection.Parse(context, depth + 1).Results is not SimulatedSqlResultSet derived)
                                throw new InvalidOperationException("Inner SELECT produced a non-Pages result set; this should be unreachable.");

                            ConsumeOptionalAliasWhereOrderBy(context, excluders, orderBy);

                            // Inner SELECT result rows are LOB-inline (projections never
                            // emit LOB pointers because they have no destination Heap),
                            // so build a HeapColumn[] schema from the SqlType[] so the
                            // decoder still strips marker bytes for text/ntext/image
                            // columns; lobStore is null because no chain to follow.
                            var derivedColumns = new HeapColumn[derived.Schema.Length];
                            for (var ci = 0; ci < derivedColumns.Length; ci++)
                                derivedColumns[ci] = new HeapColumn(string.Empty, derived.Schema[ci], maxLength: null, nullable: true);
                            return new(BuildSqlProjection(derived.ColumnNames, derivedColumns, derivedColumns, storageOrdinals: null, lobStore: null, derived.RowBytes, expressions, excluders, orderBy, distinct, topCount));
                    }

                    throw SimulatedSqlException.SyntaxErrorNear(context);

                case ReservedKeyword { Keyword: Keyword.Where or Keyword.Order }:
                    ConsumeWhereAndOrderBy(context, excluders, orderBy);
                    goto ExitWhileTokenLoop;
            }

            throw SimulatedSqlException.SyntaxErrorNear(context);
        } while (context.GetNextOptional() is not null);
    ExitWhileTokenLoop:

        return new(BuildSynthesizedSqlRow(expressions, excluders, orderBy, distinct, topCount));
    }

    /// <summary>
    /// After a FROM source (table name or derived table), parses an optional
    /// AS alias (the alias name is discarded — column resolution today is by
    /// last-component match, so the alias is informational), then any number
    /// of WHERE clauses, then an optional ORDER BY clause.
    /// </summary>
    private static void ConsumeOptionalAliasWhereOrderBy(ParserContext context, List<BooleanExpression> excluders, List<OrderBySpec> orderBy)
    {
        var nextToken = context.GetNextOptional();
        if (nextToken is ReservedKeyword { Keyword: Keyword.As })
        {
            _ = context.GetNextRequired<Name>();
            _ = context.GetNextOptional();
        }

        ConsumeWhereAndOrderBy(context, excluders, orderBy);
    }

    /// <summary>
    /// Reads zero or more WHERE clauses followed by an optional ORDER BY,
    /// starting from <see cref="ParserContext.Token"/> already positioned at
    /// the first lookahead token (e.g. WHERE, ORDER, ;, or null). On return,
    /// <see cref="ParserContext.Token"/> is the first token after the last
    /// consumed clause (typically ;, ), or null).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ParserContext.Token"/> directly between clauses
    /// instead of advancing with <see cref="ParserContext.GetNextOptional"/>
    /// — sub-Parse helpers leave Token at the first un-consumed token per
    /// the lookahead contract, and an extra advance here would silently swallow
    /// the next clause's opening keyword.
    /// </remarks>
    private static void ConsumeWhereAndOrderBy(ParserContext context, List<BooleanExpression> excluders, List<OrderBySpec> orderBy)
    {
        while (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            excluders.Add(BooleanExpression.Parse(context.MoveNextRequiredReturnSelf()));
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.Order })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ParseOrderByItems(context, orderBy);
        }
    }

    /// <summary>
    /// Reads one or more ORDER BY items (comma separated). Each item is an
    /// <see cref="Expression"/> followed by an optional <c>ASC</c>/<c>DESC</c>
    /// keyword (default ASC). A pure positive-integer literal is recorded as
    /// an ordinal reference into the projection rather than a constant
    /// expression; constant non-integer expressions silently sort by their
    /// constant (SQL Server's Msg 408 rejection isn't modeled).
    /// </summary>
    private static void ParseOrderByItems(ParserContext context, List<OrderBySpec> orderBy)
    {
        do
        {
            context.MoveNextRequired();
            var expr = Expression.Parse(context);

            var descending = false;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Asc })
            {
                context.MoveNextOptional();
            }
            else if (context.Token is ReservedKeyword { Keyword: Keyword.Desc })
            {
                descending = true;
                context.MoveNextOptional();
            }

            // A bare integer literal is the ordinal form (validated against the
            // projection count later in BuildSqlProjection). Anything else —
            // including a constant arithmetic expression like `1+0` — falls
            // through to per-row evaluation; SQL Server's Msg 408 rejection of
            // constant ORDER BY expressions isn't modeled.
            if (expr is Value valExpr
                && valExpr.Constant.Type == SqlType.Int32
                && !valExpr.Constant.IsNull)
            {
                orderBy.Add(OrderBySpec.FromOrdinal(valExpr.Constant.AsInt32, descending));
            }
            else
            {
                orderBy.Add(OrderBySpec.FromExpression(expr, descending));
            }
        }
        while (context.Token is Operator { Character: ',' });
    }

    /// <summary>
    /// Builds the result for a tableless SELECT (synthesized constant-row
    /// branch). The row is encoded into the page-row format and the bytes are
    /// handed to the result set; decoding happens lazily per column when the
    /// reader navigates it. Any <paramref name="excluders"/> are evaluated
    /// against the synthesized row; if any returns false the result is empty.
    /// <paramref name="topCount"/>, if set to zero, also suppresses the row.
    /// <paramref name="distinct"/> and <paramref name="orderBy"/> are accepted
    /// for syntactic completeness but are no-ops here — the result is at most
    /// one row, so dedup and sort have no effect.
    /// </summary>
    private static SimulatedSqlResultSet BuildSynthesizedSqlRow(List<Expression> expressions, List<BooleanExpression> excluders, List<OrderBySpec> orderBy, bool distinct, int? topCount)
    {
        _ = distinct;
        _ = orderBy;

        var values = new SqlValue[expressions.Count];
        var schema = new SqlType[expressions.Count];
        var columnNames = new string[expressions.Count];

        for (var i = 0; i < expressions.Count; i++)
        {
            values[i] = expressions[i].Run(column => throw SimulatedSqlException.InvalidColumnName(column));
            schema[i] = values[i].Type;
            columnNames[i] = expressions[i].Name;
        }

        if (topCount == 0)
            return new SimulatedSqlResultSet(schema, columnNames, []);

        foreach (var excluder in excluders)
        {
            if (excluder.Run(column => throw SimulatedSqlException.InvalidColumnName(column)) != true)
                return new SimulatedSqlResultSet(schema, columnNames, []);
        }

        return new SimulatedSqlResultSet(schema, columnNames, [RowEncoder.EncodeRow(schema, values)]);
    }

    /// <summary>
    /// Builds the result for a SELECT-FROM-source query (a heap table or a
    /// derived table). Each input row is decoded column-by-column on demand
    /// via <see cref="RowDecoder"/>; each projection expression
    /// is evaluated against that row through <see cref="Expression.Run"/>;
    /// the resulting values are re-encoded into a fresh output row.
    /// </summary>
    /// <remarks>
    /// Output schema is determined statically from the projection list using
    /// <see cref="Expression.GetSqlType"/>. Constants, references, and
    /// composite expressions all participate, as long as they have a static
    /// type-of resolution.
    /// </remarks>
    private static SimulatedSqlResultSet BuildSqlProjection(
        string[] sourceColumnNames,
        HeapColumn[] sourceSchema,
        HeapColumn[] storedSchema,
        int[]? storageOrdinals,
        Heap? lobStore,
        IEnumerable<byte[]> sourceRows,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        List<OrderBySpec> orderBy,
        bool distinct,
        int? topCount)
    {
        var outputSchema = new SqlType[expressions.Count];
        var outputColumnNames = new string[expressions.Count];

        int FindSourceColumn(List<string> name)
        {
            for (var j = 0; j < sourceColumnNames.Length; j++)
            {
                if (Collation.Default.Equals(sourceColumnNames[j], name[^1]))
                    return j;
            }
            return -1;
        }

        SqlType ResolveColumnType(List<string> name)
        {
            var idx = FindSourceColumn(name);
            return idx == -1 ? throw SimulatedSqlException.InvalidColumnName(name) : sourceSchema[idx].Type;
        }

        for (var i = 0; i < expressions.Count; i++)
        {
            outputSchema[i] = expressions[i].GetSqlType(ResolveColumnType);
            outputColumnNames[i] = expressions[i].Name;
        }

        // Validate ordinal ORDER BY items now that the projection count is
        // known. SQL Server fires Msg 108 at parse time, before any rows are
        // touched, so do the same.
        for (var i = 0; i < orderBy.Count; i++)
        {
            if (orderBy[i].IsOrdinal && (orderBy[i].Ordinal < 1 || orderBy[i].Ordinal > expressions.Count))
                throw SimulatedSqlException.OrderByPositionOutOfRange(orderBy[i].Ordinal);
        }

        // Msg 306: text/ntext/image can't appear in a sort or distinct slot.
        // ORDER BY: ordinal items index into the output schema; expression
        // items resolve through the same column type machinery the projection
        // already used. DISTINCT is row-level dedup, so any LOB output column
        // is fatal.
        if (distinct)
        {
            for (var i = 0; i < outputSchema.Length; i++)
            {
                if (outputSchema[i].IsLob)
                    throw SimulatedSqlException.LobTypesCannotBeComparedOrSorted();
            }
        }
        for (var i = 0; i < orderBy.Count; i++)
        {
            var keyType = orderBy[i].IsOrdinal
                ? outputSchema[orderBy[i].Ordinal - 1]
                : orderBy[i].Expr!.GetSqlType(ResolveColumnType);
            if (keyType.IsLob)
                throw SimulatedSqlException.LobTypesCannotBeComparedOrSorted();
        }

        return new SimulatedSqlResultSet(outputSchema, outputColumnNames, ProjectSqlRows(sourceSchema, storedSchema, storageOrdinals, lobStore, sourceRows, FindSourceColumn, expressions, excluders, outputSchema, outputColumnNames, orderBy, distinct, topCount));
    }

    private static IEnumerable<byte[]> ProjectSqlRows(
        HeapColumn[] sourceSchema,
        HeapColumn[] storedSchema,
        int[]? storageOrdinals,
        Heap? lobStore,
        IEnumerable<byte[]> sourceRows,
        Func<List<string>, int> findSourceColumn,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        SqlType[] outputSchema,
        string[] outputColumnNames,
        List<OrderBySpec> orderBy,
        bool distinct,
        int? topCount)
    {
        // Streaming path: no DISTINCT, no ORDER BY. TOP applies row-by-row.
        if (!distinct && orderBy.Count == 0)
        {
            var remaining = topCount;
            foreach (var rowBytes in sourceRows)
            {
                if (remaining == 0)
                    yield break;

                var bytes = rowBytes;

                SqlValue ResolveColumn(List<string> name)
                {
                    var columnIndex = findSourceColumn(name);
                    return columnIndex == -1
                        ? throw SimulatedSqlException.InvalidColumnName(name)
                        : DecodeOrCompute(sourceSchema, storedSchema, storageOrdinals, columnIndex, bytes, lobStore, ResolveColumn);
                }

                var include = true;
                foreach (var excluder in excluders)
                {
                    if (excluder.Run(ResolveColumn) != true)
                    {
                        include = false;
                        break;
                    }
                }
                if (!include)
                    continue;

                var projected = new SqlValue[expressions.Count];
                for (var i = 0; i < expressions.Count; i++)
                    projected[i] = expressions[i].Run(ResolveColumn);

                yield return RowEncoder.EncodeRow(outputSchema, projected);

                if (remaining is not null)
                    remaining--;
            }
            yield break;
        }

        // Buffered path: DISTINCT and/or ORDER BY require the full row set
        // before TOP can apply.
        var buffer = new List<(SqlValue[] Projected, SqlValue[] Keys)>();

        foreach (var rowBytes in sourceRows)
        {
            var bytes = rowBytes;

            SqlValue ResolveSource(List<string> name)
            {
                var columnIndex = findSourceColumn(name);
                return columnIndex == -1
                    ? throw SimulatedSqlException.InvalidColumnName(name)
                    : DecodeOrCompute(sourceSchema, storedSchema, storageOrdinals, columnIndex, bytes, lobStore, ResolveSource);
            }

            var include = true;
            foreach (var excluder in excluders)
            {
                if (excluder.Run(ResolveSource) != true)
                {
                    include = false;
                    break;
                }
            }
            if (!include)
                continue;

            var projected = new SqlValue[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
                projected[i] = expressions[i].Run(ResolveSource);

            var keys = orderBy.Count == 0 ? [] : ComputeOrderKeys(orderBy, projected, outputColumnNames, distinct, ResolveSource);
            buffer.Add((projected, keys));
        }

        IEnumerable<(SqlValue[] Projected, SqlValue[] Keys)> filtered = buffer;
        if (distinct)
        {
            var seen = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
            filtered = buffer.Where(item => seen.Add(item.Projected));
        }

        var materialized = filtered.ToList();

        if (orderBy.Count > 0)
            materialized.Sort((a, b) => CompareOrderKeys(a.Keys, b.Keys, orderBy));

        var taken = topCount is { } limit ? materialized.Take(limit) : materialized;
        foreach (var (projected, _) in taken)
            yield return RowEncoder.EncodeRow(outputSchema, projected);
    }

    /// <summary>
    /// Resolves a single column reference at <paramref name="columnIndex"/>
    /// in <paramref name="sourceSchema"/> for the row at <paramref name="bytes"/>.
    /// Stored columns (regular plus persisted-computed) decode directly via
    /// <see cref="RowDecoder.DecodeColumn(ReadOnlySpan{HeapColumn}, ReadOnlySpan{byte}, int, Heap?)"/>
    /// at their storage ordinal. Non-persisted computed columns evaluate
    /// their expression through <paramref name="resolveByName"/> — the
    /// recursive references inside the expression bind back through the same
    /// caller's resolver, but are guaranteed by Msg 1759 to land only on
    /// stored columns.
    /// </summary>
    private static SqlValue DecodeOrCompute(
        HeapColumn[] sourceSchema,
        HeapColumn[] storedSchema,
        int[]? storageOrdinals,
        int columnIndex,
        byte[] bytes,
        Heap? lobStore,
        Func<List<string>, SqlValue> resolveByName) =>
        storageOrdinals is null
            ? RowDecoder.DecodeColumn(storedSchema, bytes, columnIndex, lobStore)
            : sourceSchema[columnIndex].Computed is { } computedExpr && !sourceSchema[columnIndex].IsPersisted
                ? computedExpr.Run(resolveByName)
                : RowDecoder.DecodeColumn(storedSchema, bytes, storageOrdinals[columnIndex], lobStore);

    /// <summary>
    /// Evaluates each ORDER BY item against the current row. Ordinal items
    /// index directly into the projected row. Expression items resolve column
    /// references through an output-first resolver; without DISTINCT, names
    /// not in the output fall back to source columns (matching SQL Server's
    /// rule that ORDER BY can reference non-selected source columns). With
    /// DISTINCT, source fallback would be ambiguous post-dedup so a missing
    /// output match raises Msg 145.
    /// </summary>
    private static SqlValue[] ComputeOrderKeys(
        List<OrderBySpec> orderBy,
        SqlValue[] projected,
        string[] outputColumnNames,
        bool distinct,
        Func<List<string>, SqlValue> resolveSource)
    {
        var keys = new SqlValue[orderBy.Count];
        for (var i = 0; i < orderBy.Count; i++)
        {
            var spec = orderBy[i];
            if (spec.IsOrdinal)
            {
                keys[i] = projected[spec.Ordinal - 1];
                continue;
            }

            keys[i] = spec.Expr!.Run(name =>
            {
                var lastPart = name[^1];
                for (var j = 0; j < outputColumnNames.Length; j++)
                {
                    if (Collation.Default.Equals(outputColumnNames[j], lastPart))
                        return projected[j];
                }
                return distinct
                    ? throw SimulatedSqlException.OrderByItemNotInSelectListWithDistinct()
                    : resolveSource(name);
            });
        }
        return keys;
    }

    /// <summary>
    /// Lexicographic compare of two key tuples per the per-key descending
    /// flags. NULL is treated as the smallest value (NULL first under ASC,
    /// NULL last under DESC), matching SQL Server. Cross-type keys are
    /// promoted via <see cref="SqlType.Promote"/> before comparison.
    /// </summary>
    private static int CompareOrderKeys(SqlValue[] a, SqlValue[] b, List<OrderBySpec> orderBy)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var lk = a[i];
            var rk = b[i];
            int c;
            if (lk.IsNull && rk.IsNull)
            {
                c = 0;
            }
            else if (lk.IsNull)
            {
                c = -1;
            }
            else if (rk.IsNull)
            {
                c = 1;
            }
            else if (lk.Type == rk.Type)
            {
                c = lk.CompareTo(rk);
            }
            else
            {
                var common = SqlType.Promote(lk.Type, rk.Type);
                c = lk.CoerceTo(common).CompareTo(rk.CoerceTo(common));
            }

            if (orderBy[i].Descending)
                c = -c;
            if (c != 0)
                return c;
        }
        return 0;
    }
}

/// <summary>
/// One entry in an ORDER BY clause: either a positional ordinal (1-based
/// index into the projection) or an arbitrary expression, plus the direction
/// flag.
/// </summary>
internal readonly struct OrderBySpec
{
    public readonly Expression? Expr;
    public readonly int Ordinal;
    public readonly bool Descending;

    public bool IsOrdinal => this.Expr is null;

    private OrderBySpec(Expression? expr, int ordinal, bool descending)
    {
        this.Expr = expr;
        this.Ordinal = ordinal;
        this.Descending = descending;
    }

    public static OrderBySpec FromExpression(Expression expr, bool descending) => new(expr, 0, descending);
    public static OrderBySpec FromOrdinal(int ordinal, bool descending) => new(null, ordinal, descending);
}

/// <summary>
/// Equality comparer for projected rows (<see cref="SqlValue"/> tuples). Used
/// by DISTINCT to dedupe based on the same equality semantics as the
/// <c>=</c> operator: collation-aware string comparison, ANSI trailing-space
/// padding, two NULLs of the same type compare equal, and
/// <c>datetimeoffset</c> compares by UTC instant.
/// </summary>
internal sealed class RowEqualityComparer : IEqualityComparer<SqlValue[]>
{
    public static readonly RowEqualityComparer Instance = new();

    public bool Equals(SqlValue[]? x, SqlValue[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++)
            if (!x[i].Equals(y[i])) return false;
        return true;
    }

    public int GetHashCode(SqlValue[] obj)
    {
        var hash = new HashCode();
        foreach (var v in obj)
            hash.Add(v);
        return hash.ToHashCode();
    }
}
