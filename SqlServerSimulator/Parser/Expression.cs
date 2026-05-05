using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Contains the logic described by a SQL command and computes its results.
/// </summary>
internal abstract class Expression
{
    private protected Expression()
    {
    }

    /// <summary>
    /// A name or alias associated with an expression.
    /// Anonymous expressions return <see cref="string.Empty"/>.
    /// </summary>
    public virtual string Name => string.Empty;

    /// <summary>
    /// The relative precedence of an expression.
    /// When two are in scope, the higher one runs first, otherwise they run left-to-right.
    /// </summary>
    /// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/language-elements/operator-precedence-transact-sql</remarks>
    public virtual byte Precedence => 0;

    /// <summary>
    /// Converts the tokens from a command into a single expression. Follows
    /// the lookahead contract documented on <see cref="ParserContext"/>: on
    /// return, <see cref="ParserContext.Token"/> is the first token not
    /// consumed by the parsed expression.
    /// </summary>
    /// <param name="context">Manages the overall parsing state.</param>
    /// <returns>The parsed expression.</returns>
    /// <exception cref="SimulatedSqlException">A variety of messages are possible for various problems with the command.</exception>
    /// <exception cref="NotSupportedException">A condition was encountered that may be valid but can't currently be parsed.</exception>
    public static Expression Parse(ParserContext context)
    {
        Expression expression;
        switch (context.Token)
        {
            // Unary +/- delegate to a recursive Parse on the next token. That
            // recursive call already runs the binary-operator lookahead loop
            // and leaves context.Token at the first token NOT consumed by the
            // operand. Returning here (instead of falling into the outer
            // while loop) avoids a second GetNextOptional that would silently
            // swallow the surrounding context's terminator (e.g. the `as` in
            // CAST, the `from` in SELECT). AdjustForPrecedence on the outer
            // unary `Subtract` still correctly rebalances `-1 + 2`-style
            // continuations because the right side carries its operator tree.
            case Operator { Character: '+' }:
                return Expression.Parse(context.MoveNextRequiredReturnSelf());
            case Operator { Character: '-' }:
                return new Subtract(new Value(Storage.SqlValue.FromInt32(0)), context).AdjustForPrecedence();
        }

        expression = context.Token switch
        {
            Numeric number => new Value(number.Value),
            Literal literal => new Value(literal.Value),
            AtPrefixedString atPrefixed => new Value(atPrefixed, context),
            DoubleAtPrefixedString doubleAtPrefixedString => doubleAtPrefixedString.Parse() == AtAtKeyword.Identity
                ? new LastIdentityExpression(context.Simulation)
                : new Value(doubleAtPrefixedString),
            ReservedKeyword { Keyword: Keyword.Null } => new Value(),
            // LEFT and RIGHT are reserved (for future JOIN support) but
            // dispatch as function calls when followed by '('.
            ReservedKeyword { Keyword: Keyword.Left or Keyword.Right } reserved => new Reference(reserved.ToString()),
            Name name => new Reference(name),
            Operator { Character: '(' } => new Parenthesized(context),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context)
        };

        while (true)
        {
            switch (context.GetNextOptional())
            {
                case Operator { Character: '+' }:
                    expression = new Add(expression, context);
                    break;
                case Operator { Character: '-' }:
                    expression = new Subtract(expression, context);
                    break;
                case Operator { Character: '*' }:
                    expression = new Multiply(expression, context);
                    break;
                case Operator { Character: '/' }:
                    expression = new Divide(expression, context);
                    break;
                case Operator { Character: '%' }:
                    expression = new Modulus(expression, context);
                    break;
                case Operator { Character: '&' }:
                    expression = new BitwiseAnd(expression, context);
                    break;
                case Operator { Character: '|' }:
                    expression = new BitwiseOr(expression, context);
                    break;
                case Operator { Character: '^' }:
                    expression = new BitwiseExclusiveOr(expression, context);
                    break;

                case Operator { Character: '.' }:
                    {
                        if (expression is not Reference reference)
                            throw SimulatedSqlException.SyntaxErrorNear(context);

                        reference.AddMultiPartComponent(context.GetNextRequired<Name>());
                    }
                    continue;
                case Operator { Character: ')' }:
                    break;
                case Operator { Character: '(' }:
                    {
                        if (expression is not Reference reference)
                            break;

                        context.MoveNextRequired(); // Move past (
                        expression = ResolveBuiltIn(reference.Name, context);
                        // ResolveBuiltIn leaves context.Token at the closing ).
                        // The next loop iteration's GetNextOptional advances
                        // past it; advancing here would skip an extra token.
                        continue;
                    }
            }

            return expression is TwoSidedExpression twoSided ? twoSided.AdjustForPrecedence() : expression;
        }
    }

    /// <summary>
    /// Wraps the provided <see cref="Expression"/> in a <see cref="NamedExpression"/> with the provided <paramref name="name"/>.
    /// </summary>
    /// <param name="expression">The expression to wrap.</param>
    /// <param name="name">The name to assign.</param>
    /// <returns>The named expression.</returns>
    public static Expression AssignName(Expression expression, Name name) => new NamedExpression(expression, name.Value);

    /// <summary>
    /// Evaluates the expression against a row's column values and returns its result.
    /// </summary>
    /// <param name="getColumnValue">Provides the value for a column.</param>
    public abstract Storage.SqlValue Run(Func<List<string>, Storage.SqlValue> getColumnValue);

    /// <summary>
    /// Static type-of resolver for projection planning: returns the
    /// <see cref="Storage.SqlType"/> this expression will produce, given a
    /// resolver that maps column-name parts to their declared types. Lets a
    /// SELECT plan its output schema before any rows are read.
    /// </summary>
    /// <param name="resolveColumnType">Callback that, given a multi-part column name, returns its declared type or throws if unresolvable.</param>
    public abstract Storage.SqlType GetSqlType(Func<List<string>, Storage.SqlType> resolveColumnType);

#if DEBUG
    public abstract override string ToString();
#endif

    private static Expression ResolveBuiltIn(string name, ParserContext context)
    {
        Span<char> uppercaseName = stackalloc char[name.Length];
        return name.ToUpperInvariant(uppercaseName) switch
        {
            3 => uppercaseName switch
            {
                "ABS" => new AbsoluteValue(context),
                "LEN" => new Length(context),
                _ => null
            },
            4 => uppercaseName switch
            {
                "CAST" => new Cast(context),
                "LEFT" => new Left(context),
                "TRIM" => new Trim(context),
                _ => null
            },
            5 => uppercaseName switch
            {
                "LOWER" => new Lower(context),
                "LTRIM" => new LeftTrim(context),
                "NEWID" => new NewId(context),
                "RIGHT" => new Right(context),
                "RTRIM" => new RightTrim(context),
                "UPPER" => new Upper(context),
                _ => null
            },
            7 => uppercaseName switch
            {
                "REPLACE" => new Replace(context),
                "REVERSE" => new Reverse(context),
                _ => null
            },
            9 => uppercaseName switch
            {
                "CHARINDEX" => new CharIndex(context),
                "SUBSTRING" => new Substring(context),
                _ => null
            },
            10 => uppercaseName switch
            {
                "DATALENGTH" => new DataLength(context),
                _ => null
            },
            13 => uppercaseName switch
            {
                "IDENT_CURRENT" => new IdentCurrent(context),
                _ => null
            },
            14 => uppercaseName switch
            {
                "SCOPE_IDENTITY" => new LastIdentityExpression(context),
                _ => null
            },
            _ => (Expression?)null
        } ?? throw SimulatedSqlException.UnrecognizedBuiltInFunction(name);
    }
}
