using SqlServerSimulator.Parser.Tokens;
using System.Buffers.Binary;
using System.Data;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Type system for the storage layer. Each <see cref="SqlType"/> owns its own
/// byte representation via <see cref="Encode"/> and <see cref="Decode"/>, so
/// the row-level encoder does not need to know per-type layout details.
/// </summary>
internal abstract class SqlType
{
    private protected SqlType()
    {
    }

    /// <summary>
    /// True for types whose stored bytes are a constant width, regardless of value.
    /// </summary>
    public abstract bool IsFixedLength { get; }

    /// <summary>
    /// Byte width for fixed-length types. Throws for variable-length types.
    /// </summary>
    public virtual int FixedLength => throw new NotSupportedException($"{this} is variable-length; FixedLength is undefined.");

    /// <summary>
    /// Bytes a non-NULL value contributes to a row's variable-length data area.
    /// Throws for fixed-length types (whose values live in the fixed section instead).
    /// </summary>
    public virtual int GetVariableByteCount(SqlValue value) => throw new NotSupportedException($"{this} is fixed-length; GetVariableByteCount is undefined.");

    /// <summary>
    /// Writes a non-NULL value's bytes to <paramref name="destination"/>.
    /// NULL handling is the row encoder's responsibility (via the row-level NULL bitmap).
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public abstract int Encode(SqlValue value, Span<byte> destination);

    /// <summary>
    /// Reads bytes and reconstructs a non-NULL value.
    /// For fixed-length types, <paramref name="source"/>'s length is the type's fixed width.
    /// For variable-length types, <paramref name="source"/>'s length is the value's byte count.
    /// </summary>
    public abstract SqlValue Decode(ReadOnlySpan<byte> source);

    /// <summary>
    /// SQL Server data-type precedence for implicit conversion. Higher means
    /// "wins" when a binary expression must pick a common type. The numeric
    /// values are simulator-internal; only their relative ordering matters.
    /// </summary>
    /// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/data-types/data-type-precedence-transact-sql</remarks>
    public int Precedence =>
        this == BigInt ? 5
        : this == Int32 ? 4
        : this == SmallInt ? 3
        : this == TinyInt ? 2
        : this == Bit ? 1
        : this == SystemName ? 12
        : this == NVarchar ? 11
        : this == Varchar ? 10
        : throw new NotSupportedException($"No precedence defined for {this}.");

    /// <summary>
    /// Returns the higher-precedence type when <paramref name="a"/> and
    /// <paramref name="b"/> share a category (both numeric or both string).
    /// Cross-category promotion isn't implemented; SQL Server allows it via
    /// implicit conversion that may fail at runtime, but the simulator doesn't
    /// model that today.
    /// </summary>
    public static SqlType Promote(SqlType a, SqlType b)
    {
        if (a == b)
            return a;

        var sameCategory = (IsIntegerCategory(a) && IsIntegerCategory(b)) || (IsStringCategory(a) && IsStringCategory(b));
        return sameCategory
            ? a.Precedence >= b.Precedence ? a : b
            : throw new NotSupportedException($"Cross-category type promotion isn't implemented: {a} vs {b}.");
    }

    /// <summary>True for SQL integer-family types (bit, tinyint, smallint, int, bigint).</summary>
    public static bool IsIntegerCategory(SqlType type) =>
        type == Bit || type == TinyInt || type == SmallInt || type == Int32 || type == BigInt;

    /// <summary>True for SQL string-family types (varchar, nvarchar, sysname).</summary>
    public static bool IsStringCategory(SqlType type) =>
        type == Varchar || type == NVarchar || type == SystemName;

    public static readonly SqlType Int32 = new Int32SqlType();

    public static readonly SqlType BigInt = new BigIntSqlType();

    public static readonly SqlType SmallInt = new SmallIntSqlType();

    public static readonly SqlType TinyInt = new TinyIntSqlType();

    public static readonly SqlType Bit = new BitSqlType();

    /// <remarks>
    /// Stored as UTF-8 bytes. The simulator does not model SQL Server's
    /// per-collation code pages; UTF-8 round-trips arbitrary Unicode and matches
    /// the modern SQL Server default for UTF-8-enabled collations.
    /// </remarks>
    public static readonly SqlType Varchar = new VarcharSqlType();

    /// <remarks>
    /// Stored as UTF-16 LE bytes (2 bytes per BMP code unit, surrogate pairs for
    /// supplementary characters), matching SQL Server's on-disk nvarchar layout.
    /// </remarks>
    public static readonly SqlType NVarchar = new NVarcharSqlType();

    /// <remarks>
    /// SQL Server's <c>sysname</c> — historically <c>varchar(30)</c> in 6.5,
    /// modernly <c>nvarchar(128) NOT NULL</c>. Stored on disk identically to
    /// <see cref="NVarchar"/> (UTF-16 LE), but kept as a distinct
    /// <see cref="SqlType"/> instance because it appears with its own identity
    /// across system catalogs and in user-visible <c>sp_help</c> output.
    /// </remarks>
    public static readonly SqlType SystemName = new SystemNameSqlType();

