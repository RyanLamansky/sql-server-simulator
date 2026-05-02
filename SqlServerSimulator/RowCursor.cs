namespace SqlServerSimulator;

/// <summary>
/// Iterates a result set's rows for <see cref="SimulatedDbDataReader"/>.
/// Each row exposes its column values as .NET objects (or <c>null</c> for SQL
/// NULL); the public reader translates <c>null</c> to <see cref="DBNull.Value"/>.
/// </summary>
internal abstract class RowCursor : IDisposable
{
    private bool disposed;

    public abstract int FieldCount { get; }

    public abstract bool MoveNext();

    /// <summary>
    /// Returns the .NET object representation of the column at <paramref name="ordinal"/>,
    /// or <c>null</c> for SQL NULL. The caller is responsible for converting <c>null</c>
    /// to <see cref="DBNull.Value"/> at the public API boundary.
    /// </summary>
    public abstract object? GetValueObject(int ordinal);

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
