using System.Collections;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics.CodeAnalysis;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="DbDataReader"/> for the simulator's command pipeline. Public
/// so consumers who downcast a base-typed <see cref="DbDataReader"/> reach
/// the simulator's concrete shape — same role <c>SqlDataReader</c> plays
/// against <c>DbDataReader</c>. Instances are produced by
/// <see cref="SimulatedDbCommand.ExecuteReader()"/>.
/// </summary>
[SuppressMessage("Design", "CA1010:Generic interface should also be implemented", Justification = "Row enumeration is base-class-driven (DbDataReader → IEnumerable), not naturally generic — matches Microsoft.Data.SqlClient.SqlDataReader.")]
public sealed class SimulatedDbDataReader : DbDataReader
{
    private readonly IEnumerator<SimulatedStatementOutcome> outcomes;
    private SimulatedQueryResult? currentResult;
    private RowCursor cursor = EmptyCursor.Instance;
    private int recordsAffected;
    private bool closed;

    internal SimulatedDbDataReader(IEnumerable<SimulatedStatementOutcome> outcomes)
    {
        this.outcomes = outcomes.GetEnumerator();
        _ = this.AdvanceToNextResult();
    }

    /// <summary>
    /// Advances the outcome stream to the next thing the reader treats as a
    /// result set: a tabular <see cref="SimulatedQueryResult"/> or a
    /// <see cref="SimulatedErrorOutcome"/> (a failed statement, surfaced
    /// positionally — the error throws on the first <see cref="Read"/>). Pure
    /// <see cref="SimulatedNonQuery"/> outcomes (INSERT / UPDATE / DDL without
    /// a result set) are skipped, matching how SqlClient's reader only stops
    /// on result-set boundaries. Executing statements as the enumerator
    /// advances is what persists their side effects.
    /// </summary>
    private bool AdvanceToNextResult()
    {
        while (this.outcomes.MoveNext())
        {
            switch (this.outcomes.Current)
            {
                case SimulatedQueryResult query:
                    this.currentResult = query;
                    this.cursor = query.CreateCursor();
                    return true;
                case SimulatedErrorOutcome { RowReturning: true } error:
                    // A row-returning statement (SELECT / VALUES) that failed
                    // after real SQL Server would have sent its COLMETADATA:
                    // surface positionally. The reader advances onto the failed
                    // statement (this advance returns true) and the first Read
                    // throws — the ErrorCursor carries the throw, and the reader
                    // survives to the next result set.
                    this.currentResult = null;
                    this.cursor = new ErrorCursor(error.Exception);
                    return true;
                case SimulatedErrorOutcome error:
                    // A non-row-returning statement (INSERT / UPDATE / DELETE /
                    // DDL) that failed: real SQL Server sent no result-set
                    // envelope, so SqlClient surfaces the error on the advance
                    // itself — ExecuteReader (the constructor's advance) or
                    // NextResult throws, not a later Read. This is what lets EF
                    // Core's no-OUTPUT modification batches, which never call
                    // Read, still observe the failure. Park at end first so a
                    // caller that catches and probes the reader sees it closed.
                    this.currentResult = null;
                    this.cursor = EmptyCursor.Instance;
                    throw error.Exception;
            }
        }

        this.currentResult = null;
        this.cursor = EmptyCursor.Instance;
        return false;
    }

    /// <inheritdoc/>
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc/>
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <summary>
    /// Always 0. SqlClient's <c>SqlDataReader</c> documents this as the
    /// nesting level of the current row; SQL Server doesn't surface a
    /// non-zero value for in-band result sets, and the simulator follows.
    /// </summary>
    public override int Depth => 0;

    /// <inheritdoc/>
    public override int FieldCount => cursor.FieldCount;

    /// <inheritdoc/>
    public override bool HasRows => cursor.HasRows;

    /// <inheritdoc/>
    public override bool IsClosed => closed;

    /// <inheritdoc/>
    public override int RecordsAffected => recordsAffected;

    /// <inheritdoc/>
    public override bool GetBoolean(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsBoolean;
    }

