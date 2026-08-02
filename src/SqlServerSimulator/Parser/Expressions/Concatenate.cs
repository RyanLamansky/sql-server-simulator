using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The ANSI <c>||</c> string-concatenation operator (SQL Server 2025). Unlike
/// <c>+</c> — which concatenates only when both operands are already string /
/// binary and otherwise attempts arithmetic — <c>||</c> is always
/// concatenation: it requires at least one string operand and implicitly
/// converts the other operand to a string (probe-confirmed against SQL Server
/// 2025: <c>'a' || 1</c> → <c>'a1'</c>, whereas <c>'a' + 1</c> raises Msg 245).
/// Operands that can't participate — two non-strings (<c>1 || 2</c>), a
/// <c>binary</c>, or a <c>bit</c> — raise Msg 402 "incompatible in the concat
/// operator". NULL propagates (default <c>CONCAT_NULL_YIELDS_NULL ON</c>), and
/// the result is <c>nvarchar</c> when either operand is a national string, else
/// <c>varchar</c>.
/// </summary>
internal sealed class Concatenate(Expression left, Expression right) : Expression
{
    public override SqlValue Run(RuntimeContext runtime)
    {
        var leftValue = left.Run(runtime);
        var rightValue = right.Run(runtime);
        var resultType = ResolveResultType(leftValue.Type, rightValue.Type);
        return leftValue.IsNull || rightValue.IsNull
            ? SqlValue.Null(resultType)
            : SqlValue.FromString(resultType, Stringify(leftValue, resultType) + Stringify(rightValue, resultType));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResolveResultType(
            left.GetSqlType(batch, resolveColumnType),
            right.GetSqlType(batch, resolveColumnType));

    /// <summary>
    /// Settles the <c>nvarchar</c> / <c>varchar</c> result type (with resolved
    /// collation) for a <c>||</c> pair, or raises Msg 402 for a pair the concat
    /// operator can't join.
    /// </summary>
    private static SqlType ResolveResultType(SqlType leftType, SqlType rightType)
    {
        var leftIsString = leftType.Category == SqlTypeCategory.String;
        var rightIsString = rightType.Category == SqlTypeCategory.String;
        if (!(leftIsString || rightIsString) || !IsConcatCompatible(leftType) || !IsConcatCompatible(rightType))
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(leftType, rightType, "concat");

        var national = IsNationalString(leftType) || IsNationalString(rightType);
        SqlType baseType = national ? SqlType.NVarchar : SqlType.Varchar;

        // Real names this operator `concat`, where the `+` form's identical
        // Msg 457 / 456 / 468 say `add` (probe-confirmed both ways).
        if (leftIsString && rightIsString)
            return UnresolvedCollation.Settle(baseType, leftType, rightType, "concat");
        var stringType = leftIsString ? leftType : rightType;
        return baseType.WithCollation(stringType.Collation!, stringType.Coercibility);
    }

    private static string Stringify(SqlValue value, SqlType resultType) =>
        SqlType.IsStringCategory(value.Type) ? value.AsString : value.CoerceTo(resultType).AsString;

    /// <summary>
    /// Whether a type can participate in <c>||</c>: any non-LOB string, any
    /// exact / approximate numeric except <c>bit</c>, money, date/time, and
    /// <c>uniqueidentifier</c>. Binary, <c>bit</c>, <c>text</c> / <c>ntext</c>,
    /// and everything else raise Msg 402 (probe-confirmed:
    /// <c>0x41 || 'b'</c> and <c>'a' || CAST(1 AS bit)</c> both fail).
    /// </summary>
    private static bool IsConcatCompatible(SqlType type) => type.Category switch
    {
        SqlTypeCategory.String => !type.IsLob,
        SqlTypeCategory.Integer => type != SqlType.Bit,
        SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate or SqlTypeCategory.DateTime => true,
        _ => type == SqlType.UniqueIdentifier,
    };

    private static bool IsNationalString(SqlType type) =>
        type is NVarcharSqlType or NCharSqlType || type == SqlType.NText;

    internal override string DebugDisplay() => $"{left.DebugDisplay()} || {right.DebugDisplay()}";

    internal override void VisitColumnReferences(Action<MultiPartName> visit)
    {
        left.VisitColumnReferences(visit);
        right.VisitColumnReferences(visit);
    }

    internal override bool ContainsVariableReference => left.ContainsVariableReference || right.ContainsVariableReference;

    internal override bool IsRowIndependent => left.IsRowIndependent && right.IsRowIndependent;

    // Concatenation propagates operand nullability, the same rule string `+`
    // takes (and the opposite of arithmetic `+`, which claims nullable).
    internal override bool ResultIsNullable(NullabilityContext context) =>
        left.ResultIsNullable(context) || right.ResultIsNullable(context);

    private protected override bool IsStructuralConstant => left.IsWrittenConstant && right.IsWrittenConstant;
}
