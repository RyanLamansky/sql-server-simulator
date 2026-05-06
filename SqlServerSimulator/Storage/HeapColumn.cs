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
internal sealed class HeapColumn(string name, SqlType type, int? maxLength, bool nullable, IdentityState? identity = null)
{
    public readonly string Name = name;

    public readonly SqlType Type = type;

    public readonly int? MaxLength = maxLength;

    public readonly bool Nullable = nullable;

    /// <summary>
    /// True for columns whose values flow through LOB-chain storage rather
    /// than the row's variable section: <c>text</c>, <c>ntext</c>, <c>image</c>
    /// (always-LOB types) plus <c>varchar(MAX)</c>, <c>nvarchar(MAX)</c>,
    /// <c>varbinary(MAX)</c> (when <see cref="MaxLength"/> is the
    /// <see cref="SqlType.MaxLengthSentinel"/>).
    /// </summary>
    public bool IsLob => this.Type.IsLob || this.MaxLength == SqlType.MaxLengthSentinel;

    /// <summary>
    /// Non-null when the column was declared <c>IDENTITY(seed, increment)</c>;
    /// owns the per-table counter and answers <c>IDENT_CURRENT</c>.
    /// </summary>
    public readonly IdentityState? Identity = identity;

#if DEBUG
    public override string ToString() => $"{this.Name} {this.Type}{(this.MaxLength is int n ? $"({n})" : "")}";
#endif
}
