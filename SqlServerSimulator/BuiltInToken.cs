using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// String-matcher for T-SQL "built-in token" values — spec-defined fixed
/// strings that aren't subject to <c>ALTER DATABASE COLLATE</c>. Examples:
/// the <c>INSERTED</c> / <c>DELETED</c> trigger pseudo-table names; the
/// <c>OBJECT_ID(..., 'U')</c> type-filter codes (<c>U</c> / <c>FN</c> /
/// <c>IF</c> / <c>TF</c> / <c>V</c> / <c>P</c>); the
/// <c>sp_addextendedproperty</c> arg names (<c>@name</c>, <c>@value</c>,
/// <c>@level0type</c>, ...); and the spec-defined arg values for level
/// types (<c>SCHEMA</c>, <c>TABLE</c>, <c>VIEW</c>, <c>PROCEDURE</c>,
/// <c>FUNCTION</c>, <c>TYPE</c>, <c>COLUMN</c>, <c>CONSTRAINT</c>,
/// <c>INDEX</c>).
/// </summary>
/// <remarks>
/// <para>
/// Properties: CI + IgnoreKanaType + IgnoreWidth. Behaviorally a subset of
/// today's <see cref="Collation.Default"/> at the compare level, but
/// without any of the Collation-class machinery
/// (<see cref="Collation.Name"/> / <see cref="Collation.Description"/> /
/// <see cref="Collation.StorageEncoding"/> / coercibility-precedence /
/// supplementary-character toggle / <c>LIKE</c> case-sensitivity flag) —
/// none of which apply at fixed-token sites.
/// </para>
/// <para>
/// Probe-confirmed against SQL Server 2025 (2026-05-21) on a CS database
/// (<c>SQL_Latin1_General_CP1_CS_AS</c>): the sites above continue to
/// match canonical and wrong-case forms (<c>'u'</c>, <c>inserted</c>,
/// <c>'ｓchema'</c>, <c>'ｄbo'</c>) even when the database itself is
/// case-sensitive. By contrast, user identifiers (table / column names,
/// schema names, catalog views, system proc names,
/// <c>hierarchyid::</c> / <c>geography::</c> / <c>geometry::</c>
/// type-prefix dispatch, the <c>dbo</c>/<c>sys</c>/<c>INFORMATION_SCHEMA</c>
/// reserved-name check) <em>do</em> flip under CS — those route through the
/// database's identifier collation, not this matcher. See
/// <c>docs/claude/collations.md</c> for the regime breakdown.
/// </para>
/// </remarks>
internal static class BuiltInToken
{
    private static readonly CompareInfo compareInfo = CultureInfo.InvariantCulture.CompareInfo;

    private const CompareOptions Options =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    /// <summary>
    /// Returns true when <paramref name="x"/> and <paramref name="y"/>
    /// match under the built-in-token compare options (CI + width- /
    /// kanatype-insensitive). Both arms accept <see langword="null"/>:
    /// two nulls are equal; one null + one non-null is not.
    /// </summary>
    public static bool Equals(string? x, string? y) =>
        x is null
            ? y is null
            : y is not null && compareInfo.Compare(x, y, Options) == 0;

    /// <summary>
    /// Returns true when <paramref name="value"/> matches any of
    /// <paramref name="options"/> under the built-in-token compare options.
    /// A <see langword="null"/> <paramref name="value"/> returns false —
    /// the caller is asking "is this one of X/Y/Z?", and a null can't
    /// match any of those.
    /// </summary>
    public static bool EqualsAny(string? value, params ReadOnlySpan<string> options)
    {
        if (value is null)
            return false;
        foreach (var option in options)
        {
            if (compareInfo.Compare(value, option, Options) == 0)
                return true;
        }
        return false;
    }
}
