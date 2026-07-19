using System.Text;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>JSON_OBJECTAGG(key : value [null_clause])</c>: renders the per-row
/// key/value pairs as a JSON object. The Selection executor calls
/// <see cref="SetKey"/> with the row's evaluated key immediately before
/// <see cref="Add"/> (mirroring <see cref="StringAggAggregator"/>'s separator
/// handling), since the base <see cref="Aggregator.Add"/> contract carries
/// only the value. Keys and values render through the shared
/// <see cref="JsonValueRender"/>; a NULL key raises Msg 13638 (the same path
/// the scalar <c>JSON_OBJECT</c> builder uses).
/// <para>
/// NULL value handling follows the parsed <see cref="JsonNullClause"/>;
/// <c>JSON_OBJECTAGG</c> defaults to <see cref="JsonNullClause.NullOnNull"/>.
/// Duplicate keys are preserved verbatim (no dedup — matches SQL Server).
/// Empty input (zero rows) yields SQL NULL; a group with rows whose values
/// are all absent yields <c>{}</c>.
/// </para>
/// </summary>
internal sealed class JsonObjectAggAggregator(SqlType resultType, JsonNullClause nullClause, bool embedRaw) : Aggregator
{
    private readonly StringBuilder buffer = new StringBuilder().Append('{');

    private SqlValue currentKey;

    private int rowCount;

    private bool wrotePair;

    /// <summary>
    /// Sets the property-name value for the next <see cref="Add"/>; called by
    /// the Selection executor with the row's evaluation of the key expression
    /// before handing over the value.
    /// </summary>
    public void SetKey(SqlValue key) => this.currentKey = key;

    public override void Add(SqlValue value)
    {
        this.rowCount++;
        // Match the scalar JSON_OBJECT ordering: an absent value short-circuits
        // before the key is examined, so a NULL key paired with an absent value
        // does not raise.
        if (value.IsNull && nullClause == JsonNullClause.AbsentOnNull)
            return;
        if (this.wrotePair)
            _ = this.buffer.Append(',');
        JsonValueRender.AppendKey(this.buffer, this.currentKey);
        _ = this.buffer.Append(':');
        JsonValueRender.Append(this.buffer, value, embedRaw);
        this.wrotePair = true;
    }

    // Result can be called repeatedly (running-window frames reuse one
    // aggregator), so the closing '}' is appended to a snapshot rather than
    // mutated into the persistent buffer.
    public override SqlValue Result() =>
        this.rowCount == 0
            ? SqlValue.Null(resultType)
            : SqlValue.FromString(resultType, this.buffer.ToString() + '}');
}
