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
            // CURRENT_TIMESTAMP is a parens-less function in SQL Server's
            // grammar — it sits in the reserved-keyword space but never takes
            // an argument list. CURRENT_TIMESTAMP() with parens raises Msg 102
            // in SQL Server (probe-confirmed 2026-05-09); the simulator
            // inherits the same Msg-102 path from the surrounding parser
            // catching the unexpected `(`.
            ReservedKeyword { Keyword: Keyword.Current_Timestamp } => new CurrentTimeFunction(context.Simulation, CurrentTimeKind.CurrentTimestamp),
            // LEFT, RIGHT, CONVERT, TRY_CONVERT, COALESCE, and NULLIF are
            // reserved keywords but dispatch as function calls when followed
            // by '(' — the surrounding loop hands the call shape off to
            // ResolveBuiltIn.
            ReservedKeyword { Keyword: Keyword.Left or Keyword.Right or Keyword.Convert or Keyword.Try_Convert or Keyword.Coalesce or Keyword.NullIf } reserved => new Reference(reserved.ToString()),
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

                        var afterDot = context.GetNextRequired();
                        if (afterDot is Operator { Character: '*' })
                        {
                            // <qualifier>.* — convert the Reference into a
                            // StarProjection placeholder. Selection.ParseInner
                            // expands it once the FROM sources are known; if
                            // it survives into a non-projection context, the
                            // placeholder's Run / GetSqlType raise the
                            // surface-not-supported error.
                            expression = new StarProjection(reference.Name);
                        }
                        else if (afterDot is Name name)
                        {
                            reference.AddMultiPartComponent(name);
                        }
                        else
                        {
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        }
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
                // WITHIN GROUP (ORDER BY ...) following an aggregate is the
                // ordered-set aggregate postfix. STRING_AGG accepts it; every
                // other aggregate kind raises Msg 10757. WITHIN is contextual
                // (SQL Server doesn't reserve the identifier).
                case UnquotedString unquoted when expression is AggregateExpression aggregateForOrderBy
                    && context.AsContextual() == ContextualKeyword.Within:
                    {
                        _ = unquoted;
                        ParseWithinGroupOrderBy(aggregateForOrderBy, context);
                        continue;
                    }
                // AT TIME ZONE postfix on a date/time expression. AT, TIME,
                // and ZONE are all contextual identifiers (SQL Server doesn't
                // reserve any of them); the runtime check rejects date/time
                // LHS with Msg 8116. Binds tighter than `+` so the zone-name
                // slot is a primary expression — full expressions need parens.
                case UnquotedString atToken when context.AsContextual() == ContextualKeyword.At:
                    {
                        _ = atToken;
                        expression = AtTimeZone.ParsePostfix(expression, context);
                        continue;
                    }
            }

            return expression is TwoSidedExpression twoSided ? twoSided.AdjustForPrecedence() : expression;
        }
    }

    /// <summary>
    /// Consumes a <c>WITHIN GROUP (ORDER BY expr [ASC|DESC] [, ...])</c>
    /// postfix and attaches the parsed items to <paramref name="aggregate"/>.
    /// On entry the cursor is at the contextual <c>WITHIN</c> identifier; on
    /// return it sits on the closing <c>)</c> of the WITHIN GROUP, matching
    /// the lookahead contract that <see cref="Parse"/>'s binary loop's next
    /// <see cref="ParserContext.GetNextOptional"/> expects. Raises Msg 10757
    /// if the aggregate isn't <c>STRING_AGG</c>; Msg 5308 if any ORDER BY
    /// item is a bare integer literal (ordinal indices aren't allowed in this
    /// position, unlike the projection's ORDER BY).
    /// </summary>
    private static void ParseWithinGroupOrderBy(AggregateExpression aggregate, ParserContext context)
    {
        if (aggregate.Kind != AggregateKind.StringAgg)
            throw SimulatedSqlException.FunctionMayNotHaveWithinGroup(aggregate.LowerName);

        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Group })
            throw context.Token is ReservedKeyword rk ? SimulatedSqlException.SyntaxErrorNearKeyword(rk) : SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '(' })
            throw context.Token is ReservedKeyword rk2 ? SimulatedSqlException.SyntaxErrorNearKeyword(rk2) : SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Order })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.By })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var items = new List<OrderBySpec>();
        do
        {
            context.MoveNextRequired();
            var expr = Expression.Parse(context);
            // Reject bare integer ordinals (Msg 5308). A wrapped expression
            // like `1 + 0` falls through to the per-row path, matching SQL
            // Server's rejection-by-token-shape rather than constant folding.
            if (expr is Value valExpr
                && valExpr.Constant.Type == Storage.SqlType.Int32
                && !valExpr.Constant.IsNull)
            {
                throw SimulatedSqlException.IntegerIndexNotAllowedInOrderedAggregate();
            }

            var descending = false;
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.Asc }:
                    context.MoveNextOptional();
                    break;
                case ReservedKeyword { Keyword: Keyword.Desc }:
                    descending = true;
                    context.MoveNextOptional();
                    break;
            }
            items.Add(OrderBySpec.FromExpression(expr, descending));
        }
        while (context.Token is Operator { Character: ',' });

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        aggregate.OrderBy = items;
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
                "EXP" => new Exp(context),
                "IIF" => new Iif(context),
                "LEN" => new Length(context),
                "LOG" => new Log(context),
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
                "SIGN" => new Sign(context),
                "SQRT" => new Sqrt(context),
                "TRIM" => new Trim(context),
                "VARP" => AggregateExpression.Parse(context, AggregateKind.VarP),
                _ => null
            },
            5 => uppercaseName switch
            {
                "COUNT" => AggregateExpression.Parse(context, AggregateKind.Count),
                "FLOOR" => new Floor(context),
                "LOG10" => new Log10(context),
                "LOWER" => new Lower(context),
                "LTRIM" => new LeftTrim(context),
                "NEWID" => new NewId(context),
                "POWER" => new Power(context),
                "RIGHT" => new Right(context),
                "ROUND" => new Round(context),
                "RTRIM" => new RightTrim(context),
                "STDEV" => AggregateExpression.Parse(context, AggregateKind.Stdev),
                "UPPER" => new Upper(context),
                _ => null
            },
            6 => uppercaseName switch
            {
                "CONCAT" => new StringConcat(context, StringConcatKind.Concat),
                "ISNULL" => new IsNullExpression(context),
                "NULLIF" => new NullIf(context),
                "STDEVP" => AggregateExpression.Parse(context, AggregateKind.StdevP),
                _ => null
            },
            7 => uppercaseName switch
            {
                "CEILING" => new Ceiling(context),
                "CONVERT" => new ConvertExpression(context, tryMode: false),
                "DATEADD" => new DateAdd(context),
                "EOMONTH" => new EOMonth(context),
                "GETDATE" => new CurrentTimeFunction(context, CurrentTimeKind.GetDate),
                "REPLACE" => new Replace(context),
                "REVERSE" => new Reverse(context),
                _ => null
            },
            8 => uppercaseName switch
            {
                "COALESCE" => new Coalesce(context),
                "DATEDIFF" => new DateDiff.Standard(context),
                "DATEPART" => new DatePart(context),
                "TRY_CAST" => new Cast(context, tryMode: true),
                _ => null
            },
            9 => uppercaseName switch
            {
                "CHARINDEX" => new CharIndex(context),
                "CONCAT_WS" => new StringConcat(context, StringConcatKind.ConcatWs),
                "COUNT_BIG" => AggregateExpression.Parse(context, AggregateKind.CountBig),
                "SUBSTRING" => new Substring(context),
                _ => null
            },
            10 => uppercaseName switch
            {
                "DATALENGTH" => new DataLength(context),
                "GETUTCDATE" => new CurrentTimeFunction(context, CurrentTimeKind.GetUtcDate),
                "JSON_VALUE" => new JsonValue(context),
                "ROW_NUMBER" => WindowExpression.ParseRowNumber(context),
                "STRING_AGG" => AggregateExpression.Parse(context, AggregateKind.StringAgg),
                _ => null
            },
            11 => uppercaseName switch
            {
                "JSON_MODIFY" => new JsonModify(context),
                "SYSDATETIME" => new CurrentTimeFunction(context, CurrentTimeKind.SysDateTime),
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
                "DATEFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.DateFromParts),
                "IDENT_CURRENT" => new IdentCurrent(context),
                "TIMEFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.TimeFromParts),
                _ => null
            },
            14 => uppercaseName switch
            {
                "SCOPE_IDENTITY" => new LastIdentityExpression(context),
                "SYSUTCDATETIME" => new CurrentTimeFunction(context, CurrentTimeKind.SysUtcDateTime),
                _ => null
            },
            15 => uppercaseName switch
            {
                "NEWSEQUENTIALID" => new NewSequentialId(context),
                _ => null
            },
            17 => uppercaseName switch
            {
                "DATETIMEFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.DateTimeFromParts),
                "SYSDATETIMEOFFSET" => new CurrentTimeFunction(context, CurrentTimeKind.SysDateTimeOffset),
                _ => null
            },
            18 => uppercaseName switch
            {
                "DATETIME2FROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.DateTime2FromParts),
                _ => null
            },
            22 => uppercaseName switch
            {
                "SMALLDATETIMEFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.SmallDateTimeFromParts),
                _ => null
            },
            23 => uppercaseName switch
            {
                "DATETIMEOFFSETFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.DateTimeOffsetFromParts),
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
