using System.Globalization;

namespace SqlServerSimulator.Parser;

/// <summary>
/// An invariant-culture name comparer under <c>options</c> whose hash is
/// memoized by ordinal text — what keys a batch's variables, table variables
/// and cursor variables (<see cref="BatchContext.VariableNameComparer"/>,
/// <see cref="BatchContext.TableVariableNameComparer"/>).
/// </summary>
/// <remarks>
/// The culture-aware hash builds an ICU sort key, about 80 ns for a short
/// name, paid by every lookup — which added up to a fifth of a batch naming
/// many parameters (EF Core's multi-row insert), since it seeds, binds and
/// reads each one through <see cref="BatchContext.Variables"/> and Query
/// Store reads each again; <see cref="MemoizedHashComparer"/> holds the memo.
/// No cheaper exact hash exists: the equivalence reaches across ASCII
/// (<c>fi</c> equals the <c>ﬁ</c> ligature, and <c>ab</c> equals <c>a</c>,
/// soft hyphen, <c>b</c>), so only the sort key agrees across a class.
/// </remarks>
internal sealed class MemoizedNameComparer : StringComparer
{
    private readonly StringComparer culture;

    private readonly MemoizedHashComparer keys;

    public MemoizedNameComparer(CompareOptions options)
    {
        this.culture = Create(CultureInfo.InvariantCulture, options);
        this.keys = new(this.culture);
    }

    public override int Compare(string? x, string? y) => this.culture.Compare(x, y);

    public override bool Equals(string? x, string? y) => this.keys.Equals(x, y);

    public override int GetHashCode(string obj) => this.keys.GetHashCode(obj);
}
