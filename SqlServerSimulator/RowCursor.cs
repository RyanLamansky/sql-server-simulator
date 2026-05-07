using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Iterates a result set's rows for <see cref="SimulatedDbDataReader"/>.
/// Each accessor returns the column's <see cref="SqlValue"/> directly so the
/// reader's typed <c>Get*</c> methods can route through <c>SqlValue.As*</c>
/// without a boxing detour. Object-typed accessors (<c>GetValue</c> /
/// <c>this[int]</c>) call <c>ToObject()</c> at the public API boundary.
/// </summary>
internal abstract class RowCursor : IDisposable
{
    private bool disposed;

    public abstract int FieldCount { get; }

    public abstract bool MoveNext();

    /// <summary>
    /// Returns the column at <paramref name="ordinal"/> as a
    /// <see cref="SqlValue"/>; <see cref="SqlValue.IsNull"/> distinguishes
    /// SQL NULL from a present value of any type.
    /// </summary>
    public abstract SqlValue this[int ordinal] { get; }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        this.DisposeCore();
        GC.SuppressFinalize(this);
    }

    protected virtual void DisposeCore()
    {
    }
}
