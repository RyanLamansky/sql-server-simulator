using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

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

    public override object this[int ordinal] => cursor.GetValueObject(ordinal) ?? DBNull.Value;

    public override object this[string name] => throw new NotImplementedException();

    public override int Depth => throw new NotImplementedException();

    public override int FieldCount => cursor.FieldCount;

    public override bool HasRows => throw new NotImplementedException();

    public override bool IsClosed => throw new NotImplementedException();

    public override int RecordsAffected => recordsAffected;

    public override bool GetBoolean(int ordinal)
    {
        throw new NotImplementedException();
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

    public override DateTime GetDateTime(int ordinal) => (DateTime)this[ordinal];

    public override decimal GetDecimal(int ordinal) => (decimal)this[ordinal];

    public override double GetDouble(int ordinal) => (double)this[ordinal];

    public override IEnumerator GetEnumerator()
    {
        throw new NotImplementedException();
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override float GetFloat(int ordinal) => (float)this[ordinal];

    public override Guid GetGuid(int ordinal) => (Guid)this[ordinal];

    public override short GetInt16(int ordinal) => (short)this[ordinal];

    public override int GetInt32(int ordinal) => (int)this[ordinal];

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

    public override string GetString(int ordinal) => (string)this[ordinal];

    public override object GetValue(int ordinal) => cursor.GetValueObject(ordinal) ?? DBNull.Value;

    public override int GetValues(object[] values)
    {
        throw new NotImplementedException();
    }

    public override bool IsDBNull(int ordinal) => cursor.GetValueObject(ordinal) is null;

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

        public override object? GetValueObject(int ordinal) => throw new InvalidOperationException("No current row.");
    }
}
