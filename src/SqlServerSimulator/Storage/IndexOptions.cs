namespace SqlServerSimulator.Storage;

/// <summary>
/// What an index's <c>WITH</c> clause recorded, for an index or the index
/// behind a PRIMARY KEY / UNIQUE constraint: <c>IGNORE_DUP_KEY</c>, which
/// changes how a duplicate key is handled, and <c>FILLFACTOR</c> /
/// <c>PAD_INDEX</c>, which only the catalog reports. The latter two are null
/// when the clause didn't name them, so an <c>ALTER INDEX … REBUILD</c>
/// keeps what it leaves out.
/// </summary>
internal readonly struct IndexOptions(bool ignoreDupKey, byte? fillFactor, bool? padIndex, bool dropExisting = false)
{
    public readonly bool IgnoreDupKey = ignoreDupKey;

    /// <summary>1 to 100 when given; <c>sys.indexes.fill_factor</c> reads 0 otherwise.</summary>
    public readonly byte? FillFactor = fillFactor;

    public readonly bool? PadIndex = padIndex;

    /// <summary><c>DROP_EXISTING = ON</c>: a <c>CREATE INDEX</c> replaces the index of that name.</summary>
    public readonly bool DropExisting = dropExisting;
}
