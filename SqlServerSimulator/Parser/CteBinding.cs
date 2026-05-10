namespace SqlServerSimulator.Parser;

/// <summary>
/// One named binding from a <c>WITH</c> prefix. Lifetime is exactly one
/// following statement: <see cref="Simulation.CreateResultSetsForCommand"/>
/// populates <see cref="ParserContext.CteBindings"/> via <c>ParseCteBindings</c>
/// before dispatching to SELECT / INSERT / UPDATE / DELETE / MERGE, and
/// clears the slot at the start of the next iteration.
/// </summary>
/// <remarks>
/// <see cref="Plan"/> is null while the body is mid-parse — a sentinel so
/// <c>ParseSingleFromSource</c> can detect self-references (recursive CTEs)
/// and raise <see cref="NotSupportedException"/>. Once the body's
/// <see cref="Selection"/> resolves, the sentinel is replaced by the real
/// plan and the binding becomes resolvable from the FROM side.
/// </remarks>
internal sealed class CteBinding(string name, string[] columnNames)
{
    public readonly string Name = name;
    public readonly string[] ColumnNames = columnNames;

    /// <summary>
    /// The body's parsed plan, or <see langword="null"/> while parsing is
    /// in flight. <see cref="Plan"/> is the only mutable field — set once
    /// when the body's <c>Selection</c> resolves.
    /// </summary>
    public Selection? Plan;
}
