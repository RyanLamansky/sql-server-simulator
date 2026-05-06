using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>MERGE</c>, narrowly scoped to the shape EF Core emits for a
    /// multi-row batch insert: <c>USING (VALUES ...) AS alias (cols) ON
    /// predicate WHEN NOT MATCHED THEN INSERT (cols) VALUES (alias.col, ...)
    /// [OUTPUT ...]</c>. The <c>ON</c> predicate is evaluated per source row
    /// against an alias-only resolver — column references into the target
    /// table aren't supported, since modeling that requires a JOIN-style scan.
    /// EF's shape always emits <c>ON 1=0</c>, which cleanly degenerates to
    /// "insert every source row." A <c>WHEN MATCHED</c> branch parses
    /// syntactically (so the grammar shape isn't a surprise) but throws if
    /// the per-row predicate ever evaluates to true.
    /// </summary>
    private static SimulatedStatementOutcome ParseMerge(ParserContext context)
    {
        // Optional INTO: real SQL Server accepts both, EF drops it. Either form lands on the table name.
        var afterMerge = context.GetNextRequired();
        if (afterMerge is ReservedKeyword { Keyword: Keyword.Into })
            context.MoveNextRequired();

        var destinationTableToken = context.Token as StringToken
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        var destinationTable = context.Simulation.HeapTables.TryGetValue(destinationTableToken.Value, out var table)
            ? table
            : throw SimulatedSqlException.InvalidObjectName(destinationTableToken);

        context.MoveNextRequired();
        if (!context.MatchContextual(ContextualKeyword.Using))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Values })
            throw new NotSupportedException("MERGE source must be a VALUES clause; subqueries aren't supported.");

        var sourceTuples = ParseValuesTuples(context);

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Optional AS (EF emits it).
        var afterUsingClose = context.GetNextRequired();
        if (afterUsingClose is ReservedKeyword { Keyword: Keyword.As })
            afterUsingClose = context.GetNextRequired();

        var sourceAlias = (afterUsingClose as Name)?.Value
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var sourceColumnNames = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not Name srcCol)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            sourceColumnNames.Add(srcCol.Value);
            var sep = context.GetNextRequired();
            if (sep is Operator { Character: ')' })
                break;
            if (sep is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        if (sourceColumnNames.Count != sourceTuples[0].Length)
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var onPredicate = BooleanExpression.Parse(Expression.Parse(context), context);

        // Compute source schema by static type-of'ing the first tuple's
        // expressions. Source tuples can't reference any columns yet (they're
        // literals or parameters in EF's emit), so the resolver throws.
        var sourceSchema = new SqlType[sourceColumnNames.Count];
        for (var i = 0; i < sourceColumnNames.Count; i++)
            sourceSchema[i] = sourceTuples[0][i].GetSqlType(name => throw SimulatedSqlException.InvalidColumnName(name));

        // WHEN clauses. EF only emits a single WHEN NOT MATCHED THEN INSERT;
        // anything else (WHEN MATCHED branches with UPDATE/DELETE) parses
        // syntactically but throws if the predicate ever picks that branch.
        Expression[]? insertColumnExprs = null;
        Expression[]? insertValueExprs = null;
        var whenMatchedSeen = false;
        while (context.Token is ReservedKeyword { Keyword: Keyword.When })
        {
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Not })
            {
                context.MoveNextRequired();
                if (!context.MatchContextual(ContextualKeyword.Matched))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Then })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Insert })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                // Optional column list.
                List<Expression> insertColumns = [];
                if (context.GetNextRequired() is Operator { Character: '(' })
                {
                    while (true)
                    {
                        context.MoveNextRequired();
                        var colExpr = Expression.Parse(context);
                        insertColumns.Add(colExpr);
                        if (context.Token is Operator { Character: ')' })
                            break;
                        if (context.Token is not Operator { Character: ',' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    context.MoveNextRequired();
                }

                if (context.Token is not ReservedKeyword { Keyword: Keyword.Values })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                if (context.GetNextRequired() is not Operator { Character: '(' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                List<Expression> insertValues = [];
                while (true)
                {
                    context.MoveNextRequired();
                    insertValues.Add(Expression.Parse(context));
                    if (context.Token is Operator { Character: ')' })
                        break;
                    if (context.Token is not Operator { Character: ',' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }

                insertColumnExprs = [.. insertColumns];
                insertValueExprs = [.. insertValues];
                context.MoveNextOptional();
            }
            else
            {
                // WHEN MATCHED — parse and discard until next clause boundary.
                whenMatchedSeen = true;
                while (context.Token is not (null
                    or ReservedKeyword { Keyword: Keyword.When }
                    or Operator { Character: ';' }
                    or UnquotedString))
                {
                    context.MoveNextOptional();
                }
                if (context.Token is UnquotedString && !context.MatchContextual(ContextualKeyword.Output))
                    context.MoveNextOptional();
            }
        }

        if (insertColumnExprs is null || insertValueExprs is null)
            throw new NotSupportedException("MERGE without a WHEN NOT MATCHED THEN INSERT branch isn't supported.");

        // Resolve insert columns against destination schema.
        var insertColumns2 = new HeapColumn[insertColumnExprs.Length];
        for (var i = 0; i < insertColumnExprs.Length; i++)
        {
            var colName = (insertColumnExprs[i] as Reference)?.Name
                ?? throw SimulatedSqlException.SyntaxErrorNear(context);
            insertColumns2[i] = destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, colName))
                ?? throw SimulatedSqlException.InvalidColumnName(colName);
        }

        var output = TryParseOutputClause(context, destinationTable, (sourceAlias, [.. sourceColumnNames], sourceSchema));

        // Identity wiring (mirrors ProcessHeapInsert).
        var identityOrdinal = destinationTable.IdentityOrdinal;
        var identityColumn = identityOrdinal >= 0 ? destinationTable.Columns[identityOrdinal] : null;
        var identityInsertOn = identityColumn is not null
            && context.Simulation.IdentityInsertTable is string activeTable
            && Collation.Default.Equals(activeTable, destinationTable.Name);
        if (identityColumn is not null)
        {
            var identityListed = insertColumns2.Any(c => ReferenceEquals(c, identityColumn));
            if (identityListed && !identityInsertOn)
                throw SimulatedSqlException.CannotInsertExplicitIdentity(destinationTable.Name);
            if (!identityListed && identityInsertOn)
                throw SimulatedSqlException.ExplicitIdentityRequired(destinationTable.Name);
        }

        var outputRows = output is null ? null : new List<byte[]>(sourceTuples.Count);
        decimal? lastIdentityValue = null;
        var insertedCount = 0;
        foreach (var sourceTuple in sourceTuples)
        {
            // Evaluate the source tuple to concrete values.
            var sourceRowValues = new SqlValue[sourceColumnNames.Count];
            for (var i = 0; i < sourceTuple.Length; i++)
                sourceRowValues[i] = sourceTuple[i].Run(name => throw SimulatedSqlException.InvalidColumnName(name));

            // Resolver for the ON predicate and the INSERT value expressions:
            // matches references to the source alias and falls back to error
            // for anything else. Targeting the destination table is rejected
            // (see method-level remarks).
            SqlValue ResolveSource(List<string> name)
            {
                if (name.Count >= 2 && Collation.Default.Equals(name[0], sourceAlias))
                {
                    var lastPart = name[^1];
                    for (var i = 0; i < sourceColumnNames.Count; i++)
                    {
                        if (Collation.Default.Equals(sourceColumnNames[i], lastPart))
                            return sourceRowValues[i];
                    }
                }
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(string.Join('.', name));
            }

            if (onPredicate.Run(_ => SqlValue.Null(SqlType.Int32)))
            {
                // Predicate matched — would route to WHEN MATCHED.
                if (whenMatchedSeen)
                    throw new NotSupportedException("MERGE's WHEN MATCHED branch isn't supported.");
                continue;
            }

            // WHEN NOT MATCHED: insert one row.
            var rowValues = new SqlValue[destinationTable.Columns.Length];
            for (var i = 0; i < rowValues.Length; i++)
                rowValues[i] = SqlValue.Null(destinationTable.Schema[i]);

            for (var i = 0; i < insertColumns2.Length; i++)
            {
                var targetColumn = insertColumns2[i];
                var ordinal = -1;
                for (var j = 0; j < destinationTable.Columns.Length; j++)
                {
                    if (ReferenceEquals(destinationTable.Columns[j], targetColumn))
                    {
                        ordinal = j;
                        break;
                    }
                }

                var source = insertValueExprs[i].Run(ResolveSource);
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

            if (identityColumn is not null && !insertColumns2.Any(c => ReferenceEquals(c, identityColumn)))
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

            destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.Columns, rowValues, destinationTable.Heap));
            insertedCount++;

            if (output is { } o)
                outputRows!.Add(o.ProjectRow(rowValues, sourceRowValues));
        }

        context.Simulation.LastIdentity = lastIdentityValue;

        return output is { } o2
            ? new SimulatedSqlResultSet(o2.Schema, o2.ColumnNames, outputRows!)
            : new SimulatedNonQuery(insertedCount);
    }

    /// <summary>
    /// Parses one or more comma-separated <c>(...)</c> tuples following a
    /// <c>VALUES</c> keyword. Enters with <see cref="ParserContext.Token"/>
    /// on <c>VALUES</c>; on return <see cref="ParserContext.Token"/> sits on
    /// the first token after the last tuple's closing paren — typically a
    /// surrounding <c>)</c> for MERGE or a clause keyword for INSERT.
    /// </summary>
    private static List<Expression[]> ParseValuesTuples(ParserContext context)
    {
        var tuples = new List<Expression[]>();
        do
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var values = new List<Expression>();
            while (true)
            {
                context.MoveNextRequired();
                if (context.Token is Operator { Character: ',' or ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                values.Add(Expression.Parse(context));
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            tuples.Add([.. values]);
        }
        while (context.GetNextOptional() is Operator { Character: ',' });

        return tuples;
    }
}
