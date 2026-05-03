namespace SqlServerSimulator.Storage;

/// <summary>
/// A column in a <see cref="HeapTable"/>: name, <see cref="SqlType"/>,
/// declared maximum length (for variable-length string columns), and
/// nullability.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MaxLength"/> is non-null only for variable-length string types
/// (<c>varchar</c>, <c>nvarchar</c>). Its unit follows SQL Server: bytes for
/// <c>varchar</c>, UCS-2 code units for <c>nvarchar</c>.
/// </para>
/// </remarks>
internal sealed class HeapColumn(string name, SqlType type, int? maxLength, bool nullable)
{
    public readonly string Name = name;

    public readonly SqlType Type = type;

    public readonly int? MaxLength = maxLength;

    public readonly bool Nullable = nullable;

#if DEBUG
    public override string ToString() => $"{this.Name} {this.Type}{(this.MaxLength is int n ? $"({n})" : "")}";
#endif
}
