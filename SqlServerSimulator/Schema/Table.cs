namespace SqlServerSimulator.Schema;

using Parser;
using Parser.Tokens;

class Table
{
    public Table(string name)
    {
        this.Name = name;
    }

    public Table(string name, IEnumerable<Column> columns, IEnumerable<object?[]> rows)
        : this(name)
    {
        this.Columns.AddRange(columns);
        this.Rows.AddRange(rows);
    }

    public string Name;

    public readonly List<Column> Columns = new();

    public readonly List<object?[]> Rows = new();

    public void ReceiveData(Column[] columnsUsed, IEnumerable<Token[]> values, Func<string, object?> getVariableValue)
    {
        foreach (var row in values)
        {
            var incomingRow = new object[this.Columns.Count];

            for (var i = 0; i < columnsUsed.Length; i++)
            {
                var column = columnsUsed[i];
                var columnValue = row[i];

                var toConvert = columnValue switch
                {
                    AtPrefixedString atPrefixed => getVariableValue(atPrefixed.Value),
                    StringToken token => token.Value,
                    Numeric numeric => numeric.Value,
                    _ => throw new NotSupportedException($"Simulate data processing doesn't know how to handle input of type {columnValue.GetType().Name}"),
                };

                if (toConvert is null)
                    continue;

                incomingRow[i] = column.Type.ConvertFrom(toConvert);
            }

            this.Rows.Add(incomingRow);
        }
    }

#if DEBUG
    public override string ToString() => $"{this.Name} ({string.Join(", ", Columns.Select(column => column.Name))})";
#endif
}
