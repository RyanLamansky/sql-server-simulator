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
/// <remarks>
/// A flat chain <c>a || b || c …</c> nests a level per term down its left
/// side, so every member that reads the whole chain walks that spine
/// iteratively rather than recursing once per term, as
/// <see cref="TwoSidedExpression"/>'s members do.
/// </remarks>
internal sealed class Concatenate(Expression left, Expression right) : Expression
{
    private readonly Expression left = left;
    private readonly Expression right = right;
    private readonly int chainDepth = Math.Max(left.ChainDepth, right.ChainDepth) + 1;

    internal override int ChainDepth => this.chainDepth;

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.left is not Concatenate)
            return Concatenated(this.left.Run(runtime), this.right.Run(runtime));
        var spine = this.LeftSpine(out var leftmost);
        var accumulated = leftmost.Run(runtime);
        for (var i = spine.Count - 1; i >= 0; i--)
            accumulated = Concatenated(accumulated, spine[i].right.Run(runtime));
        return accumulated;
    }

    private static SqlValue Concatenated(SqlValue leftValue, SqlValue rightValue)
    {
        var resultType = ResolveResultType(leftValue.Type, rightValue.Type);
        return leftValue.IsNull || rightValue.IsNull
            ? SqlValue.Null(resultType)
            : SqlValue.FromString(resultType, StringScalars.ClipToFamilyCap(Stringify(leftValue, resultType) + Stringify(rightValue, resultType), resultType));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (this.left is not Concatenate)
            return ResolveResultType(this.left.GetSqlType(batch, resolveColumnType), this.right.GetSqlType(batch, resolveColumnType));
        var spine = this.LeftSpine(out var leftmost);
        var accumulated = leftmost.GetSqlType(batch, resolveColumnType);
        for (var i = spine.Count - 1; i >= 0; i--)
            accumulated = ResolveResultType(accumulated, spine[i].right.GetSqlType(batch, resolveColumnType));
        return accumulated;
    }

    /// <summary>
    /// The concatenations down this node's left side, this one first, and the
    /// operand at the bottom of them, <paramref name="leftmost"/>.
    /// </summary>
    private List<Concatenate> LeftSpine(out Expression leftmost)
    {
        var spine = new List<Concatenate>();
        Expression node = this;
        while (node is Concatenate concatenate)
        {
            spine.Add(concatenate);
            node = concatenate.left;
        }
        leftmost = node;
        return spine;
    }

    /// <summary>
    /// Whether <paramref name="test"/> holds for every operand along this
    /// node's left spine — each right operand, then the leftmost.
    /// </summary>
    private bool EveryOperand(Func<Expression, bool> test)
    {
        var node = this;
        while (test(node.right))
        {
            if (node.left is not Concatenate next)
                return test(node.left);
            node = next;
        }
        return false;
    }

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

        var national = SqlType.IsNationalStringCategory(leftType) || SqlType.IsNationalStringCategory(rightType);
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


    internal override string DebugDisplay()
    {
        var spine = this.LeftSpine(out var leftmost);
        var text = new System.Text.StringBuilder(leftmost.DebugDisplay());
        for (var i = spine.Count - 1; i >= 0; i--)
            _ = text.Append(" || ").Append(spine[i].right.DebugDisplay());
        return text.ToString();
    }

    internal override void Describe(NodeShape shape) => shape.Child(this.left).Child(this.right);

    internal override bool IsRowIndependent => this.EveryOperand(static operand => operand.IsRowIndependent);

    // Concatenation propagates operand nullability, the same rule string `+`
    // takes (and the opposite of arithmetic `+`, which claims nullable).
    internal override bool ResultIsNullable(NullabilityContext context) =>
        !this.EveryOperand(operand => !operand.ResultIsNullable(context));

    // A node marked folded answers for its whole subtree, as its
    // IsWrittenConstant does.
    private protected override bool IsStructuralConstant
    {
        get
        {
            var node = this;
            while (node.right.IsWrittenConstant)
            {
                if (node.left is not Concatenate { FoldedOverConstantArguments: false } next)
                    return node.left.IsWrittenConstant;
                node = next;
            }
            return false;
        }
    }
}
