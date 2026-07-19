using System.Collections;

namespace SqlServerSimulator;

/// <summary>
/// Read-only collection of <see cref="SimulatedError"/> instances carried by
/// a <see cref="SimulatedInfoMessageEventArgs"/>. Mirrors the public surface
/// of <c>Microsoft.Data.SqlClient.SqlErrorCollection</c>: integer-indexed,
/// <see cref="Count"/>-projecting, enumerable. Today the simulator coalesces
/// each batch's <c>PRINT</c> output into a single <see cref="SimulatedError"/>
/// entry — the collection shape matches SqlClient so a future probe that
/// splits the per-statement granularity can grow the count without changing
/// the consumer-facing API.
/// </summary>
public sealed class SimulatedErrorCollection : ICollection, IReadOnlyList<SimulatedError>
{
    private readonly SimulatedError[] entries;

    internal SimulatedErrorCollection(SimulatedError[] entries) => this.entries = entries;

    /// <summary>Indexed entry. Throws <see cref="IndexOutOfRangeException"/> if out of range, matching SqlClient.</summary>
    public SimulatedError this[int index] => this.entries[index];

    /// <summary>Number of entries.</summary>
    public int Count => this.entries.Length;

    /// <inheritdoc/>
    bool ICollection.IsSynchronized => false;

    /// <inheritdoc/>
    object ICollection.SyncRoot => this.entries;

    /// <inheritdoc/>
    public void CopyTo(Array array, int index) => this.entries.CopyTo(array, index);

    /// <summary>Copies entries into a strongly-typed array starting at <paramref name="index"/>.</summary>
    public void CopyTo(SimulatedError[] array, int index) => this.entries.CopyTo(array, index);

    /// <inheritdoc/>
    public IEnumerator<SimulatedError> GetEnumerator() => ((IEnumerable<SimulatedError>)this.entries).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.entries.GetEnumerator();
}
