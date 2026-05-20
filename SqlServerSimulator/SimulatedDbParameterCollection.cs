using System.Data.Common;
using System.Collections;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="DbParameterCollection"/> for the simulator's command pipeline.
/// Adds strongly-typed indexers and an <see cref="Add(SimulatedDbParameter)"/>
/// overload so callers that have downcast a <c>DbCommand</c> to
/// <see cref="SimulatedDbCommand"/> can work with
/// <see cref="SimulatedDbParameter"/> directly — mirrors the same
/// <c>SqlParameterCollection</c> shape <c>Microsoft.Data.SqlClient</c>
/// exposes.
/// </summary>
public sealed class SimulatedDbParameterCollection : DbParameterCollection, IReadOnlyList<SimulatedDbParameter>
{
    private readonly List<SimulatedDbParameter> parameters = [];

    /// <inheritdoc/>
    public override int Count => this.parameters.Count;

    /// <inheritdoc/>
    public override object SyncRoot => ((ICollection)this.parameters).SyncRoot;

    /// <summary>Strongly-typed indexer; shadows <see cref="DbParameterCollection.this[int]"/>.</summary>
    public new SimulatedDbParameter this[int index]
    {
        get => this.parameters[index];
        set => this.parameters[index] = value;
    }

    /// <summary>Strongly-typed indexer; shadows <see cref="DbParameterCollection.this[string]"/>.</summary>
    public new SimulatedDbParameter this[string parameterName]
    {
        get
        {
            var idx = this.IndexOf(parameterName);
            return idx >= 0 ? this.parameters[idx] : throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
        }
        set
        {
            var idx = this.IndexOf(parameterName);
            if (idx < 0)
                throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
            this.parameters[idx] = value;
        }
    }

    /// <inheritdoc/>
    public override int Add(object value)
    {
        this.parameters.Add((SimulatedDbParameter)value);
        return this.parameters.Count - 1;
    }

    /// <summary>
    /// Adds <paramref name="value"/> to the collection and returns it, mirroring
    /// <c>SqlParameterCollection.Add(SqlParameter)</c>'s chainable shape.
    /// </summary>
    public SimulatedDbParameter Add(SimulatedDbParameter value)
    {
        this.parameters.Add(value);
        return value;
    }

    /// <inheritdoc/>
    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var v in values)
            this.parameters.Add((SimulatedDbParameter)v);
    }

    /// <summary>Strongly-typed <see cref="IEnumerable{T}.GetEnumerator"/> via <see cref="IReadOnlyList{T}"/>.</summary>
    IEnumerator<SimulatedDbParameter> IEnumerable<SimulatedDbParameter>.GetEnumerator() => this.parameters.GetEnumerator();

    /// <inheritdoc/>
    public override void Clear() => this.parameters.Clear();

    /// <inheritdoc/>
    public override bool Contains(object value) => this.parameters.Contains((SimulatedDbParameter)value);

    /// <inheritdoc/>
    public override bool Contains(string value) => this.IndexOf(value) >= 0;

    /// <inheritdoc/>
    public override void CopyTo(Array array, int index) => ((ICollection)this.parameters).CopyTo(array, index);

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => this.parameters.GetEnumerator();

    /// <inheritdoc/>
    public override int IndexOf(object value) => this.parameters.IndexOf((SimulatedDbParameter)value);

    /// <inheritdoc/>
    public override int IndexOf(string parameterName) =>
        this.parameters.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public override void Insert(int index, object value) =>
        this.parameters.Insert(index, (SimulatedDbParameter)value);

    /// <inheritdoc/>
    public override void Remove(object value) =>
        _ = this.parameters.Remove((SimulatedDbParameter)value);

    /// <inheritdoc/>
    public override void RemoveAt(int index) => this.parameters.RemoveAt(index);

    /// <inheritdoc/>
    public override void RemoveAt(string parameterName)
    {
        var idx = this.IndexOf(parameterName);
        if (idx >= 0)
            this.parameters.RemoveAt(idx);
    }

    /// <inheritdoc/>
    protected override DbParameter GetParameter(int index) => this.parameters[index];

    /// <inheritdoc/>
    protected override DbParameter GetParameter(string parameterName)
    {
        var idx = this.IndexOf(parameterName);
        return idx >= 0 ? this.parameters[idx] : throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
    }

    /// <inheritdoc/>
    protected override void SetParameter(int index, DbParameter value) =>
        this.parameters[index] = (SimulatedDbParameter)value;

    /// <inheritdoc/>
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var idx = this.IndexOf(parameterName);
        if (idx < 0)
            throw new ArgumentException($"Parameter '{parameterName}' not found.", nameof(parameterName));
        this.parameters[idx] = (SimulatedDbParameter)value;
    }
}
