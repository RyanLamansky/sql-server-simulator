using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using System.Data;
using System.Globalization;

namespace SqlServerSimulator;

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

    public static DataType GetByName(Name name)
    {
        var keyword = name.Parse();
        return keyword switch
        {
            Keyword.Bit => BuiltInDbBoolean,
            Keyword.TinyInt => BuiltInDbByte,
            Keyword.SmallInt => BuiltInDbInt16,
            Keyword.Int => BuiltInDbInt32,
            _ => throw new NotSupportedException($"Simulated data type parser doesn't recognize {keyword}"),
        };
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
