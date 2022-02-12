using System.Collections.Generic;
using System.Linq;

namespace SqlServerSimulator.Schema;

class Table
{
    private readonly LazyRegeneratingWeakReference<Dictionary<Column, int>> columnByIndex;

    public Table(string name)
    {
        this.Name = name;

        this.columnByIndex = new(() => this
            .Columns
            .Select((column, i) => new KeyValuePair<Column, int>(column, i))
            .ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    public string Name;

    public readonly List<Column> Columns = new();

    public readonly List<object[]> Rows = new();

    public void ReceiveData(IEnumerable<object[]> values)
    {
        foreach (var row in values)
        {
            // TODO: Convert incoming values to the correct types.
            this.Rows.Add(row);
        }
    }

#if DEBUG
    public override string ToString() => $"{this.Name} ({string.Join(", ", Columns.Select(column => column.Name))})";
#endif
}
