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
    /// <param name="simulation">Simulation shared context.</param>
    /// <param name="tokens">The sequence of command tokens. This will be advanced to the end of the expression.</param>
    /// <param name="token">Retains the most recently provided token from <paramref name="tokens"/>.</param>
    /// <param name="getVariableValue">Provides the value to any variable included alongside the command.</param>
    /// <returns>The parsed expression.</returns>
    /// <exception cref="SimulatedSqlException">A variety of messages are possible for various problems with the command.</exception>
    /// <exception cref="NotSupportedException">A condition was encountered that may be valid but can't currently be parsed.</exception>
    public static Expression Parse(Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue)
    {
        Expression? expression = null;
        bool tokenWasRead;

        do
        {
            tokenWasRead = false;

            switch (token)
            {
                case Numeric number:
                    expression = new Value(number);
                    break;
                case AtPrefixedString atPrefixed:
                    expression = new Value(atPrefixed, getVariableValue);
                    break;
                case DoubleAtPrefixedString doubleAtPrefixedString:
                    expression = new Value(doubleAtPrefixedString);
                    break;
                case ReservedKeyword reservedKeyword:
                    switch (reservedKeyword.Keyword)
                    {
                        case Keyword.As:
                            if (expression is null || !tokens.TryMoveNext(out token) || token is not Name alias)
                                throw SimulatedSqlException.SyntaxErrorNearKeyword(reservedKeyword);

                            expression = new NamedExpression(expression, alias.Value);
                            _ = tokens.TryMoveNext(out token);
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
                case Plus:
                    if (expression is null)
                    {
                        token = tokens.RequireNext();
                        expression = Expression.Parse(simulation, tokens, ref token, getVariableValue);
                        break;
                    }

                    token = tokens.RequireNext();

                    {
                        var parsed = Parse(simulation, tokens, ref token, getVariableValue);
                        expression = new Add(expression, parsed);
                        if (parsed is NamedExpression named)
                            expression = named.TransferName(expression);
                    }

                    tokenWasRead = true;
                    break;
                case Minus:
                    if (expression is null)
                    {
                        token = tokens.RequireNext();
                        expression = Expression.Parse(simulation, tokens, ref token, getVariableValue);
                        expression = new Subtract(new Value(0), expression);
                        break;
                    }

                    token = tokens.RequireNext();

                    {
                        var parsed = Parse(simulation, tokens, ref token, getVariableValue);
                        expression = new Subtract(expression, parsed);
                        if (parsed is NamedExpression named)
                            expression = named.TransferName(expression);
                    }

                    tokenWasRead = true;
                    break;

                case Period:
                    if (expression is null)
                        throw new NotSupportedException("Simulated expression parser doesn't know how to handle '.' at the start of an expression.");

                    {
                        if (expression is not Reference reference)
                            throw new NotSupportedException("Simulated expression parser doesn't know how to handle '.' here.");

                        reference.AddMultiPartComponent(tokens.RequireNext<Name>());
                    }
                    break;
                case Comma:
                case CloseParentheses:
                    if (expression is null)
                        throw SimulatedSqlException.SyntaxErrorNear(token);
                    return expression;
                case OpenParentheses:
                    {
                        if (expression is not Reference reference)
                            throw SimulatedSqlException.SyntaxErrorNear(token);
                        token = tokens.RequireNext(); // Move past (
                        expression = ResolveBuiltIn(reference.Name, simulation, tokens, ref token, getVariableValue);
                        _ = tokens.TryMoveNext(out token); // Move past )
                        return expression;
                    }
                default:
                    throw new NotSupportedException($"Simulated expression parser doesn't know how to handle '{token}'.");
            }
        } while ((tokenWasRead && token is not null) || tokens.TryMoveNext(out token));

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

    private static Expression ResolveBuiltIn(string name, Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue)
    {
        Span<char> uppercaseName = stackalloc char[name.Length];
        return name.ToUpperInvariant(uppercaseName) switch
        {
            3 => uppercaseName switch
            {
                "ABS" => new AbsoluteValue(simulation, tokens, ref token, getVariableValue),
                _ => null
            },
            10 => uppercaseName switch
            {
                "DATALENGTH" => new DataLength(simulation, tokens, ref token, getVariableValue),
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

        public Value(AtPrefixedString atPrefixed, Func<string, object?> getVariableValue)
        {
            this.value = getVariableValue(atPrefixed.Value);
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
    public sealed class DataLength : Expression
    {
        private readonly Expression source;

        public DataLength(Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue)
            => this.source = Expression.Parse(simulation, tokens, ref token, getVariableValue);

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
    public sealed class AbsoluteValue : Expression
    {
        private readonly Expression source;

        public AbsoluteValue(Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue)
            => this.source = Expression.Parse(simulation, tokens, ref token, getVariableValue);

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
