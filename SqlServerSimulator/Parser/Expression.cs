using System.Diagnostics;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

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
            AtPrefixedString atPrefixed => new VariableReference(atPrefixed, context),
            DoubleAtPrefixedString doubleAtPrefixedString => doubleAtPrefixedString.Parse() switch
            {
                AtAtKeyword.Error => new LastErrorExpression(),
                AtAtKeyword.Identity => new LastIdentityExpression(),
                AtAtKeyword.TranCount => new TranCountExpression(context),
                AtAtKeyword.RowCount => new RowCountExpression(),
                AtAtKeyword.LockTimeout => new LockTimeoutExpression(context),
                AtAtKeyword.SpId => new SpidExpression(context),
                AtAtKeyword.NestLevel => new NestLevelExpression(),
                AtAtKeyword.Dbts => new DbTsExpression(),
                AtAtKeyword.ProcId => new ProcIdExpression(),
                AtAtKeyword.FetchStatus => new FetchStatusExpression(),
                AtAtKeyword.CursorRows => new CursorRowsExpression(),
                AtAtKeyword.Options => Value.FromAtAtOptions(context),
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
            ReservedKeyword { Keyword: Keyword.Current_Timestamp } => new CurrentTimeFunction(CurrentTimeKind.CurrentTimestamp),
            ReservedKeyword { Keyword: Keyword.Current_Date } => new CurrentTimeFunction(CurrentTimeKind.CurrentDate),
            ReservedKeyword { Keyword: Keyword.Current_User } => new CurrentPrincipalKeyword("CURRENT_USER"),
            ReservedKeyword { Keyword: Keyword.Session_User } => new CurrentPrincipalKeyword("SESSION_USER"),
            ReservedKeyword { Keyword: Keyword.System_user } => new CurrentPrincipalKeyword("SYSTEM_USER"),
            ReservedKeyword { Keyword: Keyword.User } => new CurrentPrincipalKeyword("USER"),
            // LEFT, RIGHT, CONVERT, TRY_CONVERT, COALESCE, and NULLIF are
            // reserved keywords but dispatch as function calls when followed
            // by '(' — the surrounding loop hands the call shape off to
            // ResolveBuiltIn.
            ReservedKeyword { Keyword: Keyword.Left or Keyword.Right or Keyword.Convert or Keyword.Try_Convert or Keyword.Coalesce or Keyword.NullIf } reserved => new Reference(reserved.ToString()),
            UnquotedString { ContextualKeyword: ContextualKeyword.Next } nextToken => (Expression?)TryParseNextValueForOrFallback(context) ?? new Reference(nextToken),
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
                        var afterDot = context.GetNextRequired();
                        if (afterDot is Operator { Character: '*' })
                        {
                            // <qualifier>.* — convert the Reference into a
                            // StarProjection placeholder. Selection.ParseInner
                            // expands it once the FROM sources are known; if
                            // it survives into a non-projection context, the
                            // placeholder's Run / GetSqlType raise the
                            // surface-not-supported error.
                            if (expression is not Reference starQualifier)
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            expression = new StarProjection(starQualifier.Name);
                        }
                        else if (afterDot is Name name)
                        {
                            // Hierarchyid instance-method shape:
                            // <expr>.MethodName(args). When the method-name
                            // matches the closed accept-list AND the next
                            // token is '(', dispatch as a method call so
                            // expressions like @h.GetLevel() or column.GetAncestor(1)
                            // don't get rewritten as a multipart Reference
                            // that would later fail UDF resolution.
                            if (HierarchyIdMethodCall.IsKnownMethodName(name.Value))
                            {
                                var checkpoint = context.SaveCheckpoint();
                                var probe = context.GetNextOptional();
                                if (probe is Operator { Character: '(' })
                                {
                                    expression = HierarchyIdMethodCall.Parse(expression, name.Value, context);
                                    continue;
                                }
                                context.RestoreCheckpoint(checkpoint);
                            }
                            // XML instance-method shape: <expr>.value(...) /
                            // .nodes(...) / .query(...) / .exist(...) /
                            // .modify(...). Parses cleanly so CREATE VIEW /
                            // CREATE PROCEDURE bodies that reference XML
                            // methods can be stored verbatim; runtime
                            // evaluation throws NotSupportedException (see
                            // XmlMethodCall.Run).
                            if (XmlMethodCall.IsKnownMethodName(name.Value))
                            {
                                var checkpoint = context.SaveCheckpoint();
                                var probe = context.GetNextOptional();
                                if (probe is Operator { Character: '(' })
                                {
                                    expression = XmlMethodCall.Parse(expression, name.Value, context);
                                    continue;
                                }
                                context.RestoreCheckpoint(checkpoint);
                            }
                            // Spatial instance-method shape: <expr>.STDistance(args) /
                            // .STAsText() / .ToString() / ... — broad accept-list
                            // covering OGC + Microsoft-extension methods on
                            // geography / geometry values. Parses cleanly so
                            // CREATE VIEW / CREATE PROCEDURE bodies that reference
                            // spatial methods store verbatim; runtime evaluation
                            // throws NotSupportedException except for .ToString()
                            // which returns the stored WKT (see SpatialMethodCall).
                            if (SpatialMethodCall.IsKnownMethodName(name.Value))
                            {
                                var checkpoint = context.SaveCheckpoint();
                                var probe = context.GetNextOptional();
                                if (probe is Operator { Character: '(' })
                                {
                                    expression = SpatialMethodCall.Parse(expression, name.Value, context);
                                    continue;
                                }
                                context.RestoreCheckpoint(checkpoint);
                            }
                            if (expression is not Reference reference)
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            reference.AddMultiPartComponent(name);
                        }
                        else
                        {
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        }
                    }
                    continue;
                case Operator { Character: ':' }:
                    {
                        // Type-scope `::` operator. Modeled type-scopes:
                        // `hierarchyid::` (Parse / GetRoot), `geography::` /
                        // `geometry::` (Parse / STGeomFromText / Point / ...,
                        // see SpatialStaticCall). First ':' already consumed
                        // by GetNextOptional; peek to confirm the `::` shape.
                        // When the surrounding context has set
                        // StopExpressionAtBareColon (currently JSON_OBJECT's
                        // key parse), a single ':' rewinds and breaks out so
                        // the caller can consume it as a separator.
                        var beforeSecond = context.SaveCheckpoint();
                        var secondColon = context.GetNextOptional();
                        if (secondColon is not Operator { Character: ':' })
                        {
                            if (context.StopExpressionAtBareColon)
                            {
                                context.RestoreCheckpoint(beforeSecond);
                                break;
                            }
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        }
                        if (expression is not Reference colonRef || colonRef.ReferencedName.Count != 1)
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                        var typeName = colonRef.ReferencedName.Leaf;
                        context.MoveNextRequired();
                        var typePrefixCollation = context.Batch.CurrentDatabase.Collation;
                        expression = typePrefixCollation.Equals(typeName, "hierarchyid")
                            ? HierarchyIdStaticCall.Parse(context)
                            : typePrefixCollation.Equals(typeName, "geography")
                                ? SpatialStaticCall.Parse(SqlType.Geography, context)
                                : typePrefixCollation.Equals(typeName, "geometry")
                                    ? SpatialStaticCall.Parse(SqlType.Geometry, context)
                                    : throw SimulatedSqlException.SyntaxErrorNear(context);
                        continue;
                    }
                case Operator { Character: ')' }:
                    break;
                case Operator { Character: '(' }:
                    {
                        if (expression is not Reference reference)
                            break;

                        context.MoveNextRequired(); // Move past (
                        // 2- and 3-part dotted names route to user-defined
                        // function resolution before falling back to built-ins
                        // (which are 1-part only). Bare `fn(x)` raises Msg 195
                        // through ResolveBuiltIn's default arm — probe-confirmed
                        // that real SQL Server treats unqualified UDF calls as
                        // built-in misses ("'fn' is not a recognized built-in
                        // function name."). Schema-qualified miss → Msg 4121.
                        // An inline TVF resolved here also raises Msg 4121 —
                        // probe-confirmed real SQL Server treats a table-valued
                        // function used in scalar position as "missing scalar
                        // UDF or ambiguous" rather than a distinct error.
                        expression = reference.ReferencedName.Count >= 2
                            ? context.Batch.TryResolveFunction(reference.ReferencedName, out var function) && function is ScalarFunction scalarFn
                                ? UserFunctionCall.ParseCall(scalarFn, context)
                                : throw SimulatedSqlException.CannotFindUserDefinedFunction(reference.ReferencedName)
                            : ResolveBuiltIn(reference.Name, context);
                        // ResolveBuiltIn / ParseCall leave context.Token at the
                        // closing ). The next loop iteration's GetNextOptional
                        // advances past it; advancing here would skip an extra token.
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
                case UnquotedString { ContextualKeyword: ContextualKeyword.Within }
                    when expression is AggregateExpression aggregateForOrderBy:
                    {
                        ParseWithinGroupOrderBy(aggregateForOrderBy, context);
                        continue;
                    }
                // AT TIME ZONE postfix on a date/time expression. AT, TIME,
                // and ZONE are all contextual identifiers (SQL Server doesn't
                // reserve any of them); the runtime check rejects date/time
                // LHS with Msg 8116. Binds tighter than `+` so the zone-name
                // slot is a primary expression — full expressions need parens.
                case UnquotedString { ContextualKeyword: ContextualKeyword.At }:
                    {
                        expression = AtTimeZone.ParsePostfix(expression, context);
                        continue;
                    }
                // expr COLLATE collation_name postfix. Binds tighter than
                // binary operators (so 'a' + 'b' COLLATE X parses as
                // 'a' + ('b' COLLATE X)); the wrapper passes through Run /
                // GetSqlType and exposes the resolved collation to LIKE for
                // case-sensitivity-aware regex generation. Other consumers
                // (equality, ORDER BY, …) currently ignore the override; see
                // docs/claude/database-options.md.
                case ReservedKeyword { Keyword: Keyword.Collate }:
                    {
                        expression = CollateExpression.ParsePostfix(expression, context);
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
    /// Evaluates the expression with explicit access to both per-row column
    /// values (<see cref="RuntimeContext.ResolveColumn"/>) and per-batch /
    /// per-session / per-database state (<see cref="RuntimeContext.Batch"/>).
    /// </summary>
    public abstract SqlValue Run(RuntimeContext runtime);

    /// <summary>
    /// Static type-of resolver for projection planning: returns the
    /// <see cref="SqlType"/> this expression will produce, given the active
    /// batch (for database-affine state — in particular the database
    /// collation that result-type defaults should carry) and a resolver that
    /// maps column-name parts to their declared types. Lets a SELECT plan
    /// its output schema before any rows are read; the batch parameter must
    /// agree with the one passed to <see cref="Run"/> so the
    /// projection-schema type and the produced value's <see cref="SqlValue.Type"/>
    /// stay in parity (drift breaks union / CASE / coalesce schema and the
    /// row-encoder's type validation).
    /// </summary>
    /// <param name="batch">The active batch context, used to resolve database-affine defaults like the active collation.</param>
    /// <param name="resolveColumnType">Callback that, given a multi-part column name, returns its declared type or throws if unresolvable.</param>
    public abstract SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType);

    /// <summary>
    /// Diagnostic-only string rendering, surfaced to debuggers via
    /// <see cref="DebuggerDisplayAttribute"/>. Production paths must not call
    /// this — they should produce purpose-built formats (Msg-shaped error
    /// text, CAST-to-varchar, etc.) instead. Kept non-public so accidental
    /// production use fails to compile.
    /// </summary>
    internal abstract string DebugDisplay();

    /// <summary>
    /// SELECT INTO projection inference: returns whether this expression's
    /// result can be NULL. Default is conservative-true. Subclasses that can
    /// prove non-nullability (literal-non-null, column-ref-to-NOT-NULL,
    /// ISNULL-with-non-null-arg, CASE-with-all-branches-non-null) override
    /// to return false where applicable. Probe-confirmed rules:
    /// <list type="bullet">
    /// <item>Direct column ref preserves the source column's nullability.</item>
    /// <item>Integer arithmetic, CAST, CONVERT, COALESCE, aggregates all
    /// project as nullable (always YES). Aggregates include COUNT, which
    /// real SQL Server projects as NULL allowed despite the runtime
    /// guarantee that COUNT never returns NULL.</item>
    /// <item>ISNULL(x, y) is non-null iff EITHER operand is non-null.</item>
    /// <item>CASE WHEN ... END is non-null iff every THEN/ELSE branch is
    /// non-null (no-ELSE counts as implicit ELSE NULL).</item>
    /// </list>
    /// String <c>+</c> concatenation also projects as non-null when both
    /// operands are non-null in real SQL Server, but the simulator can't
    /// easily distinguish string-vs-arithmetic <c>+</c> at static analysis
    /// time (the dispatch is runtime per operand SqlValue.Type) so it
    /// conservatively reads as nullable — a minor fidelity gap with no
    /// practical impact since staging tables rarely rely on NOT NULL.
    /// </summary>
    internal virtual bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) => true;

    /// <summary>
    /// Returns true when <paramref name="expression"/> is a bare <c>NULL</c>
    /// literal at the syntactic level — that is, the keyword <c>NULL</c>
    /// optionally wrapped in any number of parentheses. A typed NULL like
    /// <c>CAST(NULL AS int)</c> is NOT a bare NULL (it's a <see cref="Cast"/>
    /// expression carrying a typed value). Used by <see cref="CaseExpression"/>
    /// and <see cref="Iif"/> to enforce SQL Server's Msg 8133 rule: every
    /// result expression in a CASE specification being a bare NULL is a
    /// compile-time error, but a single explicitly-typed NULL among the
    /// branches satisfies the rule.
    /// </summary>
    internal static bool IsBareNullLiteral(Expression expression) => expression switch
    {
        Parenthesized p => IsBareNullLiteral(p.Wrapped),
        Value v => v.Constant.IsNull,
        _ => false,
    };

    /// <summary>
    /// Visits every <see cref="Reference"/> node in this expression's tree,
    /// calling <paramref name="visit"/> with each reference's
    /// <see cref="MultiPartName"/>. Used by CREATE TABLE's inline-CHECK
    /// validator (Msg 8141) to enumerate column references statically —
    /// distinct from <see cref="GetSqlType"/>'s walk because the latter is
    /// optimized for type inference and several function-call subclasses
    /// shortcut to a fixed result type without visiting their child
    /// expressions. Default implementation is empty; container Expression
    /// subclasses override to recurse into their child Expressions.
    /// </summary>
    /// <remarks>
    /// Coverage gap: only the most common container subclasses
    /// (<see cref="Reference"/>, <see cref="Parenthesized"/>, the binary
    /// arithmetic / bitwise via <see cref="TwoSidedExpression"/>,
    /// <see cref="Cast"/>, <see cref="Length"/>) currently override this.
    /// Less-common containers (date-arithmetic functions, JSON functions,
    /// nested CASE, etc.) fall through to the empty default, so peer
    /// references buried inside them silently escape Msg 8141 detection at
    /// CREATE TABLE. Real SQL Server catches these; the simulator surfaces
    /// the runtime error at INSERT instead. New overrides can be added as
    /// applications surface the gap.
    /// </remarks>
    internal virtual void VisitColumnReferences(Action<MultiPartName> visit) { }

    /// <summary>
    /// True when this expression's parse tree contains a
    /// <see cref="Expressions.VariableReference"/> anywhere. Used by
    /// <c>STRING_SPLIT</c>'s <c>enable_ordinal</c> gate, which must reject
    /// every variable-bearing shape (real SQL Server: Msg 8748 —
    /// probe-confirmed the wrapped forms <c>CAST(@v AS int)</c>, <c>@v + 0</c>,
    /// and <c>(@v)</c> all reject, not just a bare <c>@v</c>) while still
    /// accepting constant expressions like <c>CAST(1 AS int)</c> / <c>(1)</c> /
    /// <c>1 + 0</c>. A runtime-eval probe can't see the variable (its slot is
    /// declared in the batch), so the detection is a static parse-tree walk.
    /// Default is <see langword="false"/>; the same common-container subclasses
    /// that override <see cref="VisitColumnReferences"/> (plus
    /// <see cref="Expressions.VariableReference"/> itself) recurse here, so a
    /// variable buried in a less-common container is a residual coverage gap.
    /// </summary>
    internal virtual bool ContainsVariableReference => false;

    /// <summary>
    /// When this expression is a deterministic, side-effect-free pass-through of
    /// a single operand — a <c>CAST</c> / <c>CONVERT</c> (their value operand) or
    /// a parenthesization — returns that operand; <see langword="null"/>
    /// otherwise. Such a node is value-stable exactly when its operand is, so the
    /// index-seek planner (<c>Selection.Execution.IndexSeek.cs</c>) peels these
    /// to decide whether a WHERE value side is row-invariant and safe to evaluate
    /// once for a seek. Matches real SQL Server keeping <c>col = CAST(&lt;const&gt;
    /// AS …)</c> sargable (probe-confirmed: integer / decimal widenings seek).
    /// </summary>
    internal virtual Expression? PureConversionOperand => null;

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
            2 => uppercaseName switch
            {
                "PI" => new Pi(context),
                _ => null
            },
            3 => uppercaseName switch
            {
                "ABS" => new AbsoluteValue(context),
                "AVG" => AggregateExpression.Parse(context, AggregateKind.Avg),
                "COS" => new TrigFunction(context, TrigKind.Cos),
                "COT" => new TrigFunction(context, TrigKind.Cot),
                "DAY" => new DatePart(context, DatePartKind.Day, "day"),
                "EXP" => new Exp(context),
                "IIF" => new Iif(context),
                "LAG" => WindowExpression.ParseLag(context),
                "LEN" => new Length(context),
                "LOG" => new Log(context),
                "MAX" => AggregateExpression.Parse(context, AggregateKind.Max),
                "MIN" => AggregateExpression.Parse(context, AggregateKind.Min),
                "SIN" => new TrigFunction(context, TrigKind.Sin),
                "STR" => new Str(context),
                "SUM" => AggregateExpression.Parse(context, AggregateKind.Sum),
                "TAN" => new TrigFunction(context, TrigKind.Tan),
                "VAR" => AggregateExpression.Parse(context, AggregateKind.Var),
                _ => null
            },
            4 => uppercaseName switch
            {
                "ACOS" => new TrigFunction(context, TrigKind.Acos),
                "ASIN" => new TrigFunction(context, TrigKind.Asin),
                "ATAN" => new TrigFunction(context, TrigKind.Atan),
                "ATN2" => new Atn2(context),
                "CAST" => new Cast(context),
                "CHAR" => new CharFromCode(context),
                "LEAD" => WindowExpression.ParseLead(context),
                "LEFT" => new Left(context),
                "RAND" => new Rand(context),
                "RANK" => WindowExpression.ParseRank(context),
                "SIGN" => new Sign(context),
                "SQRT" => new Sqrt(context),
                "TRIM" => new Trim(context),
                "VARP" => AggregateExpression.Parse(context, AggregateKind.VarP),
                "YEAR" => new DatePart(context, DatePartKind.Year, "year"),
                _ => null
            },
            5 => uppercaseName switch
            {
                "ASCII" => new Ascii(context),
                "COUNT" => AggregateExpression.Parse(context, AggregateKind.Count),
                "DB_ID" => new DbId(context),
                "FLOOR" => new Floor(context),
                "LOG10" => new Log10(context),
                "LOWER" => new Lower(context),
                "LTRIM" => new LeftTrim(context),
                "MONTH" => new DatePart(context, DatePartKind.Month, "month"),
                "NCHAR" => new NCharFromCode(context),
                "NEWID" => new NewId(context),
                "NTILE" => WindowExpression.ParseNTile(context),
                "PARSE" => new ParseFunction(context, tryMode: false),
                "POWER" => new Power(context),
                "RIGHT" => new Right(context),
                "ROUND" => new Round(context),
                "RTRIM" => new RightTrim(context),
                "SPACE" => new Space(context),
                "STDEV" => AggregateExpression.Parse(context, AggregateKind.Stdev),
                "STUFF" => new Stuff(context),
                "UPPER" => new Upper(context),
                _ => null
            },
            6 => uppercaseName switch
            {
                "CHOOSE" => new Choose(context),
                "CONCAT" => new StringConcat(context, StringConcatKind.Concat),
                "FORMAT" => new Format(context),
                "ISDATE" => new IsDate(context),
                "ISJSON" => new IsJson(context),
                "ISNULL" => new IsNullExpression(context),
                "NULLIF" => new NullIf(context),
                "SQUARE" => new TrigFunction(context, TrigKind.Square),
                "STDEVP" => AggregateExpression.Parse(context, AggregateKind.StdevP),
                _ => null
            },
            7 => uppercaseName switch
            {
                "CEILING" => new Ceiling(context),
                "CONVERT" => new ConvertExpression(context, tryMode: false),
                "DATEADD" => new DateAdd(context),
                "DB_NAME" => new DbName(context),
                "DEGREES" => new Degrees(context),
                "EOMONTH" => new EOMonth(context),
                "GETDATE" => new CurrentTimeFunction(context, CurrentTimeKind.GetDate),
                "GET_BIT" => new GetBit(context),
                "RADIANS" => new Radians(context),
                "REPLACE" => new Replace(context),
                "REVERSE" => new Reverse(context),
                "SET_BIT" => new SetBit(context),
                "SOUNDEX" => new Soundex(context),
                "TYPE_ID" => new TypeId(context),
                "UNICODE" => new UnicodeCodepoint(context),
                "USER_ID" => new PrincipalIdLookup(context, PrincipalIdKind.UserId),
                _ => null
            },
            8 => uppercaseName switch
            {
                "APP_NAME" => new AppName(context),
                "CHECKSUM" => new Checksum(context, isBinary: false),
                "COALESCE" => new Coalesce(context),
                "COL_NAME" => new ColName(context),
                "COMPRESS" => new Compress(context),
                "DATEDIFF" => new DateDiff.Standard(context),
                "DATENAME" => new DateName(context),
                "DATEPART" => new DatePart(context),
                "GROUPING" => new Grouping(context),
                "PATINDEX" => new PatIndex(context),
                "SUSER_ID" => new PrincipalIdLookup(context, PrincipalIdKind.SUserId),
                "TRY_CAST" => new Cast(context, tryMode: true),
                _ => null
            },
            9 => uppercaseName switch
            {
                "BIT_COUNT" => new BitCount(context),
                "CHARINDEX" => new CharIndex(context),
                "CONCAT_WS" => new StringConcat(context, StringConcatKind.ConcatWs),
                "COUNT_BIG" => AggregateExpression.Parse(context, AggregateKind.CountBig),
                "CUME_DIST" => WindowExpression.ParseCumeDist(context),
                "DATETRUNC" => new DateTrunc(context),
                "HOST_NAME" => new HostName(context),
                "INDEX_COL" => new IndexCol(context),
                "ISNUMERIC" => new IsNumeric(context),
                "IS_MEMBER" => new RoleMemberCheck(context),
                "OBJECT_ID" => new ObjectId(context),
                "PARSENAME" => new ParseName(context),
                "QUOTENAME" => new QuoteName(context),
                "REPLICATE" => new Replicate(context),
                "SCHEMA_ID" => new SchemaId(context),
                "SUBSTRING" => new Substring(context),
                "TRANSLATE" => new Translate(context),
                "TRY_PARSE" => new ParseFunction(context, tryMode: true),
                "TYPE_NAME" => new TypeName(context),
                "USER_NAME" => new UserName(context),
                _ => null
            },
            10 => uppercaseName switch
            {
                "COL_LENGTH" => new ColLength(context),
                "DATALENGTH" => new DataLength(context),
                "DECOMPRESS" => new Decompress(context),
                "DENSE_RANK" => WindowExpression.ParseDenseRank(context),
                "DIFFERENCE" => new Difference(context),
                "ERROR_LINE" => new ErrorLineFunction(context),
                "GETUTCDATE" => new CurrentTimeFunction(context, CurrentTimeKind.GetUtcDate),
                "IDENT_INCR" => new IdentSeedIncrement(context, isSeed: false),
                "IDENT_SEED" => new IdentSeedIncrement(context, isSeed: true),
                "JSON_ARRAY" => new JsonArray(context),
                "JSON_QUERY" => new JsonQuery(context),
                "JSON_VALUE" => new JsonValue(context),
                "LAST_VALUE" => WindowExpression.ParseLastValue(context),
                "LEFT_SHIFT" => new BitShift(context, isLeftShift: true),
                "PWDCOMPARE" => new PwdCompare(context),
                "PWDENCRYPT" => new PwdEncrypt(context),
                "ROW_NUMBER" => WindowExpression.ParseRowNumber(context),
                "STATS_DATE" => new StatsDate(context),
                "STRING_AGG" => AggregateExpression.Parse(context, AggregateKind.StringAgg),
                "SUSER_NAME" => new SUserName(context, isSidVariant: false),
                "XACT_STATE" => new XactState(context),
                _ => null
            },
            11 => uppercaseName switch
            {
                "DATE_BUCKET" => new DateBucket(context),
                "ERROR_STATE" => new ErrorStateFunction(context),
                "FIRST_VALUE" => WindowExpression.ParseFirstValue(context),
                "GETANSINULL" => new GetAnsiNull(context),
                "GROUPING_ID" => new GroupingId(context),
                "JSON_MODIFY" => new JsonModify(context),
                "JSON_OBJECT" => new JsonObject(context),
                "OBJECT_NAME" => new ObjectName(context),
                "RIGHT_SHIFT" => new BitShift(context, isLeftShift: false),
                "SCHEMA_NAME" => new SchemaName(context),
                "SUSER_SNAME" => new SUserName(context, isSidVariant: true),
                "SYSDATETIME" => new CurrentTimeFunction(context, CurrentTimeKind.SysDateTime),
                "TRY_CONVERT" => new ConvertExpression(context, tryMode: true),
                _ => null
            },
            12 => uppercaseName switch
            {
                "APPLOCK_MODE" => new AppLockMode(context),
                "APPLOCK_TEST" => new AppLockTest(context),
                "CHECKSUM_AGG" => AggregateExpression.Parse(context, AggregateKind.ChecksumAgg),
                "CONTEXT_INFO" => new ContextInfoFunction(context),
                "DATEDIFF_BIG" => new DateDiff.Big(context),
                "ERROR_NUMBER" => new ErrorNumberFunction(context),
                "HAS_DBACCESS" => new HasDbAccess(context),
                "PERCENT_RANK" => WindowExpression.ParsePercentRank(context),
                "ROWCOUNT_BIG" => new RowCountBig(context),
                "SWITCHOFFSET" => new SwitchOffset(context),
                "TYPEPROPERTY" => new TypeProperty(context),
                _ => null
            },
            13 => uppercaseName switch
            {
                "CURSOR_STATUS" => new CursorStatusFunction(context),
                "DATEFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.DateFromParts),
                "ERROR_MESSAGE" => new ErrorMessageFunction(context),
                "FORMATMESSAGE" => new FormatMessage(context),
                "IDENT_CURRENT" => new IdentCurrent(context),
                "INDEXPROPERTY" => new IndexProperty(context),
                "IS_ROLEMEMBER" => new RoleMemberCheck(context),
                "JSON_ARRAYAGG" => AggregateExpression.Parse(context, AggregateKind.JsonArrayAgg),
                "LOGINPROPERTY" => new LoginProperty(context),
                "STRING_ESCAPE" => new StringEscape(context),
                "TIMEFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.TimeFromParts),
                _ => null
            },
            14 => uppercaseName switch
            {
                "COLUMNPROPERTY" => new ColumnProperty(context),
                "ERROR_SEVERITY" => new ErrorSeverityFunction(context),
                "JSON_OBJECTAGG" => AggregateExpression.Parse(context, AggregateKind.JsonObjectAgg),
                "OBJECTPROPERTY" => new ObjectProperty(context),
                "ORIGINAL_LOGIN" => new OriginalLogin(context),
                "SCOPE_IDENTITY" => new LastIdentityExpression(context),
                "SERVERPROPERTY" => new ServerProperty(context),
                "SYSUTCDATETIME" => new CurrentTimeFunction(context, CurrentTimeKind.SysUtcDateTime),
                _ => null
            },
            15 => uppercaseName switch
            {
                "BINARY_CHECKSUM" => new Checksum(context, isBinary: true),
                "ERROR_PROCEDURE" => new ErrorProcedureFunction(context),
                "NEWSEQUENTIALID" => new NewSequentialId(context),
                "PERCENTILE_CONT" => WindowExpression.ParsePercentile(context, WindowKind.PercentileCont),
                "PERCENTILE_DISC" => WindowExpression.ParsePercentile(context, WindowKind.PercentileDisc),
                "SESSION_CONTEXT" => new SessionContext(context),
                _ => null
            },
            16 => uppercaseName switch
            {
                "IS_SRVROLEMEMBER" => new RoleMemberCheck(context),
                "JSON_PATH_EXISTS" => new JsonPathExists(context),
                "OBJECTPROPERTYEX" => new ObjectPropertyEx(context),
                "ORIGINAL_DB_NAME" => new OriginalDbName(context),
                "TODATETIMEOFFSET" => new ToDateTimeOffset(context),
                _ => null
            },
            17 => uppercaseName switch
            {
                "COLLATIONPROPERTY" => new CollationProperty(context),
                "DATETIMEFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.DateTimeFromParts),
                "HAS_PERMS_BY_NAME" => new HasPermsByName(context),
                "INDEXKEY_PROPERTY" => new IndexKeyProperty(context),
                "OBJECT_DEFINITION" => new ObjectDefinition(context),
                "SYSDATETIMEOFFSET" => new CurrentTimeFunction(context, CurrentTimeKind.SysDateTimeOffset),
                "TRIGGER_NESTLEVEL" => new TriggerNestLevelFunction(context),
                _ => null
            },
            18 => uppercaseName switch
            {
                "CONNECTIONPROPERTY" => new ConnectionProperty(context),
                "CURRENT_REQUEST_ID" => new CurrentRequestId(context),
                "DATABASEPROPERTYEX" => new DatabasePropertyEx(context),
                "DATETIME2FROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.DateTime2FromParts),
                "OBJECT_SCHEMA_NAME" => new ObjectSchemaName(context),
                _ => null
            },
            21 => uppercaseName switch
            {
                "APPROX_COUNT_DISTINCT" => AggregateExpression.Parse(context, AggregateKind.ApproxCountDistinct),
                "DATABASE_PRINCIPAL_ID" => new PrincipalIdLookup(context, PrincipalIdKind.DatabasePrincipalId),
                "MIN_ACTIVE_ROWVERSION" => new MinActiveRowVersion(context),
                _ => null
            },
            22 => uppercaseName switch
            {
                "CURRENT_TRANSACTION_ID" => new CurrentTransactionId(context),
                "SMALLDATETIMEFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.SmallDateTimeFromParts),
                _ => null
            },
            23 => uppercaseName switch
            {
                "DATETIMEOFFSETFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.DateTimeOffsetFromParts),
                "FULLTEXTSERVICEPROPERTY" => new FullTextServiceProperty(context),
                _ => null
            },
            _ => (Expression?)null
        } ?? throw SimulatedSqlException.UnrecognizedBuiltInFunction(name);
    }

    /// <summary>
    /// Handles the <c>NEXT VALUE FOR [schema.]sequence [OVER (ORDER BY ...)]</c>
    /// shape when the current token is the contextual keyword <c>NEXT</c>.
    /// Returns the constructed <see cref="NextValueFor"/> when the full
    /// <c>NEXT VALUE FOR &lt;name&gt;</c> shape is present, or <c>null</c> when
    /// the <c>NEXT</c> is just a column / identifier (e.g. a user column
    /// named <c>next</c>) — caller falls back to <see cref="Reference"/>.
    /// Uses <see cref="ParserContext.SaveCheckpoint"/> / restore to roll back
    /// the lookahead on the non-match path so the column-reference fallback
    /// resumes at the original <c>NEXT</c> token.
    /// </summary>
    private static NextValueFor? TryParseNextValueForOrFallback(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var valueToken = context.GetNextOptional();
        var forToken = context.GetNextOptional();
        var nameToken = context.GetNextOptional();
        if (valueToken is not UnquotedString { ContextualKeyword: ContextualKeyword.Value }
            || forToken is not ReservedKeyword { Keyword: Keyword.For }
            || nameToken is not Tokens.Name)
        {
            context.RestoreCheckpoint(checkpoint);
            return null;
        }
        var sequenceName = BatchContext.ParseObjectName(context);
        var nvf = new NextValueFor(context, sequenceName);

        // Optional OVER (ORDER BY ...) — parsed and discarded. The simulator
        // iterates rows in one deterministic order regardless of the OVER
        // hint; the sequence-advance pattern across rows is unchanged. Peek
        // for OVER via a save/restore so the outer loop's GetNextOptional
        // resumes at the correct token whether OVER is present or not.
        var overCheckpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is not ReservedKeyword { Keyword: Keyword.Over })
        {
            context.RestoreCheckpoint(overCheckpoint);
            return nvf;
        }
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var depth = 1;
        while (depth > 0)
        {
            switch (context.GetNextRequired())
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
            }
        }
        return nvf;
    }
}
