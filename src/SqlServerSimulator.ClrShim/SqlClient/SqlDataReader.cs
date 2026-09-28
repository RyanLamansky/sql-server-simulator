using System.Collections;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Runtime.CompilerServices;

#pragma warning disable IDE0130 // The namespace is .NET Framework's own, which a SQLCLR assembly's references name.

namespace System.Data.SqlClient;

/// <summary>
/// The rows a context-connection command returned. A result set's error
/// surfaces from the <see cref="NextResult"/> that reaches it, and the batch's
/// later result sets stay readable after it; a typed getter reads only its own
/// column type, a NULL raises <see cref="SqlNullValueException"/>, and each
/// value's provider-specific form is its <see cref="System.Data.SqlTypes"/>
/// struct (probed 2026-09-28 against SQL Server 2025). The public members are
/// never inlined, so a stack trace names them as the server's does.
/// </summary>
public sealed class SqlDataReader : DbDataReader
{
    private const string NoData = "Invalid attempt to read when no data is present.";

    private readonly SqlConnection connection;
    private readonly object[] items;
    private readonly int recordsAffected;
    private readonly bool closeConnection;
    private int nextItem;
    private ContextResultSet? current;
    private int row = -1;
    private bool closed;

    internal SqlDataReader(SqlConnection connection, object[] items, int recordsAffected, bool closeConnection)
    {
        this.connection = connection;
        this.items = items;
        this.recordsAffected = recordsAffected;
        this.closeConnection = closeConnection;
    }

    public override int Depth => 0;

    public override int FieldCount => this.closed
        ? throw new InvalidOperationException("Invalid attempt to call FieldCount when reader is closed.")
        : this.current?.Names.Length ?? 0;

    public override int VisibleFieldCount => this.FieldCount;

    public override bool HasRows => this.current is { Rows.Length: > 0 };

    public override bool IsClosed => this.closed;

    public override int RecordsAffected => this.recordsAffected;

    public override object this[int ordinal] => this.GetValue(ordinal);

