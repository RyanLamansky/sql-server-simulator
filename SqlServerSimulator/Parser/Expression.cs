namespace SqlServerSimulator.Parser;

using Tokens;

internal abstract class Expression
{
    private protected Expression()
    {
    }

    public virtual string Name => string.Empty;

    public static Expression Parse(Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue)
    {
        Expression? expression = null;

        do
        {
            switch (token)
            {
                case Numeric number:
                    expression = new Value(number);
                    break;
                case AtPrefixedString atPrefixed:
                    expression = new Value(atPrefixed, getVariableValue);
                    break;
                case DoubleAtPrefixedString doubleAtPrefixedString:
                    expression = new Value(simulation, doubleAtPrefixedString);
                    break;
                case Name name:
                    if (name is UnquotedString && name.TryParse(out var keyword))
                    {
                        switch (keyword)
                        {
                            case Keyword.As:
                                if (expression is null || !tokens.TryMoveNext(out token) || token is not Name alias)
                                    throw new SimulatedSqlException("Incorrect syntax near the keyword 'as'.", 156, 15, 1);

                                expression = new NamedExpression(expression, alias.Value);
                                tokens.TryMoveNext(out token);
                                return expression;
                            case Keyword.From:
                                if (expression is null)
                                    throw new SimulatedSqlException("Incorrect syntax near the keyword 'from'.", 156, 15, 1);

                                return expression;
                        }
                    }

                    expression = new Reference(name);
                    break;
                case Plus:
                    if (expression is null)
                        throw new NotSupportedException("Simulated expression parser didn't know how to handle + at the start of an expression.");

                    if (!tokens.TryMoveNext(out token))
                        throw new SimulatedSqlException("Incorrect syntax near '+'.", 102, 15, 1);

                    expression = new Add(expression, Parse(simulation, tokens, ref token, getVariableValue));
                    break;
                default:
                    throw new NotSupportedException($"Simulated expression parser didn't know how to handle {token}.");
            }
        } while (tokens.TryMoveNext(out token));

        return expression;
    }

    public abstract object? Run(Func<string, object?> getColumnValue);

    private sealed class NamedExpression : Expression
    {
        private readonly Expression expression;
        private readonly string name;

        public NamedExpression(Expression expression, string name)
        {
            this.expression = expression;
            this.name = name;
        }

        public override string Name => this.name;

        public override object? Run(Func<string, object?> getColumnValue) => this.expression.Run(getColumnValue);
    }

    /// <summary>
    /// Values are resolved at parse time.
    /// </summary>
    private sealed class Value : Expression
    {
        private readonly object? value;

        public Value(Numeric value)
        {
            this.value = value.Value;
        }

        public Value(AtPrefixedString atPrefixed, Func<string, object?> getVariableValue)
        {
            this.value = getVariableValue(atPrefixed.Value);
        }

        public Value(Simulation simulation, DoubleAtPrefixedString doubleAtPrefixedString)
        {
            switch (doubleAtPrefixedString.Parse())
            {
                case AtAtKeyword.Version:
                    this.value = simulation.Version;
                    return;
            }

            throw new NotSupportedException($"Simulator doesn't recognize {doubleAtPrefixedString}.");
        }

        public override object? Run(Func<string, object?> getColumnValue) => value;
    }

    public sealed class Add : Expression
    {
        private readonly Expression left, right;

        public Add(Expression left, Expression right)
        {
            this.left = left;
            this.right = right;
        }

        public override object? Run(Func<string, object?> getColumnValue)
        {
            var leftValue = left.Run(getColumnValue);
            var rightValue = right.Run(getColumnValue);

            return (int)leftValue! + (int)rightValue!; // TODO: Handle varied input types here.
        }
    }

    public sealed class Reference : Expression
    {
        private readonly string name;

        public Reference(Name name)
        {
            this.name = name.Value;
        }

        public override object? Run(Func<string, object?> getColumnValue)
        {
            return getColumnValue(this.name);
        }
    }
}
