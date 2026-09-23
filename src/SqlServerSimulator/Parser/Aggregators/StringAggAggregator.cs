using System.Text;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>STRING_AGG(expr, separator)</c>: concatenates non-NULL values
/// with the separator between them. The separator is evaluated once per row
/// (SQL Server allows a per-row separator value, though it's typically a
/// constant) — the simulator uses the most recent non-NULL separator. Empty /
/// all-NULL input → NULL. A non-string operand converts to <c>nvarchar</c>
/// the way a default-style <c>CAST</c> does (<c>1e10</c> → <c>1e+010</c>, a
/// <c>datetime</c> → <c>Jan  2 2024  3:04AM</c>); see <see cref="ResultType"/>
/// for the types it refuses. Two execution modes share this class:
/// <list type="bullet">
///   <item><b>Streaming</b> (no <c>WITHIN GROUP</c>): rows append directly to
///   <see cref="streamingBuffer"/> in arrival order — O(1) per row.</item>
///   <item><b>Buffered</b> (with <c>WITHIN GROUP (ORDER BY ...)</c>): rows
///   stash their value, current separator, and the evaluated ORDER BY tuple in
///   <see cref="orderedBuffer"/>; <see cref="Result"/> sorts and concatenates.
///   The Selection executor evaluates ORDER BY expressions per row before
///   handing them off via <see cref="AddOrdered"/>.</item>
/// </list>
/// </summary>
internal sealed class StringAggAggregator : Aggregator
{
    private const int MaxResultBytes = 8000;

    private readonly SqlType resultType;

    private readonly bool[]? orderDescending;

    private readonly StringBuilder streamingBuffer;

    private readonly List<OrderedRow>? orderedBuffer;

    private string lastSeparator = "";

    private bool sawAny;

    public StringAggAggregator(SqlType resultType, IReadOnlyList<OrderBySpec>? orderBy)
    {
        this.resultType = resultType;
        if (orderBy is null)
        {
            this.streamingBuffer = new StringBuilder();
        }
        else
        {
            this.streamingBuffer = null!;
            this.orderedBuffer = [];
            this.orderDescending = new bool[orderBy.Count];
            for (var i = 0; i < orderBy.Count; i++)
                this.orderDescending[i] = orderBy[i].Descending;
        }
    }

    /// <summary>
    /// The aggregate's result type for an operand of
    /// <paramref name="operandType"/>, probed 2026-09-23 against SQL Server
    /// 2025: the ANSI family widens to <c>varchar(8000)</c>, the national
    /// family to <c>nvarchar(4000)</c>, each keeping MAX and the operand's
    /// collation, and a numeric or date/time operand is <c>nvarchar(4000)</c>
    /// in the database's collation.
    /// Every other type — <c>uniqueidentifier</c>, the binary family,
    /// <c>xml</c>, <c>sql_variant</c>, the CLR types — is Msg 8116.
    /// </summary>
    public static SqlType ResultType(SqlType operandType, BatchContext batch)
    {
        switch (operandType)
        {
            case VarcharSqlType or CharSqlType:
                return VarcharSqlType.Get(
                    operandType is VarcharSqlType { length: SqlType.MaxLengthSentinel } ? SqlType.MaxLengthSentinel : 8000,
                    operandType.Collation!,
                    operandType.Coercibility);
            case NVarcharSqlType or NCharSqlType or SystemNameSqlType:
                return NVarcharSqlType.Get(
                    operandType is NVarcharSqlType { length: SqlType.MaxLengthSentinel } ? SqlType.MaxLengthSentinel : 4000,
                    operandType.Collation!,
                    operandType.Coercibility);
        }

        if (operandType.Category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate or SqlTypeCategory.DateTime)
            return NVarcharSqlType.Get(4000, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);
        throw SimulatedSqlException.InvalidArgumentDataType(operandType.SqlServerName, 1, "string_agg");
    }

    public override void Add(SqlValue value)
    {
        StringScalars.RejectLegacyLob(value, "string_agg");
        if (value.IsNull)
            return;

        if (this.sawAny)
            _ = this.streamingBuffer.Append(this.lastSeparator);
        _ = this.streamingBuffer.Append(this.Text(value));
        this.sawAny = true;
    }