    public override object this[string name] => this.GetValue(this.GetOrdinal(name));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override bool Read()
    {
        if (this.closed)
            throw new InvalidOperationException("Invalid attempt to call Read when reader is closed.");
        if (this.current is not { } resultSet || this.row >= resultSet.Rows.Length)
            return false;
        this.row++;
        if (this.row < resultSet.Rows.Length)
            return true;
        if (resultSet.EndedByError)
            this.RaiseFollowingErrors();
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override bool NextResult()
    {
        if (this.closed)
            throw new InvalidOperationException("Invalid attempt to call NextResult when reader is closed.");
        return this.MoveToNextResultSet();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void Close()
    {
        if (this.closed)
            return;
        this.closed = true;
        this.current = null;
        if (this.connection.OpenReader == this)
            this.connection.OpenReader = null;
        if (this.closeConnection)
            this.connection.Close();
    }

    public override string GetName(int ordinal) => this.Current.Names[ordinal];

    public override string GetDataTypeName(int ordinal) => this.Current.TypeNames[ordinal];

    public override Type GetFieldType(int ordinal) => this.Current.FieldTypes[ordinal];

    public override Type GetProviderSpecificFieldType(int ordinal) => this.Current.SqlFieldTypes[ordinal];

    public override int GetOrdinal(string name)
    {
        var names = this.Current.Names;
        for (var i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return i;
        }

        for (var i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new IndexOutOfRangeException(name);
    }

    public override bool IsDBNull(int ordinal) => this.Raw(ordinal) is null or DBNull or INullable { IsNull: true };

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override object GetValue(int ordinal) => SqlParameter.ToClrValue(this.Raw(ordinal));

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, this.FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = this.GetValue(i);
        return count;
    }

    public override object GetProviderSpecificValue(int ordinal) => this.GetSqlValue(ordinal);

    public override int GetProviderSpecificValues(object[] values) => this.GetSqlValues(values);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public object GetSqlValue(int ordinal) => this.Raw(ordinal) ?? DBNull.Value;

    public int GetSqlValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, this.FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = this.GetSqlValue(i);
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override bool GetBoolean(int ordinal) => this.Get<bool>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override byte GetByte(int ordinal) => this.Get<byte>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override char GetChar(int ordinal) => throw new NotSupportedException("SqlDataReader.GetChar is not supported. Use GetString or GetChars instead.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override short GetInt16(int ordinal) => this.Get<short>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override int GetInt32(int ordinal) => this.Get<int>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override long GetInt64(int ordinal) => this.Get<long>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override float GetFloat(int ordinal) => this.Get<float>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override double GetDouble(int ordinal) => this.Get<double>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override decimal GetDecimal(int ordinal) => this.Get<decimal>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override DateTime GetDateTime(int ordinal) => this.Get<DateTime>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public DateTimeOffset GetDateTimeOffset(int ordinal) => this.Get<DateTimeOffset>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public TimeSpan GetTimeSpan(int ordinal) => this.Get<TimeSpan>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override Guid GetGuid(int ordinal) => this.Get<Guid>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override string GetString(int ordinal) => this.Get<string>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = this.Get<byte[]>(ordinal);
        if (buffer is null)
            return bytes.Length;
        var count = (int)Math.Max(0, Math.Min(length, bytes.Length - dataOffset));
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = this.Get<string>(ordinal);
        if (buffer is null)
            return text.Length;
        var count = (int)Math.Max(0, Math.Min(length, text.Length - dataOffset));
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlBoolean GetSqlBoolean(int ordinal) => this.GetSql<SqlBoolean>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlByte GetSqlByte(int ordinal) => this.GetSql<SqlByte>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlInt16 GetSqlInt16(int ordinal) => this.GetSql<SqlInt16>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlInt32 GetSqlInt32(int ordinal) => this.GetSql<SqlInt32>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlInt64 GetSqlInt64(int ordinal) => this.GetSql<SqlInt64>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlSingle GetSqlSingle(int ordinal) => this.GetSql<SqlSingle>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlDouble GetSqlDouble(int ordinal) => this.GetSql<SqlDouble>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlMoney GetSqlMoney(int ordinal) => this.GetSql<SqlMoney>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlDecimal GetSqlDecimal(int ordinal) => this.GetSql<SqlDecimal>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlDateTime GetSqlDateTime(int ordinal) => this.GetSql<SqlDateTime>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlGuid GetSqlGuid(int ordinal) => this.GetSql<SqlGuid>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlString GetSqlString(int ordinal) => this.GetSql<SqlString>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlBinary GetSqlBinary(int ordinal) => this.GetSql<SqlBinary>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlXml GetSqlXml(int ordinal) => this.GetSql<SqlXml>(ordinal);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlBytes GetSqlBytes(int ordinal) => this.GetSql<SqlBinary>(ordinal) is { IsNull: false } binary ? new SqlBytes(binary.Value) : SqlBytes.Null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlChars GetSqlChars(int ordinal) => this.GetSql<SqlString>(ordinal) is { IsNull: false } text ? new SqlChars(text.Value.ToCharArray()) : SqlChars.Null;

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, this.closeConnection);

    public override DataTable GetSchemaTable() => throw new NotSupportedException("SqlDataReader.GetSchemaTable over the context connection is not modeled by the simulator.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            this.Close();
        base.Dispose(disposing);
    }

    /// <summary>
    /// Moves to the next item that is a result set, raising the errors met on
    /// the way as one <see cref="SqlException"/>; they are consumed, so the
    /// call after carries on past them.
    /// </summary>
    internal bool MoveToNextResultSet()
    {
        this.current = null;
        this.row = -1;
        List<SqlError>? errors = null;
        while (this.nextItem < this.items.Length)
        {
            var item = this.items[this.nextItem++];
            if (item is ContextMessage { Class: > 10 } message)
            {
                (errors ??= []).Add(SqlConnection.ToError(message));
                continue;
            }

            if (item is ContextResultSet resultSet)
            {
                if (errors is not null)
                {
                    this.nextItem--;
                    break;
                }

                this.current = resultSet;
                return true;
            }
        }

        return errors is not null ? throw new SqlException(new SqlErrorCollection(errors)) : false;
    }

    /// <summary>
    /// Raises the errors that cut the current result set short, as the
    /// provider does from the <see cref="Read"/> that meets them; they are
    /// consumed, so <see cref="NextResult"/> carries on past them.
    /// </summary>
    private void RaiseFollowingErrors()
    {
        List<SqlError>? errors = null;
        while (this.nextItem < this.items.Length && this.items[this.nextItem] is ContextMessage { Class: > 10 } message)
        {
            (errors ??= []).Add(SqlConnection.ToError(message));
            this.nextItem++;
        }

        if (errors is not null)
            throw new SqlException(new SqlErrorCollection(errors));
    }

    /// <summary>
    /// Sends what is left to read — the current result set from the row after
    /// the one the reader is on, then every later one — to the routine's
    /// client, as <c>SqlPipe.Send(SqlDataReader)</c> does, leaving the reader
    /// open and at its end.
    /// </summary>
    internal void SendRemaining()
    {
        if (this.closed)
            throw new InvalidOperationException("Invalid attempt to call Read when reader is closed.");
        if (this.current is { } resultSet)
            this.connection.SendResultSet(resultSet.Handle, this.row + 1);
        while (this.MoveToNextResultSet())
            this.connection.SendResultSet(this.current!.Value.Handle, 0);
    }

    private ContextResultSet Current => this.closed
        ? throw new InvalidOperationException("Invalid attempt to call MetaData when reader is closed.")
        : this.current ?? throw new IndexOutOfRangeException();

    private object? Raw(int ordinal)
    {
        var rows = this.Current.Rows;
        return this.row < 0 || this.row >= rows.Length
            ? throw new InvalidOperationException(NoData)
            : rows[this.row][ordinal];
    }

    private T Get<T>(int ordinal)
    {
        var value = this.Raw(ordinal);
        if (value is null or DBNull or INullable { IsNull: true })
            throw new SqlNullValueException();
        var clr = SqlParameter.ToClrValue(value);
        return clr is T typed
            ? typed
            : throw new InvalidCastException($"Unable to cast object of type '{clr.GetType().FullName}' to type '{typeof(T).FullName}'.");
    }

    private T GetSql<T>(int ordinal)
    {
        var value = this.Raw(ordinal);
        return value is T typed
            ? typed
            : throw new InvalidCastException($"Unable to cast object of type '{value?.GetType().FullName ?? "System.DBNull"}' to type '{typeof(T).FullName}'.");
    }
}
