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
        Expression expression;

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
                expression = new Subtract(new Value(0), expression);
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

            return expression;
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
    public abstract object? Run(Func<List<string>, object?> getColumnValue);

#if DEBUG
    public abstract override string ToString();
#endif

    /// <summary>
    /// An expression that's wrapped in parentheses, potentially affecting the order of operations.
    /// </summary>
    private sealed class Parenthesized(Expression wrapped) : Expression
    {
        private readonly Expression wrapped = wrapped;

        public override object? Run(Func<List<string>, object?> getColumnValue) => wrapped.Run(getColumnValue);

#if DEBUG
        public override string ToString() => $"( {wrapped} )";
#endif
    }

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

    public abstract class TwoSidedExpression(Expression left, Expression right) : Expression
    {
        private readonly Expression left = left, right = right;

        public sealed override object? Run(Func<List<string>, object?> getColumnValue)
            => Run(left.Run(getColumnValue), right.Run(getColumnValue));

        protected abstract object? Run(object? left, object? right);

#if DEBUG
        protected abstract char Operator { get; }

        public sealed override string ToString() => $"{left} {Operator} {right}";
#endif
    }

    public sealed class Add(Expression left, Expression right) : TwoSidedExpression(left, right)
    {
        protected override object? Run(object? left, object? right) => (int)left! + (int)right!;

#if DEBUG
        protected override char Operator => '+';
#endif
    }

    public sealed class Subtract(Expression left, Expression right) : TwoSidedExpression(left, right)
    {
        protected override object? Run(object? left, object? right) => (int)left! - (int)right!;

#if DEBUG
        protected override char Operator => '-';
#endif
    }

    public sealed class Multiply(Expression left, Expression right) : TwoSidedExpression(left, right)
    {
        protected override object? Run(object? left, object? right) => (int)left! * (int)right!;

#if DEBUG
        protected override char Operator => '*';
#endif
    }

    public sealed class Divide(Expression left, Expression right) : TwoSidedExpression(left, right)
    {
        protected override object? Run(object? left, object? right) => (int)left! / (int)right!;

#if DEBUG
        protected override char Operator => '/';
#endif
    }

    public sealed class BitwiseAnd(Expression left, Expression right) : TwoSidedExpression(left, right)
    {
        protected override object? Run(object? left, object? right) => (int)left! & (int)right!;

#if DEBUG
        protected override char Operator => '&';
#endif
    }

    public sealed class BitwiseOr(Expression left, Expression right) : TwoSidedExpression(left, right)
    {
        protected override object? Run(object? left, object? right) => (int)left! | (int)right!;

#if DEBUG
        protected override char Operator => '|';
#endif
    }

    public sealed class BitwiseExclusiveOr(Expression left, Expression right) : TwoSidedExpression(left, right)
    {
        protected override object? Run(object? left, object? right) => (int)left! ^ (int)right!;

#if DEBUG
        protected override char Operator => '^';
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
