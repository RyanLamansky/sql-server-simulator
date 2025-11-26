using SqlServerSimulator.Parser;
using System.Collections;
using System.Collections.Frozen;

namespace SqlServerSimulator;

sealed class SimulatedResultSet(List<Expression> expressions, IEnumerable<object?[]> records) : SimulatedStatementOutcome(-1), IEnumerable<object?[]>
{
    internal readonly IEnumerable<object?[]> records = records;
    internal readonly FrozenDictionary<string, int> columnIndexes = expressions
        .Select((x, i) => (x, i))
        .Where(x => x.x.Name.Length > 0)
        .ToFrozenDictionary(x => x.x.Name, x => x.i);
    internal readonly string[] columnNames = [.. expressions.Select(x => x.Name)];

    public IEnumerator<object?[]> GetEnumerator() => this.records.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
