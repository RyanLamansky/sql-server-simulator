using SqlServerSimulator.Parser.Tokens;
using System.Data;
using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// Bridges .NET's native types, the various <see cref="DbType"/>s, and SQL Server's actual behavior.
/// </summary>
internal abstract class DataType
{
    private protected DataType()
    {
    }

    public abstract DbType Type { get; }

    public abstract object ConvertFrom(object value);

    public override string ToString() => Type.ToString();

    public static readonly DataType BuiltInDbBoolean = new DbBoolean();

    public static readonly DataType BuiltInDbByte = new DbByte();

    public static readonly DataType BuiltInDbInt16 = new DbInt16();

    public static readonly DataType BuiltInDbInt32 = new DbInt32();

    public static readonly DataType BuiltInDbAnsiString = new DbAnsiString();

    public static readonly DataType BuiltInDbString = new DbString();

    public static readonly DataType BuiltInDbSystemName = new DbSystemName();

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

    private sealed class DbBoolean : DataType
    {
        public override DbType Type => DbType.Boolean;

        public override object ConvertFrom(object value) => Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private sealed class DbByte : DataType
    {
        public override DbType Type => DbType.Byte;

        public override object ConvertFrom(object value) => Convert.ToByte(value, CultureInfo.InvariantCulture);
    }

    private sealed class DbInt16 : DataType
    {
        public override DbType Type => DbType.Int16;

        public override object ConvertFrom(object value) => Convert.ToInt16(value, CultureInfo.InvariantCulture);
    }

    private sealed class DbInt32 : DataType
    {
        public override DbType Type => DbType.Int32;

        public override object ConvertFrom(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private sealed class DbAnsiString : DataType
    {
        public override DbType Type => DbType.AnsiString;

        public override object ConvertFrom(object value) => value.ToString() ?? throw new InvalidOperationException("value's ToString method returned null.");
    }

    private class DbString : DataType
    {
        public override DbType Type => DbType.String;

        public override object ConvertFrom(object value) => value.ToString() ?? throw new InvalidOperationException("value's ToString method returned null.");
    }

    private sealed class DbSystemName : DbString
    {
    }
}
