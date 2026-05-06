using SqlServerSimulator.Parser;

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
internal sealed class HeapColumn(string name, SqlType type, int? maxLength, bool nullable, IdentityState? identity = null, Expression? defaultExpression = null, Expression? computedExpression = null, bool isPersisted = false)
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

    /// <summary>
    /// Parsed <c>DEFAULT</c> expression — non-null when the column declared
    /// one. Evaluated per-row in the INSERT path whenever the column is
    /// omitted from the destination list, replacing the implicit-NULL fill.
    /// </summary>
    public readonly Expression? Default = defaultExpression;

    /// <summary>
    /// Parsed computed-column expression (<c>col AS expr</c>); non-null only
    /// for computed columns. Evaluated per-row at insert time when
    /// <see cref="IsPersisted"/> is true (the result is stored), or per-read
    /// otherwise (the slot has no row storage). Reference resolution against
    /// other computed columns is rejected by the engine (Msg 1759), so an
    /// expression here is guaranteed to bind only to stored columns.
    /// </summary>
    public readonly Expression? Computed = computedExpression;

    /// <summary>
    /// True when a computed column was declared <c>PERSISTED</c>. Persisted
    /// computed columns occupy a row-storage slot like a regular column;
    /// non-persisted ones are absent from the row layout entirely and are
    /// re-evaluated every time they're read.
    /// </summary>
    public readonly bool IsPersisted = isPersisted;

    /// <summary>
    /// True when this column has row storage. Regular columns always do;
    /// computed columns are stored only when <see cref="IsPersisted"/>. The
    /// row encoder/decoder operate over the stored subset; non-stored
    /// computed columns occupy ordinals in <see cref="HeapTable.Columns"/>
    /// for name binding but consume no row bytes.
    /// </summary>
    public bool IsStored => this.Computed is null || this.IsPersisted;

#if DEBUG
    public override string ToString() => $"{this.Name} {this.Type}{(this.MaxLength is int n ? $"({n})" : "")}";
#endif
}
