using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Schema;

sealed class Table(string name)
{
    public Table(string name, IEnumerable<Column> columns, IEnumerable<object?[]> rows)
        : this(name)
    {
        this.Columns.AddRange(columns);

        foreach (var row in rows)
        {
            var typed = new DataValue[this.Columns.Count];
            this.Rows.Add(typed);

            for (var i = 0; i < typed.Length; i++)
                typed[i] = new(row[i], this.Columns[i].Type);
        }
    }

    public string Name = name;

    public readonly List<Column> Columns = [];

    public readonly List<DataValue[]> Rows = [];

    public void ReceiveData(Column[] columnsUsed, IEnumerable<Token[]> values, Func<string, DataValue> getVariableValue)
    {
        foreach (var row in values)
        {
            var incomingRow = new DataValue[this.Columns.Count];

            for (var i = 0; i < columnsUsed.Length; i++)
            {
                incomingRow[i] = row[i] switch
                {
                    AtPrefixedString atPrefixed => getVariableValue(atPrefixed.Value),
                    StringToken token => columnsUsed[i].Type.ConvertFrom(token.Value),
                    Numeric numeric => numeric.Value,
                    var columnValue => throw new NotSupportedException($"Simulate data processing doesn't know how to handle input of type {columnValue.GetType().Name}"),
                };
            }

            this.Rows.Add(incomingRow);
        }
    }

#if DEBUG
    public override string ToString() => $"{this.Name} ({string.Join(", ", Columns.Select(column => column.Name))})";
#endif
}
