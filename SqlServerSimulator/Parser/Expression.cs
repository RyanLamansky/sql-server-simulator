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
    /// Converts the tokens from a command into a single expression.
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
            case Numeric number:
                expression = new Value(number.Value);
                break;
            case AtPrefixedString atPrefixed:
                expression = new Value(atPrefixed, context);
                break;
            case DoubleAtPrefixedString doubleAtPrefixedString:
                expression = new Value(doubleAtPrefixedString);
                break;
            case ReservedKeyword { Keyword: Keyword.Null }:
                expression = new Value();
                break;
            case Name name:
                expression = new Reference(name);
                break;
            case Operator { Character: '+' }:
                context.MoveNextRequired();
                expression = Expression.Parse(context);
                break;
            case Operator { Character: '-' }:
                context.MoveNextRequired();
                expression = Parse(context);
                expression = new Subtract(new Value(new DataValue(0, DataType.BuiltInDbInt32)), expression);
                break;
            case Operator { Character: '(' }:
                context.MoveNextRequired();
                expression = new Parenthesized(Parse(context));
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        while (true)
        {
            switch (context.GetNextOptional())
            {
                case Operator { Character: '+' }:
                    context.MoveNextRequired();
                    expression = new Add(expression, Parse(context));
                    break;
                case Operator { Character: '-' }:
                    context.MoveNextRequired();
                    expression = new Subtract(expression, Parse(context));
                    break;
                case Operator { Character: '*' }:
                    context.MoveNextRequired();
                    expression = new Multiply(expression, Parse(context));
                    break;
                case Operator { Character: '/' }:
                    context.MoveNextRequired();
                    expression = new Divide(expression, Parse(context));
                    break;
                case Operator { Character: '%' }:
                    context.MoveNextRequired();
                    expression = new Modulus(expression, Parse(context));
                    break;
                case Operator { Character: '&' }:
                    context.MoveNextRequired();
                    expression = new BitwiseAnd(expression, Parse(context));
                    break;
                case Operator { Character: '|' }:
                    context.MoveNextRequired();
                    expression = new BitwiseOr(expression, Parse(context));
                    break;
                case Operator { Character: '^' }:
                    context.MoveNextRequired();
                    expression = new BitwiseExclusiveOr(expression, Parse(context));
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
                        {
                            expression = new Parenthesized(Parse(context));
                            break;
                        }

                        context.MoveNextRequired(); // Move past (
                        expression = ResolveBuiltIn(reference.Name, context);
                        context.MoveNextOptional(); // Move past )
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
    /// Runs the expression, returning its result.
    /// </summary>
    /// <param name="getColumnValue">Provides the value for a column.</param>
    /// <returns>The result of the expression.</returns>
    public abstract DataValue Run(Func<List<string>, DataValue> getColumnValue);

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
                _ => null
            },
            10 => uppercaseName switch
            {
                "DATALENGTH" => new DataLength(context),
                _ => null
            },
            _ => (Expression?)null
        } ?? throw SimulatedSqlException.UnrecognizedBuiltInFunction(name);
    }
}
