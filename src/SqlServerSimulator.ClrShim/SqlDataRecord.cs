using System.Data;
using System.Data.SqlTypes;
using System.Runtime.CompilerServices;

namespace Microsoft.SqlServer.Server;

/// <summary>
/// One row a routine fills and sends through <see cref="SqlPipe"/>. Each typed
/// setter is accepted only for the column types it can write, answering
/// anything else with <see cref="InvalidCastException"/> as the in-server
/// record does, and a string or binary value longer than its column is cut to
/// the column's length (both probed 2026-09-28 against SQL Server 2025). The
/// setters are never inlined, so a stack trace names the setter the routine
/// called, as the server's does.
/// </summary>
public class SqlDataRecord : IDataRecord
{
    /// <summary>The column definitions, read by the pipe that sends this record.</summary>
    internal readonly SqlMetaData[] MetaData;

    /// <summary>The column values; <see langword="null"/> is SQL NULL.</summary>
    private readonly object?[] values;

    public SqlDataRecord(params SqlMetaData[] metaData)
    {
        ArgumentNullException.ThrowIfNull(metaData);
        if (metaData.Length == 0)
            throw new ArgumentException("A SqlDataRecord needs at least one column.", nameof(metaData));
        for (var i = 0; i < metaData.Length; i++)
        {
            if (metaData[i] is null)
                throw new ArgumentNullException($"metaData[{i}]");
        }

        this.MetaData = (SqlMetaData[])metaData.Clone();
        this.values = new object?[metaData.Length];
    }

    public virtual int FieldCount => this.MetaData.Length;

    public virtual object this[int ordinal] => this.GetValue(ordinal);

    public virtual object this[string name] => this.GetValue(this.GetOrdinal(name));

    /// <summary>A copy of the current values, for the pipe that sends this record.</summary>
    internal object?[] SnapshotValues() => (object?[])this.values.Clone();

    public virtual string GetName(int ordinal) => this.MetaData[ordinal].Name;

    public virtual string GetDataTypeName(int ordinal) => this.MetaData[ordinal].TypeName;

    public virtual Type GetFieldType(int ordinal) => ClrTypeOf(this.MetaData[ordinal].SqlDbType);

    public virtual Type GetSqlFieldType(int ordinal) => SqlTypeOf(this.MetaData[ordinal].SqlDbType);

    public virtual SqlMetaData GetSqlMetaData(int ordinal) => this.MetaData[ordinal];

    public virtual int GetOrdinal(string name)
    {
        for (var i = 0; i < this.MetaData.Length; i++)
        {
            if (string.Equals(this.MetaData[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new IndexOutOfRangeException(name);
    }

    public virtual bool IsDBNull(int ordinal) => this.values[ordinal] is null;

    public virtual object GetValue(int ordinal) => ToClrValue(this.values[ordinal]) ?? DBNull.Value;

    public virtual object GetSqlValue(int ordinal) => ToSqlValue(this.values[ordinal], this.MetaData[ordinal].SqlDbType);

    public virtual int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, this.values.Length);
        for (var i = 0; i < count; i++)
            values[i] = this.GetValue(i);
        return count;
    }

    public virtual int GetSqlValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, this.values.Length);
        for (var i = 0; i < count; i++)
            values[i] = this.GetSqlValue(i);
        return count;
    }

    public virtual bool GetBoolean(int ordinal) => (bool)this.GetValue(ordinal);

    public virtual byte GetByte(int ordinal) => (byte)this.GetValue(ordinal);

    public virtual char GetChar(int ordinal) => throw new NotSupportedException();

    public virtual short GetInt16(int ordinal) => (short)this.GetValue(ordinal);

    public virtual int GetInt32(int ordinal) => (int)this.GetValue(ordinal);

    public virtual long GetInt64(int ordinal) => (long)this.GetValue(ordinal);

    public virtual float GetFloat(int ordinal) => (float)this.GetValue(ordinal);

    public virtual double GetDouble(int ordinal) => (double)this.GetValue(ordinal);

    public virtual decimal GetDecimal(int ordinal) => (decimal)this.GetValue(ordinal);

    public virtual DateTime GetDateTime(int ordinal) => (DateTime)this.GetValue(ordinal);

    public virtual DateTimeOffset GetDateTimeOffset(int ordinal) => (DateTimeOffset)this.GetValue(ordinal);

    public virtual TimeSpan GetTimeSpan(int ordinal) => (TimeSpan)this.GetValue(ordinal);

    public virtual Guid GetGuid(int ordinal) => (Guid)this.GetValue(ordinal);

    public virtual string GetString(int ordinal) => (string)this.GetValue(ordinal);

