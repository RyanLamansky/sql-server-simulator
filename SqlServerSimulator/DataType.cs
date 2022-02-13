using System;
using System.Data;

namespace SqlServerSimulator;

using Parser;
using Parser.Tokens;

internal abstract class DataType
{
    private protected DataType()
    {
    }

    public abstract DbType Type { get; }

    public abstract object ConvertFrom(object value);

    public override string ToString() => Type.ToString();

    private static readonly DbInt32 BuiltInDbInt32 = new();

    private static readonly DbString BuiltInDbString = new();

    public static DataType GetByName(Name name)
    {
        var keyword = name.Parse();
        return keyword switch
        {
            Keyword.Int => BuiltInDbInt32,
            _ => throw new NotSupportedException($"Simulated data type parser doesn't recognize {keyword}"),
        };
    }

    public static DataType GetByDbType(DbType dbType) => dbType switch
    {
        DbType.String => BuiltInDbString,
        DbType.Int32 => BuiltInDbInt32,
        _ => throw new NotSupportedException($"Simulated data type parser doesn't recognize DbType {dbType}"),
    };

    private sealed class DbString : DataType
    {
        public override DbType Type => DbType.String;

        public override object ConvertFrom(object value) => value.ToString() ?? throw new InvalidOperationException("value's ToString method returned null.");
    }

    private sealed class DbInt32 : DataType
    {
        public override DbType Type => DbType.Int32;

        public override object ConvertFrom(object value) => Convert.ToInt32(value);
    }
}
