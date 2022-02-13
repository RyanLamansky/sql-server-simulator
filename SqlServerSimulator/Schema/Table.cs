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

    public void ReceiveData(Column[] columnsUsed, IEnumerable<object[]> values)
    {
        foreach (var row in values)
        {
            var incomingRow = new object[this.Columns.Count];

            for (var i = 0; i < columnsUsed.Length; i++)
            {
                var column = columnsUsed[i];
                var columnValue = row[i];

                incomingRow[i] = column.Type.ConvertFrom(columnValue);
            }

            this.Rows.Add(incomingRow);
        }
    }

#if DEBUG
    public override string ToString() => $"{this.Name} ({string.Join(", ", Columns.Select(column => column.Name))})";
#endif
}