    public virtual long GetBytes(int ordinal, long fieldOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])this.GetValue(ordinal);
        if (buffer is null)
            return bytes.Length;
        var count = (int)Math.Max(0, Math.Min(length, bytes.Length - fieldOffset));
        Array.Copy(bytes, fieldOffset, buffer, bufferOffset, count);
        return count;
    }

    public virtual long GetChars(int ordinal, long fieldOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = (string)this.GetValue(ordinal);
        if (buffer is null)
            return text.Length;
        var count = (int)Math.Max(0, Math.Min(length, text.Length - fieldOffset));
        text.CopyTo((int)fieldOffset, buffer, bufferOffset, count);
        return count;
    }

    public virtual IDataReader GetData(int ordinal) => throw new NotSupportedException();

    public virtual SqlBoolean GetSqlBoolean(int ordinal) => (SqlBoolean)this.GetSqlValue(ordinal);

    public virtual SqlByte GetSqlByte(int ordinal) => (SqlByte)this.GetSqlValue(ordinal);

    public virtual SqlInt16 GetSqlInt16(int ordinal) => (SqlInt16)this.GetSqlValue(ordinal);

    public virtual SqlInt32 GetSqlInt32(int ordinal) => (SqlInt32)this.GetSqlValue(ordinal);

    public virtual SqlInt64 GetSqlInt64(int ordinal) => (SqlInt64)this.GetSqlValue(ordinal);

    public virtual SqlSingle GetSqlSingle(int ordinal) => (SqlSingle)this.GetSqlValue(ordinal);

    public virtual SqlDouble GetSqlDouble(int ordinal) => (SqlDouble)this.GetSqlValue(ordinal);

    public virtual SqlMoney GetSqlMoney(int ordinal) => (SqlMoney)this.GetSqlValue(ordinal);

    public virtual SqlDecimal GetSqlDecimal(int ordinal) => (SqlDecimal)this.GetSqlValue(ordinal);

    public virtual SqlDateTime GetSqlDateTime(int ordinal) => (SqlDateTime)this.GetSqlValue(ordinal);

    public virtual SqlGuid GetSqlGuid(int ordinal) => (SqlGuid)this.GetSqlValue(ordinal);

    public virtual SqlString GetSqlString(int ordinal) => (SqlString)this.GetSqlValue(ordinal);

    public virtual SqlBinary GetSqlBinary(int ordinal) => (SqlBinary)this.GetSqlValue(ordinal);

    public virtual SqlXml GetSqlXml(int ordinal) => (SqlXml)this.GetSqlValue(ordinal);

    public virtual SqlBytes GetSqlBytes(int ordinal) => this.values[ordinal] is null ? SqlBytes.Null : new SqlBytes((byte[])this.GetValue(ordinal));

    public virtual SqlChars GetSqlChars(int ordinal) => this.values[ordinal] is null ? SqlChars.Null : new SqlChars(((string)this.GetValue(ordinal)).ToCharArray());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetDBNull(int ordinal) => this.values[ordinal] = null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetBoolean(int ordinal, bool value) => this.Store(ordinal, value, SqlDbType.Bit);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetByte(int ordinal, byte value) => this.Store(ordinal, value, SqlDbType.TinyInt);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetInt16(int ordinal, short value) => this.Store(ordinal, value, SqlDbType.SmallInt);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetInt32(int ordinal, int value) => this.Store(ordinal, value, SqlDbType.Int);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetInt64(int ordinal, long value) => this.Store(ordinal, value, SqlDbType.BigInt);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetFloat(int ordinal, float value) => this.Store(ordinal, value, SqlDbType.Real);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetDouble(int ordinal, double value) => this.Store(ordinal, value, SqlDbType.Float);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetDecimal(int ordinal, decimal value) => this.Store(ordinal, value, SqlDbType.Decimal, SqlDbType.Money, SqlDbType.SmallMoney);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetDateTime(int ordinal, DateTime value) =>
        this.Store(ordinal, value, SqlDbType.DateTime, SqlDbType.SmallDateTime, SqlDbType.Date, SqlDbType.DateTime2);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetDateTimeOffset(int ordinal, DateTimeOffset value) => this.Store(ordinal, value, SqlDbType.DateTimeOffset);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetTimeSpan(int ordinal, TimeSpan value) => this.Store(ordinal, value, SqlDbType.Time);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetGuid(int ordinal, Guid value) => this.Store(ordinal, value, SqlDbType.UniqueIdentifier);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetString(int ordinal, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this.Store(ordinal, this.Cut(ordinal, value), SqlDbType.Char, SqlDbType.NChar, SqlDbType.VarChar, SqlDbType.NVarChar, SqlDbType.Text, SqlDbType.NText, SqlDbType.Xml);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetChar(int ordinal, char value) => this.SetString(ordinal, value.ToString());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetChars(int ordinal, long fieldOffset, char[] buffer, int bufferOffset, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var existing = this.values[ordinal] as string ?? "";
        var prefix = existing.Length >= fieldOffset ? existing[..(int)fieldOffset] : existing.PadRight((int)fieldOffset, '\0');
        this.SetString(ordinal, prefix + new string(buffer, bufferOffset, length));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetBytes(int ordinal, long fieldOffset, byte[] buffer, int bufferOffset, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var existing = this.values[ordinal] as byte[] ?? [];
        var combined = new byte[(int)fieldOffset + length];
        Array.Copy(existing, combined, (int)Math.Min(existing.Length, fieldOffset));
        Array.Copy(buffer, bufferOffset, combined, (int)fieldOffset, length);
        this.Store(ordinal, this.Cut(ordinal, combined), SqlDbType.Binary, SqlDbType.VarBinary, SqlDbType.Image, SqlDbType.Timestamp);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlBoolean(int ordinal, SqlBoolean value) => this.StoreSql(ordinal, value, SqlDbType.Bit);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlByte(int ordinal, SqlByte value) => this.StoreSql(ordinal, value, SqlDbType.TinyInt);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlInt16(int ordinal, SqlInt16 value) => this.StoreSql(ordinal, value, SqlDbType.SmallInt);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlInt32(int ordinal, SqlInt32 value) => this.StoreSql(ordinal, value, SqlDbType.Int);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlInt64(int ordinal, SqlInt64 value) => this.StoreSql(ordinal, value, SqlDbType.BigInt);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlSingle(int ordinal, SqlSingle value) => this.StoreSql(ordinal, value, SqlDbType.Real);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlDouble(int ordinal, SqlDouble value) => this.StoreSql(ordinal, value, SqlDbType.Float);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlMoney(int ordinal, SqlMoney value) => this.StoreSql(ordinal, value, SqlDbType.Money, SqlDbType.SmallMoney, SqlDbType.Decimal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlDecimal(int ordinal, SqlDecimal value) => this.StoreSql(ordinal, value, SqlDbType.Decimal, SqlDbType.Money, SqlDbType.SmallMoney);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlDateTime(int ordinal, SqlDateTime value) =>
        this.StoreSql(ordinal, value, SqlDbType.DateTime, SqlDbType.SmallDateTime, SqlDbType.Date, SqlDbType.DateTime2);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlGuid(int ordinal, SqlGuid value) => this.StoreSql(ordinal, value, SqlDbType.UniqueIdentifier);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlString(int ordinal, SqlString value)
    {
        if (value.IsNull)
            this.StoreSql(ordinal, value, SqlDbType.Char, SqlDbType.NChar, SqlDbType.VarChar, SqlDbType.NVarChar, SqlDbType.Text, SqlDbType.NText, SqlDbType.Xml);
        else
            this.SetString(ordinal, value.Value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlBinary(int ordinal, SqlBinary value)
    {
        if (value.IsNull)
            this.StoreSql(ordinal, value, SqlDbType.Binary, SqlDbType.VarBinary, SqlDbType.Image, SqlDbType.Timestamp);
        else
            this.SetBytes(ordinal, 0, value.Value, 0, value.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlBytes(int ordinal, SqlBytes value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this.SetSqlBinary(ordinal, value.IsNull ? SqlBinary.Null : new SqlBinary(value.Value));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlChars(int ordinal, SqlChars value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this.SetSqlString(ordinal, value.IsNull ? SqlString.Null : new SqlString(new string(value.Value)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetSqlXml(int ordinal, SqlXml value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this.StoreSql(ordinal, value, SqlDbType.Xml);
    }

    /// <summary>
    /// Stores a value whose CLR or <see cref="System.Data.SqlTypes"/> type is
    /// one the column's typed setter takes — an <see cref="int"/> into a
    /// <c>bigint</c> column is <see cref="InvalidCastException"/>, as on the
    /// server (probed 2026-09-28 against SQL Server 2025) — or anything into a
    /// <c>sql_variant</c> one. <see langword="null"/> and
    /// <see cref="DBNull"/> store NULL.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual void SetValue(int ordinal, object? value)
    {
        var column = this.MetaData[ordinal].SqlDbType;
        if (value is not (null or DBNull) && column != SqlDbType.Variant && Array.IndexOf(ColumnsTaking(value), column) < 0)
            throw new InvalidCastException("Specified cast is not valid.");
        this.values[ordinal] = value switch
        {
            null or DBNull => null,
            INullable { IsNull: true } => null,
            string text => this.Cut(ordinal, text),
            char character => character.ToString(),
            char[] chars => this.Cut(ordinal, new string(chars)),
            byte[] bytes => this.Cut(ordinal, bytes),
            SqlString text => this.Cut(ordinal, text.Value),
            SqlBinary bytes => this.Cut(ordinal, bytes.Value),
            SqlChars chars => this.Cut(ordinal, new string(chars.Value)),
            SqlBytes bytes => this.Cut(ordinal, bytes.Value),
            _ => value,
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual int SetValues(params object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, this.values.Length);
        for (var i = 0; i < count; i++)
            this.SetValue(i, values[i]);
        return count;
    }

    /// <summary>The column types a value of <paramref name="value"/>'s type may be stored in.</summary>
    private static SqlDbType[] ColumnsTaking(object value) => value switch
    {
        bool or SqlBoolean => [SqlDbType.Bit],
        byte or SqlByte => [SqlDbType.TinyInt],
        short or SqlInt16 => [SqlDbType.SmallInt],
        int or SqlInt32 => [SqlDbType.Int],
        long or SqlInt64 => [SqlDbType.BigInt],
        float or SqlSingle => [SqlDbType.Real],
        double or SqlDouble => [SqlDbType.Float],
        decimal or SqlDecimal or SqlMoney => [SqlDbType.Decimal, SqlDbType.Money, SqlDbType.SmallMoney],
        DateTime or SqlDateTime => [SqlDbType.DateTime, SqlDbType.SmallDateTime, SqlDbType.Date, SqlDbType.DateTime2],
        DateTimeOffset => [SqlDbType.DateTimeOffset],
        TimeSpan => [SqlDbType.Time],
        Guid or SqlGuid => [SqlDbType.UniqueIdentifier],
        string or char or char[] or SqlString or SqlChars
            => [SqlDbType.Char, SqlDbType.NChar, SqlDbType.VarChar, SqlDbType.NVarChar, SqlDbType.Text, SqlDbType.NText, SqlDbType.Xml],
        byte[] or SqlBinary or SqlBytes => [SqlDbType.Binary, SqlDbType.VarBinary, SqlDbType.Image, SqlDbType.Timestamp],
        SqlXml => [SqlDbType.Xml],
        _ => [],
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Store(int ordinal, object value, params SqlDbType[] accepted)
    {
        var column = this.MetaData[ordinal].SqlDbType;
        if (column != SqlDbType.Variant && Array.IndexOf(accepted, column) < 0)
            throw new InvalidCastException("Specified cast is not valid.");
        this.values[ordinal] = value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StoreSql<T>(int ordinal, T value, params SqlDbType[] accepted)
        where T : INullable
    {
        if (value.IsNull)
        {
            var column = this.MetaData[ordinal].SqlDbType;
            if (column != SqlDbType.Variant && Array.IndexOf(accepted, column) < 0)
                throw new InvalidCastException("Specified cast is not valid.");
            this.values[ordinal] = null;
            return;
        }

        this.Store(ordinal, value, accepted);
    }

    /// <summary>A string cut to its column's declared length.</summary>
    private string Cut(int ordinal, string value)
    {
        var maxLength = this.MetaData[ordinal].MaxLength;
        return maxLength > 0 && value.Length > maxLength && this.MetaData[ordinal].SqlDbType is SqlDbType.Char or SqlDbType.NChar or SqlDbType.VarChar or SqlDbType.NVarChar
            ? value[..(int)maxLength]
            : value;
    }

    /// <summary>A byte array cut to its column's declared length.</summary>
    private byte[] Cut(int ordinal, byte[] value)
    {
        var maxLength = this.MetaData[ordinal].MaxLength;
        return maxLength > 0 && value.Length > maxLength && this.MetaData[ordinal].SqlDbType is SqlDbType.Binary or SqlDbType.VarBinary
            ? value[..(int)maxLength]
            : value;
    }

    /// <summary>The plain-CLR form of a stored value, or <see langword="null"/> for NULL.</summary>
    private static object? ToClrValue(object? value) => value switch
    {
        null => null,
        INullable { IsNull: true } => null,
        SqlBoolean v => v.Value,
        SqlByte v => v.Value,
        SqlInt16 v => v.Value,
        SqlInt32 v => v.Value,
        SqlInt64 v => v.Value,
        SqlSingle v => v.Value,
        SqlDouble v => v.Value,
        SqlMoney v => v.Value,
        SqlDecimal v => v.Value,
        SqlDateTime v => v.Value,
        SqlGuid v => v.Value,
        SqlString v => v.Value,
        SqlBinary v => v.Value,
        SqlXml v => v.Value,
        _ => value,
    };

    /// <summary>The <see cref="System.Data.SqlTypes"/> form of a stored value, per its column's type.</summary>
    private static object ToSqlValue(object? value, SqlDbType column)
    {
        if (value is INullable)
            return value;
        return column switch
        {
            SqlDbType.BigInt => value is null ? SqlInt64.Null : new SqlInt64(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
            SqlDbType.Binary or SqlDbType.VarBinary or SqlDbType.Image or SqlDbType.Timestamp => value is null ? SqlBinary.Null : new SqlBinary((byte[])value),
            SqlDbType.Bit => value is null ? SqlBoolean.Null : new SqlBoolean((bool)value),
            SqlDbType.Char or SqlDbType.NChar or SqlDbType.VarChar or SqlDbType.NVarChar or SqlDbType.Text or SqlDbType.NText
                => value is null ? SqlString.Null : new SqlString((string)value),
            SqlDbType.DateTime or SqlDbType.SmallDateTime => value is null ? SqlDateTime.Null : new SqlDateTime((DateTime)value),
            SqlDbType.Decimal => value is null ? SqlDecimal.Null : new SqlDecimal(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)),
            SqlDbType.Float => value is null ? SqlDouble.Null : new SqlDouble(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
            SqlDbType.Int => value is null ? SqlInt32.Null : new SqlInt32(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)),
            SqlDbType.Money or SqlDbType.SmallMoney => value is null ? SqlMoney.Null : new SqlMoney(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)),
            SqlDbType.Real => value is null ? SqlSingle.Null : new SqlSingle(Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture)),
            SqlDbType.SmallInt => value is null ? SqlInt16.Null : new SqlInt16(Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture)),
            SqlDbType.TinyInt => value is null ? SqlByte.Null : new SqlByte(Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture)),
            SqlDbType.UniqueIdentifier => value is null ? SqlGuid.Null : new SqlGuid((Guid)value),
            _ => value ?? DBNull.Value,
        };
    }

    private static Type ClrTypeOf(SqlDbType column) => column switch
    {
        SqlDbType.BigInt => typeof(long),
        SqlDbType.Binary or SqlDbType.VarBinary or SqlDbType.Image or SqlDbType.Timestamp => typeof(byte[]),
        SqlDbType.Bit => typeof(bool),
        SqlDbType.Char or SqlDbType.NChar or SqlDbType.VarChar or SqlDbType.NVarChar or SqlDbType.Text or SqlDbType.NText or SqlDbType.Xml => typeof(string),
        SqlDbType.Date or SqlDbType.DateTime or SqlDbType.DateTime2 or SqlDbType.SmallDateTime => typeof(DateTime),
        SqlDbType.DateTimeOffset => typeof(DateTimeOffset),
        SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney => typeof(decimal),
        SqlDbType.Float => typeof(double),
        SqlDbType.Int => typeof(int),
        SqlDbType.Real => typeof(float),
        SqlDbType.SmallInt => typeof(short),
        SqlDbType.Time => typeof(TimeSpan),
        SqlDbType.TinyInt => typeof(byte),
        SqlDbType.UniqueIdentifier => typeof(Guid),
        _ => typeof(object),
    };

    private static Type SqlTypeOf(SqlDbType column) => column switch
    {
        SqlDbType.BigInt => typeof(SqlInt64),
        SqlDbType.Binary or SqlDbType.VarBinary or SqlDbType.Image or SqlDbType.Timestamp => typeof(SqlBinary),
        SqlDbType.Bit => typeof(SqlBoolean),
        SqlDbType.Char or SqlDbType.NChar or SqlDbType.VarChar or SqlDbType.NVarChar or SqlDbType.Text or SqlDbType.NText => typeof(SqlString),
        SqlDbType.DateTime or SqlDbType.SmallDateTime => typeof(SqlDateTime),
        SqlDbType.Decimal => typeof(SqlDecimal),
        SqlDbType.Float => typeof(SqlDouble),
        SqlDbType.Int => typeof(SqlInt32),
        SqlDbType.Money or SqlDbType.SmallMoney => typeof(SqlMoney),
        SqlDbType.Real => typeof(SqlSingle),
        SqlDbType.SmallInt => typeof(SqlInt16),
        SqlDbType.TinyInt => typeof(SqlByte),
        SqlDbType.UniqueIdentifier => typeof(SqlGuid),
        SqlDbType.Xml => typeof(SqlXml),
        _ => typeof(object),
    };
}
