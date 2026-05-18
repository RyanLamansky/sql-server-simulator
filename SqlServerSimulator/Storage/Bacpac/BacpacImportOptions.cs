namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Options passed to <see cref="Simulation.ImportBacpac(string, out BacpacImportResult, BacpacImportOptions?)"/>
/// and its <see cref="System.IO.Stream"/> overload. Defaulted-everything: the
/// parameterless constructor (and a <see langword="null"/>-defaulted
/// <c>options</c> argument) produce the simulator's standard import behavior.
/// </summary>
/// <remarks>
/// A <c>record class</c> for the with-expression update ergonomics
/// (<c>options with { DatabaseName = "audit" }</c>) and for cheap forward
/// compatibility as load knobs accumulate.
/// </remarks>
public sealed record class BacpacImportOptions
{
    /// <summary>
    /// Target database name inside the destination <see cref="Simulation"/>.
    /// <see langword="null"/> defers to the calling overload's default:
    /// <see cref="Path.GetFileNameWithoutExtension(string)"/> of the source
    /// path for the file-based overloads, the simulator's default database
    /// name for the stream-based overloads.
    /// </summary>
    /// <remarks>
    /// The import always creates a fresh <see cref="Database"/>; a name that
    /// already exists in <see cref="Simulation"/> raises
    /// <see cref="InvalidOperationException"/> (matches DACFx's create-only
    /// import contract).
    /// </remarks>
    public string? DatabaseName { get; init; }

    /// <summary>
    /// Worker-thread cap for the per-table parallel data-load phase. <c>-1</c>
    /// (the default) uses <see cref="Environment.ProcessorCount"/>, matching
    /// the convention on <see cref="System.Threading.Tasks.ParallelOptions"/>.
    /// Positive values cap at that many workers; the loader still won't spin
    /// up more workers than there are work items.
    /// </summary>
    /// <remarks>
    /// Test suites that already run their cases in parallel benefit from
    /// pinning to <c>1</c> here so the import doesn't contend with the
    /// per-test parallelism for CPU.
    /// </remarks>
    public int MaxDegreeOfParallelism { get; init; } = -1;
}
