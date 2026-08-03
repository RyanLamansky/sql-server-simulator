using System.Buffers;
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
/// today's <see cref="Collation.Baseline"/> at the compare level, but
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
    /// The character set over which the linguistic compare below and
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> are interchangeable.
    /// </summary>
    private static readonly SearchValues<char> ordinalComparableCharacters =
        SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Whether <paramref name="value"/> lies in the character range where an
    /// ordinal-ignore-case compare answers what
    /// <see cref="CompareInfo.Compare(string, string, CompareOptions)"/> under
    /// <see cref="Options"/> answers, letting the cheaper comparison stand in.
    /// </summary>
    /// <remarks>
    /// The two comparisons agree on ASCII alphanumerics because each such
    /// character carries its own primary collation weight, case is the only
    /// difference the options erase, and none of them participate in a
    /// contraction, an expansion, or a zero weight. Every character outside
    /// that range is a candidate to break one of those properties, and the
    /// ones that do are not exotic: a fullwidth <c>Ｓ</c> matches an ASCII
    /// <c>S</c> under <see cref="CompareOptions.IgnoreWidth"/>, and control
    /// characters carry no weight at all, so an <c>a</c> followed by
    /// U+0001 and an <c>a</c> followed by U+0002 compare equal
    /// linguistically while an ordinal compare separates them. Both cases
    /// land on the linguistic path.
    /// </remarks>
    private static bool IsOrdinalComparable(string value) =>
        !value.AsSpan().ContainsAnyExcept(ordinalComparableCharacters);

    /// <summary>
    /// Returns true when <paramref name="x"/> and <paramref name="y"/>
    /// match under the built-in-token compare options (CI + width- /
    /// kanatype-insensitive). Both arms accept <see langword="null"/>:
    /// two nulls are equal; one null + one non-null is not.
    /// </summary>
    public static bool Equals(string? x, string? y) =>
        x is null
            ? y is null
            : y is not null && MatchesNonNull(x, y);

    /// <summary>
    /// The compare itself, on the cheaper path when
    /// <see cref="IsOrdinalComparable"/> admits both arguments.
    /// </summary>
    private static bool MatchesNonNull(string x, string y) =>
        IsOrdinalComparable(x) && IsOrdinalComparable(y)
            ? x.Equals(y, StringComparison.OrdinalIgnoreCase)
            : compareInfo.Compare(x, y, Options) == 0;

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

        // The whole option list is walked per call, so classify the one
        // argument that varies once rather than per option.
        if (!IsOrdinalComparable(value))
        {
            foreach (var option in options)
            {
                if (compareInfo.Compare(value, option, Options) == 0)
                    return true;
            }
            return false;
        }

        foreach (var option in options)
        {
            var matched = IsOrdinalComparable(option)
                ? value.Equals(option, StringComparison.OrdinalIgnoreCase)
                : compareInfo.Compare(value, option, Options) == 0;
            if (matched)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Hash code consistent with <see cref="Equals(string?, string?)"/>:
    /// two strings that compare equal under the built-in-token options
    /// hash to the same value. Required when a built-in-token-keyed type
    /// participates in a dictionary or hashset.
    /// </summary>
    /// <remarks>
    /// Stays on the linguistic path for every input, including the ones
    /// <see cref="IsOrdinalComparable"/> admits: equality still reaches
    /// across that boundary — a fullwidth <c>Ｓchema</c> equals an ASCII
    /// <c>SCHEMA</c> — so only a hash that folds width and case alike keeps
    /// the pair in the same bucket.
    /// </remarks>
    public static int GetHashCode(string value) =>
        compareInfo.GetHashCode(value, Options);

    /// <summary>
    /// Singleton <see cref="IEqualityComparer{T}"/> wrapper for use as a
    /// dictionary / hashset comparer. The wire-up routes through the
    /// static <see cref="Equals(string?, string?)"/> /
    /// <see cref="GetHashCode(string)"/> entry points, so dict-backed
    /// state stays semantically identical to ad-hoc compares.
    /// </summary>
    internal static readonly ComparerImpl Comparer = new();

    internal sealed class ComparerImpl : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => BuiltInToken.Equals(x, y);

        public int GetHashCode(string obj) => BuiltInToken.GetHashCode(obj);
    }
}
