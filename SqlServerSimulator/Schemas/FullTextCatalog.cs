namespace SqlServerSimulator.Schemas;

/// <summary>
/// A database-scope full-text catalog created via
/// <c>CREATE FULLTEXT CATALOG name [AS DEFAULT] [AUTHORIZATION owner]</c>.
/// Stored on <see cref="Database.FullTextCatalogs"/>; surfaced through
/// <c>sys.fulltext_catalogs</c>.
/// </summary>
/// <remarks>
/// The simulator has no full-text search engine — this type exists for
/// (a) AW model.xml round-trip (CREATE FULLTEXT CATALOG <c>[AW2025FullTextCatalog]</c>
/// is one of the elements the bacpac loader emits), and (b) resolving the
/// <c>ON catalog_name</c> back-reference in CREATE FULLTEXT INDEX. The
/// query-time predicates (CONTAINS / FREETEXT / CONTAINSTABLE /
/// FREETEXTTABLE) raise <see cref="NotSupportedException"/> with an
/// explanatory message rather than evaluating against indexed columns.
/// </remarks>
internal sealed class FullTextCatalog(
    int id,
    string name,
    bool isDefault,
    bool isAccentSensitive,
    int principalId,
    DateTime createDate)
{
    public readonly int Id = id;
    public readonly string Name = name;

    /// <summary>True when this catalog is the database's default. Set by
    /// <c>AS DEFAULT</c> at create; flipped when a subsequent
    /// <c>CREATE FULLTEXT CATALOG … AS DEFAULT</c> demotes prior defaults.</summary>
    public bool IsDefault = isDefault;

    /// <summary>Real SQL Server's <c>sys.fulltext_catalogs.is_accent_sensitivity_on</c>
    /// column; defaults to true (matches probe). The simulator preserves the
    /// value but has no semantics that observe it.</summary>
    public readonly bool IsAccentSensitive = isAccentSensitive;

    /// <summary>Owning principal id — <c>sys.fulltext_catalogs.principal_id</c>.
    /// Defaults to <c>dbo</c> (principal_id = 1) unless an explicit
    /// <c>AUTHORIZATION</c> clause names another principal.</summary>
    public readonly int PrincipalId = principalId;

    public readonly DateTime CreateDate = createDate;
}