    /// <inheritdoc/>
    public override byte GetByte(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsByte;
    }

    /// <summary>
    /// Materializes the column's bytes and copies the requested window into
    /// the caller's buffer. Real SqlClient streams from off-row pages;
    /// the simulator decodes the column once via <see cref="RowDecoder"/>
    /// and slices, so behavior matches per-call but the streaming-memory
    /// guarantee doesn't. Honors SqlClient's <c>buffer == null</c> contract:
    /// returns the total length without copying so callers can size their
    /// own buffer.
    /// </summary>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var v = cursor[ordinal];
        if (v.IsNull)
            throw new SqlNullValueException();
        var bytes = v.AsBytes;
        if (buffer is null)
            return bytes.Length;
        if (dataOffset is < 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(dataOffset));
        if (dataOffset >= bytes.Length)
            return 0;
        var available = bytes.Length - (int)dataOffset;
        var toCopy = Math.Min(length, available);
        if (toCopy <= 0)
            return 0;
        Buffer.BlockCopy(bytes, (int)dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    /// <summary>
    /// SqlClient surfaces a single character as <see cref="char"/> from
    /// catalog rows that are exactly <c>nchar(1)</c>; the documented
    /// contract throws <see cref="InvalidCastException"/> for everything
    /// else, and even the supported case is rare enough that we mirror
    /// the throw here.
    /// </summary>
    public override char GetChar(int ordinal) =>
        throw new InvalidCastException("GetChar is not supported by SQL Server's data reader.");

    /// <summary>
    /// String-column counterpart to <see cref="GetBytes"/>: materializes the
    /// column's value and copies the requested window into the caller's
    /// buffer. Length is in <see cref="char"/> units (UTF-16 code units),
    /// matching SqlClient. Honors the <c>buffer == null</c> length-only
    /// contract.
    /// </summary>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var v = cursor[ordinal];
        if (v.IsNull)
            throw new SqlNullValueException();
        var s = v.AsString;
        if (buffer is null)
            return s.Length;
        if (dataOffset is < 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(dataOffset));
        if (dataOffset >= s.Length)
            return 0;
        var available = s.Length - (int)dataOffset;
        var toCopy = Math.Min(length, available);
        if (toCopy <= 0)
            return 0;
        s.CopyTo((int)dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    /// <inheritdoc/>
    public override string GetDataTypeName(int ordinal) => CurrentSchema[ordinal].SqlServerName;

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
                var t when t == SqlType.DateTime => DateTimeSqlType.RoundToClientMilliseconds(v.AsDateTime),
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

    /// <inheritdoc/>
    public override double GetDouble(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsDouble;
    }

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    /// <inheritdoc/>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    [UnconditionalSuppressMessage("Trimming", "IL2073:Member return value does not satisfy 'DynamicallyAccessedMembersAttribute' requirements.", Justification = "The closed set of concrete SqlType subclasses returns BCL types whose public surface is not trimmed away in practice; the simulator never feeds linker-pruned types here.")]
    public override Type GetFieldType(int ordinal) => CurrentSchema[ordinal].ClrType;

    /// <inheritdoc/>
    public override float GetFloat(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsSingle;
    }

    /// <inheritdoc/>
    public override Guid GetGuid(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsGuid;
    }

    /// <inheritdoc/>
    public override short GetInt16(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsInt16;
    }

    /// <inheritdoc/>
    public override int GetInt32(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsInt32;
    }

    /// <inheritdoc/>
    public override long GetInt64(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsInt64;
    }

    /// <inheritdoc/>
    public override string GetName(int ordinal)
    {
        if (ordinal >= this.FieldCount)
#pragma warning disable CA2201 // Do not raise reserved exception types
            // This is thrown by the official SqlDataReader class so we do it here, too.
            throw new IndexOutOfRangeException();
#pragma warning restore

        return this.CurrentQuery.ColumnNames[ordinal];
    }

    /// <summary>
    /// Two-pass linear scan of the current result set's column names:
    /// case-sensitive first, then case-insensitive — matching SqlClient's
    /// documented match precedence. Typical column counts are small enough
    /// that this is cheaper than building and caching a dictionary per
    /// result set.
    /// </summary>
    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var names = this.currentResult?.ColumnNames ?? [];
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
#pragma warning disable CA2201 // SqlDataReader throws IndexOutOfRangeException for unknown column names.
        throw new IndexOutOfRangeException(name);
#pragma warning restore
    }

    /// <inheritdoc/>
    public override string GetString(int ordinal)
    {
        var v = cursor[ordinal];
        return v.IsNull ? throw new SqlNullValueException() : v.AsString;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, this.FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = this.GetValue(i);
        return count;
    }

    /// <inheritdoc/>
    public override bool IsDBNull(int ordinal) => cursor[ordinal].IsNull;

    /// <inheritdoc/>
    public override bool NextResult()
    {
        this.cursor.Dispose();
        var hasNext = this.AdvanceToNextResult();

        if (hasNext)
            this.recordsAffected = 0;

        return hasNext;
    }

    /// <inheritdoc/>
    public override bool Read()
    {
        var hasNext = this.cursor.MoveNext();

        if (hasNext)
            this.recordsAffected++;

        return hasNext;
    }

    /// <summary>
    /// Closes the reader. Real SqlClient closes a reader by running the
    /// batch's remaining statements to completion (so their side effects
    /// persist) and discarding any results and errors — a disposed reader
    /// never throws. This drains the outcome stream at statement granularity:
    /// each remaining statement executes, and a continued error (already an
    /// outcome, not a throw) is simply enumerated past. Row-level pull inside
    /// the statement the reader was parked on stays abandoned — the documented
    /// non-draining-reader divergence is unchanged.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (!this.closed)
        {
            this.closed = true;
            this.cursor.Dispose();
            try
            {
                while (this.outcomes.MoveNext())
                {
                }
            }
            catch (SimulatedSqlException)
            {
                // A batch-aborting error (e.g. deadlock) thrown out of the
                // stream during the drain — swallowed; dispose never surfaces
                // batch errors.
            }
            catch (NotSupportedException)
            {
                // An unmodeled feature (e.g. BEGIN DISTRIBUTED TRANSACTION)
                // reached during the drain — likewise swallowed.
            }

            this.outcomes.Dispose();
        }

        base.Dispose(disposing);
    }

