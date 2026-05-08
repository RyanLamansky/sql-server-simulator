using System.Data.Common;
using System.Collections;

namespace SqlServerSimulator;

sealed class SimulatedDbParameterCollection : DbParameterCollection
{
    readonly List<SimulatedDbParameter> parameters = [];

    public override int Count => this.parameters.Count;

    public override object SyncRoot => ((ICollection)this.parameters).SyncRoot;

    public override int Add(object value)
    {
        this.parameters.Add((SimulatedDbParameter)value);
        return this.parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values)
            this.parameters.Add((SimulatedDbParameter)v);
    }

    public override void Clear() => this.parameters.Clear();

    public override bool Contains(object value) => this.parameters.Contains((SimulatedDbParameter)value);

    public override bool Contains(string value) => this.IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)this.parameters).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => this.parameters.GetEnumerator();

    public override int IndexOf(object value) => this.parameters.IndexOf((SimulatedDbParameter)value);

    public override int IndexOf(string parameterName) =>
        this.parameters.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));

    public override void Insert(int index, object value) =>
        this.parameters.Insert(index, (SimulatedDbParameter)value);

    public override void Remove(object value) =>
        _ = this.parameters.Remove((SimulatedDbParameter)value);

    public override void RemoveAt(int index) => this.parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        var idx = this.IndexOf(parameterName);
        if (idx >= 0)
            this.parameters.RemoveAt(idx);
    }

    protected override DbParameter GetParameter(int index) => this.parameters[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        var idx = this.IndexOf(parameterName);
        return idx >= 0 ? this.parameters[idx] : throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
    }

    protected override void SetParameter(int index, DbParameter value) =>
        this.parameters[index] = (SimulatedDbParameter)value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var idx = this.IndexOf(parameterName);
        if (idx < 0)
            throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
        this.parameters[idx] = (SimulatedDbParameter)value;
    }
}
