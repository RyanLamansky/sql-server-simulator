using System.Diagnostics;
using System.Runtime.CompilerServices;
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
        // Stack-probe guard: a .NET stack overflow is uncatchable and
        // process-fatal — unacceptable for an in-process library fed a
        // pathological query. Real SQL Server's Msg 8631 is likewise a
        // genuine stack probe (threshold varies with its thread stack), so
        // deferring to the runtime's own remaining-stack check is the
        // faithful shape. Every recursive parse path (nested parens, function
        // arguments, subquery projections, unary-operator stacks) passes
        // through here at least once per nesting level, so this single site
        // bounds them all. Left-associative binary-operator chains, by
        // contrast, parse iteratively (see ParseBinaryContinuation) so a flat
        // `a + b + c + …` chain of arbitrary length no longer recurses per
        // term — matching real SQL Server's tolerance of thousands of terms.
        EnsureParseStack();
        var primary = ParsePrimary(context);
        var parsed = ParseBinaryContinuation(primary, minTightness: 1, context);
        // Argument bookkeeping for the enclosing constant-folded call, if any:
        // every argument of such a call is parsed through this entry point, so
        // one AND per return decides whether the call folds.
        if (context.FoldableArguments && !parsed.IsWrittenConstant)
            context.FoldableArguments = false;
        return parsed;
    }

    /// <summary>
    /// Converts the runtime's remaining-stack check into Msg 8631, isolated in
    /// its own non-inlined frame so the <c>try/catch</c> doesn't inflate the
    /// hot recursive <see cref="Parse"/> frame (deep function / paren nesting
    /// is stack-bound, so every byte on the recursive frame lowers the depth
    /// the simulator tolerates before this guard fires).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void EnsureParseStack()
    {
        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
        }
        catch (InsufficientExecutionStackException)
        {
            throw SimulatedSqlException.ServerStackLimitReached();
        }
    }

    /// <summary>
    /// The binding tightness of a left-associative binary operator: <c>* / %</c>
    /// bind tighter (2) than <c>+ - &amp; | ^</c> and the <c>&lt;&lt;</c> /
    /// <c>&gt;&gt;</c> shifts (1); every other token is a non-operator
    /// terminator (0). This is SQL Server's arithmetic/bitwise operator
    /// precedence (multiplicative above additive; shifts share the additive
    /// level, probe-confirmed <c>2 * 3 &lt;&lt; 1</c> = 12, <c>4 | 1 &lt;&lt; 2</c>
    /// = 20 — a flat left-associative <c>+ - &amp; | ^ &lt;&lt; &gt;&gt;</c>
    /// level), the sole distinction the precedence-climbing loop needs. Takes
    /// the context (not the bare token) so <c>&lt;&lt;</c> / <c>&gt;&gt;</c> —
    /// tokenized as two adjacent <c>&lt;</c> / <c>&gt;</c> operators — resolve
    /// via a doubled-adjacent peek, leaving a lone <c>&lt;</c> / <c>&gt;</c>
    /// (comparison) as a terminator for the boolean layer.
    /// </summary>
    private static int BinaryTightness(ParserContext context) => context.Token switch
    {
        Operator { Character: '*' or '/' or '%' } => 2,
        Operator { Character: '+' or '-' or '&' or '|' or '^' } => 1,
        Operator { Character: '<' or '>' } when IsAdjacentDoubledOperator(context) => 1,
        _ => 0,
    };

    /// <summary>
    /// True when the current <c>&lt;</c> / <c>&gt;</c> operator token is
    /// immediately followed by an adjacent identical operator — the
    /// two-character <c>&lt;&lt;</c> / <c>&gt;&gt;</c> shift form. Peeks the
    /// next token and restores, leaving the cursor on the first operator
    /// (mirrors the <c>||</c> concat detection); adjacency (second token's
    /// start == first token's end) keeps a whitespace-separated pair out.
    /// </summary>
    private static bool IsAdjacentDoubledOperator(ParserContext context)
    {
        var first = (Operator)context.Token!;
        var checkpoint = context.SaveCheckpoint();
        var doubled = context.GetNextOptional() is Operator second
            && second.Character == first.Character
            && second.StartIndex == first.EndIndex;
        context.RestoreCheckpoint(checkpoint);
        return doubled;
    }

    /// <summary>
    /// Iterative precedence-climbing over the left-associative binary
    /// operators (<c>+ - * / % &amp; | ^</c>). Consumes operators whose
    /// tightness is at least <paramref name="minTightness"/>, parsing each
    /// right operand as a primary and folding in any strictly-tighter
    /// following operators via a bounded recursion (depth ≤ the number of
    /// precedence levels, i.e. 2). A flat same-precedence chain stays in the
    /// loop, so <c>1 + 1 + … + 1</c> of any length builds a left-leaning tree
    /// with no per-term parse recursion — the structural fix that lets flat
    /// chains reach real SQL Server's thousands-of-terms tolerance instead of
    /// tripping the stack probe. On entry <see cref="ParserContext.Token"/> is
    /// already positioned on the first operator (or terminator); on return it
    /// is on the first token not part of the chain.
    /// </summary>
    private static Expression ParseBinaryContinuation(Expression left, int minTightness, ParserContext context)
    {
        var tightness = BinaryTightness(context);
        while (tightness >= minTightness && tightness > 0)
        {
            var opToken = (Operator)context.Token!;
            var op = opToken.Character;
            var opTightness = tightness;
            // `||` is the ANSI concat operator (two adjacent pipes) — same
            // precedence / associativity as `+`, distinct runtime semantics
            // (Concatenate). Detect it before the single `|` is consumed as
            // bitwise-OR: peek one token and require it be an immediately
            // adjacent second pipe.
            var isConcat = false;
            // `<<` / `>>` shift: a `<` / `>` reaching this loop is a confirmed
            // doubled-adjacent operator (BinaryTightness gates that). Consume
            // the second character (leaving the cursor on it, like the `||`
            // path) so MoveNext below reaches the right operand.
            var shiftLeft = (bool?)null;
            if (op == '|')
            {
                var checkpoint = context.SaveCheckpoint();
                if (context.GetNextOptional() is Operator { Character: '|' } secondPipe && secondPipe.StartIndex == opToken.EndIndex)
                    isConcat = true;
                else
                    context.RestoreCheckpoint(checkpoint);
            }
            else if (op is '<' or '>')
            {
                context.MoveNextRequired();
                shiftLeft = op == '<';
            }
            var right = ParsePrimary(context.MoveNextRequiredReturnSelf());
            var nextTightness = BinaryTightness(context);
            while (nextTightness > opTightness)
            {
                right = ParseBinaryContinuation(right, opTightness + 1, context);
                nextTightness = BinaryTightness(context);
            }
            left = isConcat ? new Concatenate(left, right)
                : shiftLeft is bool isLeftShift ? new BitShift(isLeftShift, left, right)
                : TwoSidedExpression.FromCompoundOp(op, left, right);
            tightness = BinaryTightness(context);
        }
        return left;
    }

    /// <summary>
    /// Parses a single operand: a leading atom (literal, reference, grouped
    /// expression, CASE, function name, …) plus every postfix that binds
    /// tighter than a binary operator (member / method access, the <c>::</c>
    /// type-scope, a function-call argument list, <c>OVER</c>,
    /// <c>WITHIN GROUP</c>, <c>AT TIME ZONE</c>, <c>COLLATE</c>), behind any
    /// leading unary operator (<c>+ - ~</c>). Returns with
    /// <see cref="ParserContext.Token"/> on the first token not consumed by
    /// the operand (a binary operator no tighter than the one the unary
    /// prefixes already absorbed, or a surrounding terminator), so the caller's
    /// <see cref="ParseBinaryContinuation"/> reads it without a further
    /// advance. Exposed for the legacy bare <c>SELECT TOP n</c> form, whose
    /// count is a lone operand: parsing it as a full expression would fold
    /// <c>TOP 1 *</c> into a multiplication and swallow the select-list star.
    /// </summary>
    internal static Expression ParsePrimary(ParserContext context)
    {
        // The two signs sit at SQL Server's *additive* precedence level —
        // below `* / %` — so a sign takes the whole following multiplicative
        // chain as its operand (ParseSignedOperand), while `~` binds tighter
        // than `*` and takes a lone operand. Probe-confirmed against SQL Server
        // 2025 (2026-08-03): `100 / -10 / 2` = -20 (`100 / -(10 / 2)`),
        // `8 / - 2 * 4` = -1, `100 / - 20 % 7` = -16, `~ 2 * 3` = -9
        // ((~2) * 3), and `~ - 2 * 3` = 5 (`~(-(2 * 3))` — `~`'s own operand
        // may itself be a sign, which then reaches for the chain).
        // Operators looser than multiplicative stop the sign's reach:
        // `- 6 + 2` = -4, `- 6 & 3` = 2, `- 2 << 3` = -16.
        // Unary minus is a dedicated Negate node (not `0 - x`) so it preserves
        // the operand's numeric precision/scale and keeps a negated integer
        // literal a digit-count literal for decimal-arithmetic sizing.
        return context.Token switch
        {
            Operator { Character: '+' } => ParseSignedOperand(context),
            Operator { Character: '-' } => Negate.Of(ParseSignedOperand(context)),
            Operator { Character: '~' } => BitwiseNot.Create(ParsePrimary(context.MoveNextRequiredReturnSelf())),
            _ => ParsePostfix(ParseLeadingAtom(context), context),
        };
    }

    /// <summary>
    /// Parses the operand of a leading unary <c>+</c> / <c>-</c>: the following
    /// multiplicative chain (<c>* / %</c>), which is everything binding tighter
    /// than the additive level the signs themselves occupy. Called with
    /// <see cref="ParserContext.Token"/> on the sign; returns with it on the
    /// first token the chain didn't consume. Carries its own stack probe
    /// because a stack of signs recurses through here once per sign without
    /// passing through <see cref="Parse"/>.
    /// </summary>
    private static Expression ParseSignedOperand(ParserContext context)
    {
        EnsureParseStack();
        return ParseBinaryContinuation(ParsePrimary(context.MoveNextRequiredReturnSelf()), minTightness: 2, context);
    }

    /// <summary>
    /// Dispatches the leading token of a primary to its atom expression.
    /// Extracted (and kept off the hot postfix / binary path) so its large
    /// switch doesn't inflate the recursive frame that deep function / paren
    /// nesting keeps live.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Expression ParseLeadingAtom(ParserContext context) => context.Token switch
    {
        Numeric number => new Value(number.Value, number.IntegerLiteralDigitCount),
        Literal literal => new Value(literal.Value),
        AtPrefixedString atPrefixed => new VariableReference(atPrefixed, context),
        DoubleAtPrefixedString doubleAtPrefixedString => doubleAtPrefixedString.Parse() switch
        {
            AtAtKeyword.Connections => new ConnectionsExpression(),
            AtAtKeyword.Error => new LastErrorExpression(),
            AtAtKeyword.Identity => new LastIdentityExpression(),
            AtAtKeyword.TranCount => new TranCountExpression(context),
            AtAtKeyword.RowCount => new RowCountExpression(),
            AtAtKeyword.LockTimeout => new LockTimeoutExpression(context),
            AtAtKeyword.TextSize => new TextSizeExpression(context),
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
        ReservedKeyword { Keyword: Keyword.System_user } => new CurrentPrincipalKeyword("SYSTEM_USER", isLogin: true),
        ReservedKeyword { Keyword: Keyword.User } => new CurrentPrincipalKeyword("USER"),
        // LEFT, RIGHT, CONVERT, TRY_CONVERT, COALESCE, and NULLIF are
        // reserved keywords but dispatch as function calls when followed
        // by '(' — the postfix loop hands the call shape off to ResolveBuiltIn.
        ReservedKeyword { Keyword: Keyword.Left or Keyword.Right or Keyword.Convert or Keyword.Try_Convert or Keyword.Coalesce or Keyword.NullIf } reserved => Counted(context, new Reference(reserved.ToString())),
        UnquotedString { ContextualKeyword: ContextualKeyword.Next } nextToken => (Expression?)TryParseNextValueForOrFallback(context) ?? Counted(context, new Reference(nextToken)),
        Name name => Counted(context, new Reference(name)),
        Operator { Character: '(' } => ParseGroupedExpression(context),
        // ODBC escape sequence: {d '…'} / {t '…'} / {ts '…'} / {guid '…'} typed
        // literals and {fn NAME(…)} the scalar-function escape.
        Operator { Character: '{' } => ParseOdbcEscape(context),
        // A reserved keyword can't lead an expression (the valid keyword-headed
        // forms — NULL / CASE / LEFT / CONVERT / CURRENT_* / … — are matched
        // above). Real SQL Server reports these as Msg 156 ("near the keyword")
        // rather than the generic Msg 102, e.g. the `(WHERE …)` inside an
        // unsupported `COUNT(*) FILTER (WHERE …)`.
        ReservedKeyword reservedAtom => throw SimulatedSqlException.SyntaxErrorNearKeyword(reservedAtom),
        _ => throw SimulatedSqlException.SyntaxErrorNear(context)
    };

    /// <summary>
    /// Consumes the postfix operators that bind tighter than any binary
    /// operator onto <paramref name="expression"/>, looping until the next
    /// token is a binary operator or a surrounding terminator (which it leaves
    /// on <see cref="ParserContext.Token"/> for the caller). See
    /// <see cref="ParsePrimary"/> for the full postfix set.
    /// </summary>
    private static Expression ParsePostfix(Expression expression, ParserContext context)
    {
        while (true)
        {
            switch (context.GetNextOptional())
            {
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
                            // spatial methods store verbatim; the members whose
                            // evaluation isn't built raise at Run (see
                            // SpatialMethodCall).
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
                            // Spatial property shape: <expr>.STX / .Lat / .STSrid
                            // — no argument list, so nothing distinguishes it from
                            // a dotted column name by syntax alone. A receiver that
                            // can't be a table qualifier (a constructor call, a
                            // variable, a parenthesized expression) dispatches off
                            // the member catalog; a name-shaped receiver asks the
                            // query scope whether it is a spatial column, which is
                            // what reads `Location.Lat` off a column and leaves
                            // `t.Lat` a two-part column name (Msg 326 where both
                            // bind). A method written without an argument list and
                            // a property written with one both reach here, and
                            // real reports each as a missing member (Msg 6592 /
                            // 6506) rather than as a syntax error.
                            var spatialMember = expression is Reference spatialQualifier
                                ? SpatialMethodCall.BindsAsColumnProperty(spatialQualifier.ReferencedName, name.Value, context)
                                : SpatialMethodCall.IsKnownMemberName(name.Value);
                            if (spatialMember)
                            {
                                var memberCheckpoint = context.SaveCheckpoint();
                                if (context.GetNextOptional() is Operator { Character: '(' })
                                {
                                    expression = SpatialMethodCall.Parse(expression, name.Value, context);
                                    continue;
                                }
                                context.RestoreCheckpoint(memberCheckpoint);
                                expression = SpatialMethodCall.Property(expression, name.Value);
                                continue;
                            }
                            if (expression is not Reference reference)
                                throw SimulatedSqlException.SyntaxErrorNear(context);
                            reference.AddMultiPartComponent(name);
                        }
                        else
                        {
                            // A reserved keyword can't be a name segment, and
                            // real names it: `dbo.user('a')` / `t.user` →
                            // Msg 156, not the generic Msg 102. The
                            // compatibility-gated `REGEXP_LIKE` reaches here
                            // the same way at level 170, which is what makes
                            // the unbracketed `dbo.REGEXP_LIKE(...)` CLR-UDF
                            // spelling fail to parse there.
                            throw afterDot is ReservedKeyword afterDotKeyword
                                ? SimulatedSqlException.SyntaxErrorNearKeyword(afterDotKeyword)
                                : SimulatedSqlException.SyntaxErrorNear(context);
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
                        // key parse), a single ':' rewinds and returns so
                        // the caller can consume it as a separator.
                        var beforeSecond = context.SaveCheckpoint();
                        var secondColon = context.GetNextOptional();
                        if (secondColon is not Operator { Character: ':' })
                        {
                            if (context.StopExpressionAtBareColon)
                            {
                                context.RestoreCheckpoint(beforeSecond);
                                return expression;
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
                case Operator { Character: '(' }:
                    {
                        if (expression is not Reference reference)
                            return expression;

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
                        expression = ParseCallArguments(reference, context);
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
                default:
                    return expression;
            }
        }
    }

    /// <summary>
    /// Resolves and parses a function-call argument list once the opening
    /// <c>(</c> of a <c>&lt;reference&gt;(</c> shape has been consumed. Charges
    /// the shared nesting budget (a function-argument level costs the same as a
    /// paren level; probe-confirmed 2026-07-18) so deeply nested calls raise
    /// Msg 191 rather than driving the stack probe. On entry
    /// <see cref="ParserContext.Token"/> is the first argument token; on return
    /// it is the closing <c>)</c>.
    /// </summary>
    private static Expression ParseCallArguments(Reference reference, ParserContext context)
    {
        // The name that got us here was counted as a column reference on the way
        // in (the parser can't know a `(` follows until now); un-count it so
        // ParserContext.ColumnReferencesParsed nets genuine columns only.
        context.ColumnReferencesParsed--;

        context.NestingDepth += FunctionCallNestingCost;
        if (context.NestingDepth > MaxNestingDepth)
            throw SimulatedSqlException.StatementNestedTooDeeply();
        try
        {
            if (reference.ReferencedName.Count >= 2)
            {
                if (VarbinaryToHex.TryResolve(reference.ReferencedName, context) is { } systemFunction)
                    return systemFunction;
                if (context.Batch.TryResolveFunction(reference.ReferencedName, out var function))
                {
                    switch (function)
                    {
                        case ScalarFunction scalarFn:
                            return UserFunctionCall.ParseCall(scalarFn, context);
                        case ClrScalarFunction clrFn:
                            return ClrFunctionCall.ParseCall(clrFn, context);
                    }
                }
                // Skip mode: real SQL Server defers user-function binding, so
                // an un-taken branch calling a missing schema-qualified
                // function compiles and is discarded. Parse-and-discard the
                // argument list so the statement (and any trailing ELSE / END)
                // parses to completion instead of throwing Msg 4121 mid-
                // expression. A bare 1-part unrecognized function still raises
                // Msg 195 below — real SQL Server errors on that at compile
                // time even in a dead branch.
                return context.Batch.IsSkipping
                    ? ParseDeferredCallAndDiscard(context)
                    : throw SimulatedSqlException.CannotFindUserDefinedFunction(reference.ReferencedName);
            }
            return ResolveBuiltIn(reference.Name, context);
        }
        finally
        {
            context.NestingDepth -= FunctionCallNestingCost;
        }
    }

    /// <summary>
    /// Skip-mode fallback for a schema-qualified function call whose name
    /// doesn't resolve. Parses and discards the comma-separated argument list
    /// so the cursor advances past the call, then returns a placeholder NULL
    /// expression. On entry the cursor is on the token after the opening
    /// <c>(</c>; on return it sits on the closing <c>)</c>, matching
    /// <see cref="UserFunctionCall.ParseFunctionArguments"/>'s post-condition so
    /// the outer parse loop resumes cleanly. Never runs outside skip mode — the
    /// discarded statement is conceptually never bound.
    /// </summary>
    private static Value ParseDeferredCallAndDiscard(ParserContext context)
    {
        if (context.Token is not Operator { Character: ')' })
        {
            while (true)
            {
                _ = Parse(context);
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
        }
        // Not a written literal: the placeholder stands for a call, so a dead
        // branch's `ORDER BY dbo.missing()` mustn't read as a constant term.
        return Value.UntypedNullPlaceholder();
    }

    /// <summary>
    /// Consumes a <c>WITHIN GROUP (ORDER BY expr [ASC|DESC] [, ...])</c>
    /// postfix and attaches the parsed items to <paramref name="aggregate"/>.
    /// On entry the cursor is at the contextual <c>WITHIN</c> identifier; on
    /// return it sits on the closing <c>)</c> of the WITHIN GROUP, matching
    /// the lookahead contract that <see cref="Parse"/>'s binary loop's next
    /// <see cref="ParserContext.GetNextOptional"/> expects. Raises Msg 10757
    /// if the aggregate isn't <c>STRING_AGG</c>; a constant ORDER BY item
    /// lands on Msg 5308 / 5309 via
    /// <see cref="ConstantFolding.RejectConstantWindowOrderByTerm"/> (this
    /// position carries no ordinal semantics, unlike the projection's
    /// ORDER BY).
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
            ConstantFolding.RejectConstantWindowOrderByTerm(expr, context);

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
    /// Projection nullability: whether this expression's result can be NULL.
    /// Drives the TDS COLMETADATA <c>fNullable</c> flag and the SELECT INTO
    /// destination's column declaration, which real derives identically
    /// (probe-confirmed cell for cell). The default is a conservative
    /// <see langword="true"/>; nothing SQL Server marks NOT NULL is reached by
    /// a rule, so each of the families below is its own override.
    /// </summary>
    /// <remarks>
    /// <para><b>Structural rules.</b> A direct column reference preserves the
    /// source column's declaration; a non-NULL literal is NOT NULL; a
    /// parenthesization, a <c>COLLATE</c> postfix and an <c>AS alias</c> pass
    /// their operand's answer through.</para>
    /// <para><b>Per-built-in dispositions</b>, probed one call per name — there
    /// is no rule behind them, only a table. Three groups:</para>
    /// <list type="bullet">
    /// <item><b>Always NOT NULL</b>: <c>CONCAT</c> / <c>CONCAT_WS</c> (NULL
    /// arguments are skipped, an all-NULL input yields the empty string),
    /// <c>PI</c>, the <c>GETDATE</c> family (<c>GETUTCDATE</c>,
    /// <c>SYSDATETIME</c>, <c>SYSUTCDATETIME</c>, <c>SYSDATETIMEOFFSET</c>,
    /// <c>CURRENT_TIMESTAMP</c>), <c>ROWCOUNT_BIG</c>,
    /// <c>MIN_ACTIVE_ROWVERSION</c>, <c>CURRENT_REQUEST_ID</c>,
    /// <c>CURSOR_STATUS</c>, <c>APPLOCK_TEST</c>, and every <c>@@</c> constant
    /// but <c>@@IDENTITY</c>.</item>
    /// <item><b>NOT NULL when every argument is</b>: <c>CEILING</c>,
    /// <c>FLOOR</c>, <c>ROUND</c> (all three arguments), <c>SIGN</c>,
    /// <c>RADIANS</c>, <c>GREATEST</c> / <c>LEAST</c>, and the six
    /// <c>…FROMPARTS</c> constructors.</item>
    /// <item><b>Always nullable</b>: everything else, including neighbours that
    /// read like the propagating group — <c>ABS</c>, <c>POWER</c>,
    /// <c>SQUARE</c>, <c>SQRT</c>, <c>EXP</c>, <c>LOG</c>, <c>DEGREES</c>, the
    /// trig family, <c>CHECKSUM</c>, <c>RAND</c>, <c>NEWID</c>, every date
    /// function taking a date (<c>DATEADD</c>, <c>DATEDIFF</c>,
    /// <c>DATEPART</c>, <c>YEAR</c>, <c>EOMONTH</c>, …), every string scalar
    /// (<c>LEN</c>, <c>LEFT</c>, <c>UPPER</c>, <c>REPLACE</c>, …),
    /// <c>CAST</c> / <c>CONVERT</c> / <c>PARSE</c> and their <c>TRY_</c>
    /// forms, <c>@@IDENTITY</c> / <c>SCOPE_IDENTITY</c>, and every aggregate
    /// and window function — <c>COUNT</c> included, which real marks nullable
    /// despite never returning NULL.</item>
    /// </list>
    /// <para><b>Operators.</b> Arithmetic <c>+ - * / %</c> is always nullable,
    /// even over two NOT NULL <c>int</c>s and even for <c>1 + 1</c>. Bitwise
    /// <c>~ &amp; | ^</c> and both concatenation operators — string / binary
    /// <c>+</c> and <c>||</c> — are NOT NULL when their operands are, which is
    /// why the inference resolves operand types (see
    /// <see cref="NullabilityContext"/>). Unary minus is arithmetic and so
    /// nullable, except over a constant real folds (<c>-1</c>, <c>-(1)</c>).</para>
    /// <para><b>The CASE family</b> — <c>CASE</c>, <c>IIF</c>, <c>COALESCE</c>,
    /// <c>NULLIF</c>, all of which desugar into one — is NOT NULL iff every
    /// surviving value arm is, with a missing <c>ELSE</c> counting as an
    /// implicit <c>ELSE NULL</c>. <b>Surviving</b> is the constant fold real
    /// applies first: an arm whose condition folds FALSE (or UNKNOWN) drops
    /// out, and an arm whose condition folds TRUE becomes the whole answer.
    /// That is what makes <c>NULLIF(1, 2)</c>, <c>COALESCE(NULL, 5)</c> and
    /// <c>CASE WHEN 1 = 1 THEN 5 END</c> NOT NULL while their unfoldable
    /// spellings stay nullable. <c>ISNULL</c> is not in this family: it is NOT
    /// NULL when <i>either</i> operand is, where <c>COALESCE</c> needs all of
    /// them (the classic ISNULL-vs-COALESCE metadata quirk).</para>
    /// <para><b>Arm conversions.</b> A surviving CASE-family arm — and every
    /// <c>GREATEST</c> / <c>LEAST</c> argument — additionally answers for the
    /// conversion the arm unification puts on it, reading nullable whenever
    /// that conversion could alter the value (see
    /// <see cref="SqlType.ConversionPreservesEveryValue"/>). That is what makes
    /// <c>COALESCE(&lt;decimal(9, 2) col&gt;, 0)</c> nullable while
    /// <c>COALESCE(&lt;decimal(9, 2) col&gt;, 0.0)</c> is NOT NULL. Set
    /// operators and a VALUES constructor unify the same way and do <i>not</i>
    /// carry the rule; <c>ISNULL</c> takes its first argument's type outright,
    /// so nothing converts there either.</para>
    /// <para>A VALUES row-constructor column is non-null iff no row supplies a
    /// nullable cell there.</para>
    /// </remarks>
    internal virtual bool ResultIsNullable(NullabilityContext context) => true;

    /// <summary>
    /// True when this expression's result — <b>if</b> it is decimal-family —
    /// is one SQL Server reports under the <c>numeric</c> type name rather than
    /// <c>decimal</c> (JDBC <c>getColumnTypeName</c> / the TDS COLMETADATA
    /// DECIMALN-vs-NUMERICN token). The two names store identically
    /// (<c>decimal(5, 2)</c> and <c>numeric(5, 2)</c> are the same
    /// <see cref="SqlType"/>), so this is projection-time metadata only, never
    /// part of type identity — the row encoder's stored-type equality would
    /// reject inserts otherwise. Sources of the numeric name: a
    /// decimal/numeric literal (all are numeric-named), a
    /// <c>CAST</c>/<c>CONVERT … AS numeric</c>, and any arithmetic or
    /// decimal-returning function that carries a numeric-named operand. A
    /// bare <c>decimal</c> keyword, integer operands, and — until declared
    /// per-column reported names are modeled — a decimal-typed column do NOT force it
    /// (probe-confirmed against SQL Server 2025: <c>10.0 + 1</c> → numeric,
    /// <c>d + 1</c> → decimal, <c>SUM(d)</c> → decimal). Callers gate on the
    /// result actually being <see cref="DecimalSqlType"/>; the flag is
    /// meaningless for non-decimal results and defaults to <see langword="false"/>.
    /// </summary>
    internal virtual bool ResultReportsNumeric => false;

    /// <summary>
    /// Records a freshly-built <see cref="Reference"/> against
    /// <see cref="ParserContext.ColumnReferencesParsed"/> — and against
    /// <see cref="ParserContext.FromSourceColumnSink"/> when a FROM source's
    /// arguments are being read — and returns it, so both stay a one-token
    /// change at each construction site inside the primary-expression switch.
    /// </summary>
    private static Reference Counted(ParserContext context, Reference reference)
    {
        context.ColumnReferencesParsed++;
        // The reference is recorded rather than its name: the dotted parts are
        // appended to `ReferencedName` after construction, so the whole
        // multi-part name only exists once the postfix loop is done with it.
        context.FromSourceColumnSink?.Add(reference);
        return reference;
    }

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
    /// Whether <paramref name="expression"/> is a <b>NULL constant</b> — the
    /// operand shape real SQL Server settles a comparison against while
    /// compiling, without ever looking at the other side (see
    /// <see cref="BooleanExpression"/>'s comparison folding). That is the
    /// <c>NULL</c> keyword, seen through parentheses, unary minus and a
    /// <c>CAST</c> / <c>CONVERT</c> wrapper — every one of those probe-confirmed
    /// to fold (<c>CAST(NULL AS int) &gt; &lt;overflowing expression&gt;</c>
    /// answers no rows where the expression alone raises Msg 8115).
    /// <para>
    /// A NULL that <em>arithmetic</em> produced is deliberately not one:
    /// real leaves <c>NULL + 1</c> and <c>CAST(NULL AS int) + 1</c> — and
    /// <c>NULLIF(1, 1)</c> — as ordinary operands and raises the other side's
    /// error, so the narrower syntactic reading matches it and never folds a
    /// comparison real evaluates.
    /// </para>
    /// </summary>
    internal static bool IsNullConstant(Expression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case Value value:
                    return value.IsLiteral && value.Constant.IsNull;
                // Unary minus isn't a PureConversionOperand (it computes), but
                // real folds `-CAST(NULL AS int)` all the same.
                case Negate negate:
                    expression = negate.Operand;
                    break;
                default:
                    if (expression.PureConversionOperand is not { } inner)
                        return false;
                    expression = inner;
                    break;
            }
        }
    }

    /// <summary>
    /// Significant-digit count when <paramref name="expression"/> is (or wraps,
    /// through parentheses or unary minus) a non-negative integer literal —
    /// e.g. <c>3</c>, <c>-3</c>, <c>-(3)</c>, <c>- -3</c> all report <c>1</c>;
    /// <c>0</c> for anything that isn't a literal (a column, <c>CAST(3 AS int)</c>,
    /// <c>3 + 4</c>). A negated integer literal stays a signed integer literal
    /// in SQL Server's decimal-arithmetic sizing (probe-confirmed:
    /// <c>10.0/-3</c> → <c>numeric(8, 6)</c>, same as <c>10.0/3</c>). The
    /// promotion sites size such an operand as <c>numeric(digit_count, 0)</c>
    /// when it meets a decimal partner.
    /// </summary>
    internal static int IntegerLiteralDigits(Expression expression) => expression switch
    {
        Value v => v.IntegerLiteralDigitCount,
        Parenthesized p => IntegerLiteralDigits(p.Wrapped),
        Negate n => IntegerLiteralDigits(n.Operand),
        NamedExpression named => IntegerLiteralDigits(named.Inner),
        _ => 0,
    };

    /// <summary>
    /// The signed value of an <c>int</c>-typed integer literal, seen through
    /// the same wrappers <see cref="IntegerLiteralDigits"/> walks (a leading
    /// unary <c>+</c> never reaches the tree — <c>ParsePrimary</c> drops it),
    /// or <see langword="null"/> when the expression is anything else: a
    /// column, a variable, a <c>CAST</c>, an arithmetic result, or a literal
    /// whose own type isn't <c>int</c> (a decimal literal, or an integer
    /// literal past int's range, which is <c>numeric(digit_count, 0)</c>).
    /// The negation is computed in <c>long</c> so <c>-(-2147483648)</c>
    /// reports a value outside int's range rather than wrapping.
    /// <para><c>NULLIF</c>'s result-narrowing rule is the one reader — see
    /// <see cref="Expressions.NullIf"/>.</para>
    /// </summary>
    internal static long? IntegerLiteralValue(Expression expression) => expression switch
    {
        Value { IsLiteral: true, Constant: { IsNull: false } constant } when constant.Type == SqlType.Int32 => constant.AsInt32,
        Parenthesized p => IntegerLiteralValue(p.Wrapped),
        Negate n => IntegerLiteralValue(n.Operand) is long value ? -value : null,
        NamedExpression named => IntegerLiteralValue(named.Inner),
        _ => null,
    };

    /// <summary>
    /// True when <paramref name="expression"/> is (or wraps, through
    /// parentheses) the bare untyped <c>NULL</c> keyword. Distinct from
    /// <see cref="IsBareNullLiteral"/>: this excludes a typed NULL constant
    /// (<c>@@REMSERVER</c>). Used by the common-type promotion sites so an
    /// untyped NULL yields to any typed sibling rather than forcing its
    /// placeholder <see cref="SqlType.Int32"/> onto the result.
    /// </summary>
    internal static bool IsUntypedNullLiteral(Expression expression) => expression switch
    {
        Parenthesized p => IsUntypedNullLiteral(p.Wrapped),
        Value v => v.IsUntypedNull,
        _ => false,
    };

    /// <summary>
    /// Joint-envelope common type across a set of value arms — the shared
    /// promotion seam for <c>CASE</c> / <c>COALESCE</c> / <c>IIF</c>. Untyped
    /// NULL arms yield to their typed siblings (all-NULL → <see cref="SqlType.Int32"/>),
    /// and integer-literal arms are sized by digit count against a decimal
    /// sibling. See <see cref="SqlType.PromoteBranches"/>.
    /// <para>Two string arms whose collations can't resolve raise
    /// <strong>Msg 457</strong> naming the <c>CASE</c> operator — real reports
    /// the unification failure there for <c>COALESCE</c> too (probe-confirmed:
    /// <c>COALESCE</c> desugars to <c>CASE</c>, so its message says
    /// <c>CASE</c>), while <c>ISNULL</c>, which takes the first argument's
    /// collation outright rather than unifying, never conflicts.</para>
    /// </summary>
    internal static SqlType PromoteValueArms(ReadOnlySpan<Expression> arms, BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var branches = new (SqlType, int)[arms.Length];
        var count = 0;
        SqlType? stringSoFar = null;
        foreach (var arm in arms)
        {
            if (IsUntypedNullLiteral(arm))
                continue;
            var armType = arm.GetSqlType(batch, resolveColumnType);
            if (armType.Category == SqlTypeCategory.String)
            {
                if (stringSoFar is { } accumulated && accumulated != armType)
                {
                    // Right-then-left naming, matching every other collation
                    // conflict message: the arm being folded in is named first.
                    stringSoFar = UnresolvedCollation.Settle(SqlType.Promote(accumulated, armType), accumulated, armType, "CASE");
                }
                else
                {
                    stringSoFar = armType;
                }
            }
            branches[count++] = (armType, IntegerLiteralDigits(arm));
        }
        var promoted = SqlType.PromoteBranches(branches.AsSpan(0, count));
        // The width / family promotion above resolves collation pairwise on its
        // own and settles on one when it can't; re-stamp the unresolved marker
        // the arm fold produced so the conflict survives into the result type.
        return stringSoFar is not null && UnresolvedCollation.On(stringSoFar) is { } unresolved && promoted.Category == SqlTypeCategory.String
            ? unresolved.Mark(promoted)
            : promoted;
    }

    /// <summary>
    /// Whether unifying <paramref name="arm"/> onto <paramref name="promoted"/>
    /// inserts a conversion real reports as nullable — one that could alter the
    /// value. The CASE family and <c>GREATEST</c> / <c>LEAST</c> apply this to
    /// each surviving value arm on top of the arm's own nullability; an untyped
    /// <c>NULL</c> arm carries no value to convert and is skipped. See
    /// <see cref="SqlType.ConversionPreservesEveryValue"/> for the rule and
    /// <see cref="ResultIsNullable"/> for the family it belongs to.
    /// </summary>
    private protected static bool ArmConversionIsNullable(Expression arm, SqlType promoted, NullabilityContext context) =>
        !IsUntypedNullLiteral(arm) && !SqlType.ConversionPreservesEveryValue(context.TypeOf(arm), promoted);

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
    /// <see cref="VariableReference"/> anywhere. Used by
    /// <c>STRING_SPLIT</c>'s <c>enable_ordinal</c> gate, which must reject
    /// every variable-bearing shape (real SQL Server: Msg 8748 —
    /// probe-confirmed the wrapped forms <c>CAST(@v AS int)</c>, <c>@v + 0</c>,
    /// and <c>(@v)</c> all reject, not just a bare <c>@v</c>) while still
    /// accepting constant expressions like <c>CAST(1 AS int)</c> / <c>(1)</c> /
    /// <c>1 + 0</c>. A runtime-eval probe can't see the variable (its slot is
    /// declared in the batch), so the detection is a static parse-tree walk.
    /// Default is <see langword="false"/>; the same common-container subclasses
    /// that override <see cref="VisitColumnReferences"/> (plus
    /// <see cref="VariableReference"/> itself) recurse here, so a
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
    /// True when this expression's value is fixed across a single table scan —
    /// it reads no column, so it evaluates to the same result for every row.
    /// Literals, variables, and parameters qualify; column references and
    /// anything reaching a column (directly or through an operand) don't. The
    /// default is a conservative <see langword="false"/>: an unrecognized node is
    /// assumed row-dependent, so only nodes that explicitly opt in (and prove
    /// their operands do too) are treated as constant. Consumed by the
    /// catalog-view predicate-pushdown detector
    /// (<c>Selection.Execution.cs::DetectCatalogPushdown</c>) to decide whether a
    /// WHERE equality's comparand can be evaluated once and pushed into a catalog
    /// row generator; a false negative only forgoes the optimization, so the safe
    /// direction is to under-claim.
    /// </summary>
    internal virtual bool IsRowIndependent => false;

    /// <summary>
    /// True when real SQL Server folds this expression to a constant at
    /// compile time — a literal, a parenthesization or unary minus over one,
    /// arithmetic / concatenation / <c>CAST</c> / <c>CONVERT</c> /
    /// <c>COALESCE</c> / <c>COLLATE</c> over such operands, a <c>CASE</c>
    /// whose conditions and arms are all constant, and a call to one of
    /// <see cref="ConstantFolding.IsFoldedBuiltIn"/>'s built-ins over constant
    /// arguments. Narrower than <see cref="IsRowIndependent"/>, which also
    /// admits variables and parameters. Consumed by the ORDER BY parsers'
    /// Msg 408 (statement) and Msg 5308 / 5309 (<c>OVER</c> /
    /// <c>WITHIN GROUP</c>) gates: real rejects a folded term, while a
    /// variable, a subquery, a UDF call and any function reading server or
    /// session state all sort fine (probe-confirmed — <c>@v + 1</c>,
    /// <c>(SELECT 1)</c>, <c>GETDATE()</c>, <c>@@SPID</c>, <c>ISNULL(NULL, 1)</c>
    /// are all accepted, <c>'x'</c> / <c>1 + 0</c> / <c>CAST(1 AS int)</c> /
    /// <c>COALESCE(NULL, 1)</c> / <c>ABS(-1)</c> / <c>LEN('abc')</c> are not).
    /// </summary>
    internal bool IsWrittenConstant => this.FoldedOverConstantArguments || this.IsStructuralConstant;

    /// <summary>
    /// Set by <see cref="ResolveBuiltIn"/> (and <c>CaseExpression.ParseCase</c>)
    /// when the node is a call real folds and every argument parsed inside it
    /// was itself a written constant. The decision is recorded at parse time
    /// because the operand fields live on ~50 unrelated built-in classes with
    /// no shared shape to walk.
    /// </summary>
    private protected bool FoldedOverConstantArguments;

    /// <summary>
    /// The structural half of <see cref="IsWrittenConstant"/>: node kinds that
    /// answer from their own operands rather than from a parse-time mark.
    /// The default is a conservative <see langword="false"/>, so a node that
    /// doesn't opt in is assumed non-constant and the gates under-reject
    /// rather than refusing a term real accepts.
    /// </summary>
    private protected virtual bool IsStructuralConstant => false;

    /// <summary>
    /// Whether this is a computation over non-NULL literals alone, which real
    /// types NOT NULL however it evaluates — an overflow or a divide by zero
    /// included, since a NULL operand would have propagated NULL rather than
    /// raising. Read by the <c>COUNT(&lt;NOT NULL expression&gt;)</c> reduction
    /// (see <c>Selection.ReduceConstantCounts</c>), which is the one place the
    /// distinction from the projection-metadata nullability
    /// (<see cref="ResultIsNullable"/>, where arithmetic claims nullable even
    /// over two literals) is observable. The default is a conservative
    /// <see langword="false"/>: a shape that doesn't opt in keeps evaluating
    /// its argument, which is what real does for everything nullable.
    /// </summary>
    internal virtual bool IsNonNullConstantComputation => false;

    /// <summary>
    /// Maximum shared nesting budget (see <see cref="ParserContext.NestingDepth"/>)
    /// before Msg 191. Grouped-expression parens and function-argument levels
    /// each cost <see cref="ParenNestingCost"/>; a scalar subquery costs
    /// <see cref="SubqueryNestingCost"/> (probe-confirmed 2026-07-18: on real
    /// SQL Server the three share one budget where a subquery level costs
    /// roughly six paren levels). Real's absolute limit is higher and
    /// stack-dependent (1015 nested parens succeed, 1016 fail; 168 nested
    /// subqueries succeed, 169 fail) but the simulator's parse frames are
    /// fatter — a 1 MB thread parses only ~1000 nested parens (Debug) before
    /// <see cref="Parse"/>'s stack probe would claim the same shape as Msg 8631.
    /// So the cap is set to 500 (paren limit 500, subquery limit ⌊500/6⌋ = 83),
    /// which keeps the structural Msg 191 firing with ~2× headroom on the
    /// tightest test configuration (a 1 MB Debug thread) and preserves the
    /// probed subquery ≈ 6× paren ratio; the lower absolute numbers are a
    /// documented divergence (see docs/claude/grammar.md). Function-call
    /// nesting alone has fatter frames still (~76 levels on a 1 MB Debug
    /// thread), so it reaches the stack probe (Msg 8631) before this cap —
    /// another documented divergence from real's Msg 191.
    /// </summary>
    private const int MaxNestingDepth = 500;

    /// <summary>Shared-budget cost of one grouped-expression paren level.</summary>
    private const int ParenNestingCost = 1;

    /// <summary>Shared-budget cost of one function-call argument-list level (same as a paren).</summary>
    private const int FunctionCallNestingCost = 1;

    /// <summary>
    /// Shared-budget cost of one scalar-subquery level — six paren-equivalents,
    /// fitting real SQL Server's probed 1015-paren / 168-subquery ratio.
    /// </summary>
    private const int SubqueryNestingCost = 6;

    /// <summary>
    /// Parses a subquery body with <c>NEXT VALUE FOR</c> refused inside it
    /// (real's Msg 11719 — see <see cref="ParserContext.NextValueForRejection"/>).
    /// A subquery is one of the constructs real's message names, and the
    /// refusal holds for the body's whole parse rather than only its clauses.
    /// </summary>
    internal static Selection ParseSubqueryRejectingNextValueFor(ParserContext context)
    {
        var saved = context.NextValueForRejection;
        context.NextValueForRejection = NextValueForScope.Nested;
        try
        {
            return Selection.Parse(context, depth: 1, outerTypeResolver: context.OuterTypeResolver);
        }
        finally
        {
            context.NextValueForRejection = saved;
        }
    }

    /// <summary>
    /// Parses a grouped expression starting at the opening <c>(</c>. Two
    /// shapes share the leading paren: a parenthesized expression
    /// (<c>(1 + 2)</c>, parses as <see cref="Parenthesized"/>) or a scalar
    /// subquery (<c>(SELECT col FROM t)</c>, parses as
    /// <see cref="ScalarSubqueryExpression"/>). Dispatch is by peeking the
    /// token immediately after <c>(</c>: a <c>SELECT</c> keyword routes to
    /// the subquery path; anything else falls through to the standard
    /// parenthesized form. Both leave the cursor on the closing <c>)</c>,
    /// matching the lookahead contract <see cref="Parse"/> expects. Each level
    /// charges the shared nesting budget before recursing (Msg 191 on
    /// overflow).
    /// </summary>
    private static Expression ParseGroupedExpression(ParserContext context)
    {
        context.MoveNextRequired();
        var isSubquery = context.Token is ReservedKeyword { Keyword: Keyword.Select };
        var cost = isSubquery ? SubqueryNestingCost : ParenNestingCost;
        context.NestingDepth += cost;
        if (context.NestingDepth > MaxNestingDepth)
            throw SimulatedSqlException.StatementNestedTooDeeply();
        try
        {
            if (isSubquery)
            {
                var inner = ParseSubqueryRejectingNextValueFor(context);
                context.SubqueriesParsed++;
                return inner.Schema.Length != 1
                    ? throw SimulatedSqlException.SubqueryNotIntroducedWithExists()
                    : context.Token is not Operator { Character: ')' }
                        ? throw SimulatedSqlException.SyntaxErrorNear(context)
                        : (Expression)new ScalarSubqueryExpression(inner);
            }

            return new Parenthesized(Expression.Parse(context));
        }
        finally
        {
            context.NestingDepth -= cost;
        }
    }

    /// <summary>
    /// Parses an ODBC escape sequence (cursor on the opening <c>{</c>), leaving
    /// the cursor on the closing <c>}</c>:
    /// <list type="bullet">
    /// <item><c>{d '…'}</c> / <c>{ts '…'}</c> → a <c>datetime</c> literal;
    /// <c>{t '…'}</c> → a <c>datetime</c> on the current date (matching real
    /// SQL Server's time-escape semantics); <c>{guid '…'}</c> →
    /// <c>uniqueidentifier</c>.</item>
    /// <item><c>{fn NAME(args)}</c> → the mapped built-in scalar function
    /// (ODBC-specific names like <c>UCASE</c>/<c>LCASE</c>/<c>LENGTH</c> are
    /// renamed to their T-SQL equivalents; names already matching a T-SQL
    /// built-in pass through).</item>
    /// </list>
    /// </summary>
    private static Expression ParseOdbcEscape(ParserContext context)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        if (context.GetNextRequired() is not Name escapeToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var escape = escapeToken.Value;

        if (collation.Equals(escape, "fn"))
        {
            if (context.GetNextRequired() is not Name functionNameToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var reference = Counted(context, new Reference(MapOdbcFunctionName(functionNameToken.Value, collation)));
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            var call = ParseCallArguments(reference, context);
            return context.GetNextRequired() is not Operator { Character: '}' }
                ? throw SimulatedSqlException.SyntaxErrorNear(context)
                : call;
        }

        // Typed-literal escapes: the payload is a single string literal.
        var targetType =
            collation.Equals(escape, "d") || collation.Equals(escape, "ts") || collation.Equals(escape, "t") ? (SqlType)SqlType.DateTime
            : collation.Equals(escape, "guid") ? SqlType.UniqueIdentifier
            : throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Literal literal)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var value = Expressions.Cast.ApplyCoercion(literal.Value, targetType, null);
        // The {t} time escape resolves to the current date plus the given time
        // (probe-confirmed against SQL Server 2025); a plain datetime coercion
        // of a time-only string lands on 1900-01-01, so re-home it onto today.
        if (collation.Equals(escape, "t") && !value.IsNull)
            value = Storage.SqlValue.FromDateTime(DateTime.UtcNow.Date + value.AsDateTime.TimeOfDay);
        return context.GetNextRequired() is not Operator { Character: '}' }
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : new Value(value);
    }

    /// <summary>
    /// Maps an ODBC <c>{fn NAME}</c> scalar-function name to its T-SQL
    /// equivalent. Only the ODBC-distinct spellings are renamed; a name that is
    /// already a T-SQL built-in (CONCAT, LEFT, CEILING, …) passes through
    /// unchanged and resolves normally. ODBC functions with no same-arity T-SQL
    /// rename (DAYOFWEEK / HOUR / MOD / TRUNCATE / CURDATE / …) are left
    /// unmapped and fall to the normal not-a-built-in path.
    /// </summary>
    private static string MapOdbcFunctionName(string name, Collation collation) =>
        collation.Equals(name, "UCASE") ? "UPPER"
        : collation.Equals(name, "LCASE") ? "LOWER"
        : collation.Equals(name, "LENGTH") ? "LEN"
        : collation.Equals(name, "LOCATE") ? "CHARINDEX"
        : collation.Equals(name, "REPEAT") ? "REPLICATE"
        : collation.Equals(name, "IFNULL") ? "ISNULL"
        : collation.Equals(name, "INSERT") ? "STUFF"
        : collation.Equals(name, "NOW") ? "GETDATE"
        : collation.Equals(name, "ATAN2") ? "ATN2"
        : collation.Equals(name, "DAYOFMONTH") ? "DAY"
        : name;

    /// <summary>
    /// Notes a nondeterministic built-in for the indexed-view battery
    /// (Msg 1949, whose text embeds the function name lower-cased). Only the
    /// closed set of built-ins whose value can differ between two evaluations
    /// of the same row matters here — that is exactly what makes a view's
    /// materialized contents unreproducible.
    /// </summary>
    private static void RecordNondeterministicBuiltIn(string name, ParserContext context)
    {
        if (context.IndexedViewShapeCollector is not { } shape || shape.NondeterministicFunction is not null)
            return;

        // The arms yield the lower-cased spelling real reports, so no
        // case conversion of the caller's text is needed (and CA1308's
        // normalization concern doesn't arise).
        Span<char> upper = stackalloc char[name.Length];
        var length = name.ToUpperInvariant(upper);
        shape.NondeterministicFunction = length switch
        {
            4 => upper[..length] is "RAND" ? "rand" : null,
            5 => upper[..length] is "NEWID" ? "newid" : null,
            7 => upper[..length] is "GETDATE" ? "getdate" : null,
            10 => upper[..length] is "GETUTCDATE" ? "getutcdate" : null,
            11 => upper[..length] is "SYSDATETIME" ? "sysdatetime" : null,
            14 => upper[..length] is "SYSUTCDATETIME" ? "sysutcdatetime" : null,
            15 => upper[..length] is "NEWSEQUENTIALID" ? "newsequentialid" : null,
            17 => upper[..length] is "SYSDATETIMEOFFSET" ? "sysdatetimeoffset" : null,
            _ => null,
        };
    }

    /// <summary>
    /// Notes a side-effecting built-in inside a function body being bound at
    /// <c>CREATE</c> — real's Msg 443, which embeds the name the way the
    /// catalog spells it (probe-confirmed lower-case for these three; the
    /// unmodeled <c>CRYPT_GEN_RANDOM</c> real reports as
    /// <c>'Crypt_Gen_Random'</c>). The date / time built-ins are deterministic
    /// enough for real to allow them here even though the indexed-view battery
    /// above rejects them, so the two sets deliberately differ.
    /// </summary>
    private static void RecordSideEffectingBuiltIn(string name, ParserContext context)
    {
        if (context.Batch.FunctionBodyShape is null)
            return;

        Span<char> upper = stackalloc char[name.Length];
        var length = name.ToUpperInvariant(upper);
        var operatorName = length switch
        {
            4 => upper[..length] is "RAND" ? "rand" : null,
            5 => upper[..length] is "NEWID" ? "newid" : null,
            15 => upper[..length] is "NEWSEQUENTIALID" ? "newsequentialid" : null,
            _ => null,
        };
        if (operatorName is not null)
            FunctionBodyShape.NoteSideEffect(context.Batch, operatorName, FunctionBodyShape.BuiltInOperatorState);
    }

    private static Expression ResolveBuiltIn(string name, ParserContext context)
    {
        Span<char> uppercaseName = stackalloc char[name.Length];
        RecordNondeterministicBuiltIn(name, context);
        RecordSideEffectingBuiltIn(name, context);
        _ = name.ToUpperInvariant(uppercaseName);
        if (!ConstantFolding.IsFoldedBuiltIn(uppercaseName))
            return ResolveBuiltInCore(uppercaseName, name, context);

        // A folded call's arguments parse inside its own frame: the flag comes
        // back false the moment any of them isn't a written constant.
        var savedFoldableArguments = context.FoldableArguments;
        context.FoldableArguments = true;
        try
        {
            var call = ResolveBuiltInCore(uppercaseName, name, context);
            if (context.FoldableArguments)
                call.FoldedOverConstantArguments = true;
            return call;
        }
        finally
        {
            context.FoldableArguments = savedFoldableArguments;
        }
    }

    /// <summary>
    /// Dispatches an uppercased built-in name to its expression parser,
    /// raising Msg 195 when no built-in answers to it.
    /// </summary>
    private static Expression ResolveBuiltInCore(ReadOnlySpan<char> uppercaseName, string name, ParserContext context) =>
        uppercaseName.Length switch
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
                "LEAST" => new GreatestLeast(context, isLeast: true),
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
                "FILE_ID" => new FileId(context, extended: false),
                "GETDATE" => new CurrentTimeFunction(context, CurrentTimeKind.GetDate),
                "GET_BIT" => new GetBit(context),
                "RADIANS" => new Radians(context),
                "REPLACE" => new Replace(context),
                "REVERSE" => new Reverse(context),
                "SET_BIT" => new SetBit(context),
                "SOUNDEX" => new Soundex(context),
                "TEXTPTR" => new TextPointer(context),
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
                "GREATEST" => new GreatestLeast(context, isLeast: false),
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
                "EVENTDATA" => new EventDataFunction(context),
                "FILE_IDEX" => new FileId(context, extended: true),
                "FILE_NAME" => new FileNameLookup(context),
                "HASHBYTES" => new HashBytes(context),
                "HOST_NAME" => new HostName(context),
                "INDEX_COL" => new IndexCol(context),
                "ISNUMERIC" => new IsNumeric(context),
                "IS_MEMBER" => new RoleMemberCheck(context, serverScope: false),
                "OBJECT_ID" => new ObjectId(context),
                "PARSENAME" => new ParseName(context),
                "QUOTENAME" => new QuoteName(context),
                "REPLICATE" => new Replicate(context),
                "SCHEMA_ID" => new SchemaId(context),
                "SUBSTRING" => new Substring(context),
                "SUSER_SID" => new SUserSid(context),
                "TEXTVALID" => new TextValid(context),
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
                "SID_BINARY" => new SidBinary(context),
                "STATS_DATE" => new StatsDate(context),
                "STRING_AGG" => AggregateExpression.Parse(context, AggregateKind.StringAgg),
                "SUSER_NAME" => new SUserName(context, isSidVariant: false),
                "XACT_STATE" => new XactState(context),
                _ => null
            },
            11 => uppercaseName switch
            {
                "CERTENCODED" => new CertificateFunction(context, isPrivateKey: false),
                "DATE_BUCKET" => new DateBucket(context),
                "ERROR_STATE" => new ErrorStateFunction(context),
                "FIRST_VALUE" => WindowExpression.ParseFirstValue(context),
                "GETANSINULL" => new GetAnsiNull(context),
                "GROUPING_ID" => new GroupingId(context),
                "JSON_MODIFY" => new JsonModify(context),
                "JSON_OBJECT" => new JsonObject(context),
                "OBJECT_NAME" => new ObjectName(context),
                "PERMISSIONS" => new Permissions(context),
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
                "FILEGROUP_ID" => new FilegroupId(context),
                "FILEPROPERTY" => new FileProperty(context),
                "HAS_DBACCESS" => new HasDbAccess(context),
                "PERCENT_RANK" => WindowExpression.ParsePercentRank(context),
                "REGEXP_COUNT" => RegexpScalar.ParseCall(context, RegexpScalarKind.Count),
                "REGEXP_INSTR" => RegexpScalar.ParseCall(context, RegexpScalarKind.Instr),
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
                "IS_ROLEMEMBER" => new RoleMemberCheck(context, serverScope: false),
                "JSON_ARRAYAGG" => AggregateExpression.Parse(context, AggregateKind.JsonArrayAgg),
                "LOGINPROPERTY" => new LoginProperty(context),
                "REGEXP_SUBSTR" => RegexpScalar.ParseCall(context, RegexpScalarKind.Substr),
                "STRING_ESCAPE" => new StringEscape(context),
                "TIMEFROMPARTS" => new DatePartsBuilder(context, DatePartsBuilderKind.TimeFromParts),
                _ => null
            },
            14 => uppercaseName switch
            {
                "CERTPRIVATEKEY" => new CertificateFunction(context, isPrivateKey: true),
                "COLUMNPROPERTY" => new ColumnProperty(context),
                "ERROR_SEVERITY" => new ErrorSeverityFunction(context),
                "FILEGROUP_NAME" => new FilegroupName(context),
                "JSON_OBJECTAGG" => AggregateExpression.Parse(context, AggregateKind.JsonObjectAgg),
                "OBJECTPROPERTY" => new ObjectProperty(context),
                "ORIGINAL_LOGIN" => new OriginalLogin(context),
                "REGEXP_REPLACE" => RegexpScalar.ParseCall(context, RegexpScalarKind.Replace),
                "SCOPE_IDENTITY" => new LastIdentityExpression(context),
                "SERVERPROPERTY" => new ServerProperty(context),
                "SYSUTCDATETIME" => new CurrentTimeFunction(context, CurrentTimeKind.SysUtcDateTime),
                _ => null
            },
            15 => uppercaseName switch
            {
                "BINARY_CHECKSUM" => new Checksum(context, isBinary: true),
                "COLUMNS_UPDATED" => new ColumnsUpdatedFunction(context),
                "ERROR_PROCEDURE" => new ErrorProcedureFunction(context),
                "NEWSEQUENTIALID" => new NewSequentialId(context),
                "PERCENTILE_CONT" => WindowExpression.ParsePercentile(context, WindowKind.PercentileCont),
                "PERCENTILE_DISC" => WindowExpression.ParsePercentile(context, WindowKind.PercentileDisc),
                "SESSIONPROPERTY" => new SessionProperty(context),
                "SESSION_CONTEXT" => new SessionContext(context),
                _ => null
            },
            16 => uppercaseName switch
            {
                "ASSEMBLYPROPERTY" => new AssemblyProperty(context),
                "IS_SRVROLEMEMBER" => new RoleMemberCheck(context, serverScope: true),
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
                "FILEGROUPPROPERTY" => new FilegroupProperty(context),
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
            20 => uppercaseName switch
            {
                "SQL_VARIANT_PROPERTY" => new SqlVariantProperty(context),
                "XML_SCHEMA_NAMESPACE" => new XmlSchemaNamespaceFunction(context),
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
                "FULLTEXTCATALOGPROPERTY" => new FullTextCatalogProperty(context),
                "FULLTEXTSERVICEPROPERTY" => new FullTextServiceProperty(context),
                _ => null
            },
            34 => uppercaseName switch
            {
                "GET_FILESTREAM_TRANSACTION_CONTEXT" => new GetFilestreamTransactionContext(context),
                _ => null
            },
            _ => (Expression?)null
        } ?? throw SimulatedSqlException.UnrecognizedBuiltInFunction(name);

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
        // hint; the sequence-advance pattern across rows is unchanged. The
        // body still goes through the window parser rather than a token skip
        // so its ORDER BY reaches the Msg 5308 / 5309 constant gate — the
        // message real reports here names NEXT VALUE FOR by name. Peek for
        // OVER via a save/restore so the outer loop's GetNextOptional resumes
        // at the correct token whether OVER is present or not.
        var overCheckpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is not ReservedKeyword { Keyword: Keyword.Over })
        {
            context.RestoreCheckpoint(overCheckpoint);
            return nvf;
        }
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        _ = WindowExpression.ParseWindowBody(context);
        return nvf;
    }
}
