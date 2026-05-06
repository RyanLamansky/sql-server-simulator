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

        var rawColumns = new List<(Name Name, Name TypeName, int? DeclaredMaxLength, int? DeclaredScale, bool Nullable, IdentityState? Identity)>();
        bool suppressAdvanceToken;
        do
        {
            suppressAdvanceToken = false;
            var columnName = context.GetNextRequired<Name>();
            var type = context.GetNextRequired<Name>();

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

            IdentityState? identity = null;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Identity })
            {
                identity = ParseIdentitySpec(context, columnName.Value);
            }

            bool nullable;
            if (context.Token is ReservedKeyword next)
            {
                switch (next.Keyword)
                {
                    case Keyword.Not:
                        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Null })
                            throw SimulatedSqlException.SyntaxErrorNear(context);

                        nullable = false;
                        break;
                    case Keyword.Null:
                        nullable = true;
                        break;
                    default:
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
            }
            else
            {
                suppressAdvanceToken = true;
                nullable = identity is null;
            }

            rawColumns.Add((columnName, type, declaredMaxLength, declaredScale, nullable, identity));
        } while ((suppressAdvanceToken ? context.Token : context.GetNextRequired()) is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            return false;

        var heapColumns = new HeapColumn[rawColumns.Count];
        var fixedWidthSum = 0;
        var identityCount = 0;
        for (var i = 0; i < rawColumns.Count; i++)
        {
            var raw = rawColumns[i];
            var (resolvedType, maxLength) = SqlType.GetByName(raw.TypeName, raw.DeclaredMaxLength, raw.DeclaredScale, i + 1, raw.Name.Value);
            if (raw.Identity is not null)
            {
                if (++identityCount > 1)
                    throw SimulatedSqlException.MultipleIdentityColumns(tableName.Value);
                if (raw.Nullable)
                    throw SimulatedSqlException.IdentityOnNullableColumn(raw.Name.Value, tableName.Value);
                if (resolvedType != SqlType.Int32 && resolvedType != SqlType.BigInt && resolvedType != SqlType.SmallInt && resolvedType != SqlType.TinyInt)
                    throw SimulatedSqlException.IdentityInvalidType(raw.Name.Value);
            }
            heapColumns[i] = new(raw.Name.Value, resolvedType, maxLength, raw.Nullable, raw.Identity);
            if (resolvedType.IsFixedLength)
                fixedWidthSum += resolvedType.FixedLength;
        }

        // Schemas whose fixed-width columns alone exceed SQL Server's 8060-byte
        // in-row limit can never hold a row; reject at CREATE TABLE (Msg 1701).
        // The variable-width-aware warning path is deferred until warning
        // infrastructure exists.
        if (fixedWidthSum > Heap.MaxRowSize)
            throw SimulatedSqlException.RowSizeExceedsMaximum(tableName.Value, fixedWidthSum, Heap.MaxRowSize);

        var heapTable = new HeapTable(tableName.Value, heapColumns);
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
