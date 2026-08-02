using System.Text;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>JSON_ARRAYAGG(value [ORDER BY ...] [null_clause])</c>: renders the
/// per-row values as a JSON array. Each value is formatted through the shared
/// <see cref="JsonValueRender"/> (so numbers / booleans emit unquoted, nested
/// JSON embeds raw, everything else escapes as a JSON string). NULL handling
/// follows the parsed <see cref="JsonNullClause"/> — <c>ABSENT ON NULL</c>
/// (the default) drops NULLs, <c>NULL ON NULL</c> emits a JSON <c>null</c>.
/// <para>
/// Empty input — i.e. a group with zero rows — yields SQL NULL, but a group
/// that <em>has</em> rows whose values are all absent yields <c>[]</c>
/// (probe-confirmed against SQL Server 2025). <see cref="rowCount"/> tracks
/// the former independently of how many fragments were emitted.
/// </para>
/// <para>
/// Two execution modes, mirroring <see cref="StringAggAggregator"/>:
/// <list type="bullet">
///   <item><b>Streaming</b> (no in-parens <c>ORDER BY</c>): rows render into
///   <see cref="streamingBuffer"/> in arrival order.</item>
///   <item><b>Buffered</b> (with <c>ORDER BY</c>): rows stash their value and
///   evaluated ORDER BY tuple in <see cref="orderedBuffer"/> via
///   <see cref="AddOrdered"/>; <see cref="Result"/> sorts then renders.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class JsonArrayAggAggregator : Aggregator
{
    private readonly SqlType resultType;

    private readonly JsonNullClause nullClause;

    private readonly bool embedRaw;

    private readonly bool[]? orderDescending;

    private readonly StringBuilder? streamingBuffer;

    private readonly List<OrderedRow>? orderedBuffer;

    private int rowCount;

    private bool wroteFragment;

    public JsonArrayAggAggregator(SqlType resultType, JsonNullClause nullClause, bool embedRaw, IReadOnlyList<OrderBySpec>? orderBy)
    {
        this.resultType = resultType;
        this.nullClause = nullClause;
        this.embedRaw = embedRaw;
        if (orderBy is null)
        {
            this.streamingBuffer = new StringBuilder().Append('[');
        }
        else
        {
            this.orderedBuffer = [];
            this.orderDescending = new bool[orderBy.Count];
            for (var i = 0; i < orderBy.Count; i++)
                this.orderDescending[i] = orderBy[i].Descending;
        }
    }

    public override void Add(SqlValue value)
    {
        this.rowCount++;
        if (value.IsNull && this.nullClause == JsonNullClause.AbsentOnNull)
            return;
        if (this.wroteFragment)
            _ = this.streamingBuffer!.Append(',');
        JsonValueRender.Append(this.streamingBuffer!, value, this.embedRaw);
        this.wroteFragment = true;
    }

    /// <summary>
    /// Buffered-path companion to <see cref="Add"/>: stashes the row's value
    /// and evaluated ORDER BY tuple. Sorting and rendering happen in
    /// <see cref="Result"/>. The null-clause filter is applied at render time
    /// (so a dropped NULL still counts toward <see cref="rowCount"/>).
    /// </summary>
    public void AddOrdered(SqlValue value, SqlValue[] orderKeys)
    {
        this.rowCount++;
        this.orderedBuffer!.Add(new OrderedRow(value, orderKeys));
    }

    public override SqlValue Result()
    {
        if (this.rowCount == 0)
            return SqlValue.Null(this.resultType);

        // Result can be called repeatedly (running-window frames reuse one
        // streaming aggregator), so the closing ']' is appended to a snapshot
        // rather than mutated into the persistent buffer.
        if (this.orderedBuffer is null)
            return SqlValue.FromString(this.resultType, this.streamingBuffer!.ToString() + ']');

        this.orderedBuffer.Sort(this.CompareOrderedRows);
        var output = new StringBuilder().Append('[');
        var wrote = false;
        foreach (var row in this.orderedBuffer)
        {
            if (row.Value.IsNull && this.nullClause == JsonNullClause.AbsentOnNull)
                continue;
            if (wrote)
                _ = output.Append(',');
            JsonValueRender.Append(output, row.Value, this.embedRaw);
            wrote = true;
        }
        return SqlValue.FromString(this.resultType, output.Append(']').ToString());
    }

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

    private readonly struct OrderedRow(SqlValue value, SqlValue[] orderKeys)
    {
        public readonly SqlValue Value = value;
        public readonly SqlValue[] OrderKeys = orderKeys;
    }
}
