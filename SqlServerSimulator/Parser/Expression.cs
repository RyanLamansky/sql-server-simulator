using System.Diagnostics;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Contains the logic described by a SQL command and computes its results.
/// </summary>
[DebuggerDisplay("{DebugDisplay(),nq}")]
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
            DoubleAtPrefixedString doubleAtPrefixedString => doubleAtPrefixedString.Parse() switch
            {
                AtAtKeyword.Identity => new LastIdentityExpression(context.Simulation),
                AtAtKeyword.TranCount => new TranCountExpression(context),
                _ => new Value(doubleAtPrefixedString),
            },
            ReservedKeyword { Keyword: Keyword.Null } => new Value(),
            ReservedKeyword { Keyword: Keyword.Case } => CaseExpression.ParseCase(context),
            // LEFT, RIGHT, CONVERT, and TRY_CONVERT are reserved keywords
            // but dispatch as function calls when followed by '(' — the
            // surrounding loop hands the call shape off to ResolveBuiltIn.
            ReservedKeyword { Keyword: Keyword.Left or Keyword.Right or Keyword.Convert or Keyword.Try_Convert or Keyword.Coalesce } reserved => new Reference(reserved.ToString()),
            Name name => new Reference(name),
            Operator { Character: '(' } => ParseGroupedExpression(context),
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
                // OVER following an aggregate function call promotes the
                // aggregate into a window expression. ROW_NUMBER's OVER is
                // already consumed inside its own parser; reaching this case
                // means the inner parse left the cursor on `)` of an
                // aggregate, the outer GetNextOptional advanced past it, and
                // landed here. STRING_AGG OVER is rejected by WrapAggregate
                // (Msg 4113).
                case ReservedKeyword { Keyword: Keyword.Over }:
                    {
                        if (expression is not AggregateExpression aggregate)
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        expression = WindowExpression.WrapAggregate(aggregate, context);
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
    public abstract Storage.SqlValue Run(Func<MultiPartName, Storage.SqlValue> getColumnValue);

    /// <summary>
    /// Static type-of resolver for projection planning: returns the
    /// <see cref="Storage.SqlType"/> this expression will produce, given a
    /// resolver that maps column-name parts to their declared types. Lets a
    /// SELECT plan its output schema before any rows are read.
    /// </summary>
    /// <param name="resolveColumnType">Callback that, given a multi-part column name, returns its declared type or throws if unresolvable.</param>
    public abstract Storage.SqlType GetSqlType(Func<MultiPartName, Storage.SqlType> resolveColumnType);

    /// <summary>
    /// Diagnostic-only string rendering, surfaced to debuggers via
    /// <see cref="DebuggerDisplayAttribute"/>. Production paths must not call
    /// this — they should produce purpose-built formats (Msg-shaped error
    /// text, CAST-to-varchar, etc.) instead. Kept non-public so accidental
    /// production use fails to compile.
    /// </summary>
    internal abstract string DebugDisplay();

    /// <summary>
    /// Parses a grouped expression starting at the opening <c>(</c>. Two
    /// shapes share the leading paren: a parenthesized expression
    /// (<c>(1 + 2)</c>, parses as <see cref="Parenthesized"/>) or a scalar
    /// subquery (<c>(SELECT col FROM t)</c>, parses as
    /// <see cref="ScalarSubqueryExpression"/>). Dispatch is by peeking the
    /// token immediately after <c>(</c>: a <c>SELECT</c> keyword routes to
    /// the subquery path; anything else falls through to the standard
    /// parenthesized form. Both leave the cursor on the closing <c>)</c>,
    /// matching the lookahead contract <see cref="Parse"/>'s
    /// binary loop expects.
    /// </summary>
    private static Expression ParseGroupedExpression(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Select })
        {
            var inner = Selection.Parse(context, depth: 1, outerTypeResolver: context.OuterTypeResolver);
            return inner.Schema.Length != 1
                ? throw SimulatedSqlException.SubqueryNotIntroducedWithExists()
                : context.Token is not Operator { Character: ')' }
                    ? throw SimulatedSqlException.SyntaxErrorNear(context)
                    : (Expression)new ScalarSubqueryExpression(inner);
        }

        return new Parenthesized(Expression.Parse(context));
    }

    private static Expression ResolveBuiltIn(string name, ParserContext context)
    {
        Span<char> uppercaseName = stackalloc char[name.Length];
        return name.ToUpperInvariant(uppercaseName) switch
        {
            3 => uppercaseName switch
            {
                "ABS" => new AbsoluteValue(context),
                "AVG" => AggregateExpression.Parse(context, AggregateKind.Avg),
                "LEN" => new Length(context),
                "MAX" => AggregateExpression.Parse(context, AggregateKind.Max),
                "MIN" => AggregateExpression.Parse(context, AggregateKind.Min),
                "SUM" => AggregateExpression.Parse(context, AggregateKind.Sum),
                "VAR" => AggregateExpression.Parse(context, AggregateKind.Var),
                _ => null
            },
            4 => uppercaseName switch
            {
                "CAST" => new Cast(context),
                "LEFT" => new Left(context),
                "TRIM" => new Trim(context),
                "VARP" => AggregateExpression.Parse(context, AggregateKind.VarP),
                _ => null
            },
            5 => uppercaseName switch
            {
                "COUNT" => AggregateExpression.Parse(context, AggregateKind.Count),
                "LOWER" => new Lower(context),
                "LTRIM" => new LeftTrim(context),
                "NEWID" => new NewId(context),
                "RIGHT" => new Right(context),
                "RTRIM" => new RightTrim(context),
                "STDEV" => AggregateExpression.Parse(context, AggregateKind.Stdev),
                "UPPER" => new Upper(context),
                _ => null
            },
            6 => uppercaseName switch
            {
                "STDEVP" => AggregateExpression.Parse(context, AggregateKind.StdevP),
                _ => null
            },
            7 => uppercaseName switch
            {
                "CONVERT" => new ConvertExpression(context, tryMode: false),
                "DATEADD" => new DateAdd(context),
                "REPLACE" => new Replace(context),
                "REVERSE" => new Reverse(context),
                _ => null
            },
            8 => uppercaseName switch
            {
                "COALESCE" => new Coalesce(context),
                "DATEDIFF" => new DateDiff.Standard(context),
                "DATEPART" => new DatePart(context),
                _ => null
            },
            9 => uppercaseName switch
            {
                "CHARINDEX" => new CharIndex(context),
                "COUNT_BIG" => AggregateExpression.Parse(context, AggregateKind.CountBig),
                "SUBSTRING" => new Substring(context),
                _ => null
            },
            10 => uppercaseName switch
            {
                "DATALENGTH" => new DataLength(context),
                "ROW_NUMBER" => WindowExpression.ParseRowNumber(context),
                "STRING_AGG" => AggregateExpression.Parse(context, AggregateKind.StringAgg),
                _ => null
            },
            11 => uppercaseName switch
            {
                "TRY_CONVERT" => new ConvertExpression(context, tryMode: true),
                _ => null
            },
            12 => uppercaseName switch
            {
                "CHECKSUM_AGG" => AggregateExpression.Parse(context, AggregateKind.ChecksumAgg),
                "DATEDIFF_BIG" => new DateDiff.Big(context),
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
            15 => uppercaseName switch
            {
                "NEWSEQUENTIALID" => new NewSequentialId(context),
                _ => null
            },
            21 => uppercaseName switch
            {
                "APPROX_COUNT_DISTINCT" => AggregateExpression.Parse(context, AggregateKind.ApproxCountDistinct),
                _ => null
            },
            _ => (Expression?)null
        } ?? throw SimulatedSqlException.UnrecognizedBuiltInFunction(name);
    }
}