    private string Text(SqlValue value) =>
        SqlType.IsStringCategory(value.Type) ? value.AsString : Cast.CoerceToDeclared(value, this.resultType).AsString;

    /// <summary>
    /// Buffered-path companion to <see cref="Add"/>: stashes the row's value
    /// alongside the separator that was current when the row arrived and the
    /// evaluated ORDER BY tuple. Sorting and concatenation happen in
    /// <see cref="Result"/>. NULL values are skipped (matching streaming and
    /// SQL Server semantics).
    /// </summary>
    public void AddOrdered(SqlValue value, SqlValue[] orderKeys)
    {
        StringScalars.RejectLegacyLob(value, "string_agg");
        if (value.IsNull)
            return;
        this.orderedBuffer!.Add(new OrderedRow(this.Text(value), this.lastSeparator, orderKeys));
        this.sawAny = true;
    }

    /// <summary>
    /// Sets the separator that the next <see cref="Add"/> / <see cref="AddOrdered"/>
    /// will use; called by the Selection executor before each input row with
    /// the row's per-row evaluation of <see cref="AggregateExpression.Separator"/>.
    /// </summary>
    public void SetSeparator(string separator) => this.lastSeparator = separator;

    public override SqlValue Result()
    {
        if (!this.sawAny)
            return SqlValue.Null(this.resultType);

        if (this.orderedBuffer is null)
            return this.Materialize(this.streamingBuffer.ToString());

        // Sort under SqlValue.CompareTo with each column's direction; ties
        // resolve in encounter order (List<T>.Sort is unstable, but the
        // delegate's strict compare keys ensure equal-key rows aren't
        // observably reordered for the user — they still produce the same
        // concatenated string regardless of relative position because their
        // separators and values are identical from the user's perspective).
        this.orderedBuffer.Sort(this.CompareOrderedRows);

        var output = new StringBuilder();
        for (var i = 0; i < this.orderedBuffer.Count; i++)
        {
            if (i > 0)
                _ = output.Append(this.orderedBuffer[i].Separator);
            _ = output.Append(this.orderedBuffer[i].Value);
        }
        return this.Materialize(output.ToString());
    }

    /// <summary>
    /// Wraps the concatenated text in the aggregator's result type, first
    /// enforcing SQL Server's 8000-byte limit for a bounded (non-MAX) operand:
    /// an overflow raises Msg 9829 rather than silently truncating (or, on the
    /// wire, overflowing the bounded 2-byte length prefix). A MAX-typed operand
    /// streams unbounded and skips the check.
    /// </summary>
    private SqlValue Materialize(string result)
    {
        if (!IsMaxForm(this.resultType))
        {
            var byteLength = this.resultType is NVarcharSqlType or NCharSqlType || this.resultType == SqlType.NText
                ? result.Length * 2
                : (this.resultType.Collation ?? Collation.Baseline).StorageEncoding.GetByteCount(result);
            if (byteLength > MaxResultBytes)
                throw SimulatedSqlException.StringAggResultExceededLimit();
        }

        return SqlValue.FromString(this.resultType, result);
    }

    private static bool IsMaxForm(SqlType type) =>
        type.IsLob
            || type is NVarcharSqlType { length: SqlType.MaxLengthSentinel }
            || type is VarcharSqlType { length: SqlType.MaxLengthSentinel };

    private int CompareOrderedRows(OrderedRow left, OrderedRow right)
    {
        for (var i = 0; i < this.orderDescending!.Length; i++)
        {
            var l = left.OrderKeys[i];
            var r = right.OrderKeys[i];
            // Match SQL Server ORDER BY: NULLs sort first under ASC; reverse under DESC.
            var cmp = l.IsNull && r.IsNull ? 0
                : l.IsNull ? -1
                : r.IsNull ? 1
                : l.CompareTo(r);
            if (cmp != 0)
                return this.orderDescending[i] ? -cmp : cmp;
        }
        return 0;
    }

    private readonly struct OrderedRow(string value, string separator, SqlValue[] orderKeys)
    {
        public readonly string Value = value;
        public readonly string Separator = separator;
        public readonly SqlValue[] OrderKeys = orderKeys;
    }
}