    /// <summary>
    /// Resolves an ADO.NET <see cref="DbType"/> to its corresponding
    /// <see cref="SqlType"/>. Used at the parameter boundary, where typed
    /// parameter values arrive with a <see cref="DbType"/> and need to land in
    /// the simulator's type system.
    /// </summary>
    /// <exception cref="NotSupportedException">No mapping exists for <paramref name="dbType"/>.</exception>
    public static SqlType GetByDbType(DbType dbType) => dbType switch
    {
        DbType.Boolean => Bit,
        DbType.Byte => TinyInt,
        DbType.Int16 => SmallInt,
        DbType.Int32 => Int32,
        DbType.Int64 => BigInt,
        DbType.AnsiString => Varchar,
        DbType.String => NVarchar,
        _ => throw new NotSupportedException($"No SqlType mapping for DbType {dbType}."),
    };

    /// <summary>
    /// Resolves a SQL type name (as seen in CREATE TABLE) to its <see cref="SqlType"/>.
    /// </summary>
    /// <param name="name">The type name token.</param>
    /// <param name="index">1-based column index, used for the error message.</param>
    /// <exception cref="SimulatedSqlException">Column, parameter, or variable #<paramref name="index"/>: Cannot find data type <paramref name="name"/>.</exception>
    public static SqlType GetByName(Name name, int index)
    {
        Span<char> upper = stackalloc char[name.Span.Length];
        return name.Span.ToUpperInvariant(upper) switch
        {
            3 => upper switch
            {
                "BIT" => Bit,
                "INT" => Int32,
                _ => null
            },
            7 => upper switch
            {
                "TINYINT" => TinyInt,
                _ => null
            },
            8 => upper switch
            {
                "SMALLINT" => SmallInt,
                _ => null
            },
            _ => null,
        } ?? throw SimulatedSqlException.CannotFindDataType(name.Span, index);
    }

    private sealed class Int32SqlType : SqlType
    {
        public override bool IsFixedLength => true;

        public override int FixedLength => 4;

        public override int Encode(SqlValue value, Span<byte> destination)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, value.AsInt32);
            return 4;
        }

        public override SqlValue Decode(ReadOnlySpan<byte> source)
            => SqlValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(source));

        public override string ToString() => "int";
    }

    private sealed class BigIntSqlType : SqlType
    {
        public override bool IsFixedLength => true;

        public override int FixedLength => 8;

        public override int Encode(SqlValue value, Span<byte> destination)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination, value.AsInt64);
            return 8;
        }

        public override SqlValue Decode(ReadOnlySpan<byte> source)
            => SqlValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(source));

        public override string ToString() => "bigint";
    }

    private sealed class SmallIntSqlType : SqlType
    {
        public override bool IsFixedLength => true;

        public override int FixedLength => 2;

        public override int Encode(SqlValue value, Span<byte> destination)
        {
            BinaryPrimitives.WriteInt16LittleEndian(destination, value.AsInt16);
            return 2;
        }

        public override SqlValue Decode(ReadOnlySpan<byte> source)
            => SqlValue.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(source));

        public override string ToString() => "smallint";
    }

    /// <remarks>
    /// SQL Server's <c>tinyint</c> is unsigned 0-255, stored as a single byte.
    /// </remarks>
    private sealed class TinyIntSqlType : SqlType
    {
        public override bool IsFixedLength => true;

        public override int FixedLength => 1;

        public override int Encode(SqlValue value, Span<byte> destination)
        {
            destination[0] = value.AsByte;
            return 1;
        }

        public override SqlValue Decode(ReadOnlySpan<byte> source)
            => SqlValue.FromByte(source[0]);

        public override string ToString() => "tinyint";
    }

    /// <remarks>
    /// One byte per Bit value; SQL Server packs up to 8 Bit columns into a shared
    /// byte but that optimization isn't worth the complexity at this stage.
    /// </remarks>
    private sealed class BitSqlType : SqlType
    {
        public override bool IsFixedLength => true;

        public override int FixedLength => 1;

        public override int Encode(SqlValue value, Span<byte> destination)
        {
            destination[0] = value.AsBoolean ? (byte)0x01 : (byte)0x00;
            return 1;
        }

        public override SqlValue Decode(ReadOnlySpan<byte> source) => source[0] switch
        {
            0x00 => SqlValue.FromBoolean(false),
            0x01 => SqlValue.FromBoolean(true),
            var b => throw new InvalidDataException($"Invalid Bit byte: 0x{b:X2}."),
        };

        public override string ToString() => "bit";
    }

    private sealed class VarcharSqlType : SqlType
    {
        public override bool IsFixedLength => false;

        public override int GetVariableByteCount(SqlValue value) => Encoding.UTF8.GetByteCount(value.AsString);

        public override int Encode(SqlValue value, Span<byte> destination) => Encoding.UTF8.GetBytes(value.AsString, destination);

        public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromVarchar(Encoding.UTF8.GetString(source));

        public override string ToString() => "varchar";
    }

    private sealed class NVarcharSqlType : SqlType
    {
        public override bool IsFixedLength => false;

        public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

        public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

        public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNVarchar(Encoding.Unicode.GetString(source));

        public override string ToString() => "nvarchar";
    }

    private sealed class SystemNameSqlType : SqlType
    {
        public override bool IsFixedLength => false;

        public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

        public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

        public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromSystemName(Encoding.Unicode.GetString(source));

        public override string ToString() => "sysname";
    }
}
