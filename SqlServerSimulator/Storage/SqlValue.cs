#if DEBUG
using System.Globalization;
#endif

namespace SqlServerSimulator.Storage;

/// <summary>
/// A type-tagged value for the storage layer.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="SqlValue"/> always carries its <see cref="Type"/>; NULL values
/// retain their type so that, e.g., <c>NULL</c> of <c>int</c> can be
/// distinguished from <c>NULL</c> of <c>varchar</c> when needed.
/// </para>
/// <para>
/// The payload is split into a 64-bit primitive field (covering all current
/// fixed-width primitive types) and a reference field (for variable-length
/// types added later such as <c>varchar</c> and <c>varbinary</c>). Each
/// <see cref="SqlType"/> documents which field it uses.
/// </para>
/// </remarks>
internal readonly struct SqlValue : IEquatable<SqlValue>, IComparable<SqlValue>
{
    private readonly long primitive;
    private readonly object? reference;

    public readonly SqlType Type;

    public readonly bool IsNull;

    private SqlValue(SqlType type, long primitive, object? reference, bool isNull)
    {
        this.Type = type;
        this.primitive = primitive;
        this.reference = reference;
        this.IsNull = isNull;
    }

    /// <summary>NULL value of the given type.</summary>
    public static SqlValue Null(SqlType type) => new(type, 0, null, isNull: true);

    /// <summary>Non-NULL <see cref="int"/> value.</summary>
    public static SqlValue FromInt32(int value) => new(SqlType.Int32, value, null, isNull: false);

    /// <summary>Non-NULL <see cref="long"/> (SQL <c>bigint</c>) value.</summary>
    public static SqlValue FromInt64(long value) => new(SqlType.BigInt, value, null, isNull: false);

    /// <summary>Non-NULL <see cref="short"/> (SQL <c>smallint</c>) value.</summary>
    public static SqlValue FromInt16(short value) => new(SqlType.SmallInt, value, null, isNull: false);

    /// <summary>Non-NULL <see cref="byte"/> (SQL <c>tinyint</c>) value.</summary>
    public static SqlValue FromByte(byte value) => new(SqlType.TinyInt, value, null, isNull: false);

    /// <summary>Non-NULL <see cref="bool"/> (SQL <c>bit</c>) value.</summary>
    public static SqlValue FromBoolean(bool value) => new(SqlType.Bit, value ? 1L : 0L, null, isNull: false);

    /// <summary>Non-NULL SQL <c>varchar</c> value (UTF-8 on disk, .NET string in memory).</summary>
    public static SqlValue FromVarchar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.Varchar, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL <c>nvarchar</c> value (UTF-16 LE on disk, .NET string in memory).</summary>
    public static SqlValue FromNVarchar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.NVarchar, 0, value, isNull: false);
    }

    /// <summary>Non-NULL SQL <c>sysname</c> value (encoded identically to <c>nvarchar</c>; identity preserved across system catalogs).</summary>
    public static SqlValue FromSystemName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(SqlType.SystemName, 0, value, isNull: false);
    }

    /// <summary>Implicit lift from <see cref="int"/>; convenient in test code.</summary>
    public static implicit operator SqlValue(int value)
    {
        return FromInt32(value);
    }

    private T As<T>(SqlType expected, Func<long, T> project) => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type != expected
            ? throw new InvalidOperationException($"Value is {this.Type}, not {expected}.")
            : project(this.primitive);

    /// <summary>Returns the value as <see cref="int"/>. Throws if NULL or wrong type.</summary>
    public int AsInt32 => this.As(SqlType.Int32, p => (int)p);

    /// <summary>Returns the value as <see cref="long"/>. Throws if NULL or wrong type.</summary>
    public long AsInt64 => this.As(SqlType.BigInt, p => p);

    /// <summary>Returns the value as <see cref="short"/>. Throws if NULL or wrong type.</summary>
    public short AsInt16 => this.As(SqlType.SmallInt, p => (short)p);

    /// <summary>Returns the value as <see cref="byte"/>. Throws if NULL or wrong type.</summary>
    public byte AsByte => this.As(SqlType.TinyInt, p => (byte)p);

    /// <summary>Returns the value as <see cref="bool"/>. Throws if NULL or wrong type.</summary>
    public bool AsBoolean => this.As(SqlType.Bit, p => p != 0);

    /// <summary>Returns the value as <see cref="string"/>. Throws if NULL or if not a string-typed value.</summary>
    public string AsString => this.IsNull
        ? throw new InvalidOperationException("Value is NULL.")
        : this.Type != SqlType.Varchar && this.Type != SqlType.NVarchar && this.Type != SqlType.SystemName
            ? throw new InvalidOperationException($"Value is {this.Type}, not a string type.")
            : (string)this.reference!;

    /// <summary>
    /// Returns the .NET object representation of this value, or <c>null</c> for SQL NULL.
    /// Used by the data reader at the public-API boundary to expose values via
    /// <see cref="object"/>-typed accessors.
    /// </summary>
    internal object? ToObject() => this.IsNull ? null : this.Type switch
    {
        var t when t == SqlType.Int32 => this.AsInt32,
        var t when t == SqlType.BigInt => this.AsInt64,
        var t when t == SqlType.SmallInt => this.AsInt16,
        var t when t == SqlType.TinyInt => this.AsByte,
        var t when t == SqlType.Bit => this.AsBoolean,
        var t when t == SqlType.Varchar || t == SqlType.NVarchar || t == SqlType.SystemName => this.AsString,
        _ => throw new NotSupportedException($"No object representation for {this.Type}."),
    };

    /// <summary>
    /// Returns this value re-typed as <paramref name="target"/> when a safe
    /// conversion exists; widens or narrows between SQL integer-family types
    /// using <c>checked</c> arithmetic so out-of-range narrowings throw
    /// <see cref="OverflowException"/>. Bit participates as a 0/1 integer
    /// (true=1, false=0; non-zero on the way back is true). Same-typed values
    /// pass through. NULLs re-type freely (no overflow possible). Cross-
    /// category coercions (integer↔string) aren't implemented.
    /// </summary>
    /// <exception cref="OverflowException">Value doesn't fit the narrower target type.</exception>
    /// <exception cref="NotSupportedException">No conversion is implemented between this value's type and <paramref name="target"/>.</exception>
    public SqlValue CoerceTo(SqlType target)
    {
        if (this.Type == target)
            return this;
        if (this.IsNull)
            return Null(target);

        if (SqlType.IsIntegerCategory(this.Type) && SqlType.IsIntegerCategory(target))
        {
            var widened = this.Type == SqlType.Bit ? (this.AsBoolean ? 1L : 0L)
                : this.Type == SqlType.TinyInt ? this.AsByte
                : this.Type == SqlType.SmallInt ? this.AsInt16
                : this.Type == SqlType.Int32 ? this.AsInt32
                : this.AsInt64;
            return target == SqlType.Bit ? FromBoolean(widened != 0)
                : target == SqlType.TinyInt ? FromByte(checked((byte)widened))
                : target == SqlType.SmallInt ? FromInt16(checked((short)widened))
                : target == SqlType.Int32 ? FromInt32(checked((int)widened))
                : FromInt64(widened);
        }

        throw new NotSupportedException($"No coercion implemented from {this.Type} to {target}.");
    }

    public bool Equals(SqlValue other)
        => this.Type == other.Type
        && this.IsNull == other.IsNull
        && (this.IsNull || (this.primitive == other.primitive && Equals(this.reference, other.reference)));

    /// <summary>
    /// Orders two non-NULL same-typed values. NULL handling is the caller's
    /// responsibility (SQL's NULL-comparison semantics differ from .NET's
    /// IComparable convention, so we throw here rather than pick a side).
    /// </summary>
    /// <exception cref="InvalidOperationException">Either operand is NULL.</exception>
    /// <exception cref="NotSupportedException">The operands' types differ, or comparison for that type isn't implemented yet.</exception>
    public int CompareTo(SqlValue other) =>
        this.IsNull || other.IsNull ? throw new InvalidOperationException("CompareTo on NULL is undefined; check IsNull before calling.")
        : this.Type != other.Type ? throw new NotSupportedException($"Cross-type comparison isn't implemented: {this.Type} vs {other.Type}.")
        : this.Type == SqlType.Int32 ? this.AsInt32.CompareTo(other.AsInt32)
        : this.Type == SqlType.BigInt ? this.AsInt64.CompareTo(other.AsInt64)
        : this.Type == SqlType.SmallInt ? this.AsInt16.CompareTo(other.AsInt16)
        : this.Type == SqlType.TinyInt ? this.AsByte.CompareTo(other.AsByte)
        : this.Type == SqlType.Bit ? this.AsBoolean.CompareTo(other.AsBoolean)
        : throw new NotSupportedException($"Comparison for {this.Type} isn't implemented yet.");

    public override bool Equals(object? obj) => obj is SqlValue other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Type, this.IsNull, this.primitive, this.reference);

    public static bool operator ==(SqlValue left, SqlValue right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SqlValue left, SqlValue right)
    {
        return !left.Equals(right);
    }

#if DEBUG
    public override string ToString() => this.IsNull ? $"NULL ({this.Type})" : $"{this.AsCurrentType()} ({this.Type})";

    private string AsCurrentType() =>
        this.Type == SqlType.Int32 ? this.AsInt32.ToString(CultureInfo.InvariantCulture) :
        this.Type == SqlType.BigInt ? this.AsInt64.ToString(CultureInfo.InvariantCulture) :
        this.Type == SqlType.SmallInt ? this.AsInt16.ToString(CultureInfo.InvariantCulture) :
        this.Type == SqlType.TinyInt ? this.AsByte.ToString(CultureInfo.InvariantCulture) :
        this.Type == SqlType.Bit ? (this.AsBoolean ? "1" : "0") :
        this.Type == SqlType.Varchar || this.Type == SqlType.NVarchar || this.Type == SqlType.SystemName ? $"'{this.AsString}'" :
        "?";
#endif
}
