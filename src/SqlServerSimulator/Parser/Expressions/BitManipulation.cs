using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared helpers for the SQL Server 2022+ bit-manipulation family
/// (<c>BIT_COUNT</c> / <c>GET_BIT</c> / <c>SET_BIT</c> / <c>LEFT_SHIFT</c> /
/// <c>RIGHT_SHIFT</c>). All five share an "argument-1 must be an integer
/// or binary type" rule with the same Msg 8116 wording on NULL or
/// disallowed types — kept here so each per-function class stays
/// type-dispatch only.
/// </summary>
internal static class BitOperandHelpers
{
    /// <summary>Raises Msg 8116 with the function-name token verbatim — matches real-server wording.</summary>
    public static SimulatedSqlException ArgInvalidForBitFunc(string functionName, string typeName, int argumentIndex) =>
        SimulatedSqlException.ArgumentDataTypeInvalidForBitFunction(typeName, argumentIndex, functionName);

    /// <summary>
    /// Returns the bit-width of the integer category for the operand —
    /// 8 / 16 / 32 / 64 for tinyint / smallint / int / bigint. Used for
    /// per-type range-checking of bit indices in <c>GET_BIT</c> / <c>SET_BIT</c>
    /// (probe-confirmed against SQL Server 2025: <c>GET_BIT(cast(8 as int), 32)</c>
    /// raises Msg 9838, "Parameter 2 in function 'get_bit' is out of range 0 to 31.").
    /// </summary>
    public static int IntegerBitWidth(SqlType type)
    {
        return type == SqlType.TinyInt ? 8
            : type == SqlType.SmallInt ? 16
            : type == SqlType.Int32 ? 32
            : type == SqlType.BigInt ? 64
            : -1;
    }

    /// <summary>
    /// Reads a position / shift-distance / bit-value argument as a
    /// <c>bigint</c>. Real never narrows one to <c>int</c>: the argument has
    /// to be an integer type already (anything else — decimal, float, money,
    /// string, binary — is Msg 8116 naming the type), and the range check
    /// that follows runs against the widened value, so a position past
    /// <c>int</c> range is an ordinary out-of-range failure rather than a
    /// conversion overflow (probe-confirmed 2026-07-31).
    /// </summary>
    /// <remarks>
    /// A NULL argument reports the same "data type NULL" Msg 8116 the first
    /// operand does. Real distinguishes the bare NULL keyword (this error)
    /// from a NULL-valued typed argument (result NULL); the simulator
    /// resolves both to the same placeholder-typed value, so the family
    /// answers uniformly the way its first operand always has.
    /// </remarks>
    /// <param name="value">The evaluated argument.</param>
    /// <param name="functionName">Lowercase function name for the error wording.</param>
    /// <param name="argumentIndex">1-based argument position for the error wording.</param>
    /// <param name="allowBit">
    /// <c>bit</c> is accepted only at <c>SET_BIT</c>'s third argument; every
    /// other position rejects it alongside the non-integer types, which is
    /// real's own split.
    /// </param>
    public static long IntegerArgument(SqlValue value, string functionName, int argumentIndex, bool allowBit) =>
        value.IsNull ? throw ArgInvalidForBitFunc(functionName, "NULL", argumentIndex)
            : !SqlType.IsIntegerCategory(value.Type) || (value.Type == SqlType.Bit && !allowBit)
                ? throw ArgInvalidForBitFunc(functionName, value.Type.SqlServerName, argumentIndex)
                : value.CoerceTo(SqlType.BigInt).AsInt64;
}

/// <summary>
/// SQL <c>BIT_COUNT(num)</c>: returns the count of 1-bits in the integer
/// or binary input as <c>bigint</c>. Probe-confirmed against SQL Server
/// 2025 (2026-05-22): NULL input raises Msg 8116 (not accepted); binary
/// inputs count across all bytes.
/// </summary>
internal sealed class BitCount : Expression
{
    private readonly Expression operand;

