namespace SqlServerSimulator.Parser;

/// <summary>
/// What one <c>ALTER TABLE</c> did, for the <c>AlterTableActionList</c> its
/// <c>ALTER_TABLE</c> event carries (probed 2026-10-21 against SQL Server
/// 2025): the columns and constraints it created, in creation order, the
/// column it altered, the columns and constraints it dropped, in written
/// order, and the constraints or triggers it enabled or disabled — by name,
/// or all of them. <c>REBUILD</c>, <c>SET</c> and <c>SWITCH</c> list none.
/// </summary>
internal sealed class AlterTableActions
{
    public readonly List<string> CreatedColumns = [];
    public readonly List<string> CreatedConstraints = [];
    public readonly List<string> AlteredColumns = [];
    public readonly List<string> DroppedColumns = [];
    public readonly List<string> DroppedConstraints = [];

    /// <summary><c>Enable</c> or <c>Disable</c> for a toggle, null for none.</summary>
    public string? Toggle;

    /// <summary>Whether the toggle names triggers rather than constraints.</summary>
    public bool ToggleTriggers;

    /// <summary>The names the toggle lists, null for <c>ALL</c>.</summary>
    public List<string>? ToggleNames;
}
