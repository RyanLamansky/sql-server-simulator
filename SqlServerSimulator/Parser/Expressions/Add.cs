using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Add(Expression left, ParserContext context) : TwoSidedExpression(left, context)
{
    public override byte Precedence => 3;

    protected override SqlValue Run(SqlValue left, SqlValue right) =>
        IsStringConcatPair(left, right)
            ? StringConcatenation(left, right)
            : AdditiveArithmetic(left, right, '+', "add", static (a, b) => a + b);

    /// <summary>
    /// Detects whether <c>+</c> should run as string concatenation rather
    /// than arithmetic. Both-string operands are obvious; the mixed
    /// non-null-string + NULL case covers SQL Server's "untyped NULL inherits
    /// the string side's type" rule for bare <c>NULL</c> literals
    /// (<c>'a' + NULL</c>). The simulator can't distinguish bare NULL from
    /// <c>cast(null as int)</c> at runtime — both surface as
    /// <see cref="SqlType.Int32"/> typed NULL — so this rule minor-diverges
    /// from real SQL Server on the rare typed-null-int case (real raises
    /// Msg 245 from a string-to-int parse; the simulator returns NULL).
    /// </summary>
    private static bool IsStringConcatPair(SqlValue left, SqlValue right)
    {
        var leftIsString = left.Type.Category == SqlTypeCategory.String;
        var rightIsString = right.Type.Category == SqlTypeCategory.String;
        return (leftIsString && rightIsString)
            || (leftIsString && !left.IsNull && right.IsNull)
            || (rightIsString && !right.IsNull && left.IsNull);
    }

    /// <summary>
    /// String <c>+</c> concatenation: NULL-propagating (matching SQL Server's
    /// default <c>CONCAT_NULL_YIELDS_NULL ON</c>; the OFF setting isn't
    /// modeled). Result type is delegated to
    /// <see cref="SqlType.PromoteForArithmetic"/>, which preserves char(N) /
    /// nchar(N) length combination for fixed-length-pair concatenation.
    /// <c>text</c> / <c>ntext</c> operands raise Msg 402 matching real SQL
    /// Server's restriction on LOB string types in arithmetic operators.
    /// Fixed-length <c>char(N)</c> / <c>nchar(N)</c> operands carry their
    /// trailing-space padding through the storage layer, so
    /// <c>cast('a' as char(5)) + cast('b' as char(5))</c> yields
    /// <c>'a    b    '</c> as a side-effect of the per-value rep — no special
    /// handling in this method.
    /// </summary>
    private static SqlValue StringConcatenation(SqlValue left, SqlValue right)
    {
        if (left.Type == SqlType.Text || left.Type == SqlType.NText || right.Type == SqlType.Text || right.Type == SqlType.NText)
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(left.Type, right.Type, "add");

        var resultType = ResolveResultType(left.Type, right.Type);
        return left.IsNull || right.IsNull
            ? SqlValue.Null(resultType)
            : SqlValue.FromString(resultType, left.AsString + right.AsString);
    }

    /// <summary>
    /// Settles on the result <see cref="SqlType"/> for a string-concat <c>+</c>.
    /// Both-string pairs delegate to <see cref="SqlType.PromoteForArithmetic"/>
    /// (which preserves char/nchar length combination). Mixed string + NULL
    /// (a non-string-typed NULL on one side, reached via the bare-NULL rule
    /// in <see cref="IsStringConcatPair"/>) collapses to length-less
    /// varchar/nvarchar — the result is NULL anyway, so length doesn't matter.
    /// </summary>
    private static SqlType ResolveResultType(SqlType a, SqlType b)
    {
        if (a.Category == SqlTypeCategory.String && b.Category == SqlTypeCategory.String)
            return SqlType.PromoteForArithmetic(a, b, '+');
        var stringType = a.Category == SqlTypeCategory.String ? a : b;
        return IsNationalString(stringType) ? SqlType.NVarchar : SqlType.Varchar;
    }

    private static bool IsNationalString(SqlType type) =>
        type == SqlType.NVarchar || type is NCharSqlType || type == SqlType.NText;

    protected override char Operator => '+';
}