    public BitCount(ParserContext context)
    {
        this.operand = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.operand.Run(runtime);
        return v.IsNull
            ? throw BitOperandHelpers.ArgInvalidForBitFunc("bit_count", "NULL", 1)
            : v.Type is BinarySqlType or VarbinarySqlType
                ? SqlValue.FromInt64((long)ToUnsignedBits(v))
                : SqlValue.FromInt64(System.Numerics.BitOperations.PopCount(ToUnsignedBits(v)));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.BigInt;

    internal override string DebugDisplay() => $"BIT_COUNT({this.operand.DebugDisplay()})";

    /// <summary>
    /// Reinterprets the operand's value as a 64-bit unsigned integer for
    /// the bit-population count. Integer types are sign-extended to 64
    /// bits then masked (so <c>-1</c> as int → 32 ones, as bigint → 64
    /// ones — probe-confirmed). Binary types are accumulated across all
    /// bytes.
    /// </summary>
    internal static ulong ToUnsignedBits(SqlValue v)
    {
        var t = v.Type;
        var width = BitOperandHelpers.IntegerBitWidth(t);
        if (width > 0)
        {
            var unsigned = t == SqlType.TinyInt ? v.AsByte
                : t == SqlType.SmallInt ? (ulong)(long)v.AsInt16
                : t == SqlType.Int32 ? (ulong)(long)v.AsInt32
                : (ulong)v.AsInt64;
            return width == 64 ? unsigned : unsigned & ((1UL << width) - 1);
        }
        if (t is BinarySqlType or VarbinarySqlType)
        {
            ulong total = 0;
            foreach (var b in v.AsBytes)
            {
                total += (ulong)System.Numerics.BitOperations.PopCount(b);
            }
            return total;
        }
        throw BitOperandHelpers.ArgInvalidForBitFunc("bit_count", t.ToString()!, 1);
    }
}

/// <summary>
/// SQL <c>GET_BIT(num, position)</c>: returns the bit at the given
/// 0-based position (LSB = 0) as <c>bit</c>. Position must be in range
/// <c>[0, bit-width-1]</c> for integer types (probe-confirmed Msg 9838
/// state 1 for out-of-range against SQL Server 2025, 2026-07-31). NULL or
/// non-integer operand raises Msg 8116.
/// </summary>
internal sealed class GetBit : Expression
{
    private readonly Expression numArg;
    private readonly Expression positionArg;

