using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>CREATE TABLE</c>. Returns false if the leading <c>CREATE</c>
    /// isn't followed by <c>TABLE</c> (so the caller can route to the syntax
    /// error). Other malformed forms throw <see cref="SimulatedSqlException"/>
    /// directly with the matching SQL Server error.
    /// </summary>
    private bool TryParseCreate(ParserContext context)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Table })
            return false;

        if (context.GetNextRequired() is not Name tableName)
            return false;

        if (context.GetNextRequired() is not Operator { Character: '(' })
            return false;

        // Two-pass column resolution: regular columns build a HeapColumn during
        // pass 1; computed columns leave a placeholder entry plus an entry in
        // pendingComputed to be resolved after the column list is closed (so
        // forward column references inside computed expressions can bind).
        var heapColumns = new List<HeapColumn?>();
        var pendingComputed = new List<(int Index, string Name, Expression Expression, bool Persisted, bool Nullable)>();
        var identityCount = 0;
        do
        {
            var columnName = context.GetNextRequired<Name>();
            context.MoveNextRequired();

            if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            {
                context.MoveNextRequired();
                var computed = Expression.Parse(context);
                var (persisted, computedNullable) = ParseComputedSuffix(context);
                pendingComputed.Add((heapColumns.Count, columnName.Value, computed, persisted, computedNullable));
                heapColumns.Add(null);
                continue;
            }

            if (context.Token is not Name typeName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();

            int? declaredMaxLength = null;
            int? declaredScale = null;
            if (context.Token is Operator { Character: '(' })
            {
                var lengthToken = context.GetNextRequired();
                declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                    ? numericValue.AsInt32
                    : context.MatchContextual(ContextualKeyword.Max)
                        ? SqlType.MaxLengthSentinel
                        : throw SimulatedSqlException.SyntaxErrorNear(context);

                switch (context.GetNextRequired())
                {
                    case Operator { Character: ',' }:
                        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        declaredScale = scaleValue.AsInt32;
                        if (context.GetNextRequired() is not Operator { Character: ')' })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        break;
                    case Operator { Character: ')' }:
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }

                context.MoveNextRequired();
            }

            // Loop over the column-constraint clauses (IDENTITY, NULL/NOT NULL,
            // DEFAULT) in any order. Each branch leaves Token at the first
            // un-consumed token; the loop exits when that token isn't a
            // recognized constraint keyword (typically the comma separating
            // columns or the column-list's closing paren).
            IdentityState? identity = null;
            bool? nullable = null;
            Expression? defaultExpression = null;
            while (true)
            {
                switch (context.Token)
                {
                    case ReservedKeyword { Keyword: Keyword.Identity } when identity is null:
                        identity = ParseIdentitySpec(context, columnName.Value);
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Not } when !nullable.HasValue:
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        nullable = false;
                        context.MoveNextRequired();
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Null } when !nullable.HasValue:
                        nullable = true;
                        context.MoveNextRequired();
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Default } when defaultExpression is null:
                        context.MoveNextRequired();
                        context.InDefaultClause = true;
                        try { defaultExpression = Expression.Parse(context); }
                        finally { context.InDefaultClause = false; }
                        continue;
                }
                break;
            }

            var actualNullable = nullable ?? (identity is null);
            var (resolvedType, maxLength) = SqlType.GetByName(typeName, declaredMaxLength, declaredScale, heapColumns.Count + 1, columnName.Value);

            if (identity is not null)
            {
                if (++identityCount > 1)
                    throw SimulatedSqlException.MultipleIdentityColumns(tableName.Value);
                if (actualNullable)
                    throw SimulatedSqlException.IdentityOnNullableColumn(columnName.Value, tableName.Value);
                if (resolvedType != SqlType.Int32 && resolvedType != SqlType.BigInt && resolvedType != SqlType.SmallInt && resolvedType != SqlType.TinyInt)
                    throw SimulatedSqlException.IdentityInvalidType(columnName.Value);
            }

            heapColumns.Add(new HeapColumn(columnName.Value, resolvedType, maxLength, actualNullable, identity, defaultExpression));
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            return false;

        // Pass 2: resolve computed columns now that every column's name has
        // been seen. The resolver throws Msg 1759 for any reference to another
        // computed column (including persisted) and Msg 207 for an unknown
        // name; valid references resolve to the source column's SqlType so
        // <see cref="Expression.GetSqlType"/> can infer the computed column's
        // own type.
        SqlType ResolveComputedReference(List<string> reference)
        {
            var leaf = reference[^1];
            for (var i = 0; i < heapColumns.Count; i++)
            {
                if (heapColumns[i] is { } existing && Collation.Default.Equals(existing.Name, leaf))
                {
                    return existing.Computed is not null
                        ? throw SimulatedSqlException.ComputedColumnReferencedInComputed(existing.Name, tableName.Value)
                        : existing.Type;
                }
                if (heapColumns[i] is null)
                {
                    foreach (var pending in pendingComputed)
                    {
                        if (pending.Index == i && Collation.Default.Equals(pending.Name, leaf))
                            throw SimulatedSqlException.ComputedColumnReferencedInComputed(pending.Name, tableName.Value);
                    }
                }
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        foreach (var pending in pendingComputed)
        {
            var resolvedType = pending.Expression.GetSqlType(ResolveComputedReference);
            heapColumns[pending.Index] = new HeapColumn(
                pending.Name,
                resolvedType,
                maxLength: null,
                nullable: pending.Nullable,
                computedExpression: pending.Expression,
                isPersisted: pending.Persisted);
        }

        // Schemas whose fixed-width stored columns alone exceed SQL Server's
        // 8060-byte in-row limit can never hold a row; reject at CREATE TABLE
        // (Msg 1701). Persisted computed columns of fixed-length type
        // contribute; non-persisted computed columns have no row storage.
        var fixedWidthSum = 0;
        for (var i = 0; i < heapColumns.Count; i++)
        {
            var column = heapColumns[i]!;
            if (column.IsStored && column.Type.IsFixedLength)
                fixedWidthSum += column.Type.FixedLength;
        }
        if (fixedWidthSum > Heap.MaxRowSize)
            throw SimulatedSqlException.RowSizeExceedsMaximum(tableName.Value, fixedWidthSum, Heap.MaxRowSize);

        var heapTable = new HeapTable(tableName.Value, [.. heapColumns!]);
        return this.HeapTables.TryAdd(heapTable.Name, heapTable)
            ? true
            : throw SimulatedSqlException.ThereIsAlreadyAnObject(heapTable.Name);
    }

    /// <summary>
    /// Parses the optional suffix of a computed-column declaration (after the
    /// expression): bare empty, <c>PERSISTED</c>, or <c>PERSISTED NOT NULL</c>.
    /// Any other constraint keyword in this position (<c>IDENTITY</c>,
    /// <c>DEFAULT</c>, bare <c>NULL</c>/<c>NOT NULL</c>, or <c>PERSISTED NULL</c>)
    /// raises Msg 8183 — real SQL Server's blanket "computed columns must be
    /// persisted to carry a NULL/NOT NULL/CHECK/FK constraint" error.
    /// </summary>
    private static (bool Persisted, bool Nullable) ParseComputedSuffix(ParserContext context)
    {
        var persisted = false;
        bool? nullable = null;
        while (true)
        {
            if (!persisted && context.MatchContextual(ContextualKeyword.Persisted))
            {
                persisted = true;
                context.MoveNextRequired();
                continue;
            }
            if (persisted && !nullable.HasValue && context.Token is ReservedKeyword { Keyword: Keyword.Not })
            {
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                nullable = false;
                context.MoveNextRequired();
                continue;
            }
            if (context.Token is ReservedKeyword { Keyword: Keyword.Identity or Keyword.Default or Keyword.Not or Keyword.Null })
                throw SimulatedSqlException.ComputedColumnConstraintRequiresPersisted();
            break;
        }
        return (persisted, nullable ?? true);
    }

    /// <summary>
    /// Parses the <c>IDENTITY [(seed, increment)]</c> property after a column's
    /// data type. Enters with <see cref="ParserContext.Token"/> on the
    /// <c>IDENTITY</c> keyword and leaves it on the next non-identity token
    /// (a nullability keyword, comma, or the column-list's closing paren).
    /// Bare <c>IDENTITY</c> is shorthand for <c>IDENTITY(1, 1)</c>.
    /// </summary>
    private static IdentityState ParseIdentitySpec(ParserContext context, string columnName)
    {
        long seed = 1;
        long increment = 1;
        var afterIdentity = context.GetNextRequired();
        if (afterIdentity is Operator { Character: '(' })
        {
            context.MoveNextRequired();
            seed = EvaluateLiteralBigInt(Expression.Parse(context));
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            increment = EvaluateLiteralBigInt(Expression.Parse(context));
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        return increment == 0
            ? throw SimulatedSqlException.IdentityInvalidIncrement(columnName)
            : new IdentityState(seed, increment);
    }

    private static long EvaluateLiteralBigInt(Expression expression) =>
        expression.Run(name => throw SimulatedSqlException.InvalidColumnName(name)).CoerceTo(SqlType.BigInt).AsInt64;
}
