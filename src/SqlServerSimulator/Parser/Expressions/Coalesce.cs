using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>COALESCE(expr1, expr2, ...)</c>: returns the first non-NULL
/// argument; NULL only if all arguments are NULL. Result type is the
/// highest-precedence type among the operands per
/// <see cref="SqlType.Promote"/>; NULL operands contribute their type for
/// promotion purposes. EF Core emits this around aggregate
/// expressions to substitute a default for empty-input NULLs.
/// </summary>
internal sealed class Coalesce : Expression
{
    private readonly Expression[] arguments;

    /// <summary>
    /// Whether a leading argument real folds to a <em>non-NULL</em> constant
    /// decides the call, which makes the whole COALESCE a constant however the
    /// arguments behind it read — a column and an aggregate alike. Probed on
    /// real's own constant classification: <c>ORDER BY COALESCE(61, col)</c>
    /// and <c>ORDER BY COALESCE(61, MAX(col))</c> are both Msg 408 where
    /// <c>ORDER BY COALESCE(col, 61)</c> sorts. Settled while parsing because
    /// that is the only place the fold has a context to run against.
    /// </summary>
    private readonly bool decidedByLeadingValue;

    private SqlType? cachedResultType;

    /// <summary>
    /// The argument real settles the call on while compiling, or null (see
    /// <see cref="SettleArgument"/> and <see cref="Expression.SettledArmType"/>).
    /// </summary>
    private readonly Expression? settledArm;

    public Coalesce(ParserContext context)
    {
        // Where each argument's aggregate registrations start, so the ones a
        // leading constant decides away stop being evaluated (real drops them
        // with the argument: `SELECT COALESCE(61, SUM(7 / 0))` answers 61
        // there, while `SELECT col, COALESCE(61, SUM(other))` is still
        // Msg 8120 — so the aggregate stays registered).
        List<int> aggregateBounds = [context.AggregateCollector?.Count ?? 0];
        var firstArgument = context.Token;
        Token? lastComma = null;
        List<Expression> args = [Expression.Parse(context)];
        while (context.Token is Tokens.Operator { Character: ',' })
        {
            aggregateBounds.Add(context.AggregateCollector?.Count ?? 0);
            lastComma = context.Token;
            context.MoveNextRequired();
            args.Add(Expression.Parse(context));
        }
        // Real binds COALESCE as the CASE it stands for, every argument but
        // the last twice: once tested, once returned.
        if (context.Batch.BindErrors is { } report && report.Covers(firstArgument) && lastComma is not null)
            report.Echo(firstArgument!.StartIndex, lastComma.StartIndex, lastComma.StartIndex);
        aggregateBounds.Add(context.AggregateCollector?.Count ?? 0);
        if (args.Count < 2)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (args.TrueForAll(IsUntypedNullLiteral))
            throw SimulatedSqlException.AllCoalesceArgumentsAreNull();
        this.arguments = [.. args];
        this.settledArm = SettleArgument(this.arguments, context);
        // A constant-NULL argument drops out of the walk; the first constant
        // non-NULL one answers for the call. A fold that raises, or an argument
        // real can't fold, stops the walk — the arguments behind it are then
        // live.
        for (var i = 0; i < this.arguments.Length - 1; i++)
        {
            if (!ConstantFolding.TryFold(this.arguments[i], context, out var folded))
                return;
            if (!folded.IsNull)
            {
                this.decidedByLeadingValue = true;
                if (context.AggregateCollector is { } collector)
                {
                    for (var j = aggregateBounds[i + 1]; j < aggregateBounds[^1]; j++)
                        collector[j].OperandUnreachable = true;
                }
                return;
            }
        }
    }

