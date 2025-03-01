using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

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
                    expression = new Value(doubleAtPrefixedString);
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
                                _ = tokens.TryMoveNext(out token);
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
                        throw new NotSupportedException("Simulated expression parser doesn't know how to handle + at the start of an expression.");

                    token = tokens.RequireNext();

                    expression = new Add(expression, Parse(simulation, tokens, ref token, getVariableValue));
                    break;
                case Period:
                    if (expression is null)
                        throw new NotSupportedException("Simulated expression parser doesn't know how to handle '.' at the start of an expression.");

                    if (expression is not Reference reference)
                        throw new NotSupportedException("Simulated expression parser doesn't know how to handle '.' here.");

                    reference.AddMultiPartComponent(tokens.RequireNext<Name>());
                    break;
                case Comma:
                    if (expression is null)
                        throw SimulatedSqlException.SyntaxErrorNear(token);
                    return expression;
                default:
                    throw new NotSupportedException($"Simulated expression parser doesn't know how to handle '{token}'.");
            }
        } while (tokens.TryMoveNext(out token));

        return expression;
    }

    public abstract object? Run(Func<List<string>, object?> getColumnValue);

#if DEBUG
    public abstract override string ToString();
#endif

    private sealed class NamedExpression(Expression expression, string name) : Expression
    {
        private readonly Expression expression = expression;
        private readonly string name = name;

        public override string Name => this.name;

        public override object? Run(Func<List<string>, object?> getColumnValue) => this.expression.Run(getColumnValue);

#if DEBUG
        public override string ToString() => $"{expression} {name}";
#endif
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

    public sealed class Reference : Expression
    {
        private readonly List<string> name = [];

        public Reference(Name name)
        {
            this.name.Add(name.Value);
        }

        public override string Name => this.name.Last();

        public void AddMultiPartComponent(Name name)
        {
            this.name.Add(name.Value);
        }

        public override object? Run(Func<List<string>, object?> getColumnValue)
        {
            return getColumnValue(this.name);
        }

#if DEBUG
        public override string ToString() => string.Join('.', name);
#endif
    }
}
