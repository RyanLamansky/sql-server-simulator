using SqlServerSimulator.Parser.Tokens;
using System.Collections.Frozen;

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
                case Name name:
                    if (name is UnquotedString && name.TryParse(out var keyword))
                    {
                        switch (keyword)
                        {
                            case Keyword.As:
                                if (expression is null || !tokens.TryMoveNext(out token) || token is not Name alias)
                                    throw SimulatedSqlException.SyntaxErrorNear(name);

                                expression = new NamedExpression(expression, alias.Value);
                                _ = tokens.TryMoveNext(out token);
                                return expression;
                            case Keyword.From:
                                if (expression is null)
                                    throw SimulatedSqlException.SyntaxErrorNear(name);

                                return expression;
                        }
                    }

                    expression = new Reference(name);
                    break;
                case Plus:
                    if (expression is null)
                        throw new NotSupportedException("Simulated expression parser doesn't know how to handle + at the start of an expression.");

                    token = tokens.RequireNext();

                    var parsed = Parse(simulation, tokens, ref token, getVariableValue);
                    expression = new Add(expression, parsed);
                    if (parsed is NamedExpression named)
                        expression = named.TransferName(expression);

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
                        if (!BuiltInFunctions.TryGetValue(reference.Name, out var builtInFunction))
                            throw SimulatedSqlException.UnrecognizedBuiltInFunction(reference.Name);

                        token = tokens.RequireNext(); // Move past (
                        expression = builtInFunction(simulation, tokens, ref token, getVariableValue);
                        _ = tokens.TryMoveNext(out token); // Move past )
                        return expression;
                    }
                default:
                    throw new NotSupportedException($"Simulated expression parser doesn't know how to handle '{token}'.");
            }
        } while ((tokenWasRead && token is not null) || tokens.TryMoveNext(out token));

        return expression;
    }

    public abstract object? Run(Func<List<string>, object?> getColumnValue);

#if DEBUG
    public abstract override string ToString();
#endif

    private delegate Expression FunctionResolver(Simulation simulation, IEnumerator<Token> tokens, ref Token? token, Func<string, object?> getVariableValue);

    private static readonly FrozenDictionary<string, FunctionResolver> BuiltInFunctions = FrozenDictionary.Create<string, FunctionResolver>(Collation.Default, [
        new("datalength", (simulation, tokens, ref token, getVariableValue) => new DataLength(Expression.Parse(simulation, tokens, ref token, getVariableValue)))
        ]);

    private sealed class NamedExpression(Expression expression, string name) : Expression
    {
        private readonly Expression expression = expression;
        private readonly string name = name;
#if DEBUG
        private bool transferred;
#endif

        public override string Name => this.name;

        public override object? Run(Func<List<string>, object?> getColumnValue) => this.expression.Run(getColumnValue);

#if DEBUG
        /// <summary>
        /// Transfers the name to an outer expression.
        /// </summary>
        /// <param name="destination">The expression wrapping this<see cref="NamedExpression"/>.</param>
        /// <returns>A new <see cref="NamedExpression"/> wrapping <paramref name="destination"/> using <see cref="Name"/>.</returns>
#endif
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

    public sealed class DataLength(Expression source) : Expression
    {
        private readonly Expression source = source;

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
}
