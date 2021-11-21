using System.Collections;
using System.Collections.Generic;

namespace SqlServerSimulator;

sealed class SimulatedResultSet : SimulatedStatementOutcome, IEnumerable<object[]>
{
    internal readonly IEnumerable<object[]> records;
    internal readonly Dictionary<string, int> columnIndexes;

    public SimulatedResultSet(Dictionary<string, int> columnIndexes, params object[][] records)
        : this(columnIndexes, (IEnumerable<object[]>)records)
    {
    }

    public SimulatedResultSet(Dictionary<string, int> columnIndexes, IEnumerable<object[]> records)
        : base(-1)
    {
        this.records = records;
        this.columnIndexes = columnIndexes;
    }

    public IEnumerator<object[]> GetEnumerator() => this.records.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
