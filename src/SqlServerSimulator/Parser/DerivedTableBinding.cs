namespace SqlServerSimulator.Parser;

/// <summary>
/// A derived table, <c>(SELECT …) alias</c>, as a write through it reads it:
/// real writes through one exactly as through a view with that body — as a
/// view's or CTE's source, and as a joined <c>UPDATE</c> / <c>DELETE</c>'s
/// target (probed 2026-10-01 against SQL Server 2025).
/// </summary>
internal sealed class DerivedTableBinding(Selection body, string alias, string[] columnNames, Database database)
{
    public readonly Selection Body = body;

    /// <summary>The alias, which every refusal of a write through the derived table names.</summary>
    public readonly string Alias = alias;

    /// <summary>The body's projected column names, or the alias's column list.</summary>
    public readonly string[] ColumnNames = columnNames;

    /// <summary>The database whose statement wrote the derived table, whose <c>dbo</c> the unstored view names.</summary>
    public readonly Database Database = database;

    /// <summary>
    /// The derived table analyzed as an unstored view, built the first time a
    /// write reaches it.
    /// </summary>
    public Schemas.View? DmlTarget;
}
