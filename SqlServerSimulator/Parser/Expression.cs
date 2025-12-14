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
    /// Converts the tokens from a command into a single expression.
    /// </summary>
    /// <param name="context">Manages the overall parsing state.</param>
    /// <returns>The parsed expression.</returns>
    /// <exception cref="SimulatedSqlException">A variety of messages are possible for various problems with the command.</exception>
    /// <exception cref="NotSupportedException">A condition was encountered that may be valid but can't currently be parsed.</exception>
    public static Expression Parse(ParserContext context)
    {
        Expression? expression = null;
        bool tokenWasRead;

        do
        {
            tokenWasRead = false;

            switch (context.Token)
            {
                case Numeric number:
                    expression = new Value(number);
                    break;
                case AtPrefixedString atPrefixed:
                    expression = new Value(atPrefixed, context);
                    break;
                case DoubleAtPrefixedString doubleAtPrefixedString:
                    expression = new Value(doubleAtPrefixedString);
                    break;
                case ReservedKeyword reservedKeyword:
                    switch (reservedKeyword.Keyword)
                    {
                        case Keyword.As:
                            if (expression is null || context.GetNextOptional() is not Name alias)
                                throw SimulatedSqlException.SyntaxErrorNearKeyword(reservedKeyword);

                            expression = new NamedExpression(expression, alias.Value);
                            context.MoveNextOptional();
                            return expression;
                        case Keyword.From:
                            if (expression is null)
                                throw SimulatedSqlException.SyntaxErrorNearKeyword(reservedKeyword);

                            return expression;
                        case Keyword.Null:
                            expression = new Value();
                            continue;
                    }

                    throw SimulatedSqlException.SyntaxErrorNearKeyword(reservedKeyword);
                case Name name:
                    expression = new Reference(name);
                    break;
                case Operator { Character: '+' }:
                    if (expression is null)
                    {
                        context.MoveNextRequired();
                        expression = Expression.Parse(context);
                        break;
                    }

                    context.MoveNextRequired();

                    {
                        var parsed = Parse(context);
                        expression = new Add(expression, parsed);
                        if (parsed is NamedExpression named)
                            expression = named.TransferName(expression);
                    }

                    tokenWasRead = true;
                    break;
                case Operator { Character: '-' }:
                    if (expression is null)
                    {
                        context.MoveNextRequired();
                        expression = Parse(context);
                        expression = new Subtract(new Value(0), expression);
                        break;
                    }

                    context.MoveNextRequired();

                    {
                        var parsed = Parse(context);
                        expression = new Subtract(expression, parsed);
                        if (parsed is NamedExpression named)
                            expression = named.TransferName(expression);
                    }

                    tokenWasRead = true;
                    break;

                case Operator { Character: '.' }:
                    if (expression is null)
                        throw new NotSupportedException("Simulated expression parser doesn't know how to handle '.' at the start of an expression.");

                    {
                        if (expression is not Reference reference)
                            throw new NotSupportedException("Simulated expression parser doesn't know how to handle '.' here.");

                        reference.AddMultiPartComponent(context.GetNextRequired<Name>());
                    }
                    break;
                case Operator { Character: ',' }:
                case Operator { Character: ')' }:
                    if (expression is null)
                        throw SimulatedSqlException.SyntaxErrorNear(context.Token);
                    return expression;
                case Operator { Character: '(' }:
                    {
                        if (expression is not Reference reference)
                            throw SimulatedSqlException.SyntaxErrorNear(context.Token);
                        context.MoveNextRequired(); // Move past (
                        expression = ResolveBuiltIn(reference.Name, context);
                        context.MoveNextOptional(); // Move past )
                        return expression;
                    }
                default:
                    throw new NotSupportedException($"Simulated expression parser doesn't know how to handle '{context.Token}'.");
            }
        } while ((tokenWasRead && context.Token is not null) || context.GetNextOptional() is not null);

        return expression;
    }

    /// <summary>
    /// Runs the expression, returning its result.
    /// </summary>
    /// <param name="getColumnValue">Provides the value for a column.</param>
    /// <returns>The result of the expression.</returns>
    public abstract object? Run(Func<List<string>, object?> getColumnValue);

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

    /// <summary>
    /// An expression that has been given a name, such as with `as`.
    /// </summary>
    /// <param name="expression">The expression to be named.</param>
    /// <param name="name">The name of the expression, exposed via the <see cref="Name"/> property.</param>
    private sealed class NamedExpression(Expression expression, string name) : Expression
    {
        private readonly Expression expression = expression;
        private readonly string name = name;
#if DEBUG
        private bool transferred;
#endif

        public override string Name => this.name;

        public override object? Run(Func<List<string>, object?> getColumnValue) => this.expression.Run(getColumnValue);

        /// <summary>
        /// Transfers the name to an outer expression.
        /// </summary>
        /// <param name="destination">The expression wrapping this<see cref="NamedExpression"/>.</param>
        /// <returns>A new <see cref="NamedExpression"/> wrapping <paramref name="destination"/> using <see cref="Name"/>.</returns>
        public NamedExpression TransferName(Expression destination)
        {
#if DEBUG
            transferred = true;
#endif

            return new(destination, this.name);
        }

#if DEBUG
        public override string ToString() => transferred ? expression.ToString() : $"{expression} {name}";
#endif
    }

    /// <summary>
    /// Values are resolved at parse time.
    /// </summary>
    private sealed class Value : Expression
    {
        private readonly object? value;

        public Value()
        {
        }

        public Value(object? value) => this.value = value;

        public Value(Numeric value)
            : this(value.Value)
        {
        }

        public Value(AtPrefixedString atPrefixed, ParserContext context)
        {
            this.value = context.GetVariableValue(atPrefixed.Value);
        }

        public Value(DoubleAtPrefixedString doubleAtPrefixedString)
        {
            switch (doubleAtPrefixedString.Parse())
            {
                case AtAtKeyword.Version:
                    this.value = "SQL Server Simulator";
                    return;
            }

            throw new NotSupportedException($"Simulator doesn't recognize {doubleAtPrefixedString}.");
        }

        public override object? Run(Func<List<string>, object?> getColumnValue) => value;

#if DEBUG
        public override string ToString() => value?.ToString() ?? "null";
#endif
    }

    public sealed class Add(Expression left, Expression right) : Expression
    {
        private readonly Expression left = left, right = right;

        public override object? Run(Func<List<string>, object?> getColumnValue)
        {
            var leftValue = left.Run(getColumnValue);
            var rightValue = right.Run(getColumnValue);

            return (int)leftValue! + (int)rightValue!; // TODO: Handle varied input types here.
        }

#if DEBUG
        public override string ToString() => $"{left} + {right}";
#endif
    }

    public sealed class Subtract(Expression left, Expression right) : Expression
    {
        private readonly Expression left = left, right = right;

        public override object? Run(Func<List<string>, object?> getColumnValue)
        {
            var leftValue = left.Run(getColumnValue);
            var rightValue = right.Run(getColumnValue);

            return (int)leftValue! - (int)rightValue!; // TODO: Handle varied input types here.
        }

#if DEBUG
        public override string ToString() => $"{left} + {right}";
#endif
    }

    public sealed class Reference(Name name) : Expression
    {
        private readonly List<string> name = [name.Value];

        public override string Name => this.name.Last();

        public void AddMultiPartComponent(Name name) => this.name.Add(name.Value);

        public override object? Run(Func<List<string>, object?> getColumnValue) => getColumnValue(this.name);

#if DEBUG
        public override string ToString() => string.Join('.', name);
#endif
    }

    /// <summary>
    /// Encapsulates the SQL DATALENGTH command: https://learn.microsoft.com/en-us/sql/t-sql/functions/datalength-transact-sql
    /// </summary>
    public sealed class DataLength(ParserContext context) : Expression
    {
        private readonly Expression source = Parse(context);

        public override object? Run(Func<List<string>, object?> getColumnValue) => source.Run(getColumnValue) switch
        {
            null => null,
            int => 4,
            _ => throw new NotSupportedException($"Simulation unable to to run DATALENGTH function on the provided expression."),
        };

#if DEBUG
        public override string ToString() => $"DATALENGTH({source})";
#endif
    }

    /// <summary>
    /// Encapsulates the SQL ABS command: https://learn.microsoft.com/en-us/sql/t-sql/functions/abs-transact-sql
    /// </summary>
    public sealed class AbsoluteValue(ParserContext context) : Expression
    {
        private readonly Expression source = Parse(context);

        public override object? Run(Func<List<string>, object?> getColumnValue) => source.Run(getColumnValue) switch
        {
            null => null,
            int value => Math.Abs(value),
            _ => throw new NotSupportedException($"Simulation unable to to run DATALENGTH function on the provided expression."),
        };

#if DEBUG
        public override string ToString() => $"ABS({source})";
#endif
    }
}
