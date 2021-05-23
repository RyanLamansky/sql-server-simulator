using System.Collections.Generic;
using System.Linq;

namespace SqlServerSimulator.Schema
{
    class Table
    {
        public Table(string name)
        {
            this.Name = name;
        }

        public string Name;

        public List<Column> Columns = new();

#if DEBUG
        public override string ToString() => $"{this.Name} ({string.Join(", ", Columns.Select(column => column.Name))})";
#endif
    }
}
