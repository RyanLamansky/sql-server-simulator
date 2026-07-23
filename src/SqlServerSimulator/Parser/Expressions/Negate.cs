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
/// The value is still computed via the shared <c>0 - x</c> arithmetic (so
/// string coercion, date rejection, NULL propagation, and overflow all match
/// the subtraction path), then re-boxed to the preserved result type. A negated
/// integer literal stays a digit-count literal for decimal-arithmetic sizing —
/// see <see cref="Expression.IntegerLiteralDigits"/>.
/// </summary>
internal sealed class Negate(Expression operand) : Expression
{
    /// <summary>The negated operand, exposed so
    /// <see cref="Expression.IntegerLiteralDigits"/> can see through a unary
    /// minus to an integer literal.</summary>
    internal readonly Expression Operand = operand;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.Operand.Run(runtime);
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

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) =>
        this.Operand.ResultIsNullable(resolveColumnNullable);

    internal override bool ResultReportsNumeric => this.Operand.ResultReportsNumeric;

    internal override void VisitColumnReferences(Action<MultiPartName> visit) =>
        this.Operand.VisitColumnReferences(visit);

    internal override bool ContainsVariableReference => this.Operand.ContainsVariableReference;

    internal override bool IsRowIndependent => this.Operand.IsRowIndependent;

    internal override string DebugDisplay() => $"-{this.Operand.DebugDisplay()}";
}
