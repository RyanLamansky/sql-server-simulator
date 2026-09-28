using System.Buffers.Binary;
using System.Data.SqlTypes;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SqlServerSimulator.Clr;

/// <summary>
/// The byte form <c>Format.Native</c> gives a CLR user-defined type: every
/// instance field in declaration order, each at a fixed width, so the whole
/// value is fixed-length and its bytes sort as the values do.
/// </summary>
/// <remarks>
/// Probed 2026-09-28 against SQL Server 2025 through <c>CAST(x AS varbinary)</c>:
/// integers are big-endian with the sign bit flipped (unsigned ones plain
/// big-endian), a <c>bool</c> is one byte 0 / 1, a <c>float</c> or
/// <c>double</c> is its IEEE bits big-endian with the sign bit flipped when
/// positive and every bit inverted when negative, and a nested struct
/// contributes its own fields in place. The <see cref="System.Data.SqlTypes"/>
/// structs contribute their Framework fields: a not-null byte then the value
/// for <see cref="SqlByte"/> / <see cref="SqlInt16"/> / <see cref="SqlInt32"/>
/// / <see cref="SqlInt64"/> / <see cref="SqlSingle"/> / <see cref="SqlDouble"/>,
/// the value scaled by 10,000 for <see cref="SqlMoney"/>, the day and
/// 1/300-second tick counts for <see cref="SqlDateTime"/>, and one byte (0
/// null, 1 false, 2 true) for <see cref="SqlBoolean"/>. Any other field type
/// is refused at <c>CREATE TYPE</c>.
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers", Justification = TrimmingJustification.RegisteredAssembly)]
[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers", Justification = TrimmingJustification.RegisteredAssembly)]
[UnconditionalSuppressMessage("Trimming", "IL2077:DynamicallyAccessedMembers", Justification = TrimmingJustification.RegisteredAssembly)]
internal sealed class ClrNativeLayout
{
    private readonly FieldInfo[] fields;
    private readonly NativeKind[] kinds;
    private readonly ClrNativeLayout?[] nested;
    private readonly Type type;

    /// <summary>The fixed byte width of a serialized value.</summary>
    public readonly int Size;

    private ClrNativeLayout(Type type, FieldInfo[] fields, NativeKind[] kinds, ClrNativeLayout?[] nested, int size)
    {
        this.type = type;
        this.fields = fields;
        this.kinds = kinds;
        this.nested = nested;
        this.Size = size;
    }

    private enum NativeKind : byte
    {
        Boolean,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        SqlBoolean,
        SqlByte,
        SqlInt16,
        SqlInt32,
        SqlInt64,
        SqlSingle,
        SqlDouble,
        SqlMoney,
        SqlDateTime,
        Struct,
    }

    /// <summary>
    /// Builds the layout of <paramref name="type"/>, or answers the first
    /// field that has none: a reference-typed field (real's Msg 6225) or a
    /// value type native serialization can't carry (Msg 6222).
    /// </summary>
    public static ClrNativeLayout? TryBuild(Type type, out FieldInfo? refused)
    {
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Array.Sort(fields, static (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
        var kinds = new NativeKind[fields.Length];
        var nested = new ClrNativeLayout?[fields.Length];
        var size = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            var fieldType = fields[i].FieldType;
            if (!fieldType.IsValueType)
            {
                refused = fields[i];
                return null;
            }

            NativeKind? kind = fieldType.IsEnum ? null : Type.GetTypeCode(fieldType) switch
            {
                TypeCode.Boolean => NativeKind.Boolean,
                TypeCode.Byte => NativeKind.Byte,
                TypeCode.SByte => NativeKind.SByte,
                TypeCode.Int16 => NativeKind.Int16,
                TypeCode.UInt16 => NativeKind.UInt16,
                TypeCode.Int32 => NativeKind.Int32,
                TypeCode.UInt32 => NativeKind.UInt32,
                TypeCode.Int64 => NativeKind.Int64,
                TypeCode.UInt64 => NativeKind.UInt64,
                TypeCode.Single => NativeKind.Single,
                TypeCode.Double => NativeKind.Double,
                TypeCode.Object => fieldType == typeof(SqlBoolean) ? NativeKind.SqlBoolean
                    : fieldType == typeof(SqlByte) ? NativeKind.SqlByte
                    : fieldType == typeof(SqlInt16) ? NativeKind.SqlInt16
                    : fieldType == typeof(SqlInt32) ? NativeKind.SqlInt32
                    : fieldType == typeof(SqlInt64) ? NativeKind.SqlInt64
                    : fieldType == typeof(SqlSingle) ? NativeKind.SqlSingle
                    : fieldType == typeof(SqlDouble) ? NativeKind.SqlDouble
                    : fieldType == typeof(SqlMoney) ? NativeKind.SqlMoney
                    : fieldType == typeof(SqlDateTime) ? NativeKind.SqlDateTime
                    : fieldType.Assembly == type.Assembly ? NativeKind.Struct
                    : null,
                _ => null,
            };

            if (kind is not { } resolved)
            {
                refused = fields[i];
                return null;
            }

            if (resolved == NativeKind.Struct)
            {
                if (TryBuild(fieldType, out refused) is not { } inner)
                    return null;
                nested[i] = inner;
                size += inner.Size;
            }
            else
            {
                size += Width(resolved);
            }

            kinds[i] = resolved;
        }

        refused = null;
        return new ClrNativeLayout(type, fields, kinds, nested, size);
    }

    private static int Width(NativeKind kind) => kind switch
    {
        NativeKind.Boolean or NativeKind.Byte or NativeKind.SByte or NativeKind.SqlBoolean => 1,
        NativeKind.Int16 or NativeKind.UInt16 or NativeKind.SqlByte => 2,
        NativeKind.SqlInt16 => 3,
        NativeKind.Int32 or NativeKind.UInt32 or NativeKind.Single => 4,
        NativeKind.SqlInt32 or NativeKind.SqlSingle => 5,
        NativeKind.Int64 or NativeKind.UInt64 or NativeKind.Double => 8,
        _ => 9,
    };

    /// <summary>The serialized bytes of <paramref name="instance"/>.</summary>
    public byte[] Serialize(object instance)
    {
        var bytes = new byte[this.Size];
        _ = this.Write(instance, bytes);
        return bytes;
    }

    /// <summary>
    /// The instance <paramref name="bytes"/> hold, which the caller has
    /// checked are exactly <see cref="Size"/> long.
    /// </summary>
    public object Deserialize(ReadOnlySpan<byte> bytes)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(this.type);
        _ = this.Read(instance, bytes);
        return instance;
    }

    private int Write(object instance, Span<byte> destination)
    {
        var offset = 0;
        for (var i = 0; i < this.fields.Length; i++)
        {
            var value = this.fields[i].GetValue(instance)!;
            var slot = destination[offset..];
            switch (this.kinds[i])
            {
                case NativeKind.Boolean:
                    slot[0] = (bool)value ? (byte)1 : (byte)0;
                    break;
                case NativeKind.Byte:
                    slot[0] = (byte)value;
                    break;
                case NativeKind.SByte:
                    slot[0] = (byte)((sbyte)value ^ unchecked((sbyte)0x80));
                    break;
                case NativeKind.Int16:
                    WriteInt16(slot, (short)value);
                    break;
                case NativeKind.UInt16:
                    BinaryPrimitives.WriteUInt16BigEndian(slot, (ushort)value);
                    break;
                case NativeKind.Int32:
                    WriteInt32(slot, (int)value);
                    break;
                case NativeKind.UInt32:
                    BinaryPrimitives.WriteUInt32BigEndian(slot, (uint)value);
                    break;
                case NativeKind.Int64:
                    WriteInt64(slot, (long)value);
                    break;
                case NativeKind.UInt64:
                    BinaryPrimitives.WriteUInt64BigEndian(slot, (ulong)value);
                    break;
                case NativeKind.Single:
                    WriteSingle(slot, (float)value);
                    break;
                case NativeKind.Double:
                    WriteDouble(slot, (double)value);
                    break;
                case NativeKind.SqlBoolean:
                    var flag = (SqlBoolean)value;
                    slot[0] = flag.IsNull ? (byte)0 : flag.Value ? (byte)2 : (byte)1;
                    break;
                case NativeKind.SqlByte:
                    var small = (SqlByte)value;
                    slot[0] = small.IsNull ? (byte)0 : (byte)1;
                    slot[1] = small.IsNull ? (byte)0 : small.Value;
                    break;
                case NativeKind.SqlInt16:
                    var int16 = (SqlInt16)value;
                    slot[0] = int16.IsNull ? (byte)0 : (byte)1;
                    WriteInt16(slot[1..], int16.IsNull ? (short)0 : int16.Value);
                    break;
                case NativeKind.SqlInt32:
                    var int32 = (SqlInt32)value;
                    slot[0] = int32.IsNull ? (byte)0 : (byte)1;
                    WriteInt32(slot[1..], int32.IsNull ? 0 : int32.Value);
                    break;
                case NativeKind.SqlInt64:
                    var int64 = (SqlInt64)value;
                    slot[0] = int64.IsNull ? (byte)0 : (byte)1;
                    WriteInt64(slot[1..], int64.IsNull ? 0 : int64.Value);
                    break;
                case NativeKind.SqlSingle:
                    var single = (SqlSingle)value;
                    slot[0] = single.IsNull ? (byte)0 : (byte)1;
                    WriteSingle(slot[1..], single.IsNull ? 0 : single.Value);
                    break;
                case NativeKind.SqlDouble:
                    var real = (SqlDouble)value;
                    slot[0] = real.IsNull ? (byte)0 : (byte)1;
                    WriteDouble(slot[1..], real.IsNull ? 0 : real.Value);
                    break;
                case NativeKind.SqlMoney:
                    var money = (SqlMoney)value;
                    slot[0] = money.IsNull ? (byte)0 : (byte)1;
                    WriteInt64(slot[1..], money.IsNull ? 0 : money.GetTdsValue());
                    break;
                case NativeKind.SqlDateTime:
                    var moment = (SqlDateTime)value;
                    slot[0] = moment.IsNull ? (byte)0 : (byte)1;
                    WriteInt32(slot[1..], moment.IsNull ? 0 : moment.DayTicks);
                    WriteInt32(slot[5..], moment.IsNull ? 0 : moment.TimeTicks);
                    break;
                default:
                    _ = this.nested[i]!.Write(value, slot);
                    break;
            }

            offset += this.kinds[i] == NativeKind.Struct ? this.nested[i]!.Size : Width(this.kinds[i]);
        }

        return offset;
    }

    private int Read(object instance, ReadOnlySpan<byte> source)
    {
        var offset = 0;
        for (var i = 0; i < this.fields.Length; i++)
        {
            var slot = source[offset..];
            object value;
            switch (this.kinds[i])
            {
                case NativeKind.Boolean:
                    value = slot[0] != 0;
                    break;
                case NativeKind.Byte:
                    value = slot[0];
                    break;
                case NativeKind.SByte:
                    value = (sbyte)(slot[0] ^ 0x80);
                    break;
                case NativeKind.Int16:
                    value = ReadInt16(slot);
                    break;
                case NativeKind.UInt16:
                    value = BinaryPrimitives.ReadUInt16BigEndian(slot);
                    break;
                case NativeKind.Int32:
                    value = ReadInt32(slot);
                    break;
                case NativeKind.UInt32:
                    value = BinaryPrimitives.ReadUInt32BigEndian(slot);
                    break;
                case NativeKind.Int64:
                    value = ReadInt64(slot);
                    break;
                case NativeKind.UInt64:
                    value = BinaryPrimitives.ReadUInt64BigEndian(slot);
                    break;
                case NativeKind.Single:
                    value = ReadSingle(slot);
                    break;
                case NativeKind.Double:
                    value = ReadDouble(slot);
                    break;
                case NativeKind.SqlBoolean:
                    value = slot[0] switch
                    {
                        0 => SqlBoolean.Null,
                        1 => SqlBoolean.False,
                        _ => SqlBoolean.True,
                    };
                    break;
                case NativeKind.SqlByte:
                    value = slot[0] == 0 ? SqlByte.Null : new SqlByte(slot[1]);
                    break;
                case NativeKind.SqlInt16:
                    value = slot[0] == 0 ? SqlInt16.Null : new SqlInt16(ReadInt16(slot[1..]));
                    break;
                case NativeKind.SqlInt32:
                    value = slot[0] == 0 ? SqlInt32.Null : new SqlInt32(ReadInt32(slot[1..]));
                    break;
                case NativeKind.SqlInt64:
                    value = slot[0] == 0 ? SqlInt64.Null : new SqlInt64(ReadInt64(slot[1..]));
                    break;
                case NativeKind.SqlSingle:
                    value = slot[0] == 0 ? SqlSingle.Null : new SqlSingle(ReadSingle(slot[1..]));
                    break;
                case NativeKind.SqlDouble:
                    value = slot[0] == 0 ? SqlDouble.Null : new SqlDouble(ReadDouble(slot[1..]));
                    break;
                case NativeKind.SqlMoney:
                    value = slot[0] == 0 ? SqlMoney.Null : SqlMoney.FromTdsValue(ReadInt64(slot[1..]));
                    break;
                case NativeKind.SqlDateTime:
                    value = slot[0] == 0 ? SqlDateTime.Null : new SqlDateTime(ReadInt32(slot[1..]), ReadInt32(slot[5..]));
                    break;
                default:
                    var inner = this.nested[i]!;
                    var boxed = RuntimeHelpers.GetUninitializedObject(inner.type);
                    _ = inner.Read(boxed, slot);
                    value = boxed;
                    break;
            }

            this.fields[i].SetValue(instance, value);
            offset += this.kinds[i] == NativeKind.Struct ? this.nested[i]!.Size : Width(this.kinds[i]);
        }

        return offset;
    }

    private static void WriteInt16(Span<byte> slot, short value) =>
        BinaryPrimitives.WriteUInt16BigEndian(slot, (ushort)(value ^ short.MinValue));

    private static void WriteInt32(Span<byte> slot, int value) =>
        BinaryPrimitives.WriteUInt32BigEndian(slot, (uint)(value ^ int.MinValue));

    private static void WriteInt64(Span<byte> slot, long value) =>
        BinaryPrimitives.WriteUInt64BigEndian(slot, (ulong)(value ^ long.MinValue));

    private static void WriteSingle(Span<byte> slot, float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        BinaryPrimitives.WriteInt32BigEndian(slot, bits < 0 ? ~bits : bits ^ int.MinValue);
    }

    private static void WriteDouble(Span<byte> slot, double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        BinaryPrimitives.WriteInt64BigEndian(slot, bits < 0 ? ~bits : bits ^ long.MinValue);
    }

    private static short ReadInt16(ReadOnlySpan<byte> slot) =>
        (short)(BinaryPrimitives.ReadUInt16BigEndian(slot) ^ 0x8000);

    private static int ReadInt32(ReadOnlySpan<byte> slot) =>
        (int)BinaryPrimitives.ReadUInt32BigEndian(slot) ^ int.MinValue;

    private static long ReadInt64(ReadOnlySpan<byte> slot) =>
        (long)BinaryPrimitives.ReadUInt64BigEndian(slot) ^ long.MinValue;

    private static float ReadSingle(ReadOnlySpan<byte> slot)
    {
        var bits = BinaryPrimitives.ReadInt32BigEndian(slot);
        return BitConverter.Int32BitsToSingle(bits < 0 ? bits ^ int.MinValue : ~bits);
    }

    private static double ReadDouble(ReadOnlySpan<byte> slot)
    {
        var bits = BinaryPrimitives.ReadInt64BigEndian(slot);
        return BitConverter.Int64BitsToDouble(bits < 0 ? bits ^ long.MinValue : ~bits);
    }
}
