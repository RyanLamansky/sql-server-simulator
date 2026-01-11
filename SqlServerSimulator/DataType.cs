using SqlServerSimulator.Parser.Tokens;
using System.Data;
using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// Bridges .NET's native types, the various <see cref="DbType"/>s, and SQL Server's actual behavior.
/// </summary>
internal abstract class DataType : IComparer<DataValue>, IComparable<DataType>
{
    private protected DataType()
    {
    }

    public abstract DbType Type { get; }

    /// <summary>
    /// Precedence according to the ranking at https://learn.microsoft.com/en-us/sql/t-sql/data-types/data-type-precedence-transact-sql
    /// </summary>
    protected abstract int Precedence { get; }

    public int CompareTo(DataType? other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return this == other ? 0 : this.CompareTo(other);
    }

    public DataValue ConvertFrom(object? value) => ConvertFrom(new DataValue(value, this));

    public abstract DataValue ConvertFrom(DataValue value);

    public abstract int Compare(DataValue x, DataValue y);

    public abstract int DataLength(DataValue value);

    public override string ToString() => Type.ToString();

    public static readonly DataType BuiltInDbBoolean = new DbBoolean();

    public static readonly DataType BuiltInDbByte = new DbByte();

    public static readonly DataType BuiltInDbInt16 = new DbInt16();

    public static readonly DataType BuiltInDbInt32 = new DbInt32();

    public static readonly DataType BuiltInDbAnsiString = new DbAnsiString();

    public static readonly DataType BuiltInDbString = new DbString();

    public static readonly DataType BuiltInDbSystemName = new DbSystemName();

    public static NumericCompatibleDataType CommonNumeric(DataValue a, DataValue b, char op)
    {
        var (at, bt) = (a.Type, b.Type);
        return at is not NumericCompatibleDataType atb || bt is not NumericCompatibleDataType btb
            ? throw SimulatedSqlException.DataTypeIncompatible(at, bt, op)
            : at.CompareTo(bt) switch
            {
                < 0 => atb,
                0 => atb,
                > 0 => btb,
            };
    }

    public static BitwiseCompatibleDataType CommonInteger(DataValue a, DataValue b, char op)
    {
        var (at, bt) = (a.Type, b.Type);
        return at is not BitwiseCompatibleDataType atb || bt is not BitwiseCompatibleDataType btb
            ? throw SimulatedSqlException.DataTypeIncompatible(at, bt, op)
            : at.CompareTo(bt) switch
            {
                < 0 => atb,
                0 => atb,
                > 0 => btb,
            };
    }

    /// <summary>
    /// Looks up the <see cref="DataType"/> for the provided type name.
    /// </summary>
    /// <param name="name">The name of the type.</param>
    /// <param name="index">The 1-based index of the type, used for an error message.</param>
    /// <returns>The matching data type.</returns>
    /// <exception cref="SimulatedSqlException">Column, parameter, or variable #<paramref name="index"/>: Cannot find data type <paramref name="name"/>.</exception>
    public static DataType GetByName(Name name, int index)
    {
        Span<char> upper = stackalloc char[name.Span.Length];
        return name.Span.ToUpperInvariant(upper) switch
        {
            3 => upper switch
            {
                "BIT" => BuiltInDbBoolean,
                "INT" => BuiltInDbInt32,
                _ => null
            },
            7 => upper switch
            {
                "TINYINT" => BuiltInDbByte,
                _ => null
            },
            8 => upper switch
            {
                "SMALLINT" => BuiltInDbInt16,
                _ => null
            },
            _ => null,
        } ?? throw SimulatedSqlException.CannotFindDataType(name.Span, index);
    }

    public static DataType GetByDbType(DbType dbType) => dbType switch
    {
        DbType.Boolean => BuiltInDbBoolean,
        DbType.Byte => BuiltInDbByte,
        DbType.Int16 => BuiltInDbInt16,
        DbType.Int32 => BuiltInDbInt32,
        DbType.AnsiString => BuiltInDbAnsiString,
        DbType.String => BuiltInDbString,
        _ => throw new NotSupportedException($"Simulated data type parser doesn't recognize DbType {dbType}"),
    };

    public abstract class NumericCompatibleDataType : DataType
    {
        protected abstract int DataLength();

        public sealed override int DataLength(DataValue value) => DataLength();

        public abstract object? Add(object? a, object? b);

        public DataValue Add(DataValue a, DataValue b) => new(Add(a.Value, b.Value), this);

        public abstract object? Subtract(object? a, object? b);

        public DataValue Subtract(DataValue a, DataValue b) => new(Subtract(a.Value, b.Value), this);

        public abstract object? Multiply(object? a, object? b);

        public DataValue Multiply(DataValue a, DataValue b) => new(Multiply(a.Value, b.Value), this);

        public abstract object? Divide(object? a, object? b);

        public DataValue Divide(DataValue a, DataValue b) => new(Divide(a.Value, b.Value), this);
    }

    public abstract class BitwiseCompatibleDataType : NumericCompatibleDataType
    {
        public abstract object? BitwiseAnd(object? a, object? b);

        public DataValue BitwiseAnd(DataValue a, DataValue b) => new(BitwiseAnd(a.Value, b.Value), this);

        public abstract object? BitwiseOr(object? a, object? b);

        public DataValue BitwiseOr(DataValue a, DataValue b) => new(BitwiseOr(a.Value, b.Value), this);

        public abstract object? BitwiseExclusiveOr(object? a, object? b);

        public DataValue BitwiseExclusiveOr(DataValue a, DataValue b) => new(BitwiseExclusiveOr(a.Value, b.Value), this);
    }

    private sealed class DbBoolean : BitwiseCompatibleDataType
    {
        public override DbType Type => DbType.Boolean;

