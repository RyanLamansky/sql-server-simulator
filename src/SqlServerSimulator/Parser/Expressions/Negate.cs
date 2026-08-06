using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Unary minus. SQL Server negates a numeric while <b>preserving the operand's
/// own type</b> — not by evaluating <c>0 - x</c> (which would inflate an
/// exact-numeric's precision by one through the additive rule and re-type the
/// operand against a fixed <c>int</c> zero). Probe-confirmed against SQL Server
/// 2025 (2026-07-21):
/// <list type="bullet">
/// <item><c>-1.1</c> → <c>numeric(2, 1)</c> (same as <c>1.1</c>);
/// <c>-CAST(1.5 AS decimal(5, 3))</c> → <c>decimal(5, 3)</c>.</item>
/// <item><c>-1</c> → <c>int</c>; <c>-CAST(1 AS bigint)</c> → <c>bigint</c>;
/// <c>-CAST(1 AS smallint)</c> → <c>smallint</c>; but <c>-CAST(1 AS tinyint)</c>
/// widens to <c>smallint</c> (tinyint is unsigned, so negation needs a signed
/// type), and <c>-CAST(1 AS bit)</c> raises Msg 8117.</item>
/// <item><c>-$1.00</c> → <c>money</c>; <c>-CAST(1 AS real)</c> → <c>real</c>
/// (not <c>float</c>); <c>-CAST(1 AS float)</c> → <c>float</c>.</item>
/// </list>
/// The value is computed via the shared <c>0 - x</c> arithmetic (so string
/// coercion, date rejection, NULL propagation, and overflow all match the
/// subtraction path), then re-boxed to the preserved result type — except for
/// <c>float</c> / <c>real</c>, which flip the IEEE 754 sign bit instead so a
/// negated zero stays negative. A negated integer literal stays a digit-count
/// literal for decimal-arithmetic sizing — see
/// <see cref="Expression.IntegerLiteralDigits"/>.
/// </summary>
internal sealed class Negate(Expression operand) : Expression
{
    /// <summary>The negated operand, exposed so
    /// <see cref="Expression.IntegerLiteralDigits"/> can see through a unary
    /// minus to an integer literal.</summary>
    internal readonly Expression Operand = operand;

    /// <summary>
    /// Builds the unary-minus node, folding a negated integer <b>literal</b>
    /// whose negation lands back inside <c>int</c> to a plain <c>int</c>
    /// constant. Real types a bare integer literal past int's range
    /// <c>numeric(digit_count, 0)</c>, yet types the folded constant by its
    /// resulting value, so <c>-2147483648</c> — and <c>-(2147483648)</c>,
    /// parentheses included — is <c>int</c> while <c>-3000000000</c> stays
    /// <c>numeric(10, 0)</c>. <c>2147483648</c> is the only magnitude where
    /// that applies, since int's range is asymmetric by exactly one. The fold
    /// is literal-only: <c>-@d</c> over a <c>numeric(10, 0)</c> variable
    /// holding the same value stays <c>numeric(10, 0)</c>.
    /// </summary>
    internal static Expression Of(Expression operand) =>
        Unwrap(operand) is Value { IsLiteral: true, Constant: { Type: DecimalSqlType { scale: 0 }, IsNull: false } constant }
            && constant.AsDecimal38 == Decimal38.FromInt64(-(long)int.MinValue)
            ? new Value(SqlValue.FromInt32(int.MinValue), integerLiteralDigitCount: 0)
            : new Negate(operand);

    /// <summary>Peels the parentheses real's own constant fold sees through.</summary>
    private static Expression Unwrap(Expression expression) =>
        expression is Parenthesized p ? Unwrap(p.Wrapped) : expression;

    internal override bool ParallelSafe => this.Operand.ParallelSafe;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.Operand.Run(runtime);

        // float / real negate by flipping the IEEE 754 sign bit, which the
        // shared `0 - x` path does not reproduce: subtraction folds the two
        // zeros together (0.0 - 0.0 is +0.0 under round-to-nearest), so the
        // negative zero real reports for `-CAST(0 AS real)` would be lost.
        // Every other operand type keeps the additive path, whose typing,
        // string coercion, date rejection and overflow rules match real's.
        if (!value.IsNull && value.Type.Category == SqlTypeCategory.Approximate)
        {
            return value.Type == SqlType.Float
                ? SqlValue.FromDouble(-value.AsDouble)
                : SqlValue.FromSingle(-value.AsSingle);
        }

        var resultType = PreservedResultType(value.Type);
        var raw = Subtract.NegateViaZero(value);
        return resultType is null ? raw
            : raw.IsNull ? SqlValue.Null(resultType)
            : raw.CoerceTo(resultType);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var operandType = this.Operand.GetSqlType(batch, resolveColumnType);
        // The `0 - x` fallback typing already preserves money / smallmoney /
        // float / int / bigint and rejects strings/dates exactly as real does;
        // only the cases below diverge from that additive result and need a
        // preserved override.
        return PreservedResultType(operandType)
            ?? SqlType.PromoteForArithmetic(SqlType.Int32, operandType, '-');
    }

    /// <summary>
    /// The result type when unary minus must preserve or adjust the operand's
    /// type rather than take the <c>0 - x</c> additive result. Returns
    /// <see langword="null"/> for operands the additive path already types
    /// correctly (int / bigint / money / smallmoney / float / string / …).
    /// <c>bit</c> raises Msg 8117 (no arithmetic negation), matching real.
    /// </summary>
    private static SqlType? PreservedResultType(SqlType operandType) =>
        operandType is DecimalSqlType ? operandType
        : operandType == SqlType.Real ? SqlType.Real
        : operandType == SqlType.SmallInt ? SqlType.SmallInt
        : operandType == SqlType.TinyInt ? SqlType.SmallInt
        : operandType == SqlType.Bit ? throw SimulatedSqlException.OperandDataTypeInvalid(SqlType.Bit, "minus")
        : null;

    // Unary minus is arithmetic, so real projects it nullable even over a NOT
    // NULL operand (`-col` is nullable where `+col` is not) — the exception is
    // the constant real folds away first, which makes `-1` and `-(1)` NOT NULL.
    internal override bool ResultIsNullable(NullabilityContext context) =>
        !context.TryFold(this, out var folded) || folded.IsNull;

    internal override bool ResultReportsNumeric => this.Operand.ResultReportsNumeric;

    internal override void VisitColumnReferencesCore(ColumnReferenceVisitor visit) =>
        this.Operand.VisitColumnReferences(visit);

    internal override bool ContainsVariableReference => this.Operand.ContainsVariableReference;

    internal override bool IsRowIndependent => this.Operand.IsRowIndependent;

    private protected override bool IsStructuralConstant => this.Operand.IsWrittenConstant;

    internal override bool IsNonNullConstantComputation => this.Operand.IsNonNullConstantComputation;

    internal override string DebugDisplay() => $"-{this.Operand.DebugDisplay()}";
}
