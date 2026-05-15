namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Diagnostics carrier returned from <see cref="Simulation.FromBacpac(string, out BacpacLoadResult)"/>.
/// Tracks model.xml elements the loader recognized but couldn't fully apply
/// (e.g. a feature the simulator doesn't model end-to-end) so the caller has
/// a feature-gap report rather than a single throw.
/// </summary>
/// <remarks>
/// The list shape evolves as the loader matures. The baseline goal is "every
/// AW element loads without an entry on Skipped"; entries that show up during
/// the baseline pass become the next development checklist.
/// </remarks>
internal sealed class BacpacLoadResult
{
    /// <summary>
    /// Model.xml elements the loader didn't translate to a CREATE statement.
    /// Each entry names the element type (e.g. <c>SqlFullTextIndex</c>) and
    /// the failing element's <c>Name</c> attribute when present.
    /// </summary>
    internal readonly List<BacpacSkipped> Skipped = [];

    /// <summary>
    /// Non-fatal warnings — element accepted but with reduced fidelity
    /// (e.g. a body whose embedded SQL the simulator's parser rejected,
    /// loaded as a no-op placeholder).
    /// </summary>
    internal readonly List<string> Warnings = [];

    /// <summary>Element counts seen during the walk, keyed by element type.</summary>
    internal readonly Dictionary<string, int> ElementCounts = new(StringComparer.Ordinal);
}

/// <summary>
/// One entry in <see cref="BacpacLoadResult.Skipped"/> — names the element
/// type and its <c>Name</c> attribute, plus a free-form reason.
/// </summary>
internal readonly record struct BacpacSkipped(string ElementType, string? ElementName, string Reason);
