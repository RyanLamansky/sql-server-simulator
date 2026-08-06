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
/// so it takes a lone primary and <c>~2 * 3</c> is <c>(~2) * 3</c> — but a
/// sign is allowed to be that primary, and a sign reaches for the whole
/// multiplicative chain, so <c>~ + 2 * 3</c> is <c>~(2 * 3)</c> = -7 and
/// <c>~ - 2 * 3</c> is <c>~(-(2 * 3))</c> = 5 (probe-confirmed). Whatever the
/// operand parse produced is therefore the operand as written.
/// </summary>
internal sealed class BitwiseNot(Expression operand) : Expression
{
    public override SqlValue Run(RuntimeContext runtime) => Compute(operand.Run(runtime));

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
        var type = operand.GetSqlType(batch, resolveColumnType);
        return type.Category == SqlTypeCategory.Integer
            ? type
            : throw SimulatedSqlException.OperandDataTypeInvalid(type, "'~'");
    }

    internal override bool ResultIsNullable(NullabilityContext context) =>
        operand.ResultIsNullable(context);

    internal override void VisitColumnReferencesCore(ColumnReferenceVisitor visit) =>
        operand.VisitColumnReferences(visit);

    internal override bool ContainsVariableReference => operand.ContainsVariableReference;

    internal override string DebugDisplay() => $"~{operand.DebugDisplay()}";
}