    public GetBit(ParserContext context)
    {
        this.numArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.positionArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.numArg.Run(runtime);
        if (v.IsNull)
            throw BitOperandHelpers.ArgInvalidForBitFunc("get_bit", "NULL", 1);
        var width = BitOperandHelpers.IntegerBitWidth(v.Type);
        if (width < 0)
            throw BitOperandHelpers.ArgInvalidForBitFunc("get_bit", v.Type.ToString()!, 1);
        var pos = BitOperandHelpers.IntegerArgument(this.positionArg.Run(runtime), "get_bit", 2, allowBit: false);
        if (pos < 0 || pos >= width)
            throw SimulatedSqlException.BitFunctionPositionOutOfRange("get_bit", width - 1, state: 1);
        var bits = BitCount.ToUnsignedBits(v);
        return SqlValue.FromBoolean(((bits >> (int)pos) & 1UL) == 1UL);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Bit;

    internal override string DebugDisplay() => $"GET_BIT({this.numArg.DebugDisplay()}, {this.positionArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>SET_BIT(num, position [, value])</c>: sets the bit at the given
/// 0-based position to the optional value argument (default 1). Returns
/// the same integer type as the first argument. Out-of-range position
/// raises Msg 9838 state 2; NULL or non-integer operand raises Msg 8116;
/// value other than 0 or 1 raises Msg 9839 (probe-confirmed against
/// SQL Server 2025, 2026-07-31).
/// </summary>
internal sealed class SetBit : Expression
{
    private readonly Expression numArg;
    private readonly Expression positionArg;
    private readonly Expression? valueArg;

    public SetBit(ParserContext context)
    {
        this.numArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.positionArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            this.valueArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.numArg.Run(runtime);
        if (v.IsNull)
            throw BitOperandHelpers.ArgInvalidForBitFunc("set_bit", "NULL", 1);
        var width = BitOperandHelpers.IntegerBitWidth(v.Type);
        if (width < 0)
            throw BitOperandHelpers.ArgInvalidForBitFunc("set_bit", v.Type.ToString()!, 1);
        var pos = BitOperandHelpers.IntegerArgument(this.positionArg.Run(runtime), "set_bit", 2, allowBit: false);
        if (pos < 0 || pos >= width)
            throw SimulatedSqlException.BitFunctionPositionOutOfRange("set_bit", width - 1, state: 2);
        var setTo = this.valueArg is null ? 1L : BitOperandHelpers.IntegerArgument(this.valueArg.Run(runtime), "set_bit", 3, allowBit: true);
        if (setTo is not (0 or 1))
            throw SimulatedSqlException.BitFunctionValueNotZeroOrOne();
        var bits = BitCount.ToUnsignedBits(v);
        bits = setTo == 1 ? bits | (1UL << (int)pos) : bits & ~(1UL << (int)pos);
        return UnsignedBitsToTyped(bits, v.Type);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.numArg.GetSqlType(batch, resolveColumnType);

    internal override string DebugDisplay() => $"SET_BIT({this.numArg.DebugDisplay()}, {this.positionArg.DebugDisplay()})";

    internal static SqlValue UnsignedBitsToTyped(ulong bits, SqlType type) =>
        type == SqlType.TinyInt ? SqlValue.FromByte((byte)(bits & 0xff))
        : type == SqlType.SmallInt ? SqlValue.FromInt16((short)(bits & 0xffff))
        : type == SqlType.Int32 ? SqlValue.FromInt32((int)(bits & 0xffffffff))
        : type == SqlType.BigInt ? SqlValue.FromInt64((long)bits)
        : throw new NotSupportedException($"Bit-manipulation result type {type} not handled.");
}

/// <summary>
/// SQL <c>LEFT_SHIFT(num, num_of_bits)</c> and <c>RIGHT_SHIFT</c>:
/// per-bit shift returning the same integer type as the first argument.
/// Shifts beyond the bit-width zero out the result for unsigned-wrap
/// semantics — probe-confirmed: <c>LEFT_SHIFT(cast(1 as tinyint), 8)</c>
/// returns <c>0</c>. <c>RIGHT_SHIFT</c> uses logical (unsigned) shift
/// semantics — probe-confirmed: <c>RIGHT_SHIFT(cast(-16 as int), 2)</c>
/// returns <c>1073741820</c>, not <c>-4</c>.
/// A negative distance shifts the opposite way instead of zeroing —
/// probe-confirmed 2026-07-31: <c>LEFT_SHIFT(cast(255 as int), -4)</c>
/// returns <c>15</c> and <c>RIGHT_SHIFT(cast(255 as int), -4)</c> returns
/// <c>4080</c> — and neither direction has a range check, so a distance
/// past <c>int</c> range simply zeroes rather than failing to convert.
/// </summary>
internal sealed class BitShift : Expression
{
    private readonly bool isLeftShift;
    private readonly Expression numArg;
    private readonly Expression shiftArg;
    private readonly string functionName;

    public BitShift(ParserContext context, bool isLeftShift)
    {
        this.isLeftShift = isLeftShift;
        this.functionName = isLeftShift ? "left_shift" : "right_shift";
        this.numArg = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.shiftArg = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Operator form of the shift: the <c>&lt;&lt;</c> / <c>&gt;&gt;</c>
    /// binary operators desugar to this over two already-parsed operands.
    /// Probe-confirmed against SQL Server 2025 that operator and function
    /// share identical semantics (<c>5 &lt;&lt; 1</c> = <c>LEFT_SHIFT(5, 1)</c>,
    /// binary → varbinary, negative-shift reversal), so both share this class;
    /// the function-name error wording stays <c>LeftShift</c> / <c>RightShift</c>
    /// to match the operator diagnostic real emits.
    /// </summary>
    public BitShift(bool isLeftShift, Expression numArg, Expression shiftArg)
    {
        this.isLeftShift = isLeftShift;
        this.functionName = isLeftShift ? "left_shift" : "right_shift";
        this.numArg = numArg;
        this.shiftArg = shiftArg;
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.numArg.Run(runtime);
        if (v.IsNull)
            throw BitOperandHelpers.ArgInvalidForBitFunc(this.functionName, "NULL", 1);
        var width = BitOperandHelpers.IntegerBitWidth(v.Type);
        if (width < 0)
            throw BitOperandHelpers.ArgInvalidForBitFunc(this.functionName, v.Type.ToString()!, 1);
        var shift = BitOperandHelpers.IntegerArgument(this.shiftArg.Run(runtime), this.functionName, 2, allowBit: false);
        var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        var bits = BitCount.ToUnsignedBits(v) & mask;

        // A negative distance shifts the other way rather than zeroing the
        // result — probe-confirmed LEFT_SHIFT(255, -4) = 15 and
        // RIGHT_SHIFT(255, -4) = 4080 — and any magnitude at or past the
        // operand's bit width zeroes it whichever way it points, which is
        // also how a distance beyond int range lands (that comparison runs
        // first, so the negation below can't overflow).
        var beyondWidth = shift <= -width || shift >= width;
        var towardHighBits = this.isLeftShift ^ (shift < 0);
        var distance = beyondWidth ? width : (int)(shift < 0 ? -shift : shift);
        var result = beyondWidth ? 0UL
            : towardHighBits ? (bits << distance) & mask
            : bits >> distance;
        return SetBit.UnsignedBitsToTyped(result, v.Type);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.numArg.GetSqlType(batch, resolveColumnType);

    internal override string DebugDisplay() => $"{(this.isLeftShift ? "LEFT_SHIFT" : "RIGHT_SHIFT")}({this.numArg.DebugDisplay()}, {this.shiftArg.DebugDisplay()})";
}
