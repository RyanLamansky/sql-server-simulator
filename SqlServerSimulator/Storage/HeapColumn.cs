namespace SqlServerSimulator.Storage;

/// <summary>
/// A column in a <see cref="HeapTable"/>: name, <see cref="SqlType"/>,
/// and nullability.
/// </summary>
internal sealed class HeapColumn(string name, SqlType type, bool nullable)
{
    public readonly string Name = name;

    public readonly SqlType Type = type;

    public readonly bool Nullable = nullable;

#if DEBUG
    public override string ToString() => $"{this.Name} {this.Type}";
#endif
}