    /// <summary>
    /// The argument real settles a <c>COALESCE</c> on while compiling, in one
    /// of two ways (probed 2026-10-06 against SQL Server 2025). A call over
    /// constants alone folds whole, to its first non-NULL argument, so
    /// <c>COALESCE(CAST('a' AS char(5)), CAST('b' AS varchar(10)))</c> is
    /// <c>char(5)</c>. Otherwise each leading <c>IS NOT NULL</c> test of the
    /// <c>CASE</c> it stands for folds only where the argument is a NULL
    /// constant, which drops out, or one the metadata types NOT NULL — a
    /// literal, signed, parenthesized or concatenated with another — which
    /// settles the call: <c>COALESCE('ab', col)</c> is <c>varchar(2)</c>,
    /// while a <c>CAST</c>, a function or arithmetic leaves the test standing
    /// and the arguments unified. Null when nothing settles.
    /// </summary>
    private static Expression? SettleArgument(Expression[] arguments, ParserContext context)
    {
        var firstNonNull = -1;
        for (var i = 0; i < arguments.Length; i++)
        {
            if (!ConstantFolding.TryFold(arguments[i], context, out var folded))
            {
                firstNonNull = -2;
                break;
            }
            if (firstNonNull == -1 && !folded.IsNull)
                firstNonNull = i;
        }
        if (firstNonNull != -2)
            return firstNonNull >= 0 ? arguments[firstNonNull] : arguments[^1];

        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (IsNullConstant(arguments[i]))
                continue;
            return IsNotNullLiteral(arguments[i], context) ? arguments[i] : null;
        }
        return arguments[^1];
    }

    /// <summary>A literal real's metadata types NOT NULL, as <see cref="SettleArgument"/> reads one.</summary>
    private static bool IsNotNullLiteral(Expression argument, ParserContext context) => argument switch
    {
        Value value => value.IsLiteral && !value.Constant.IsNull,
        Parenthesized parenthesized => IsNotNullLiteral(parenthesized.Wrapped, context),
        Negate negate => IsNotNullLiteral(negate.Operand, context),
        Add concatenation => concatenation.BothOperandsMatch(operand => IsNotNullLiteral(operand, context))
            && ConstantFolding.TryFold(concatenation, context, out var joined) && SqlType.IsStringCategory(joined.Type),
        _ => false,
    };

    internal override bool ParallelSafe => AllParallelSafe(this.arguments);

    public override SqlValue Run(RuntimeContext runtime)
    {
        SqlValue value = default;
        for (var i = 0; i < this.arguments.Length; i++)
        {
            value = this.arguments[i].Run(runtime);
            if (!value.IsNull)
                return this.cachedResultType is { } target && value.Type != target ? Cast.CoerceArm(value, target, runtime.Batch) : value;
        }
        return value; // all NULL — return the last (typed-NULL) result
    }

    // Untyped-NULL arguments yield to the typed arguments (so
    // `COALESCE(NULL, 'z')` is varchar, not the int that a bare NULL's
    // placeholder type would poison the promote with) and integer-literal
    // arguments size by digit count against a decimal sibling — both handled
    // by the shared PromoteValueArms seam.
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        this.cachedResultType = SettledArmType(this.settledArm, PromoteValueArms(this.arguments, batch, resolveColumnType), batch, resolveColumnType);
        this.namingArm = FirstDecimalArm(this.arguments, batch, resolveColumnType);
        return this.cachedResultType;
    }

    private Expression? namingArm;

    internal override bool ResultReportsNumeric => this.namingArm?.ResultReportsNumeric ?? false;

    /// <summary>
    /// COALESCE takes its nullability from the CASE it desugars to —
    /// <c>CASE WHEN a IS NOT NULL THEN a … ELSE last END</c> — so it is NOT
    /// NULL when every argument is, where <c>ISNULL</c> needs only one of its
    /// two (the classic ISNULL-vs-COALESCE metadata quirk:
    /// <c>COALESCE(nullable_col, 0)</c> is nullable, <c>ISNULL(nullable_col, 0)</c>
    /// is not). Real folds the <c>IS NOT NULL</c> tests first, which drops a
    /// constant-NULL argument out of the walk and lets the argument the call
    /// settles on (<see cref="SettleArgument"/>) answer for the whole call —
    /// <c>COALESCE(NULL, 5)</c> and <c>COALESCE(5, nullable_col)</c> both
    /// project NOT NULL, while <c>COALESCE(CAST(5 AS int), nullable_col)</c>
    /// and <c>COALESCE(5 + 0, 7)</c> are nullable on the arm's own account
    /// (probe-confirmed against SQL Server 2025).
    /// <para>Each surviving argument additionally answers for the conversion
    /// the arm unification put on it, so <c>COALESCE(&lt;decimal(9, 2) col&gt;, 0)</c>
    /// is nullable on the int literal's account alone — see
    /// <see cref="Expression.ArmConversionIsNullable"/>.</para>
    /// </summary>
    internal override bool ResultIsNullable(NullabilityContext context)
    {
        var promoted = context.TypeOf(this);
        if (FoldsIntoMaxConstant(this.settledArm, this.arguments, promoted, context))
            return true;
        if (this.settledArm is { } settled)
            return settled.ResultIsNullable(context) || ArmConversionIsNullable(settled, promoted, context);
        for (var i = 0; i < this.arguments.Length - 1; i++)
        {
            if (IsNullConstant(this.arguments[i]))
                continue;
            if (this.arguments[i].ResultIsNullable(context) || ArmConversionIsNullable(this.arguments[i], promoted, context))
                return true;
        }

        // The last argument is the desugared CASE's ELSE: it contributes its
        // own nullability rather than an IS NOT NULL test.
        return this.arguments[^1].ResultIsNullable(context) || ArmConversionIsNullable(this.arguments[^1], promoted, context);
    }

    // The desugared CASE's first constant non-NULL test is TRUE, so every
    // argument behind that one is unreachable — whatever precedes it:
    // `COALESCE(b, 5, a)` never reads `a`.
    internal override void AddFoldedAwayOperands(NullabilityContext context, HashSet<ExpressionNode> foldedAway)
    {
        for (var i = 0; i < this.arguments.Length - 1; i++)
        {
            if (!context.TryFold(this.arguments[i], out var folded) || folded.IsNull)
                continue;
            for (var j = i + 1; j < this.arguments.Length; j++)
                _ = foldedAway.Add(this.arguments[j]);
            return;
        }
    }

    internal override string DebugDisplay() => $"COALESCE({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";

    internal override void Describe(NodeShape shape) => shape.Children(this.arguments);

    // Real desugars COALESCE to a CASE and folds an all-literal one, so
    // `ORDER BY COALESCE(NULL, 1)` is Msg 408 while `ORDER BY ISNULL(NULL, 1)`
    // (a runtime call) sorts — probe-confirmed. A leading constant non-NULL
    // argument settles the call on its own, so the arguments behind it don't
    // have to be constant (see decidedByLeadingValue).
    private protected override bool IsStructuralConstant
    {
        get
        {
            if (this.decidedByLeadingValue)
                return true;
            foreach (var argument in this.arguments)
            {
                if (!argument.IsWrittenConstant)
                    return false;
            }
            return true;
        }
    }
}
