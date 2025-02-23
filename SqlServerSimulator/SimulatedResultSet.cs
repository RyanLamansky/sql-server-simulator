using System.Collections;

namespace SqlServerSimulator;

sealed class SimulatedResultSet(Dictionary<string, int> columnIndexes, IEnumerable<object?[]> records) : SimulatedStatementOutcome(-1), IEnumerable<object?[]>
{
    internal readonly IEnumerable<object?[]> records = records;
    internal readonly Dictionary<string, int> columnIndexes = columnIndexes;
    internal readonly string[] columnNames = [.. columnIndexes
        .OrderBy(kv => kv.Value)
        .Select(kv => kv.Key)];

    public SimulatedResultSet(Dictionary<string, int> columnIndexes, params object?[][] records)
        : this(columnIndexes, (IEnumerable<object?[]>)records)
    {
    }

    public IEnumerator<object?[]> GetEnumerator() => this.records.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
