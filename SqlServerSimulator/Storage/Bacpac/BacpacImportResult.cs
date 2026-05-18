namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Diagnostics carrier returned from <see cref="Simulation.ImportBacpac(string, out BacpacImportResult, BacpacImportOptions?)"/>
/// and its <see cref="System.IO.Stream"/> overload. Tracks model.xml elements
/// the loader recognized but couldn't fully apply (e.g. a feature the
/// simulator doesn't model end-to-end) so the caller has a feature-gap report
/// rather than a single throw.
/// </summary>
public sealed class BacpacImportResult
{
    private readonly List<BacpacSkipped> _skipped = [];
    private readonly List<string> _warnings = [];
    private readonly Dictionary<string, int> _elementCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-table per-column "declared via UDDT alias" flags, keyed by
    /// <c>[schema].[table]</c>. UDDT-typed columns get a 1-byte length prefix
    /// in BCP wire format even when NOT NULL, regardless of the underlying
    /// type's natural encoding — the BCP decoder needs this distinction to
    /// align column boundaries correctly. Populated during model.xml
    /// emission, consumed during the data-load pass.
    /// </summary>
    internal readonly Dictionary<string, bool[]> TableColumnIsAlias = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Model.xml elements the loader didn't translate to a CREATE statement.
    /// Each entry names the element type (e.g. <c>SqlFullTextIndex</c>) and
    /// the failing element's <c>Name</c> attribute when present.
    /// </summary>
    public IReadOnlyList<BacpacSkipped> Skipped => _skipped;

    /// <summary>
    /// Non-fatal warnings — element accepted but with reduced fidelity
    /// (e.g. a body whose embedded SQL the simulator's parser rejected,
    /// loaded as a no-op placeholder).
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Element counts seen during the walk, keyed by element type.</summary>
    public IReadOnlyDictionary<string, int> ElementCounts => _elementCounts;

    internal void AddSkipped(BacpacSkipped entry) => _skipped.Add(entry);

    internal void AddWarning(string warning) => _warnings.Add(warning);

    internal void IncrementElementCount(string elementType)
    {
        _ = _elementCounts.TryGetValue(elementType, out var c);
        _elementCounts[elementType] = c + 1;
    }

    internal void AddToElementCount(string elementType, int by)
    {
        _ = _elementCounts.TryGetValue(elementType, out var c);
        _elementCounts[elementType] = c + by;
    }
}

/// <summary>
/// One entry in <see cref="BacpacImportResult.Skipped"/> — names the element
/// type and its <c>Name</c> attribute, plus a free-form reason.
/// </summary>
public readonly record struct BacpacSkipped(string ElementType, string? ElementName, string Reason);