    private SqlType[] CurrentSchema => this.currentResult?.Schema ?? [];

    /// <summary>
    /// The query result the reader is parked on. Metadata accessors reach it
    /// only after a <see cref="FieldCount"/> guard, so a position on a failed
    /// statement (where <see cref="currentResult"/> is null and
    /// <see cref="FieldCount"/> is 0) never dereferences it.
    /// </summary>
    private SimulatedQueryResult CurrentQuery =>
        this.currentResult ?? throw new InvalidOperationException("The reader is not positioned on a result set.");

    /// <summary>Stand-in cursor used before any result-set is opened or after the last is exhausted.</summary>
    private sealed class EmptyCursor : RowCursor
    {
        public static readonly EmptyCursor Instance = new();

        public override int FieldCount => 0;

        public override bool HasRows => false;

        public override bool MoveNext() => false;

        public override SqlValue this[int ordinal] => throw new InvalidOperationException("No current row.");
    }

    /// <summary>
    /// Cursor for a <see cref="SimulatedErrorOutcome"/> position: the reader
    /// advanced onto a statement that failed, so the first <see cref="Read"/>
    /// (its first <see cref="MoveNext"/>) throws the carried error — real
    /// SqlClient's positional error surfacing. After the throw it reports no
    /// rows, matching "the failed statement yields no further rows."
    /// </summary>
    private sealed class ErrorCursor(SimulatedSqlException exception) : RowCursor
    {
        private bool thrown;

        public override int FieldCount => 0;

        public override bool HasRows => false;

        public override bool MoveNext()
        {
            if (this.thrown)
                return false;
            this.thrown = true;
            throw exception;
        }

        public override SqlValue this[int ordinal] => throw new InvalidOperationException("No current row.");
    }
}
