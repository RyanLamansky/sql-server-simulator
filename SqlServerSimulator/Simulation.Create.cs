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

        var heapColumns = new List<HeapColumn>();
        var fixedWidthSum = 0;
        var identityCount = 0;
        do
        {
            var columnName = context.GetNextRequired<Name>();
            var typeName = context.GetNextRequired<Name>();

            int? declaredMaxLength = null;
            int? declaredScale = null;
            context.MoveNextRequired();
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

            if (resolvedType.IsFixedLength)
                fixedWidthSum += resolvedType.FixedLength;

            heapColumns.Add(new HeapColumn(columnName.Value, resolvedType, maxLength, actualNullable, identity, defaultExpression));
        } while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            return false;

        // Schemas whose fixed-width columns alone exceed SQL Server's 8060-byte
        // in-row limit can never hold a row; reject at CREATE TABLE (Msg 1701).
        // The variable-width-aware warning path is deferred until warning
        // infrastructure exists.
        if (fixedWidthSum > Heap.MaxRowSize)
            throw SimulatedSqlException.RowSizeExceedsMaximum(tableName.Value, fixedWidthSum, Heap.MaxRowSize);

        var heapTable = new HeapTable(tableName.Value, [.. heapColumns]);
        return this.HeapTables.TryAdd(heapTable.Name, heapTable)
            ? true
            : throw SimulatedSqlException.ThereIsAlreadyAnObject(heapTable.Name);
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
