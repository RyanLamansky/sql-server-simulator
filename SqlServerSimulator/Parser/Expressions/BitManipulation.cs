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
    /// raises Msg 8120, "Parameter 2 in function 'get_bit' is out of range 0 to 31.").
    /// </summary>
    public static int IntegerBitWidth(SqlType type)
    {
        return type == SqlType.TinyInt ? 8
            : type == SqlType.SmallInt ? 16
            : type == SqlType.Int32 ? 32
            : type == SqlType.BigInt ? 64
            : -1;
    }
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
/// <c>[0, bit-width-1]</c> for integer types (probe-confirmed Msg 8120
/// for out-of-range against SQL Server 2025, 2026-05-22). NULL operand
/// raises Msg 8116.
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
        var pos = this.positionArg.Run(runtime).CoerceTo(SqlType.Int32).AsInt32;
        if (pos < 0 || pos >= width)
            throw SimulatedSqlException.BitFunctionPositionOutOfRange("get_bit", 2, 0, width - 1);
        var bits = BitCount.ToUnsignedBits(v);
        return SqlValue.FromBoolean(((bits >> pos) & 1UL) == 1UL);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Bit;

    internal override string DebugDisplay() => $"GET_BIT({this.numArg.DebugDisplay()}, {this.positionArg.DebugDisplay()})";
}

/// <summary>
/// SQL <c>SET_BIT(num, position [, value])</c>: sets the bit at the given
/// 0-based position to the optional value argument (default 1). Returns
/// the same integer type as the first argument. Out-of-range position
/// raises Msg 8120; NULL operand raises Msg 8116; value other than 0 or
/// 1 raises Msg 8120 (probe-confirmed against SQL Server 2025).
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
        var pos = this.positionArg.Run(runtime).CoerceTo(SqlType.Int32).AsInt32;
        if (pos < 0 || pos >= width)
            throw SimulatedSqlException.BitFunctionPositionOutOfRange("set_bit", 2, 0, width - 1);
        var setTo = this.valueArg is null ? 1 : this.valueArg.Run(runtime).CoerceTo(SqlType.Int32).AsInt32;
        if (setTo is not (0 or 1))
            throw SimulatedSqlException.BitFunctionPositionOutOfRange("set_bit", 3, 0, 1);
        var bits = BitCount.ToUnsignedBits(v);
        bits = setTo == 1 ? bits | (1UL << pos) : bits & ~(1UL << pos);
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

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.numArg.Run(runtime);
        if (v.IsNull)
            throw BitOperandHelpers.ArgInvalidForBitFunc(this.functionName, "NULL", 1);
        var width = BitOperandHelpers.IntegerBitWidth(v.Type);
        if (width < 0)
            throw BitOperandHelpers.ArgInvalidForBitFunc(this.functionName, v.Type.ToString()!, 1);
        var shift = this.shiftArg.Run(runtime).CoerceTo(SqlType.Int32).AsInt32;
        var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        var bits = BitCount.ToUnsignedBits(v) & mask;
        var result = shift < 0 || shift >= width ? 0UL
            : this.isLeftShift ? (bits << shift) & mask
            : bits >> shift;
        return SetBit.UnsignedBitsToTyped(result, v.Type);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.numArg.GetSqlType(batch, resolveColumnType);

    internal override string DebugDisplay() => $"{(this.isLeftShift ? "LEFT_SHIFT" : "RIGHT_SHIFT")}({this.numArg.DebugDisplay()}, {this.shiftArg.DebugDisplay()})";
}
