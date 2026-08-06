using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal abstract class TwoSidedExpression : Expression
{
    private readonly Expression left;
    private readonly Expression right;

    private protected TwoSidedExpression(Expression left, Expression right)
    {
        this.left = left;
        this.right = right;
    }

    // Arithmetic result reports the numeric type name when ANY contributing
    // operand is numeric-named (integer / decimal-named-column operands
    // don't force it). Callers gate on the result being decimal, so a
    // non-arithmetic subclass (string concat, comparison) propagating an
    // operand's flag here is harmless — the non-decimal result is filtered out.
    internal override bool ResultReportsNumeric => this.left.ResultReportsNumeric || this.right.ResultReportsNumeric;

    // Every subclass is arithmetic, bitwise or concatenation over the two
    // operands — value-only, with no BatchContext write between them — so the
    // pair's own answer settles the node's.
    internal override bool ParallelSafe => this.left.ParallelSafe && this.right.ParallelSafe;

    private protected override bool IsStructuralConstant => this.left.IsWrittenConstant && this.right.IsWrittenConstant;

    internal override bool IsNonNullConstantComputation => this.left.IsNonNullConstantComputation && this.right.IsNonNullConstantComputation;

    /// <summary>
    /// Builds the <see cref="TwoSidedExpression"/> that corresponds to a
    /// compound-assignment operator's arithmetic step. Used by the SET and
    /// UPDATE-SET parsers: <c>SET @v += rhs</c> becomes
    /// <c>SET @v = FromCompoundOp('+', VariableReference(@v), rhs)</c> and the
    /// existing assignment path runs unchanged.
    /// </summary>
    internal static TwoSidedExpression FromCompoundOp(char op, Expression left, Expression right) => op switch
    {
        '+' => new Add(left, right),
        '-' => new Subtract(left, right),
        '*' => new Multiply(left, right),
        '/' => new Divide(left, right),
        '%' => new Modulus(left, right),
        '&' => new BitwiseAnd(left, right),
        '|' => new BitwiseOr(left, right),
        '^' => new BitwiseExclusiveOr(left, right),
        _ => throw new ArgumentException($"'{op}' isn't a compound-assignment arithmetic operator.", nameof(op)),
    };

    public sealed override SqlValue Run(RuntimeContext runtime)
    {
        // Fast path — the dominant per-row shape (col op const, col op col, any
        // depth-1 arithmetic) has a non-chain left operand: evaluate directly
        // with no allocation, exactly as the former recursive form did.
        if (this.left is not TwoSidedExpression)
        {
            var (fastLeft, fastRight) = AdjustLiteralOperands(this.left, this.right, this.left.Run(runtime), this.right.Run(runtime));
            return Run(fastLeft, fastRight);
        }

        // Deeper left-leaning chain (a op b op c op …, the shape
        // ParseBinaryContinuation builds): walk the spine iteratively so a long
        // flat chain doesn't recurse once per term and stack-overflow. Only
        // right operands recurse, and right-leaning nesting arises solely from
        // parentheses — capped by Msg 191 — so this can't run away. Evaluation
        // order (leftmost leaf, then each right in source order) is identical to
        // the former recursive form.
        var spine = new List<TwoSidedExpression>();
        Expression node = this;
        while (node is TwoSidedExpression twoSided)
        {
            spine.Add(twoSided);
            node = twoSided.left;
        }
        var accumulated = node.Run(runtime);
        for (var i = spine.Count - 1; i >= 0; i--)
        {
            var current = spine[i];
            // Only the leftmost leaf (first fold step) can be an integer
            // literal; once folded, the accumulator is an arithmetic result, so
            // later steps pass a null left expression to the literal adjuster.
            var leftExpr = i == spine.Count - 1 ? node : null;
            var (adjustedLeft, adjustedRight) = AdjustLiteralOperands(leftExpr, current.right, accumulated, current.right.Run(runtime));
            accumulated = current.Run(adjustedLeft, adjustedRight);
        }
        return accumulated;
    }

    protected abstract SqlValue Run(SqlValue left, SqlValue right);

    public sealed override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // Fast path — shallow left operand, no allocation (mirrors Run).
        if (this.left is not TwoSidedExpression)
            return CombineType(this.left.GetSqlType(batch, resolveColumnType), batch, resolveColumnType);

        // Iterative left-spine walk, mirroring Run: fold the leftmost operand's
        // type through each node's CombineType so a flat chain resolves its
        // projection type without per-term recursion.
        var spine = new List<TwoSidedExpression>();
        Expression node = this;
        while (node is TwoSidedExpression twoSided)
        {
            spine.Add(twoSided);
            node = twoSided.left;
        }
        var accumulated = node.GetSqlType(batch, resolveColumnType);
        for (var i = spine.Count - 1; i >= 0; i--)
            accumulated = spine[i].CombineType(accumulated, batch, resolveColumnType);
        return accumulated;
    }

    /// <summary>
    /// Per-operator projection nullability. Arithmetic claims nullable
    /// unconditionally — real answers nullable even for <c>1 + 1</c> — while
    /// the bitwise operators and the concatenating spelling of <c>+</c>
    /// propagate their operands', so <c>col_a + col_b</c> over two NOT NULL
    /// <c>varchar</c>s is NOT NULL where the same shape over two NOT NULL
    /// <c>int</c>s is not (probe-confirmed against SQL Server 2025). Which
    /// <c>+</c> this is comes from the result type: <see cref="Add"/> dispatches
    /// on operand category, so a string / binary result means it concatenated.
    /// </summary>
    /// <remarks>
    /// The left spine is walked iteratively, folding the operand type and the
    /// nullability together in one pass — mirroring <see cref="Run(RuntimeContext)"/> and
    /// <see cref="GetSqlType"/>, and for the same reason: a flat chain of
    /// thousands of terms would otherwise recurse once per term and re-resolve
    /// the whole left prefix's type at every level.
    /// </remarks>
    internal sealed override bool ResultIsNullable(NullabilityContext context)
    {
        if (this.left is not TwoSidedExpression)
        {
            return this.Operator switch
            {
                '&' or '|' or '^' => this.left.ResultIsNullable(context) || this.right.ResultIsNullable(context),
                '+' when Concatenates(context.TypeOf(this)) =>
                    this.left.ResultIsNullable(context) || this.right.ResultIsNullable(context),
                _ => true,
            };
        }

        var spine = new List<TwoSidedExpression>();
        Expression node = this;
        while (node is TwoSidedExpression twoSided)
        {
            spine.Add(twoSided);
            node = twoSided.left;
        }

        var accumulatedType = node.GetSqlType(context.Batch, context.ColumnType);
        var nullable = node.ResultIsNullable(context);
        for (var i = spine.Count - 1; i >= 0; i--)
        {
            var current = spine[i];
            accumulatedType = current.CombineType(accumulatedType, context.Batch, context.ColumnType);
            nullable = current.Operator switch
            {
                '&' or '|' or '^' => nullable || current.right.ResultIsNullable(context),
                '+' when Concatenates(accumulatedType) => nullable || current.right.ResultIsNullable(context),
                _ => true,
            };
            // Nullability only widens along the chain, so the first arithmetic
            // operator settles the whole answer.
            if (nullable)
                return true;
        }
        return false;
    }

    private static bool Concatenates(SqlType resultType) =>
        resultType.Category == SqlTypeCategory.String
        || resultType is VarbinarySqlType or BinarySqlType;

    /// <summary>
    /// Combines an already-resolved left-operand type with this node's right
    /// operand and operator, applying the same string-concat collation
    /// propagation the former recursive <see cref="GetSqlType"/> did per node.
    /// </summary>
    private SqlType CombineType(SqlType leftType, BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var rightType = this.right.GetSqlType(batch, resolveColumnType);
        var result = SqlType.PromoteForArithmetic(
            ArithmeticOperandType(this.left, leftType, rightType),
            ArithmeticOperandType(this.right, rightType, leftType),
            this.Operator);
        if (result.Category == SqlTypeCategory.String
            && leftType.Category == SqlTypeCategory.String
            && rightType.Category == SqlTypeCategory.String)
        {
            // Propagate collation through string concat (+) so the projection
            // schema type matches the runtime SqlType produced in Add.Run —
            // the same UnresolvedCollation.Settle body, so the two phases agree
            // on which conflicts propagate and which report here. Other
            // arithmetic operators on string operands reject in
            // PromoteForArithmetic before this point.
            result = UnresolvedCollation.Settle(result, leftType, rightType, "add");
        }
        return result;
    }

    /// <summary>
    /// The type an operand contributes to per-operator arithmetic promotion: a
    /// non-negative integer literal (bare, negated, or parenthesized) meeting a
    /// <b>decimal</b> partner is sized <c>numeric(digit_count, 0)</c> instead of
    /// <c>int</c>'s fixed <c>(10, 0)</c> — SQL Server's literal-specific rule
    /// (<c>10.0/3</c> → <c>numeric(8, 6)</c> vs <c>10.0/CAST(3 AS int)</c> →
    /// <c>numeric(14, 12)</c>). Non-literal operands and pure-integer pairs
    /// (<c>3 + 4</c>) are unchanged.
    /// </summary>
    private static SqlType ArithmeticOperandType(Expression operand, SqlType operandType, SqlType partnerType)
    {
        var digits = Expression.IntegerLiteralDigits(operand);
        return digits > 0 && partnerType.Category == SqlTypeCategory.Decimal
            ? SqlType.GetDecimal(digits, 0)
            : operandType;
    }

    /// <summary>
    /// Runtime counterpart of <see cref="ArithmeticOperandType"/>: coerces an
    /// integer-literal operand's value to <c>numeric(digit_count, 0)</c> when
    /// its partner is a decimal, so the runtime <see cref="DecimalArithmetic"/>
    /// derives the same result type the static <see cref="CombineType"/> path
    /// does (required parity — the row encoder rejects a mismatch). A
    /// <paramref name="leftExpr"/> of <see langword="null"/> marks a left
    /// operand that is an arithmetic result (never a literal).
    /// </summary>
    private static (SqlValue Left, SqlValue Right) AdjustLiteralOperands(Expression? leftExpr, Expression rightExpr, SqlValue left, SqlValue right)
    {
        var leftCategory = left.Type.Category;
        if (leftExpr is not null
            && right.Type.Category == SqlTypeCategory.Decimal
            && Expression.IntegerLiteralDigits(leftExpr) is int leftDigits and > 0
            && !left.IsNull)
        {
            left = left.CoerceTo(SqlType.GetDecimal(leftDigits, 0));
        }
        if (leftCategory == SqlTypeCategory.Decimal
            && Expression.IntegerLiteralDigits(rightExpr) is int rightDigits and > 0
            && !right.IsNull)
        {
            right = right.CoerceTo(SqlType.GetDecimal(rightDigits, 0));
        }
        return (left, right);
    }

    /// <summary>
    /// Numeric binary-operator dispatcher. Despite the name, this is the
    /// entry point for all numeric arithmetic — the SQL Server precedence
    /// chart routes <c>float &gt; decimal &gt; money &gt; integer</c>, and
    /// each family runs in its own helper. Dispatch is structured as an
    /// outer switch on the left operand's <see cref="SqlTypeCategory"/>
    /// with each arm a switch on the right operand's category, keeping the
    /// hot path one byte-comparison deep and jump-table-friendly.
    /// </summary>
    /// <remarks>
    /// Cross-category integer ↔ string is normalized at the top: the string
    /// operand parses to the integer side's specific type (<c>tinyint + '3'</c>
    /// stays tinyint, <c>bigint + '3'</c> stays bigint — verified against
    /// SQL Server 2025), so the rest of the dispatcher stays integer ↔
    /// integer. Bit is the sole exception: bit + string raises Msg 402 (for
    /// <c>+ - %</c>) or Msg 8117 (for <c>* /</c>) without parsing, mirroring
    /// SQL Server's same treatment of bit arithmetic with another bit and
    /// matching the bitwise-operator restrictions on strings (which also
    /// fail rather than coerce).
    /// </remarks>
    private protected static SqlValue IntegerArithmetic(SqlValue left, SqlValue right, char op, Func<long, long, long> compute)
    {
        // sql_variant has no arithmetic behavior; delegate to the single-source
        // rejection so the runtime error matches GetSqlType's (Msg 402 / 257).
        if (left.Type is SqlVariantSqlType || right.Type is SqlVariantSqlType)
            _ = SqlType.PromoteForArithmetic(left.Type, right.Type, op);

        // A string operand paired with a numeric one converts to that numeric
        // type — SQL Server's low string-precedence rule (probe-confirmed 2025:
        // `decimal - '0.4'`, `'3' * float`, `money + '2.5'` all coerce, and the
        // result carries the numeric partner's type). Two exceptions: bit +
        // string raises Msg 402 / 8117 (BitWithStringArithmetic), and modulo
        // against a non-integer numeric (decimal / money / float) is Msg 402
        // "incompatible in the modulo operator" even though + - * / coerce.
        // Bitwise operators (& | ^) aren't string-coercible and fall through to
        // the unsupported-pair error below.
        var leftIsString = left.Type.Category == SqlTypeCategory.String;
        var rightIsString = right.Type.Category == SqlTypeCategory.String;
        if (op is not '&' and not '|' and not '^' && leftIsString != rightIsString)
        {
            var numericType = leftIsString ? right.Type : left.Type;
            if (IsStringCoercibleNumericCategory(numericType.Category))
            {
                if (numericType == SqlType.Bit)
                    throw BitWithStringArithmetic(left.Type, right.Type, op);
                if (op == '%' && numericType.Category != SqlTypeCategory.Integer)
                    throw SimulatedSqlException.IncompatibleDataTypesInOperator(left.Type, right.Type, OperatorWord(op));
                if (leftIsString)
                    left = left.IsNull ? SqlValue.Null(numericType) : left.CoerceTo(numericType);
                else
                    right = right.IsNull ? SqlValue.Null(numericType) : right.CoerceTo(numericType);
            }
        }

        // Binary ↔ integer: the binary operand converts to the integer side's
        // specific type (big-endian, left-truncated to that width), for
        // arithmetic AND bitwise operators alike (unlike the string path,
        // which excludes bitwise). Probe-confirmed against SQL Server 2025:
        // 1 + 0x01 → 2 (int), 255 & 0x01 → 1 (int), cast(5 as bigint) / 0x02
        // → 2 (bigint), cast(5 as tinyint) + 0x01 → 6 (tinyint).
        if (left.Type.Category == SqlTypeCategory.Integer && right.Type is VarbinarySqlType or BinarySqlType)
            right = right.IsNull ? SqlValue.Null(left.Type) : right.CoerceTo(left.Type);
        else if (right.Type.Category == SqlTypeCategory.Integer && left.Type is VarbinarySqlType or BinarySqlType)
            left = left.IsNull ? SqlValue.Null(right.Type) : left.CoerceTo(right.Type);
        else if (left.Type is VarbinarySqlType or BinarySqlType && right.Type is VarbinarySqlType or BinarySqlType)
            // Binary + binary is concatenation (handled in Add.Run before it
            // reaches here); every other operator raises Msg 402 ('- % & | ^')
            // or Msg 8117 ('* /') with the wording PromoteForArithmetic emits.
            _ = SqlType.PromoteForArithmetic(left.Type, right.Type, op);

        return left.Type.Category switch
        {
            SqlTypeCategory.Approximate => ApproximateArithmetic(left, right, op),
            SqlTypeCategory.Decimal => right.Type.Category switch
            {
                SqlTypeCategory.Approximate => ApproximateArithmetic(left, right, op),
                SqlTypeCategory.Decimal or SqlTypeCategory.Integer or SqlTypeCategory.Money => DecimalArithmetic(left, right, op),
                _ => throw UnsupportedNumericPair(left, right, op),
            },
            SqlTypeCategory.Money => right.Type.Category switch
            {
                SqlTypeCategory.Approximate => ApproximateArithmetic(left, right, op),
                SqlTypeCategory.Decimal => DecimalArithmetic(left, right, op),
                SqlTypeCategory.Money or SqlTypeCategory.Integer => MoneyArithmetic(left, right, op),
                _ => throw UnsupportedNumericPair(left, right, op),
            },
            SqlTypeCategory.Integer => right.Type.Category switch
            {
                SqlTypeCategory.Approximate => ApproximateArithmetic(left, right, op),
                SqlTypeCategory.Decimal => DecimalArithmetic(left, right, op),
                SqlTypeCategory.Money => MoneyArithmetic(left, right, op),
                SqlTypeCategory.Integer => PureIntegerArithmetic(left, right, op, compute),
                _ => throw UnsupportedNumericPair(left, right, op),
            },
            _ => throw UnsupportedNumericPair(left, right, op),
        };
    }

    /// <summary>
    /// True for the numeric categories a string operand implicitly converts to
    /// in arithmetic (integer / decimal / money / float-real). String vs
    /// date-time / uniqueidentifier / binary aren't arithmetic-coercible and
    /// surface as the unsupported-pair error instead.
    /// </summary>
    private static bool IsStringCoercibleNumericCategory(SqlTypeCategory category) =>
        category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate;

    private static SimulatedSqlException BitWithStringArithmetic(SqlType left, SqlType right, char op) =>
        op is '*' or '/'
            ? SimulatedSqlException.OperandDataTypeInvalid(left, OperatorWord(op))
            : SimulatedSqlException.IncompatibleDataTypesInOperator(left, right, OperatorWord(op));

    private static string OperatorWord(char op) => op switch
    {
        '+' => "add",
        '-' => "subtract",
        '*' => "multiply",
        '/' => "divide",
        '%' => "modulo",
        _ => op.ToString(),
    };

    /// <summary>
    /// Integer-only path: both sides are guaranteed integer-category.
    /// Promotes to SQL Server's common integer type, runs the compute
    /// callback in <c>long</c> arithmetic, and narrows the result back to
    /// the common type. NULL propagates.
    /// </summary>
    private static SqlValue PureIntegerArithmetic(SqlValue left, SqlValue right, char op, Func<long, long, long> compute)
    {
        var common = SqlType.Promote(left.Type, right.Type);
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(common);

        // SQL Server forms the quotient for % as well as /, so <type minimum>
        // % -1 overflows exactly like <type minimum> / -1 even though the
        // mathematical remainder is 0 (probe-confirmed 2026-07-24 for smallint,
        // int and bigint; -5 % -1 and <min> % 1 both compute normally). The
        // long-width computation below hides the narrow-type cases from the
        // checked narrowing — the remainder is in range — so they need this
        // guard. bigint would trap in the CLR anyway; this covers it uniformly.
        if (op is '/' or '%'
            && SignedMinimum(common) is long minimum
            && ToInt64(right) == -1
            && ToInt64(left) == minimum)
        {
            throw SimulatedSqlException.ArithmeticOverflow(common.ToString()!);
        }

        // SQL Server keeps the narrow type through arithmetic rather than
        // widening, so a result outside the operand width is an overflow, not
        // a wrap. Probe-confirmed 2026-07-24: cast(255 as tinyint) + cast(1 as
        // tinyint), cast(32767 as smallint) + cast(1 as smallint),
        // cast(2147483647 as int) + cast(1 as int) and the bigint equivalent
        // all raise Msg 8115 naming that same narrow type, while a mixed pair
        // promotes first and doesn't overflow (int + bigint = bigint). The
        // checked narrowing below covers tinyint / smallint / int; bigint-width
        // overflow is caught by the checked arithmetic in the caller's compute
        // lambda (Add / Subtract / Multiply), and long.MinValue / -1 traps in
        // the CLR on its own.
        try
        {
            var result = compute(ToInt64(left), ToInt64(right));
            return common == SqlType.Bit ? SqlValue.FromBoolean(result != 0)
                : common == SqlType.TinyInt ? SqlValue.FromByte(checked((byte)result))
                : common == SqlType.SmallInt ? SqlValue.FromInt16(checked((short)result))
                : common == SqlType.Int32 ? SqlValue.FromInt32(checked((int)result))
                : SqlValue.FromInt64(result);
        }
        catch (DivideByZeroException)
        {
            // Integer / and % by zero. Other operators routed through this
            // method never divide, so the catch is specific to those two.
            throw SimulatedSqlException.DivideByZero();
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(common.ToString()!);
        }
    }

    /// <summary>
    /// The most negative value a signed integer <see cref="SqlType"/> holds,
    /// or null for the types with no negative range (bit / tinyint), which
    /// therefore can't reach the <c>/ -1</c> overflow.
    /// </summary>
    private static long? SignedMinimum(SqlType type) =>
        type == SqlType.SmallInt ? short.MinValue
        : type == SqlType.Int32 ? int.MinValue
        : type == SqlType.BigInt ? long.MinValue
        : null;

    private static NotSupportedException UnsupportedNumericPair(SqlValue left, SqlValue right, char op) =>
        new($"Operator '{op}' currently supports only integer operands; got {left.Type} and {right.Type}.");

    /// <summary>
    /// Decimal arithmetic. The result-type computation is delegated to
    /// <see cref="SqlType.PromoteForArithmetic"/> (which encodes the same
    /// per-operator scale formulas verified against SQL Server 2025), so
    /// the static <see cref="GetSqlType"/> path and this runtime path
    /// always agree on the schema. Integer / money operands canonicalize
    /// to their decimal equivalent before the compute step.
    /// Digits past the result scale round half away from zero for every
    /// operator but division, which truncates toward zero — the split
    /// <see cref="Decimal38"/> carries.
    /// </summary>
    private protected static SqlValue DecimalArithmetic(SqlValue left, SqlValue right, char op)
    {
        var resultType = (DecimalSqlType)SqlType.PromoteForArithmetic(left.Type, right.Type, op);
        var resultPrecision = (int)resultType.precision;
        var resultScale = (int)resultType.scale;

        if (left.IsNull || right.IsNull)
            return SqlValue.Null(resultType);

        var l = ToDecimal38(left);
        var r = ToDecimal38(right);
        if (r.IsZero && op is '/' or '%')
            throw SimulatedSqlException.DivideByZero();

        // Real aligns both modulo operands at the result's scale — max(s1, s2)
        // — before taking the remainder, so an operand needing more than 38
        // digits there is an arithmetic overflow whatever the remainder itself
        // would have been. Probe-confirmed: a decimal(38, 0) holding 1 answers
        // against a decimal(38, 37) while the same shape holding 99 raises.
        if (op == '%'
            && (!Decimal38.TryRescale(l, Decimal38.MaxPrecision, resultScale, out _)
                || !Decimal38.TryRescale(r, Decimal38.MaxPrecision, resultScale, out _)))
        {
            throw SimulatedSqlException.ArithmeticOverflow("numeric");
        }

        Decimal38 settled;
        var computed = op switch
        {
            '+' => Decimal38.TryAdd(l, r, resultPrecision, resultScale, out settled),
            '-' => Decimal38.TrySubtract(l, r, resultPrecision, resultScale, out settled),
            '*' => Decimal38.TryMultiply(l, r, resultPrecision, resultScale, out settled),
            '/' => Decimal38.TryDivide(l, r, resultPrecision, resultScale, out settled),
            '%' => Decimal38.TryModulo(l, r, resultPrecision, resultScale, out settled),
            _ => throw new NotSupportedException($"Operator '{op}'."),
        };

        // A result past the declared precision is real's own arithmetic
        // overflow, reported at state 2 (probe-confirmed) — distinct from the
        // conversion overflow a CAST into a narrow target raises.
        return computed
            ? SqlValue.FromDecimal(resultType, settled)
            : throw SimulatedSqlException.ArithmeticOverflow("numeric");
    }

    private static Decimal38 ToDecimal38(SqlValue v) =>
        v.Type is DecimalSqlType ? v.AsDecimal38
        : SqlType.IsMoneyCategory(v.Type) ? v.AsMoneyDecimal38
        : Decimal38.FromInt64(SqlValue.AsInt64Widened(v));

    private protected static long ToInt64(SqlValue v) =>
        v.Type == SqlType.Bit ? (v.AsBoolean ? 1L : 0L)
        : v.Type == SqlType.TinyInt ? v.AsByte
        : v.Type == SqlType.SmallInt ? v.AsInt16
        : v.Type == SqlType.Int32 ? v.AsInt32
        : v.AsInt64;

    /// <summary>
    /// Dispatcher for <c>+</c> / <c>-</c>: routes integer×integer to
    /// <see cref="IntegerArithmetic"/> and any pair involving a date/time
    /// type to the date-arithmetic path. Date arithmetic only supports the
    /// legacy <c>datetime</c> and <c>smalldatetime</c> types; non-legacy
    /// operands raise Msg 402 / 8117 (per SQL Server's exact rules).
    /// </summary>
    private protected static SqlValue AdditiveArithmetic(SqlValue left, SqlValue right, char op, string operatorName, Func<long, long, long> compute) =>
        SqlType.IsDateTimeCategory(left.Type) || SqlType.IsDateTimeCategory(right.Type)
            ? DateAdditiveArithmetic(left, right, operatorName, compute)
            : IntegerArithmetic(left, right, op, compute);

    /// <summary>
    /// Float / real arithmetic. Both sides convert to <see cref="double"/>;
    /// result is <c>float</c> unless both operands were <c>real</c>, in
    /// which case it stays <c>real</c>. Divide-by-zero raises Msg 8134 to
    /// match the decimal path; native IEEE infinities/NaN aren't surfaced
    /// (real SQL Server raises 8134 for divide-by-zero on float too,
    /// verified earlier).
    /// </summary>
    /// <summary>
    /// Money / smallmoney arithmetic. Result stays in money when both sides
    /// are money or when one side is integer (verified <c>$5 + $3 → money</c>,
    /// <c>$5 * 3 → money</c>). Same-money-pair preserves the wider of the
    /// two; mixed money / smallmoney widens to money. Math runs on the
    /// underlying decimal values; the result re-rounds half-away-from-zero
    /// to scale 4 inside <see cref="SqlValue.FromMoney(SqlType, Decimal38)"/> — except division,
    /// which truncates toward zero at scale 4 the way the decimal family's
    /// does (<c>$1.00 / 7</c> is real's <c>0.1428</c>), leaving that rounding
    /// nothing to do.
    /// </summary>
    private protected static SqlValue MoneyArithmetic(SqlValue left, SqlValue right, char op)
    {
        var resultType = SqlType.IsMoneyCategory(left.Type) && SqlType.IsMoneyCategory(right.Type)
            ? (left.Type == SqlType.Money || right.Type == SqlType.Money ? SqlType.Money : SqlType.SmallMoney)
            : (SqlType.IsMoneyCategory(left.Type) ? left.Type : right.Type);
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(resultType);

        var l = MoneyOrIntegerToDecimal38(left);
        var r = MoneyOrIntegerToDecimal38(right);
        if (r.IsZero && op is '/' or '%')
            throw SimulatedSqlException.DivideByZero();

        // Money computes at money's own width — 19 digits at scale 4 — and a
        // product that outgrows it is real's Msg 8115 against the money target
        // rather than against numeric.
        Decimal38 raw;
        var computed = op switch
        {
            '+' => Decimal38.TryAdd(l, r, MoneySqlType.Precision, MoneySqlType.Scale, out raw),
            '-' => Decimal38.TrySubtract(l, r, MoneySqlType.Precision, MoneySqlType.Scale, out raw),
            '*' => Decimal38.TryMultiply(l, r, MoneySqlType.Precision, MoneySqlType.Scale, out raw),
            '/' => Decimal38.TryDivide(l, r, MoneySqlType.Precision, MoneySqlType.Scale, out raw),
            '%' => Decimal38.TryModulo(l, r, MoneySqlType.Precision, MoneySqlType.Scale, out raw),
            _ => throw new NotSupportedException($"Operator '{op}' on money operands isn't implemented."),
        };

        return computed
            ? SqlValue.FromMoney(resultType, raw)
            : throw SimulatedSqlException.ArithmeticOverflow(resultType.ToString()!);
    }

    private static Decimal38 MoneyOrIntegerToDecimal38(SqlValue v) =>
        SqlType.IsMoneyCategory(v.Type) ? v.AsMoneyDecimal38 : Decimal38.FromInt64(SqlValue.AsInt64Widened(v));

    private protected static SqlValue ApproximateArithmetic(SqlValue left, SqlValue right, char op)
    {
        // The result type comes from the same promotion source of truth the
        // projection schema reads, so the two can't disagree — real wins over
        // every partner except float (`real + int` → real, `real + float` →
        // float), and a mismatch here is what the row encoder rejects.
        // Computation still runs in double whatever the result type, then
        // rounds to single for a real result: probe-confirmed against SQL
        // Server 2025 that `CAST(16777216 AS real) + CAST(1 AS bigint)` and
        // `… + CAST(1 AS real)` return the same 0x4B800000, and `CAST(1 AS
        // real) / 7` matches `CAST(CAST(1 AS float) / 7 AS real)` bit for bit.
        var resultType = SqlType.PromoteForArithmetic(left.Type, right.Type, op);
        var resultIsReal = resultType == SqlType.Real;
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(resultType);

        var l = ToDouble(left);
        var r = ToDouble(right);
        var raw = op switch
        {
            '+' => l + r,
            '-' => l - r,
            '*' => l * r,
            '/' => r == 0.0 ? throw SimulatedSqlException.DivideByZero() : l / r,
            '%' => r == 0.0 ? throw SimulatedSqlException.DivideByZero() : l % r,
            _ => throw new NotSupportedException($"Operator '{op}' on float operands isn't implemented."),
        };
        return resultIsReal ? SqlValue.FromSingle((float)raw) : SqlValue.FromDouble(raw);
    }

    private static double ToDouble(SqlValue v) =>
        v.Type == SqlType.Float ? v.AsDouble
        : v.Type == SqlType.Real ? v.AsSingle
        : v.Type is DecimalSqlType ? v.AsDecimal38.ToDouble()
        : SqlType.IsMoneyCategory(v.Type) ? v.AsMoneyDecimal38.ToDouble()
        : SqlValue.AsInt64Widened(v);

    /// <summary>
    /// Date arithmetic for <c>+</c> / <c>-</c>: works only when both
    /// operands resolve to a legacy datetime tick offset (i.e. each side is
    /// either an integer treated as days-since-1900-01-01, or a
    /// <c>datetime</c>/<c>smalldatetime</c> value). Result is rendered as
    /// the higher-precedence date type (datetime > smalldatetime). NULL
    /// propagates. Three error variants:
    /// <list type="bullet">
    /// <item>Both non-legacy date types (e.g. <c>date + date</c>,
    /// <c>dt2 + date</c>) → Msg 8117 with the left operand's type;</item>
    /// <item>One legacy and one non-legacy date type (e.g. <c>dt + date</c>)
    /// → Msg 402 with both names and the operator;</item>
    /// <item>Non-legacy date + integer (e.g. <c>date + 1</c>) → Msg 206
    /// from <see cref="SqlType.Promote"/>'s integer-vs-non-legacy rule.</item>
    /// </list>
    /// Out-of-range arithmetic results raise Msg 8115 with the result type
    /// name (matching the int→datetime overflow path).
    /// </summary>
    private static SqlValue DateAdditiveArithmetic(SqlValue left, SqlValue right, string operatorName, Func<long, long, long> compute)
    {
        var leftIsLegacy = left.Type == SqlType.DateTime || left.Type == SqlType.SmallDateTime;
        var rightIsLegacy = right.Type == SqlType.DateTime || right.Type == SqlType.SmallDateTime;
        var leftIsNonLegacyDateTime = SqlType.IsDateTimeCategory(left.Type) && !leftIsLegacy;
        var rightIsNonLegacyDateTime = SqlType.IsDateTimeCategory(right.Type) && !rightIsLegacy;

        // Both non-legacy date types — including different-non-legacy pairs
        // like `date + dt2`. SQL Server reports just the left operand's type
        // in Msg 8117, so we don't need both names.
        if (leftIsNonLegacyDateTime && rightIsNonLegacyDateTime)
            throw SimulatedSqlException.OperandDataTypeInvalid(left.Type, operatorName);

        // One legacy, one non-legacy date type — e.g. `dt + date`, `dt2 + dt`.
        if ((leftIsLegacy && rightIsNonLegacyDateTime) || (leftIsNonLegacyDateTime && rightIsLegacy))
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(left.Type, right.Type, operatorName);

        // Promote handles the remaining cases: legacy×legacy, legacy×int,
        // int×non-legacy (which throws Msg 206 from inside Promote).
        var common = SqlType.Promote(left.Type, right.Type);
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(common);

        long resultTicks;
        try
        {
            resultTicks = checked(compute(TicksFromBase(left), TicksFromBase(right)));
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(common.ToString()!);
        }

        return common == SqlType.SmallDateTime
            ? SqlValue.CoerceTicksSinceBaseToSmallDateTime(resultTicks)
            : SqlValue.CoerceTicksSinceBaseToDateTime(resultTicks);
    }

    /// <summary>
    /// Resolves an arithmetic operand to ticks measured from 1900-01-01.
    /// Integer operands treat the value as a whole-day count
    /// (multiplied by <see cref="TimeSpan.TicksPerDay"/> with overflow
    /// checking — bigint × TicksPerDay can exceed <see cref="long"/>);
    /// legacy date types subtract their base-date ticks. Caller must have
    /// already filtered out non-legacy date types.
    /// </summary>
    private static long TicksFromBase(SqlValue v) =>
        SqlType.IsIntegerCategory(v.Type) ? checked(SqlValue.AsInt64Widened(v) * TimeSpan.TicksPerDay)
        : v.Type == SqlType.DateTime ? v.AsDateTime.Ticks - new DateTime(1900, 1, 1).Ticks
        : v.Type == SqlType.SmallDateTime ? v.AsSmallDateTime.Ticks - new DateTime(1900, 1, 1).Ticks
        : throw new InvalidOperationException($"TicksFromBase received unexpected type {v.Type}.");

    protected abstract char Operator { get; }

    internal sealed override string DebugDisplay() => $"{left.DebugDisplay()} {Operator} {right.DebugDisplay()}";

    /// <summary>
    /// True when <paramref name="predicate"/> holds for both operands. Lets a
    /// caller classify the whole expression structurally — e.g. the index-seek
    /// stable-value test, where a deterministic arithmetic node over two
    /// row-invariant operands is itself a row-invariant probe value — without
    /// exposing the operand fields.
    /// </summary>
    internal bool BothOperandsMatch(Func<Expression, bool> predicate) => predicate(this.left) && predicate(this.right);

    internal sealed override void VisitColumnReferencesCore(ColumnReferenceVisitor visit)
    {
        if (visit.CoversSubtree is not null)
        {
            // A covering predicate has to meet every node, and the spine walk
            // below reaches the leaves without entering the intermediate ones —
            // which is what a `SELECT a + 1 + 0` against `GROUP BY a + 1` needs.
            // Its recursion is affordable here: the predicate is asked once per
            // statement compile, never per row.
            this.left.VisitColumnReferences(visit);
            this.right.VisitColumnReferences(visit);
            return;
        }

        // Iterative left-spine walk (see Run) so a long flat chain doesn't
        // recurse per term; visits the leftmost leaf then each right operand in
        // source order, matching the former recursive traversal.
        var spine = new List<TwoSidedExpression>();
        Expression node = this;
        while (node is TwoSidedExpression twoSided)
        {
            spine.Add(twoSided);
            node = twoSided.left;
        }
        node.VisitColumnReferences(visit);
        for (var i = spine.Count - 1; i >= 0; i--)
            spine[i].right.VisitColumnReferences(visit);
    }

    internal sealed override bool ContainsVariableReference
    {
        get
        {
            // Iterative left-spine walk (see Run): OR across every right operand
            // plus the leftmost leaf, avoiding per-term recursion on long chains.
            Expression node = this;
            while (node is TwoSidedExpression twoSided)
            {
                if (twoSided.right.ContainsVariableReference)
                    return true;
                node = twoSided.left;
            }
            return node.ContainsVariableReference;
        }
    }

    internal sealed override bool IsRowIndependent
    {
        get
        {
            // Iterative left-spine walk (see Run): AND across every right operand
            // plus the leftmost leaf — the whole node is row-independent only when
            // every operand is (an arithmetic combination of constants / variables).
            Expression node = this;
            while (node is TwoSidedExpression twoSided)
            {
                if (!twoSided.right.IsRowIndependent)
                    return false;
                node = twoSided.left;
            }
            return node.IsRowIndependent;
        }
    }
}
