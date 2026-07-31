using System.Text;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>STRING_AGG(expr, separator)</c>: concatenates non-NULL values
/// with the separator between them. The separator is evaluated once per row
/// (SQL Server allows a per-row separator value, though it's typically a
/// constant) — the simulator uses the most recent non-NULL separator. Empty /
/// all-NULL input → NULL. Two execution modes share this class:
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

    public override void Add(SqlValue value)
    {
        StringScalars.RejectLegacyLob(value, "string_agg");
        if (value.IsNull)
            return;

        if (this.sawAny)
            _ = this.streamingBuffer.Append(this.lastSeparator);
        _ = this.streamingBuffer.Append(value.AsString);
        this.sawAny = true;
    }

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
        this.orderedBuffer!.Add(new OrderedRow(value.AsString, this.lastSeparator, orderKeys));
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

    private readonly record struct OrderedRow(string Value, string Separator, SqlValue[] OrderKeys);
}