        protected override int Precedence => 20;

        public override DataValue ConvertFrom(DataValue value) => new(Convert.ToBoolean(value, CultureInfo.InvariantCulture), this);

        public override int Compare(DataValue x, DataValue y) => throw new NotImplementedException();

        protected override int DataLength() => 1;

        public override object? Add(object? a, object? b) => throw new NotImplementedException();

        public override object? Subtract(object? a, object? b) => throw new NotImplementedException();

        public override object? Multiply(object? a, object? b) => throw new NotImplementedException();

        public override object? Divide(object? a, object? b) => throw new NotImplementedException();

        public override object? BitwiseAnd(object? a, object? b) => throw new NotImplementedException();

        public override object? BitwiseOr(object? a, object? b) => throw new NotImplementedException();

        public override object? BitwiseExclusiveOr(object? a, object? b) => throw new NotImplementedException();
    }

    private sealed class DbByte : BitwiseCompatibleDataType
    {
        public override DbType Type => DbType.Byte;

        protected override int Precedence => 19;

        public override DataValue ConvertFrom(DataValue value) => new(Convert.ToByte(value, CultureInfo.InvariantCulture), this);

        public override int Compare(DataValue x, DataValue y) => throw new NotImplementedException();

        protected override int DataLength() => 1;

        public override object? Add(object? a, object? b) => throw new NotImplementedException();

        public override object? Subtract(object? a, object? b) => throw new NotImplementedException();

        public override object? Multiply(object? a, object? b) => throw new NotImplementedException();

        public override object? Divide(object? a, object? b) => throw new NotImplementedException();

        public override object? BitwiseAnd(object? a, object? b) => throw new NotImplementedException();

        public override object? BitwiseOr(object? a, object? b) => throw new NotImplementedException();

        public override object? BitwiseExclusiveOr(object? a, object? b) => throw new NotImplementedException();
    }

    private sealed class DbInt16 : BitwiseCompatibleDataType
    {
        public override DbType Type => DbType.Int16;

        protected override int Precedence => 18;

        public override DataValue ConvertFrom(DataValue value) => new(Convert.ToInt16(value, CultureInfo.InvariantCulture), this);

        public override int Compare(DataValue x, DataValue y) => throw new NotImplementedException();

        protected override int DataLength() => 2;

        public override object? Add(object? a, object? b) => throw new NotImplementedException();

        public override object? Subtract(object? a, object? b) => throw new NotImplementedException();

        public override object? Multiply(object? a, object? b) => throw new NotImplementedException();

        public override object? Divide(object? a, object? b) => throw new NotImplementedException();

        public override object? BitwiseAnd(object? a, object? b) => throw new NotImplementedException();

        public override object? BitwiseOr(object? a, object? b) => throw new NotImplementedException();

        public override object? BitwiseExclusiveOr(object? a, object? b) => throw new NotImplementedException();
    }

    private sealed class DbInt32 : BitwiseCompatibleDataType
    {
        public override DbType Type => DbType.Int32;

        protected override int Precedence => 17;

        public static int ToNative(DataValue value) => Convert.ToInt32(value.Value, CultureInfo.InvariantCulture);

        public static int ToNative(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

        public override DataValue ConvertFrom(DataValue value) => new(value.Value is null ? null : ToNative(value.Value), this);

        public override int Compare(DataValue x, DataValue y)
        {
            var (xv, yv) = (x.Value, y.Value);
            if (xv is null)
                return yv is null ? 0 : -1;
            else if (yv is null)
                return 1;

            return ToNative(x).CompareTo(ToNative(y));
        }

        protected override int DataLength() => 4;

        private static bool TryToNative(object? rawA, object? rawB, out int a, out int b)
        {
            if (rawA is null || rawB is null)
            {
                a = default;
                b = default;
                return false;
            }

            a = ToNative(rawA);
            b = ToNative(rawB);
            return true;
        }

        public override object? Add(object? a, object? b) => TryToNative(a, b, out var nativeA, out var nativeB) ? nativeA + nativeB : null;

        public override object? Subtract(object? a, object? b) => TryToNative(a, b, out var nativeA, out var nativeB) ? nativeA - nativeB : null;

        public override object? Multiply(object? a, object? b) => TryToNative(a, b, out var nativeA, out var nativeB) ? nativeA * nativeB : null;

        public override object? Divide(object? a, object? b) => TryToNative(a, b, out var nativeA, out var nativeB) ? nativeA / nativeB : null;

        public override object? BitwiseAnd(object? a, object? b) => TryToNative(a, b, out var nativeA, out var nativeB) ? nativeA & nativeB : null;

        public override object? BitwiseOr(object? a, object? b) => TryToNative(a, b, out var nativeA, out var nativeB) ? nativeA | nativeB : null;

        public override object? BitwiseExclusiveOr(object? a, object? b) => TryToNative(a, b, out var nativeA, out var nativeB) ? nativeA ^ nativeB : null;
    }

    private sealed class DbAnsiString : DataType
    {
        public override DbType Type => DbType.AnsiString;

        protected override int Precedence => 28;

        public override DataValue ConvertFrom(DataValue value) => new(value.Value?.ToString(), this);

        public override int Compare(DataValue x, DataValue y) => throw new NotImplementedException();

        public override int DataLength(DataValue value) => throw new NotImplementedException();
    }

    private class DbString : DataType
    {
        public override DbType Type => DbType.String;

        protected override int Precedence => 26;

        public override DataValue ConvertFrom(DataValue value) => new(value.Value?.ToString(), this);

        public override int Compare(DataValue x, DataValue y) => throw new NotImplementedException();
        public override int DataLength(DataValue value) => throw new NotImplementedException();
    }

    private sealed class DbSystemName : DbString
    {
    }
}
