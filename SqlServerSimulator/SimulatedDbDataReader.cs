using System.Collections;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics.CodeAnalysis;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

sealed class SimulatedDbDataReader : DbDataReader
{
    private readonly IEnumerator<SimulatedQueryResult> results;
    private RowCursor cursor;
    private int recordsAffected;

    public SimulatedDbDataReader(IEnumerable<SimulatedQueryResult> results)
    {
        this.results = results.GetEnumerator();
        this.cursor = this.results.MoveNext() ? this.results.Current.CreateCursor() : EmptyCursor.Instance;
    }

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => throw new NotImplementedException();

    public override int Depth => throw new NotImplementedException();

    public override int FieldCount => cursor.FieldCount;

    public override bool HasRows => throw new NotImplementedException();

    public override bool IsClosed => throw new NotImplementedException();

    public override int RecordsAffected => recordsAffected;

    public override bool GetBoolean(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsBoolean;
    }

    public override byte GetByte(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        throw new NotImplementedException();
    }

    public override char GetChar(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        throw new NotImplementedException();
    }

    public override string GetDataTypeName(int ordinal)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Mirrors SqlClient's polymorphic <c>GetDateTime</c>: a <c>date</c> column
    /// surfaces as <see cref="DateTime"/> at midnight (<see cref="DateTimeKind.Unspecified"/>),
    /// while <c>datetime</c> / <c>smalldatetime</c> / <c>datetime2</c> return
    /// their stored value directly. Other column types raise
    /// <see cref="InvalidCastException"/>.
    /// </summary>
    public override DateTime GetDateTime(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException()
            : v.Type switch
            {
                var t when t == SqlType.Date => v.AsDate.ToDateTime(TimeOnly.MinValue),
                var t when t == SqlType.DateTime => v.AsDateTime,
                var t when t == SqlType.SmallDateTime => v.AsSmallDateTime,
                DateTime2SqlType => v.AsDateTime2,
                _ => throw new InvalidCastException($"Cannot cast column of type {v.Type} to DateTime."),
            };
    }

    /// <summary>
    /// Mirrors SqlClient's polymorphic <c>GetDecimal</c>: <c>decimal</c> /
    /// <c>numeric</c> columns return their stored value, and <c>money</c> /
    /// <c>smallmoney</c> columns surface as scale-4 <see cref="decimal"/>.
    /// Other column types raise <see cref="InvalidCastException"/>.
    /// </summary>
    public override decimal GetDecimal(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException()
            : v.Type is DecimalSqlType ? v.AsDecimal
            : v.Type == SqlType.Money || v.Type == SqlType.SmallMoney ? v.AsMoney
            : throw new InvalidCastException($"Cannot cast column of type {v.Type} to decimal.");
    }

    public override double GetDouble(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsDouble;
    }

    public override IEnumerator GetEnumerator()
    {
        throw new NotImplementedException();
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override float GetFloat(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsSingle;
    }

    public override Guid GetGuid(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsGuid;
    }

    public override short GetInt16(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsInt16;
    }

    public override int GetInt32(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsInt32;
    }

    public override long GetInt64(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override string GetName(int ordinal)
    {
        if (ordinal >= this.FieldCount)
#pragma warning disable CA2201 // Do not raise reserved exception types
            // This is thrown by the official SqlDataReader class so we do it here, too.
            throw new IndexOutOfRangeException();
#pragma warning restore

        return this.results.Current.ColumnNames[ordinal];
    }

    public override int GetOrdinal(string name)
    {
        throw new NotImplementedException();
    }

    public override string GetString(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsString;
    }

    public override object GetValue(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? DBNull.Value : v.ToObject()!;
    }

    /// <summary>
    /// Adds the conversions real <c>SqlClient</c> performs for the CLR-only
    /// types EF Core asks about explicitly: <c>date</c> values are stored as
    /// <see cref="DateOnly"/> but EF requests <see cref="DateTime"/> via
    /// <see cref="GetDateTime"/>; <c>time</c> values are surfaced as
    /// <see cref="TimeSpan"/> via the untyped path but EF requests
    /// <see cref="TimeOnly"/> here. Anything else routes through
    /// <see cref="GetValue"/> + the unboxing cast that the base class would
    /// otherwise perform; the per-row decode happens once because the SqlValue
    /// is captured up front.
    /// </summary>
    public override T GetFieldValue<T>(int ordinal)
    {
        var v = cursor[ordinal];
        if (!v.IsNull)
        {
            if (typeof(T) == typeof(DateOnly) && v.Type == SqlType.Date)
                return (T)(object)v.AsDate;
            if (typeof(T) == typeof(TimeOnly) && v.Type is TimeSqlType)
                return (T)(object)TimeOnly.FromTimeSpan(v.AsTime);
        }
        return (T)(v.IsNull ? DBNull.Value : v.ToObject()!);
    }

    public override int GetValues(object[] values)
    {
        throw new NotImplementedException();
    }

    public override bool IsDBNull(int ordinal) => cursor[ordinal].IsNull;

    public override bool NextResult()
    {
        var hasNext = this.results.MoveNext();

        if (hasNext)
            this.recordsAffected = 0;

        this.cursor.Dispose();
        this.cursor = hasNext ? this.results.Current.CreateCursor() : EmptyCursor.Instance;

        return hasNext;
    }

    public override bool Read()
    {
        var hasNext = this.cursor.MoveNext();

        if (hasNext)
            this.recordsAffected++;

        return hasNext;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        this.results.Dispose();
        this.cursor.Dispose();
    }

    /// <summary>Stand-in cursor used after the last result-set is exhausted.</summary>
    private sealed class EmptyCursor : RowCursor
    {
        public static readonly EmptyCursor Instance = new();

        public override int FieldCount => 0;

        public override bool MoveNext() => false;

        public override SqlValue this[int ordinal] => throw new InvalidOperationException("No current row.");
    }
}
