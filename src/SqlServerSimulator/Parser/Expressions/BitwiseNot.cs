using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Unary <c>~</c> (bitwise NOT): one's-complement of an integer-category
/// operand. Probe-confirmed against SQL Server 2025 (2026-07-15): the result
/// keeps the operand's exact type (<c>~CAST(0 AS tinyint)</c> → tinyint 255,
/// <c>~CAST(5 AS bigint)</c> → bigint -6, <c>~1</c> → int -2), <c>bit</c>
/// flips (<c>~CAST(1 AS bit)</c> → 0, <c>~CAST(0 AS bit)</c> → 1), NULL
/// propagates, and any non-integer operand — decimal / numeric / float /
/// money / string / binary — raises Msg 8117 with no coercion attempt.
/// <c>~</c> is SQL Server's highest-precedence operator (above <c>* / %</c>),
/// so it binds to the leftmost primary; see
/// <see cref="TwoSidedExpression.SinkUnaryPrefixToLeftmostLeaf"/> for how the
/// parser re-homes it after the operand parse consumes a whole chain.
/// </summary>
internal sealed class BitwiseNot : Expression
{
    private readonly Expression operand;

    private BitwiseNot(Expression operand) => this.operand = operand;

    /// <summary>
    /// Wraps <paramref name="operand"/> in a <see cref="BitwiseNot"/>. When
    /// the operand parsed as a binary chain (looser-binding than <c>~</c>),
    /// the prefix sinks onto the chain's leftmost leaf so <c>~2 * 3</c> means
    /// <c>(~2) * 3</c>.
    /// </summary>
    public static Expression Create(Expression operand) =>
        operand is TwoSidedExpression twoSided
            ? twoSided.SinkUnaryPrefixToLeftmostLeaf(static leaf => new BitwiseNot(leaf))
            : new BitwiseNot(operand);

    public override SqlValue Run(RuntimeContext runtime) => Compute(this.operand.Run(runtime));

    private static SqlValue Compute(SqlValue v) =>
        v.Type.Category != SqlTypeCategory.Integer ? throw SimulatedSqlException.OperandDataTypeInvalid(v.Type, "'~'")
        : v.IsNull ? SqlValue.Null(v.Type)
        : v.Type == SqlType.Bit ? SqlValue.FromBoolean(!v.AsBoolean)
        : v.Type == SqlType.TinyInt ? SqlValue.FromByte((byte)~v.AsByte)
        : v.Type == SqlType.SmallInt ? SqlValue.FromInt16((short)~v.AsInt16)
        : v.Type == SqlType.Int32 ? SqlValue.FromInt32(~v.AsInt32)
        : SqlValue.FromInt64(~v.AsInt64);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var type = this.operand.GetSqlType(batch, resolveColumnType);
        return type.Category == SqlTypeCategory.Integer
            ? type
            : throw SimulatedSqlException.OperandDataTypeInvalid(type, "'~'");
    }

    internal override bool ResultIsNullable(NullabilityContext context) =>
        this.operand.ResultIsNullable(context);

    internal override void VisitColumnReferencesCore(ColumnReferenceVisitor visit) =>
        this.operand.VisitColumnReferences(visit);

    internal override bool ContainsVariableReference => this.operand.ContainsVariableReference;

    internal override string DebugDisplay() => $"~{this.operand.DebugDisplay()}";
}
