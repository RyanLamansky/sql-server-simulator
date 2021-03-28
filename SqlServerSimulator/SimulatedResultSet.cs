using System.Collections;
using System.Collections.Generic;

namespace SqlServerSimulator
{
    sealed class SimulatedResultSet : IEnumerable<object[]>
    {
        internal readonly IEnumerable<object[]> records;
        internal readonly Dictionary<string, int> columnIndexes;

        public SimulatedResultSet(IEnumerable<object[]> records, Dictionary<string, int> columnIndexes)
        {
            this.records = records;
            this.columnIndexes = columnIndexes;
        }

        public IEnumerator<object[]> GetEnumerator() => this.records.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
