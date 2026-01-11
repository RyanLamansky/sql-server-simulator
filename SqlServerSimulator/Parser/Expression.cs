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

    /// <summary>
    /// An expression that's wrapped in parentheses, potentially affecting the order of operations.
    /// </summary>
    private sealed class Parenthesized(Expression wrapped) : Expression
    {
        private readonly Expression wrapped = wrapped;

        public override DataValue Run(Func<List<string>, DataValue> getColumnValue) => wrapped.Run(getColumnValue);

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

        public override byte Precedence => expression.Precedence;

        public override DataValue Run(Func<List<string>, DataValue> getColumnValue) => this.expression.Run(getColumnValue);

#if DEBUG
        public override string ToString() => $"{expression} {name}";
#endif
    }

    /// <summary>
    /// Values are resolved at parse time.
    /// </summary>
    private sealed class Value : Expression
    {
        private readonly DataValue value;

        public Value()
        {
        }

        public Value(DataValue value) => this.value = value;

        public Value(AtPrefixedString atPrefixed, ParserContext context)
        {
            this.value = context.GetVariableValue(atPrefixed.Value);
        }

        public Value(DoubleAtPrefixedString doubleAtPrefixedString)
        {
            switch (doubleAtPrefixedString.Parse())
            {
                case AtAtKeyword.Version:
                    this.value = new("SQL Server Simulator", DataType.BuiltInDbString);
                    return;
            }

            throw new NotSupportedException($"Simulator doesn't recognize {doubleAtPrefixedString}.");
        }

        public override DataValue Run(Func<List<string>, DataValue> getColumnValue) => value;

#if DEBUG
        public override string ToString() => value.Value?.ToString() ?? "null";
#endif
    }

    private abstract class TwoSidedExpression(Expression left, Expression right) : Expression
    {
        private Expression right = right, left = left;

        public TwoSidedExpression AdjustForPrecedence()
        {
            if (this.right is not TwoSidedExpression rightTwo || rightTwo.Precedence < this.Precedence)
                return this;

            (rightTwo.left, this.right) = (this, rightTwo.left);
            return rightTwo;
        }

        public sealed override DataValue Run(Func<List<string>, DataValue> getColumnValue)
            => Run(left.Run(getColumnValue), right.Run(getColumnValue));

        protected abstract DataValue Run(DataValue left, DataValue right);
        protected abstract char Operator { get; }

#if DEBUG
        public sealed override string ToString() => $"{left} {Operator} {right}";
#endif
    }

    private abstract class MathExpression(Expression left, Expression right) : TwoSidedExpression(left, right)
    {
        protected abstract DataValue Run(DataType.NumericCompatibleDataType common, DataValue left, DataValue right);

        protected sealed override DataValue Run(DataValue left, DataValue right) => Run(DataType.CommonNumeric(left, right, this.Operator), left, right);
    }

    private sealed class Add(Expression left, Expression right) : MathExpression(left, right)
    {
        public override byte Precedence => 3;

        protected override DataValue Run(DataType.NumericCompatibleDataType common, DataValue left, DataValue right) => common.Add(left, right);

        protected override char Operator => '+';
    }

    private sealed class Subtract(Expression left, Expression right) : MathExpression(left, right)
    {
        public override byte Precedence => 3;

        protected override DataValue Run(DataType.NumericCompatibleDataType common, DataValue left, DataValue right) => common.Subtract(left, right);

        protected override char Operator => '-';
    }

    private sealed class Multiply(Expression left, Expression right) : MathExpression(left, right)
    {
        public override byte Precedence => 2;

        protected override DataValue Run(DataType.NumericCompatibleDataType common, DataValue left, DataValue right) => common.Multiply(left, right);

        protected override char Operator => '*';
    }

    private sealed class Divide(Expression left, Expression right) : MathExpression(left, right)
    {
        public override byte Precedence => 2;

        protected override DataValue Run(DataType.NumericCompatibleDataType common, DataValue left, DataValue right) => common.Divide(left, right);

        protected override char Operator => '/';
    }

    private abstract class BitwiseExpression(Expression left, Expression right) : TwoSidedExpression(left, right)
    {
        protected abstract DataValue Run(DataType.BitwiseCompatibleDataType common, DataValue left, DataValue right);

        protected sealed override DataValue Run(DataValue left, DataValue right) => Run(DataType.CommonInteger(left, right, this.Operator), left, right);
    }

    private sealed class BitwiseAnd(Expression left, Expression right) : BitwiseExpression(left, right)
    {
        public override byte Precedence => 3;

        protected override DataValue Run(DataType.BitwiseCompatibleDataType common, DataValue left, DataValue right) => common.BitwiseAnd(left, right);

        protected override char Operator => '&';
    }

    private sealed class BitwiseOr(Expression left, Expression right) : BitwiseExpression(left, right)
    {
        public override byte Precedence => 3;

        protected override DataValue Run(DataType.BitwiseCompatibleDataType common, DataValue left, DataValue right) => common.BitwiseOr(left, right);

        protected override char Operator => '|';
    }

    private sealed class BitwiseExclusiveOr(Expression left, Expression right) : BitwiseExpression(left, right)
    {
        public override byte Precedence => 3;

        protected override DataValue Run(DataType.BitwiseCompatibleDataType common, DataValue left, DataValue right) => common.BitwiseExclusiveOr(left, right);

        protected override char Operator => '^';
    }

    private sealed class Reference(Name name) : Expression
    {
        private readonly List<string> name = [name.Value];

        public override string Name => this.name[^1];

        public void AddMultiPartComponent(Name name) => this.name.Add(name.Value);

        public override DataValue Run(Func<List<string>, DataValue> getColumnValue) => getColumnValue(this.name);

#if DEBUG
        public override string ToString() => string.Join('.', name);
#endif
    }

    /// <summary>
    /// Encapsulates the SQL DATALENGTH command: https://learn.microsoft.com/en-us/sql/t-sql/functions/datalength-transact-sql
    /// </summary>
    private sealed class DataLength(ParserContext context) : Expression
    {
        private readonly Expression source = Parse(context);

        public override DataValue Run(Func<List<string>, DataValue> getColumnValue)
        {
            var value = source.Run(getColumnValue);
            return value.Value is null ? default : new(value.Type.DataLength(value));
        }

#if DEBUG
        public override string ToString() => $"DATALENGTH({source})";
#endif
    }

    /// <summary>
    /// Encapsulates the SQL ABS command: https://learn.microsoft.com/en-us/sql/t-sql/functions/abs-transact-sql
    /// </summary>
    private sealed class AbsoluteValue(ParserContext context) : Expression
    {
        private readonly Expression source = Parse(context);

        public override DataValue Run(Func<List<string>, DataValue> getColumnValue)
        {
            var value = source.Run(getColumnValue);

            return new(value.Value switch
            {
                null => value.Value,
                int v => Math.Abs(v),
                _ => throw new NotSupportedException($"Simulation unable to to run ABS function on the provided expression."),
            }, value.Type);
        }

#if DEBUG
        public override string ToString() => $"ABS({source})";
#endif
    }
}
