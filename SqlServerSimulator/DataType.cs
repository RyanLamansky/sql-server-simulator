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

    public static DataType GetByName(Name name)
    {
        var keyword = name.Parse();
        return keyword switch
        {
            Keyword.Int => new DbInt32(),
            _ => throw new NotSupportedException($"Simulated data type parser doesn't recognize {keyword}"),
        };
    }

    private sealed class DbInt32 : DataType
    {
        public override DbType Type => DbType.Int32;

        public override object ConvertFrom(object value) => Convert.ToInt32(value);
    }
}
